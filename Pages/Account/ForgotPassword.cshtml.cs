using System.ComponentModel.DataAnnotations;
using System.Text;
using Azure;
using Azure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using MyStatus.Domain;
using MyStatus.Services;

namespace MyStatus.Pages.Account;

[EnableRateLimiting("auth")]
public sealed class ForgotPasswordModel(
    UserManager<ApplicationUser> users, IResetEmail emailSender, IConfiguration config,
    IWebHostEnvironment environment, ILogger<ForgotPasswordModel> logger) : PageModel
{
    [BindProperty, Required, EmailAddress, StringLength(256)] public string Email { get; set; } = "";
    public bool Sent { get; set; }
    public void OnGet() { }
    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();
        var user = await users.FindByEmailAsync(Email.Trim());
        if (user is not null && await users.IsEmailConfirmedAsync(user))
        {
            var origin = string.IsNullOrWhiteSpace(config["App:PublicUrl"])
                ? (environment.IsDevelopment() ? "https://localhost:7143" : "") : config["App:PublicUrl"]!;
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme != "https")
                throw new InvalidOperationException("Configure an HTTPS App:PublicUrl before requesting password resets.");
            var token = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(await users.GeneratePasswordResetTokenAsync(user)));
            var link = QueryHelpers.AddQueryString(origin.TrimEnd('/') + "/Account/ResetPassword",
                new Dictionary<string, string?> { ["userId"] = user.Id, ["code"] = token });
            try { await emailSender.SendAsync(user.Email!, link); }
            catch (Exception ex) when (ex is RequestFailedException or AuthenticationFailedException or CredentialUnavailableException or IOException or UnauthorizedAccessException)
            {
                logger.LogError(new EventId(1001, "ResetEmailFailure"), "Password reset email delivery failed ({FailureType}). Check provider authentication, sender configuration and local capture permissions.", ex.GetType().Name);
            }
        }
        Sent = true;
        return Page();
    }
}
