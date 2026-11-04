using System.ComponentModel.DataAnnotations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyStatus.Data;
using MyStatus.Domain;
using MyStatus.Services;
using Xunit;
using Activity = MyStatus.Domain.Activity;

namespace MyStatus.Tests;

public sealed class TestStore : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    public LocalDbContext Db { get; private set; } = null!;
    public LocalDbContext NewContext() => new(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
    public static async Task<TestStore> CreateAsync()
    {
        var store = new TestStore();
        await store.connection.OpenAsync();
        store.Db = store.NewContext();
        await store.Db.Database.MigrateAsync();
        store.Db.Users.AddRange(
            new ApplicationUser { Id = "owner", UserName = "owner", TimeZoneId = "UTC" },
            new ApplicationUser { Id = "other", UserName = "other", TimeZoneId = "UTC" });
        await store.Db.SaveChangesAsync();
        return store;
    }
    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        await connection.DisposeAsync();
    }
    public async Task<Category> CategoryAsync(string name = "Work", string owner = "owner", Category? parent = null)
    {
        var category = new Category { OwnerId = owner, Name = name, NormalizedName = TaxonomyService.Normalize(name), Parent = parent, ParentId = parent?.Id, ParentScope = parent?.Id ?? 0 };
        Db.Categories.Add(category); await Db.SaveChangesAsync(); return category;
    }
}

public sealed class FixedClock : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(2026, 6, 10, 0, 30, 0, TimeSpan.Zero);
}

public sealed class DomainTests
{
    [Fact]
    public async Task Report_has_exact_inclusive_membership_and_stable_groups_without_tag_duplicates()
    {
        await using var store = await TestStore.CreateAsync();
        var db = store.Db;
        var work = await store.CategoryAsync();
        var sub = await store.CategoryAsync("Alpha", parent: work);
        var other = await store.CategoryAsync("Other", "other");
        var tag1 = new Tag { OwnerId = "owner", Name = "One", NormalizedName = "ONE" };
        var tag2 = new Tag { OwnerId = "owner", Name = "Two", NormalizedName = "TWO" };
        db.Tags.AddRange(tag1, tag2); await db.SaveChangesAsync();
        var start = new DateOnly(2026, 5, 1); var end = new DateOnly(2026, 5, 31);
        Activity Row(string description, DateOnly? date, Category category, ActivityStatus status = ActivityStatus.Complete, bool flag = true) =>
            new() { OwnerId = category.OwnerId, Description = description, CategoryId = category.Id, Date = start.AddYears(-1), CompletionDate = date, Status = status, IncludeInReport = flag };
        var first = Row("Boundary start", start, work);
        first.ActivityTags.Add(new ActivityTag { Tag = tag1, OwnerId = "owner" });
        first.ActivityTags.Add(new ActivityTag { Tag = tag2, OwnerId = "owner" });
        db.Activities.AddRange(first, Row("Boundary end", end, sub), Row("A second", start, work),
            Row("Before", start.AddDays(-1), work), Row("After", end.AddDays(1), work),
            Row("Unchecked", start, work, flag: false), Row("Other owner", start, other),
            Row("Not started", null, work, ActivityStatus.NotStarted),
            Row("In progress", null, work, ActivityStatus.InProgress), Row("Cancelled", null, work, ActivityStatus.Cancelled));
        await db.SaveChangesAsync();
        var report = await new ReportService(db).GenerateAsync("owner", start, end);
        Assert.Equal(2, report.Groups.Count);
        Assert.Null(report.Groups[0].Subcategory);
        Assert.Equal("Alpha", report.Groups[1].Subcategory);
        Assert.Equal(new[] { "A second", "Boundary start", "Boundary end" }, report.Groups.SelectMany(x => x.Items).Select(x => x.Description));
        Assert.Contains("Work / Alpha", report.ToPlainText());
        Assert.DoesNotContain("Other owner", report.ToPlainText());
        Assert.True(await db.Activities.Where(x => x.Id == first.Id).Select(x => x.IncludeInReport).SingleAsync());
        Assert.Empty((await new ReportService(db).GenerateAsync("owner", end.AddYears(1), end.AddYears(1))).Groups);
        await Assert.ThrowsAsync<RuleException>(() => new ReportService(db).GenerateAsync("owner", end, start));
    }

