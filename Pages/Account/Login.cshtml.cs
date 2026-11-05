using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using MyStatus.Domain;

namespace MyStatus.Pages.Account;

[EnableRateLimiting("auth")]
public sealed class LoginModel(SignInManager<ApplicationUser> signIn) : PageModel
{
    [BindProperty, Required, StringLength(100)] public string Username { get; set; } = "";
    [BindProperty, Required, StringLength(256), DataType(DataType.Password)] public string Password { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    public void OnGet() { }
    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();
        var result = await signIn.PasswordSignInAsync(Username.Trim(), Password, isPersistent: false, lockoutOnFailure: true);
        if (result.Succeeded) return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/");
        ModelState.AddModelError("", "Sign-in failed. Check your username and password or try again later.");
        return Page();
    }
}
