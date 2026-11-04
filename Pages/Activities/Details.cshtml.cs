using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MyStatus.Data;
using MyStatus.Domain;
using MyStatus.Services;

namespace MyStatus.Pages.Activities;

public sealed class DetailsModel(AppDbContext db, ActivityService service) : OwnedPage
{
    public Activity Activity { get; set; } = null!;
    [BindProperty] public Guid Version { get; set; }
    public async Task<IActionResult> OnGetAsync(int id)
    {
        if (!await LoadAsync(id)) return NotFound();
        Version = Activity.Version;
        return Page();
    }
    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        if (!ModelState.IsValid) return BadRequest();
        try
        {
            await service.DeleteAsync(Owner, id, Version);
            TempData["Message"] = "Activity deleted.";
            return RedirectToPage("/Index");
        }
        catch (RuleException) { return NotFound(); }
        catch (DbUpdateConcurrencyException) { Conflict(); }
        catch (DbUpdateException ex) { SaveError(ex); }
        if (!await LoadAsync(id)) return NotFound();
        return Page();
    }
    private async Task<bool> LoadAsync(int id)
    {
        var activity = await db.Activities.AsNoTracking().Where(x => x.OwnerId == Owner && x.Id == id)
            .Include(x => x.Category).ThenInclude(x => x.Parent).Include(x => x.ActivityTags).ThenInclude(x => x.Tag).SingleOrDefaultAsync();
        if (activity is null) return false;
        Activity = activity;
        return true;
    }
}