    [Fact]
    public async Task Completion_transitions_dates_tags_and_persistence_work()
    {
        await using var store = await TestStore.CreateAsync();
        var category = await store.CategoryAsync();
        var db = store.Db;
        var tag = new Tag { OwnerId = "owner", Name = "Tag", NormalizedName = "TAG" };
        db.Tags.Add(tag); await db.SaveChangesAsync();
        var user = await db.Users.SingleAsync(x => x.Id == "owner");
        user.TimeZoneId = "America/Los_Angeles"; await db.SaveChangesAsync();
        var service = new ActivityService(db, new FixedClock());
        var input = new ActivityInput { Date = new(2026, 6, 8), Description = "  Plain <script>text</script>  ", CategoryId = category.Id, Status = ActivityStatus.Complete, TagIds = [tag.Id, tag.Id] };
        await service.SaveAsync("owner", null, input);
        var row = await db.Activities.Include(x => x.ActivityTags).SingleAsync();
        Assert.Equal(new DateOnly(2026, 6, 9), row.CompletionDate);
        Assert.Equal("Plain <script>text</script>", row.Description);
        Assert.Single(row.ActivityTags); Assert.True(row.IncludeInReport);
        input.Version = row.Version; input.Status = ActivityStatus.InProgress;
        input.CompletionDate = new(2026, 1, 1);
        await service.SaveAsync("owner", row.Id, input); Assert.Null(row.CompletionDate);
        input.Version = row.Version; input.Status = ActivityStatus.Complete; input.CompletionDate = null;
        await service.SaveAsync("owner", row.Id, input); Assert.Equal(new DateOnly(2026, 6, 9), row.CompletionDate);
        input.Version = row.Version; input.CompletionDate = new(2026, 5, 4);
        await service.SaveAsync("owner", row.Id, input); Assert.Equal(input.CompletionDate, row.CompletionDate);
        input.Version = row.Version; input.Status = ActivityStatus.Cancelled;
        await service.SaveAsync("owner", row.Id, input); Assert.Null(row.CompletionDate);
        await using var reopened = store.NewContext();
        Assert.Equal("Plain <script>text</script>", (await reopened.Activities.SingleAsync()).Description);
        Assert.Equal(1, await reopened.ActivityTags.CountAsync());
    }

    [Fact]
    public async Task Activity_validation_and_cross_owner_associations_are_rejected()
    {
        await using var store = await TestStore.CreateAsync();
        var own = await store.CategoryAsync(); var foreign = await store.CategoryAsync("Foreign", "other");
        var tag = new Tag { OwnerId = "other", Name = "Foreign", NormalizedName = "FOREIGN" };
        store.Db.Tags.Add(tag); await store.Db.SaveChangesAsync();
        var service = new ActivityService(store.Db, new FixedClock());
        var input = new ActivityInput { Date = new(2026, 6, 1), Description = "Valid", CategoryId = foreign.Id };
        await Assert.ThrowsAsync<RuleException>(() => service.SaveAsync("owner", null, input));
        input.CategoryId = own.Id; input.TagIds = [tag.Id];
        await Assert.ThrowsAsync<RuleException>(() => service.SaveAsync("owner", null, input));
        input.TagIds = []; input.Status = (ActivityStatus)99;
        await Assert.ThrowsAsync<ValidationException>(() => service.SaveAsync("owner", null, input));
        input.Status = null;
        await Assert.ThrowsAsync<ValidationException>(() => service.SaveAsync("owner", null, input));
        input.Status = ActivityStatus.NotStarted; input.Date = null;
        await Assert.ThrowsAsync<ValidationException>(() => service.SaveAsync("owner", null, input));
        input.Date = new(2026, 6, 1); input.Description = "   ";
        await Assert.ThrowsAsync<ValidationException>(() => service.SaveAsync("owner", null, input));
        input.Description = new string('x', 2001);
        await Assert.ThrowsAsync<ValidationException>(() => service.SaveAsync("owner", null, input));
        input.Description = "Valid"; await service.SaveAsync("owner", null, input);
        var row = await store.Db.Activities.SingleAsync();
        input.Version = row.Version;
        await Assert.ThrowsAsync<RuleException>(() => service.SaveAsync("other", row.Id, input));
        await Assert.ThrowsAsync<RuleException>(() => service.DeleteAsync("other", row.Id, row.Version));
    }

