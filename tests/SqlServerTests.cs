using Microsoft.EntityFrameworkCore;
using MyStatus.Data;
using MyStatus.Domain;
using MyStatus.Services;
using Xunit;

namespace MyStatus.Tests;

public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("MY_STATUS_SQLSERVER_TEST") != "1")
            Skip = "Opt-in Windows LocalDB test: set MY_STATUS_SQLSERVER_TEST=1.";
    }
}

public sealed class SqlServerTests
{
    [SqlServerFact]
    public async Task Production_provider_migrations_reports_constraints_and_concurrency()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(
            $"Server=(localdb)\\MSSQLLocalDB;Database=MyStatusTest_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True").Options;
        await using var db = new AppDbContext(options);
        try
        {
            await db.Database.MigrateAsync();
            Assert.False(db.Database.HasPendingModelChanges());
            db.Users.AddRange(new ApplicationUser { Id = "owner" }, new ApplicationUser { Id = "other" });
            await db.SaveChangesAsync();
            var taxonomy = new TaxonomyService(db);
            await taxonomy.SaveCategoryAsync("owner", null, "Root", null, default);
            var root = await db.Categories.SingleAsync();
            await taxonomy.SaveCategoryAsync("owner", null, "Child", root.Id, default);
            var child = await db.Categories.SingleAsync(x => x.ParentId != null);
            var activities = new ActivityService(db, new FixedClock());
            await activities.SaveAsync("owner", null, new ActivityInput
            {
                Date = new(2026, 1, 1), Description = "SQL persisted", CategoryId = child.Id,
                Status = ActivityStatus.Complete, CompletionDate = new(2026, 5, 1)
            });
            var report = await new ReportService(db).GenerateAsync("owner", new(2026, 5, 1), new(2026, 5, 1));
            Assert.Equal("SQL persisted", Assert.Single(Assert.Single(report.Groups).Items).Description);
            var activity = await db.Activities.SingleAsync();
            await using var otherContext = new AppDbContext(options);
            var stale = await otherContext.Activities.SingleAsync();
            activity.Description = "First writer"; activity.Version = Guid.NewGuid(); await db.SaveChangesAsync();
            stale.Description = "Second writer"; stale.Version = Guid.NewGuid();
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => otherContext.SaveChangesAsync());
            db.ChangeTracker.Clear();
            db.Categories.Add(new Category { OwnerId = "owner", Name = "Third", NormalizedName = "THIRD", ParentId = child.Id, ParentScope = child.Id });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();
            db.Activities.Add(new MyStatus.Domain.Activity { OwnerId = "other", CategoryId = root.Id, Description = "Wrong owner", Date = new(2026, 1, 1) });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }
}
