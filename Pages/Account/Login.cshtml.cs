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
    [BindProperty, Required, EmailAddress, StringLength(256)] public string Email { get; set; } = "";
    [BindProperty, Required, DataType(DataType.Password), StringLength(256)] public string Password { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    public void OnGet() { }
    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();
        var result = await signIn.PasswordSignInAsync(Email.Trim(), Password, false, lockoutOnFailure: true);
        if (result.Succeeded) return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/");
        ModelState.AddModelError("", "Sign-in failed. Check your credentials or try again later.");
        return Page();
    }
}
