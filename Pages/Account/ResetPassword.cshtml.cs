using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using MyStatus.Domain;

namespace MyStatus.Pages.Account;

[EnableRateLimiting("auth")]
public sealed class ResetPasswordModel(UserManager<ApplicationUser> users) : PageModel
{
    [BindProperty(SupportsGet = true), Required, StringLength(450)] public string UserId { get; set; } = "";
    [BindProperty(SupportsGet = true), Required, StringLength(4096)] public string Code { get; set; } = "";
    [BindProperty, Required, StringLength(256, MinimumLength = 12), DataType(DataType.Password)] public string Password { get; set; } = "";
    [BindProperty, Required, Compare(nameof(Password)), DataType(DataType.Password), Display(Name = "Confirm password")] public string ConfirmPassword { get; set; } = "";
    public bool Done { get; set; }
    public void OnGet() { }
    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();
        string token;
        try { token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(Code)); }
        catch (FormatException)
        {
            ModelState.AddModelError("", "This reset link is invalid or expired. Request a new link.");
            return Page();
        }
        var user = await users.FindByIdAsync(UserId);
        var result = user is null ? IdentityResult.Failed() : await users.ResetPasswordAsync(user, token, Password);
        if (!result.Succeeded)
        {
            ModelState.AddModelError("", "The reset failed. The link may be invalid, expired or already used, or the password may not meet the requirements.");
            return Page();
        }
        Done = true;
        return Page();
    }
}
