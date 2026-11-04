using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MyStatus.Data;
using MyStatus.Domain;
using MyStatus.Services;

namespace MyStatus.Pages.Activities;

public sealed class EditModel(AppDbContext db, ActivityService service, TimeProvider clock) : OwnedPage
{
    [BindProperty] public ActivityInput Input { get; set; } = new();
    public List<Category> Categories { get; set; } = [];
    public List<Tag> Tags { get; set; } = [];
    public int? ActivityId { get; set; }
    public async Task<IActionResult> OnGetAsync(int? id)
    {
        ActivityId = id;
        await LoadAsync();
        if (id.HasValue)
        {
            var activity = await service.Owned(Owner).AsNoTracking().Include(x => x.ActivityTags).SingleOrDefaultAsync(x => x.Id == id);
            if (activity is null) return NotFound();
            Input = new ActivityInput
            {
                Date = activity.Date, Description = activity.Description, CategoryId = activity.CategoryId,
                Status = activity.Status, CompletionDate = activity.CompletionDate, IncludeInReport = activity.IncludeInReport,
                TagIds = activity.ActivityTags.Select(x => x.TagId).ToArray(), Version = activity.Version
            };
        }
        else
        {
            var zone = await db.Users.Where(x => x.Id == Owner).Select(x => x.TimeZoneId).SingleAsync();
            Input.Date = UserDates.Today(zone, clock);
        }
        return Page();
    }
    public async Task<IActionResult> OnPostAsync(int? id)
    {
        ActivityId = id;
        if (id.HasValue && !await service.Owned(Owner).AnyAsync(x => x.Id == id)) return NotFound();
        if (ModelState.IsValid)
        {
            try
            {
                await service.SaveAsync(Owner, id, Input);
                TempData["Message"] = "Activity saved.";
                return RedirectToPage("/Index");
            }
            catch (RuleException ex) { ModelState.AddModelError("", ex.Message); }
            catch (DbUpdateConcurrencyException) { Conflict(); }
            catch (DbUpdateException ex) { SaveError(ex); }
        }
        await LoadAsync();
        return Page();
    }
    private async Task LoadAsync()
    {
        Categories = await db.Categories.AsNoTracking().Where(x => x.OwnerId == Owner).Include(x => x.Parent).ToListAsync();
        Tags = await db.Tags.AsNoTracking().Where(x => x.OwnerId == Owner).OrderBy(x => x.Name).ToListAsync();
    }
}
