using Azure;
using Azure.Communication.Email;
using Azure.Identity;
using System.Net;

namespace MyStatus.Services;

public interface IResetEmail
{
    Task SendAsync(string address, string resetUrl);
}

public sealed class ResetEmail(IConfiguration config, IWebHostEnvironment environment, ILogger<ResetEmail> logger) : IResetEmail
{
    public async Task SendAsync(string address, string resetUrl)
    {
        if (environment.IsDevelopment())
        {
            Directory.CreateDirectory(".local/mail");
            var path = Path.Combine(".local", "mail", $"{Guid.NewGuid():N}.html");
            await File.WriteAllTextAsync(path, $"<p>Development-only password reset. Expires in 30 minutes.</p><a href=\"{WebUtility.HtmlEncode(resetUrl)}\">Reset password</a>");
            logger.LogInformation("Development reset email captured in the gitignored .local/mail directory.");
            return;
        }
        var client = new EmailClient(new Uri(config["Email:Endpoint"]!), new DefaultAzureCredential());
        var content = new EmailContent("Reset your My Status password")
        {
            PlainText = $"Reset your password using this single-use link within 30 minutes:\n{resetUrl}\nIf you did not request this, ignore this email."
        };
        await client.SendAsync(WaitUntil.Completed, new EmailMessage(config["Email:Sender"]!, address, content));
    }
}
