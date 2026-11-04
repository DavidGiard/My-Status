using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MyStatus.Data;
using MyStatus.Domain;
using MyStatus.Services;

namespace MyStatus.Pages;

public sealed class TaxonomyModel(AppDbContext db, TaxonomyService service) : OwnedPage
{
    [BindProperty(SupportsGet = true)] public string Kind { get; set; } = "";
    [BindProperty(SupportsGet = true)] public int? EditId { get; set; }
    [BindProperty(SupportsGet = true)] public int? DeleteId { get; set; }
    [BindProperty, Required, StringLength(100)] public string Name { get; set; } = "";
    [BindProperty] public int? ParentId { get; set; }
    [BindProperty] public Guid Version { get; set; }
    public bool IsTag => Kind == "tags";
    public string Title => IsTag ? "Tags" : "Categories";
    public List<Category> Categories { get; set; } = [];
    public List<Tag> Tags { get; set; } = [];
    public async Task<IActionResult> OnGetAsync()
    {
        if (Kind is not ("categories" or "tags")) return NotFound();
        await LoadAsync();
        var selected = EditId ?? DeleteId;
        if (selected.HasValue)
        {
            if (IsTag)
            {
                var item = Tags.SingleOrDefault(x => x.Id == selected);
                if (item is null) return NotFound();
                Name = item.Name; Version = item.Version;
            }
            else
            {
                var item = Categories.SingleOrDefault(x => x.Id == selected);
                if (item is null) return NotFound();
                Name = item.Name; Version = item.Version; ParentId = item.ParentId;
            }
        }
        return Page();
    }
    public async Task<IActionResult> OnPostAsync()
    {
        if (Kind is not ("categories" or "tags")) return NotFound();
        if (ModelState.IsValid)
        {
            try
            {
                if (IsTag) await service.SaveTagAsync(Owner, EditId, Name, Version);
                else await service.SaveCategoryAsync(Owner, EditId, Name, ParentId, Version);
                TempData["Message"] = IsTag ? "Tag saved." : "Category saved.";
                return RedirectToPage(new { kind = Kind });
            }
            catch (RuleException ex) { ModelState.AddModelError("", ex.Message); }
            catch (DbUpdateConcurrencyException) { Conflict(); }
            catch (DbUpdateException ex) { SaveError(ex); }
        }
        await LoadAsync();
        return Page();
    }
    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (Kind is not ("categories" or "tags") || !DeleteId.HasValue || Version == Guid.Empty) return BadRequest();
        try
        {
            if (IsTag) await service.DeleteTagAsync(Owner, DeleteId.Value, Version);
            else await service.DeleteCategoryAsync(Owner, DeleteId.Value, Version);
            TempData["Message"] = IsTag ? "Tag and its assignments deleted. Activities were kept." : "Category deleted.";
            return RedirectToPage(new { kind = Kind });
        }
        catch (RuleException ex) { ModelState.AddModelError("", ex.Message); }
        catch (DbUpdateConcurrencyException) { Conflict(); }
        catch (DbUpdateException ex) { SaveError(ex); }
        await LoadAsync();
        return Page();
    }
    private async Task LoadAsync()
    {
        Categories = await db.Categories.AsNoTracking().Where(x => x.OwnerId == Owner).Include(x => x.Parent).OrderBy(x => x.Name).ToListAsync();
        Tags = await db.Tags.AsNoTracking().Where(x => x.OwnerId == Owner).OrderBy(x => x.Name).ToListAsync();
    }
}
