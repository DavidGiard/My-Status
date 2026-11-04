using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MyStatus.Data;
using MyStatus.Domain;

namespace MyStatus.Pages;

public sealed class IndexModel(AppDbContext db) : OwnedPage
{
    [BindProperty(SupportsGet = true), StringLength(200)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }
    [BindProperty(SupportsGet = true)] public bool CompletionDates { get; set; }
    [BindProperty(SupportsGet = true), EnumDataType(typeof(ActivityStatus))] public ActivityStatus? Status { get; set; }
    [BindProperty(SupportsGet = true)] public int? CategoryId { get; set; }
    [BindProperty(SupportsGet = true)] public int[] TagIds { get; set; } = [];
    [BindProperty(SupportsGet = true)] public bool? ReportFlag { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    public List<Activity> Activities { get; set; } = [];
    public List<Category> Categories { get; set; } = [];
    public List<Tag> Tags { get; set; } = [];
    public int Count { get; set; }
    public int Pages => Math.Max(1, (Count + 24) / 25);
    public Dictionary<string, string> PageRoute(int number)
    {
        var values = Request.Query.Where(x => !string.Equals(x.Key, "PageNumber", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(x => x.Key, x => x.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        // TagIds is rendered separately as repeated query values by the pagination links.
        values.Remove("TagIds");
        values["PageNumber"] = number.ToString();
        return values;
    }
    public string PageUrl(int number)
    {
        var url = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString("/", PageRoute(number)!);
        foreach (var tag in TagIds)
            url = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(url, "TagIds", tag.ToString());
        return url;
    }
    public async Task OnGetAsync()
    {
        Categories = await db.Categories.AsNoTracking().Where(x => x.OwnerId == Owner).Include(x => x.Parent)
            .OrderBy(x => x.Name).ToListAsync();
        Tags = await db.Tags.AsNoTracking().Where(x => x.OwnerId == Owner).OrderBy(x => x.Name).ToListAsync();
        if (From > To) ModelState.AddModelError("", "The start date must be on or before the end date.");
        if (CategoryId.HasValue && !Categories.Any(x => x.Id == CategoryId))
            ModelState.AddModelError("", "The selected category is unavailable.");
        if (TagIds.Any(id => !Tags.Any(x => x.Id == id))) ModelState.AddModelError("", "A selected tag is unavailable.");
        if (!ModelState.IsValid) return;
        var query = db.Activities.AsNoTracking().Where(x => x.OwnerId == Owner);
        if (!string.IsNullOrWhiteSpace(Search)) query = query.Where(x => x.Description.Contains(Search.Trim()));
        if (From.HasValue) query = CompletionDates ? query.Where(x => x.CompletionDate >= From) : query.Where(x => x.Date >= From);
        if (To.HasValue) query = CompletionDates ? query.Where(x => x.CompletionDate <= To) : query.Where(x => x.Date <= To);
        if (Status.HasValue) query = query.Where(x => x.Status == Status);
        if (CategoryId.HasValue) query = query.Where(x => x.CategoryId == CategoryId || x.Category.ParentId == CategoryId);
        if (TagIds.Length > 0) query = query.Where(x => x.ActivityTags.Any(t => TagIds.Contains(t.TagId)));
        if (ReportFlag.HasValue) query = query.Where(x => x.IncludeInReport == ReportFlag);
        Count = await query.CountAsync();
        PageNumber = Math.Clamp(PageNumber, 1, Pages);
        Activities = await query.Include(x => x.Category).ThenInclude(x => x.Parent)
            .Include(x => x.ActivityTags).ThenInclude(x => x.Tag)
            .OrderByDescending(x => x.Date).ThenByDescending(x => x.Id)
            .Skip((PageNumber - 1) * 25).Take(25).ToListAsync();
    }
}
