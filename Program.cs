using System.Threading.RateLimiting;
using Azure.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using MyStatus.Data;
using MyStatus.Domain;
using MyStatus.Services;

var builder = WebApplication.CreateBuilder(args);
var development = builder.Environment.IsDevelopment();
var sqlite = development && builder.Configuration["Database:Provider"] != "SqlServer";
if (sqlite)
{
    Directory.CreateDirectory(".local");
    builder.Services.AddDbContext<LocalDbContext>(o => o.UseSqlite(
        builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=.local/my-status.db"));
    builder.Services.AddScoped<AppDbContext>(sp => sp.GetRequiredService<LocalDbContext>());
}
else
{
    var connection = builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Set ConnectionStrings__DefaultConnection for SQL Server.");
    builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlServer(connection));
}
if (!development)
{
    if (!Uri.TryCreate(builder.Configuration["App:PublicUrl"], UriKind.Absolute, out var publicUrl)
        || publicUrl.Scheme != "https" || publicUrl.AbsolutePath != "/" || !string.IsNullOrEmpty(publicUrl.Query)
        || !string.IsNullOrEmpty(publicUrl.Fragment) || !string.IsNullOrEmpty(publicUrl.UserInfo))
        throw new InvalidOperationException("App:PublicUrl must be the canonical HTTPS origin.");
}
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(o =>
{
    o.Password.RequiredLength = 12;
    o.Lockout.MaxFailedAccessAttempts = 5;
    o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
}).AddEntityFrameworkStores<AppDbContext>();
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(5));
builder.Services.ConfigureApplicationCookie(o =>
{
    o.Cookie.Name = "__Host-MyStatus";
    o.Cookie.HttpOnly = true;
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.LoginPath = "/Account/Login";
    o.AccessDeniedPath = "/Error";
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.SlidingExpiration = true;
});
var keys = builder.Services.AddDataProtection().SetApplicationName("MyStatus").PersistKeysToDbContext<AppDbContext>();
if (!development)
{
    var key = builder.Configuration["DataProtection:KeyIdentifier"];
    if (!Uri.TryCreate(key, UriKind.Absolute, out var keyUri) || keyUri.Scheme != "https")
        throw new InvalidOperationException("Configure the Key Vault DataProtection:KeyIdentifier.");
    keys.ProtectKeysWithAzureKeyVault(keyUri, new DefaultAzureCredential());
}
else if (OperatingSystem.IsWindows())
{
    keys.ProtectKeysWithDpapi();
}
builder.Services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/");
    o.Conventions.AllowAnonymousToPage("/Account/Login");
    o.Conventions.AllowAnonymousToPage("/Error");
});
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 8, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("password-change", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 8, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ActivityService>();
builder.Services.AddScoped<TaxonomyService>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddScoped<AccountProvisioner>();
var app = builder.Build();

if (args.Contains("--migrate") || args.Contains("--provision") || args.Contains("--set-password"))
{
    using var scope = app.Services.CreateScope();
    if (args.Contains("--migrate"))
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    if (args.Contains("--provision") || args.Contains("--set-password"))
    {
        var replace = args.Contains("--set-password");
        if (replace)
        {
            Console.Write("This replaces the sole account's credentials, preserving its data. Type REPLACE to confirm: ");
            if (Console.ReadLine() != "REPLACE") throw new InvalidOperationException("Cancelled. No account data changed.");
        }
        Console.Write("Username: ");
        var username = Console.ReadLine() ?? "";
        Console.Write("Password (12+ characters, upper/lowercase, digit, symbol): ");
        string password;
        if (Console.IsInputRedirected) password = Console.ReadLine() ?? "";
        else
        {
            var input = new System.Text.StringBuilder();
            ConsoleKeyInfo key;
            while ((key = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
            {
                if (key.Key == ConsoleKey.Backspace && input.Length > 0) input.Length--;
                else if (!char.IsControl(key.KeyChar)) input.Append(key.KeyChar);
            }
            password = input.ToString();
            Console.WriteLine();
        }
        await scope.ServiceProvider.GetRequiredService<AccountProvisioner>().ProvisionAsync(
            username, password, replace);
        Console.WriteLine("Local account saved. Public registration is disabled.");
    }
    return;
}

app.UseExceptionHandler("/Error");
if (!development) app.UseHsts();
app.UseHttpsRedirection();
app.Use(async (context, next) =>
{
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.CacheControl = "no-store";
    await next();
});
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();
app.Run();

public partial class Program;
