using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MyStatus.Domain;

namespace MyStatus.Pages.Account;

[Authorize]
public sealed class SettingsModel(UserManager<ApplicationUser> users) : OwnedPage
{
    [BindProperty, Required, StringLength(100), Display(Name = "Time zone")] public string TimeZoneId { get; set; } = "UTC";
    [BindProperty] public string? Stamp { get; set; }
    public async Task OnGetAsync()
    {
        var user = await users.GetUserAsync(User) ?? throw new InvalidOperationException("User no longer exists.");
        TimeZoneId = user.TimeZoneId;
        Stamp = user.ConcurrencyStamp;
    }
    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(TimeZoneId, out _))
        {
            ModelState.AddModelError(nameof(TimeZoneId), "Select a valid system time zone.");
            return Page();
        }
        var user = await users.GetUserAsync(User) ?? throw new InvalidOperationException("User no longer exists.");
        if (user.ConcurrencyStamp != Stamp) { Conflict(); return Page(); }
        user.TimeZoneId = TimeZoneId;
        var result = await users.UpdateAsync(user);
        if (!result.Succeeded) { Conflict(); return Page(); }
        TempData["Message"] = "Settings saved.";
        return RedirectToPage();
    }
}
