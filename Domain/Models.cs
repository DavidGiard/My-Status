using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;

namespace MyStatus.Domain;

public sealed class ApplicationUser : IdentityUser
{
    [MaxLength(100)]
    public string TimeZoneId { get; set; } = "UTC";
}

public enum ActivityStatus { NotStarted, InProgress, Complete, Cancelled }

public static class StatusNames
{
    public static string Label(this ActivityStatus status) => status switch
    {
        ActivityStatus.NotStarted => "Not started",
        ActivityStatus.InProgress => "In progress",
        ActivityStatus.Complete => "Complete",
        ActivityStatus.Cancelled => "Cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };
}

public sealed class Activity
{
    public int Id { get; set; }
    public string OwnerId { get; set; } = "";
    public DateOnly Date { get; set; }
    public string Description { get; set; } = "";
    public int CategoryId { get; set; }
    public Category Category { get; set; } = null!;
    public ActivityStatus Status { get; set; }
    public bool IncludeInReport { get; set; } = true;
    public DateOnly? CompletionDate { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
    public ICollection<ActivityTag> ActivityTags { get; set; } = new List<ActivityTag>();
}

public sealed class Category
{
    public int Id { get; set; }
    public string OwnerId { get; set; } = "";
    public string Name { get; set; } = "";
    public string NormalizedName { get; set; } = "";
    // Zero denotes the root namespace, keeping sibling uniqueness portable across providers.
    public int ParentScope { get; set; }
    public int? ParentId { get; set; }
    public Category? Parent { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
    public string Path => Parent is null ? Name : $"{Parent.Name} > {Name}";
}

public sealed class Tag
{
    public int Id { get; set; }
    public string OwnerId { get; set; } = "";
    public string Name { get; set; } = "";
    public string NormalizedName { get; set; } = "";
    public Guid Version { get; set; } = Guid.NewGuid();
}

public sealed class ActivityTag
{
    public int ActivityId { get; set; }
    public Activity Activity { get; set; } = null!;
    public int TagId { get; set; }
    public Tag Tag { get; set; } = null!;
    public string OwnerId { get; set; } = "";
}
