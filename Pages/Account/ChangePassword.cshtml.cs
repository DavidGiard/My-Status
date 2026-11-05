using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using MyStatus.Domain;

namespace MyStatus.Pages.Account;

[EnableRateLimiting("password-change")]
public sealed class ChangePasswordModel(UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn) : PageModel
{
    [BindProperty, Required, StringLength(256), DataType(DataType.Password)] public string CurrentPassword { get; set; } = "";
    [BindProperty, Required, StringLength(256, MinimumLength = 12), DataType(DataType.Password)] public string NewPassword { get; set; } = "";
    [BindProperty, Required, Compare(nameof(NewPassword)), DataType(DataType.Password)] public string ConfirmPassword { get; set; } = "";
    public void OnGet() { }
    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();
        var user = await users.GetUserAsync(User) ?? throw new InvalidOperationException("User no longer exists.");
        var result = await users.ChangePasswordAsync(user, CurrentPassword, NewPassword);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors) ModelState.AddModelError("", error.Description);
            return Page();
        }
        await signIn.RefreshSignInAsync(user);
        TempData["Message"] = "Password changed.";
        return RedirectToPage("/Account/Settings");
    }
}
