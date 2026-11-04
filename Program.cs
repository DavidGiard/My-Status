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
var microsoft = builder.Configuration.GetSection("Microsoft").Get<MicrosoftAccountOptions>() ?? new();
builder.Services.AddSingleton(microsoft);
if (!development && !args.Contains("--migrate") && !microsoft.IsConfigured)
    throw new InvalidOperationException("Configure Microsoft:TenantId, ClientId, ClientSecret and OwnerObjectId before starting the web app.");
builder.Services.AddIdentity<ApplicationUser, IdentityRole>().AddEntityFrameworkStores<AppDbContext>();
builder.Services.AddAuthentication().AddOpenIdConnect(MicrosoftAccountOptions.Scheme, _ => { });
builder.Services.AddOptions<Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions>(MicrosoftAccountOptions.Scheme)
    .Configure<MicrosoftAccountOptions>(MicrosoftAuthentication.Configure);
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(5));
builder.Services.Configure<SecurityStampValidatorOptions>(o =>
{
    o.OnRefreshingPrincipal = context =>
    {
        // Identity rebuilds its principal periodically; retain the immutable Microsoft binding.
        if (context.NewPrincipal?.Identity is System.Security.Claims.ClaimsIdentity identity && context.CurrentPrincipal is { } current)
            foreach (var type in new[] { MicrosoftAuthentication.TenantClaim, MicrosoftAuthentication.ObjectClaim })
                foreach (var claim in current.FindAll(type))
                    identity.AddClaim(claim);
        return Task.CompletedTask;
    };
});
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
    o.Events.OnValidatePrincipal = MicrosoftAuthentication.ValidateCookieAsync;
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
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ActivityService>();
builder.Services.AddScoped<TaxonomyService>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddScoped<AccountProvisioner>();
var app = builder.Build();

if (args.Contains("--migrate") || args.Contains("--provision") || args.Contains("--link-microsoft"))
{
    using var scope = app.Services.CreateScope();
    if (args.Contains("--migrate"))
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    if (args.Contains("--provision") || args.Contains("--link-microsoft"))
    {
        if (Console.IsInputRedirected)
            throw new InvalidOperationException("Account provisioning/linking requires an interactive administrator terminal.");
        if (!microsoft.HasOwner)
            throw new InvalidOperationException("Configure Microsoft:TenantId and Microsoft:OwnerObjectId first.");
        var link = args.Contains("--link-microsoft");
        if (link)
        {
            Console.WriteLine("This links the sole existing local account and ALL its data to the configured Microsoft tenant/object ID, removes its password and invalidates existing sessions.");
            Console.WriteLine($"Target tenant: {microsoft.TenantId}; object: {microsoft.OwnerObjectId}");
            Console.Write("Type LINK to authorize this account/data binding: ");
            if (Console.ReadLine() != "LINK") throw new InvalidOperationException("Linking cancelled. No account data changed.");
        }
        string? zone = null;
        if (!link)
        {
            Console.Write("Time zone ID (blank for UTC): ");
            zone = Console.ReadLine();
        }
        await scope.ServiceProvider.GetRequiredService<AccountProvisioner>().ProvisionAsync(
            string.IsNullOrWhiteSpace(zone) ? "UTC" : zone, link);
        Console.WriteLine("Microsoft account binding saved. No local password or public registration is enabled.");
    }
    return;
}

app.UseExceptionHandler("/Error");
if (!development) app.UseHsts();
app.UseHttpsRedirection();
app.Use(async (context, next) =>
{
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; base-uri 'self'; form-action 'self' https://login.microsoftonline.com; frame-ancestors 'none'";
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