    [Fact]
    public async Task Stale_activity_edit_and_delete_cannot_overwrite()
    {
        await using var store = await TestStore.CreateAsync();
        var category = await store.CategoryAsync();
        var service = new ActivityService(store.Db, new FixedClock());
        var input = new ActivityInput { Date = new(2026, 6, 1), Description = "Initial", CategoryId = category.Id };
        await service.SaveAsync("owner", null, input);
        var row = await store.Db.Activities.SingleAsync();
        var stale = row.Version; input.Version = row.Version; input.Description = "Winner";
        await service.SaveAsync("owner", row.Id, input);
        await using var second = store.NewContext();
        var other = new ActivityService(second, new FixedClock());
        input.Version = stale; input.Description = "Loser";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => other.SaveAsync("owner", row.Id, input));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => other.DeleteAsync("owner", row.Id, stale));
        Assert.Equal("Winner", (await store.Db.Activities.AsNoTracking().SingleAsync()).Description);
    }

    [Fact]
    public async Task Categories_enforce_sibling_uniqueness_two_levels_and_referenced_deletion()
    {
        await using var store = await TestStore.CreateAsync();
        var service = new TaxonomyService(store.Db);
        var root = await store.CategoryAsync();
        var child = await store.CategoryAsync("Child", parent: root);
        var other = await store.CategoryAsync("Other");
        await Assert.ThrowsAsync<RuleException>(() => service.SaveCategoryAsync("owner", null, " work ", null, default));
        await Assert.ThrowsAsync<RuleException>(() => service.SaveCategoryAsync("owner", null, "Ｃｈｉｌｄ", root.Id, default));
        await service.SaveCategoryAsync("owner", null, "Child", other.Id, default);
        await Assert.ThrowsAsync<RuleException>(() => service.SaveCategoryAsync("owner", null, "Third", child.Id, default));
        await Assert.ThrowsAsync<RuleException>(() => service.SaveCategoryAsync("owner", root.Id, "Work", root.Id, root.Version));
        await Assert.ThrowsAsync<RuleException>(() => service.SaveCategoryAsync("owner", root.Id, "Work", other.Id, root.Version));
        await Assert.ThrowsAsync<RuleException>(() => service.DeleteCategoryAsync("owner", root.Id, root.Version));
        await new ActivityService(store.Db, new FixedClock()).SaveAsync("owner", null,
            new ActivityInput { Date = new(2026, 1, 1), Description = "Work", CategoryId = child.Id });
        await Assert.ThrowsAsync<RuleException>(() => service.DeleteCategoryAsync("owner", child.Id, child.Version));
        await Assert.ThrowsAsync<RuleException>(() => service.SaveCategoryAsync("other", null, "Foreign", root.Id, default));
    }

    [Fact]
    public async Task Tag_delete_removes_assignments_not_activities_and_stale_edits_fail()
    {
        await using var store = await TestStore.CreateAsync();
        var service = new TaxonomyService(store.Db); var category = await store.CategoryAsync();
        await service.SaveTagAsync("owner", null, "Tag", default);
        var tag = await store.Db.Tags.SingleAsync(); var stale = tag.Version;
        await Assert.ThrowsAsync<RuleException>(() => service.SaveTagAsync("owner", null, " Ｔａｇ ", default));
        await service.SaveTagAsync("owner", tag.Id, "Renamed", stale);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.SaveTagAsync("owner", tag.Id, "Lost", stale));
        await new ActivityService(store.Db, new FixedClock()).SaveAsync("owner", null,
            new ActivityInput { Date = new(2026, 1, 1), Description = "Work", CategoryId = category.Id, TagIds = [tag.Id] });
        store.Db.ChangeTracker.Clear();
        await service.DeleteTagAsync("owner", tag.Id, tag.Version);
        Assert.Empty(await store.Db.ActivityTags.ToListAsync()); Assert.Single(await store.Db.Activities.ToListAsync());
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.SaveCategoryAsync("owner", category.Id, "Lost", null, Guid.NewGuid()));
    }

    [Theory]
    [InlineData("description")]
    [InlineData("completion")]
    [InlineData("status")]
    [InlineData("owner")]
    public async Task Database_rejects_invalid_direct_writes(string violation)
    {
        await using var store = await TestStore.CreateAsync();
        var own = await store.CategoryAsync(); var foreign = await store.CategoryAsync("Foreign", "other");
        var row = new Activity { OwnerId = "owner", CategoryId = own.Id, Date = new(2026, 1, 1), Description = "Valid" };
        switch (violation)
        {
            case "description": row.Description = new string('a', 2001); break;
            case "completion": row.Status = ActivityStatus.Complete; break;
            case "status": row.Status = (ActivityStatus)9; break;
            case "owner": row.CategoryId = foreign.Id; break;
        }
        store.Db.Activities.Add(row);
        await Assert.ThrowsAsync<DbUpdateException>(() => store.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task Database_prevents_third_level_and_duplicate_names_and_assignment_owners()
    {
        await using var store = await TestStore.CreateAsync();
        var root = await store.CategoryAsync(); var child = await store.CategoryAsync("Child", parent: root);
        var db = store.Db;
        db.Categories.Add(new Category { OwnerId = "owner", Name = "Third", NormalizedName = "THIRD", ParentId = child.Id, ParentScope = child.Id });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        db.Categories.Add(new Category { OwnerId = "owner", Name = "Work", NormalizedName = "WORK" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        await new ActivityService(db, new FixedClock()).SaveAsync("owner", null, new ActivityInput { Date = new(2026, 1, 1), Description = "Own", CategoryId = root.Id });
        var row = await db.Activities.SingleAsync();
        var foreign = new Tag { OwnerId = "other", Name = "Foreign", NormalizedName = "FOREIGN" };
        db.Tags.Add(foreign); await db.SaveChangesAsync();
        db.ActivityTags.Add(new ActivityTag { ActivityId = row.Id, OwnerId = "owner", TagId = foreign.Id });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
