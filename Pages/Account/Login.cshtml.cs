using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using MyStatus.Services;

namespace MyStatus.Pages.Account;

[EnableRateLimiting("auth")]
public sealed class LoginModel(MicrosoftAccountOptions microsoft) : PageModel
{
    public bool Configured => microsoft.IsConfigured;
    [BindProperty(SupportsGet = true)] public bool SignInFailed { get; set; }
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    public void OnGet() { }
    public IActionResult OnPost()
    {
        if (!ModelState.IsValid) return Page();
        if (!Configured)
        {
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            ModelState.AddModelError("", "Microsoft sign-in is not configured. Follow the setup instructions in README.md.");
            return Page();
        }
        return Challenge(new AuthenticationProperties { RedirectUri = Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/" },
            MicrosoftAccountOptions.Scheme);
    }
}
