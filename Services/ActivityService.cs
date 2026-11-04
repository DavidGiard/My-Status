using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using MyStatus.Data;
using MyStatus.Domain;

namespace MyStatus.Services;

public sealed class RuleException(string message) : Exception(message);

public sealed class ActivityInput
{
    [Required, DataType(DataType.Date), Display(Name = "Activity / planned date")]
    public DateOnly? Date { get; set; }
    [Required, StringLength(2000)]
    public string Description { get; set; } = "";
    [Range(1, int.MaxValue), Display(Name = "Category or subcategory")]
    public int CategoryId { get; set; }
    [Required, EnumDataType(typeof(ActivityStatus))]
    public ActivityStatus? Status { get; set; } = ActivityStatus.NotStarted;
    [Display(Name = "Include in status reports")]
    public bool IncludeInReport { get; set; } = true;
    [DataType(DataType.Date), Display(Name = "Completion date")]
    public DateOnly? CompletionDate { get; set; }
    public int[] TagIds { get; set; } = [];
    public Guid Version { get; set; }
}

public static class UserDates
{
    public static DateOnly Today(string timeZoneId, TimeProvider clock) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById(timeZoneId)).DateTime);
}

public sealed class ActivityService(AppDbContext db, TimeProvider clock)
{
    public IQueryable<Activity> Owned(string owner) => db.Activities.Where(x => x.OwnerId == owner);

    public async Task SaveAsync(string owner, int? id, ActivityInput input, CancellationToken ct = default)
    {
        Validator.ValidateObject(input, new ValidationContext(input), validateAllProperties: true);
        var description = input.Description.Trim();
        if (description.Length == 0) throw new RuleException("Enter a nonblank description.");
        if (!await db.Categories.AnyAsync(x => x.Id == input.CategoryId && x.OwnerId == owner, ct))
            throw new RuleException("Choose one of your categories.");
        var tagIds = input.TagIds.Distinct().ToArray();
        if (await db.Tags.CountAsync(x => x.OwnerId == owner && tagIds.Contains(x.Id), ct) != tagIds.Length)
            throw new RuleException("One or more selected tags are not available.");
        var activity = id.HasValue
            ? await Owned(owner).Include(x => x.ActivityTags).SingleOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new RuleException("Activity not found.")
            : new Activity { OwnerId = owner };
        if (id.HasValue)
        {
            if (activity.Version != input.Version) throw new DbUpdateConcurrencyException();
            db.Entry(activity).Property(x => x.Version).OriginalValue = input.Version;
        }
        var zone = await db.Users.Where(x => x.Id == owner).Select(x => x.TimeZoneId).SingleAsync(ct);
        activity.Date = input.Date!.Value;
        activity.Description = description;
        activity.CategoryId = input.CategoryId;
        activity.Status = input.Status!.Value;
        activity.IncludeInReport = input.IncludeInReport;
        activity.CompletionDate = activity.Status == ActivityStatus.Complete
            ? input.CompletionDate ?? UserDates.Today(zone, clock) : null;
        activity.Version = Guid.NewGuid();
        foreach (var removed in activity.ActivityTags.Where(x => !tagIds.Contains(x.TagId)).ToList())
            db.ActivityTags.Remove(removed);
        foreach (var tagId in tagIds.Except(activity.ActivityTags.Select(x => x.TagId)))
            activity.ActivityTags.Add(new ActivityTag { TagId = tagId, OwnerId = owner });
        if (!id.HasValue) db.Activities.Add(activity);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string owner, int id, Guid version, CancellationToken ct = default)
    {
        var activity = await Owned(owner).SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new RuleException("Activity not found.");
        db.Entry(activity).Property(x => x.Version).OriginalValue = version;
        db.Activities.Remove(activity);
        await db.SaveChangesAsync(ct);
    }
}
