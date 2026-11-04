using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using MyStatus.Data;
using MyStatus.Domain;
using MyStatus.Services;
using Xunit;

namespace MyStatus.Tests;

public sealed class CaptureEmail : IResetEmail
{
    public string? Link { get; set; }
    public bool Fail { get; set; }
    public Task SendAsync(string address, string resetUrl)
    {
        if (Fail) throw new IOException("Synthetic delivery failure");
        Link = resetUrl;
        return Task.CompletedTask;
    }
}

public sealed class AppFactory : WebApplicationFactory<Program>
{
    public const string Email = "test-user@example.invalid";
    public const string Password = "Only-for-tests-4!Secure";
    private readonly string database;
    private readonly bool keepDatabase;
    public CaptureEmail Mail { get; } = new();
    private readonly bool browser;
    public AppFactory(bool browser = false, string? databasePath = null, bool keepDatabase = false)
    {
        this.browser = browser;
        this.keepDatabase = keepDatabase;
        database = databasePath ?? Path.GetFullPath(Path.Combine(".local", $"test-{Guid.NewGuid():N}.db"));
        if (browser) UseKestrel();
    }
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        if (browser)
            builder.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps()));
        builder.ConfigureServices(services =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(database)!);
            if (browser)
            {
                using var setup = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite($"Data Source={database}").Options);
                setup.Database.Migrate();
            }
            services.RemoveAll<DbContextOptions<LocalDbContext>>();
            services.AddDbContext<LocalDbContext>(o => o.UseSqlite($"Data Source={database}"));
            services.RemoveAll<IResetEmail>();
            services.AddSingleton<IResetEmail>(Mail);
        });
    }
    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = builder.Build();
        using (var scope = host.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
        host.Start();
        return host;
    }
    public HttpClient Client() => CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost")
    });
    public async Task SeedAsync()
    {
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<AccountProvisioner>().ProvisionAsync(Email, Password, "UTC");
    }
    public async Task<HttpClient> SignedInAsync()
    {
        await SeedAsync();
        var client = Client();
        var response = await PostAsync(client, "/Account/Login", new() { ["Email"] = Email, ["Password"] = Password });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }
    public static string Field(string html, string name)
    {
        var input = Regex.Match(html, $"<input[^>]*name=\"{Regex.Escape(name)}\"[^>]*>");
        Assert.True(input.Success, $"Missing input {name}");
        return WebUtility.HtmlDecode(Regex.Match(input.Value, "value=\"([^\"]*)\"").Groups[1].Value);
    }
    public static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, Dictionary<string, string> fields)
    {
        var getPath = path.Contains('?') ? path[..path.IndexOf('?')] + path[path.IndexOf('?')..].Replace("handler=Delete", "") : path;
        var html = await client.GetStringAsync(getPath);
        fields["__RequestVerificationToken"] = Field(html, "__RequestVerificationToken");
        return await client.PostAsync(path, new FormUrlEncodedContent(fields));
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && !keepDatabase)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(database + suffix)) File.Delete(database + suffix);
        }
    }
}

public sealed class WebTests
{
    [Fact]
    public async Task Pagination_preserves_any_tag_filters_and_concurrent_http_edits_keep_the_draft()
    {
        using var factory = new AppFactory(); using var client = await factory.SignedInAsync();
        int categoryId, tag1Id, tag2Id, activityId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var owner = (await db.Users.SingleAsync()).Id;
            var taxonomy = scope.ServiceProvider.GetRequiredService<TaxonomyService>();
            await taxonomy.SaveCategoryAsync(owner, null, "Work", null, default);
            categoryId = (await db.Categories.SingleAsync()).Id;
            await taxonomy.SaveTagAsync(owner, null, "One", default);
            await taxonomy.SaveTagAsync(owner, null, "Two", default);
            var tags = await db.Tags.OrderBy(x => x.Id).ToListAsync();
            tag1Id = tags[0].Id; tag2Id = tags[1].Id;
            for (var i = 0; i < 27; i++)
                await scope.ServiceProvider.GetRequiredService<ActivityService>().SaveAsync(owner, null, new ActivityInput
                { Date = new(2026, 1, 1), Description = $"Work {i:D2}", CategoryId = categoryId, TagIds = [tag1Id, tag2Id] });
            activityId = await db.Activities.OrderBy(x => x.Id).Select(x => x.Id).FirstAsync();
        }
        var first = await client.GetStringAsync($"/?search=Work&tagids={tag1Id}&tagids={tag2Id}&CategoryId={categoryId}");
        Assert.Contains("27 matching activities", first);
        Assert.Equal(25, Regex.Matches(first, "<article class=\"card activity\">").Count);
        var next = WebUtility.HtmlDecode(Regex.Match(first, "<a href=\"([^\"]*)\">Next</a>").Groups[1].Value);
        Assert.NotEmpty(next);
        var second = await client.GetStringAsync(next);
        Assert.Contains("27 matching activities", second);
        Assert.Equal(2, Regex.Matches(second, "<article class=\"card activity\">").Count);
        Assert.Contains("Page 2 of 2", second);

        var path = $"/Activities/Edit/{activityId}";
        var edit = await client.GetStringAsync(path);
        var version = AppFactory.Field(edit, "Input.Version");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.Activities.SingleAsync(x => x.Id == activityId);
            row.Description = "Another tab saved"; row.Version = Guid.NewGuid(); await db.SaveChangesAsync();
        }
        var response = await AppFactory.PostAsync(client, path, new()
        {
            ["Input.Date"] = "2026-01-01", ["Input.CategoryId"] = categoryId.ToString(),
            ["Input.Description"] = "My unsaved draft", ["Input.Status"] = "0", ["Input.Version"] = version
        });
        var conflict = await response.Content.ReadAsStringAsync();
        Assert.Contains("changed or was deleted", conflict);
        Assert.Contains("My unsaved draft", conflict);
        using var verify = factory.Services.CreateScope();
        Assert.Equal("Another tab saved", (await verify.ServiceProvider.GetRequiredService<AppDbContext>().Activities.SingleAsync(x => x.Id == activityId)).Description);
    }

    [Fact]
    public async Task Database_and_protected_authentication_keys_survive_host_restart()
    {
        var path = Path.GetFullPath(Path.Combine(".local", $"test-restart-{Guid.NewGuid():N}.db"));
        string cookie;
        using (var first = new AppFactory(databasePath: path, keepDatabase: true))
        {
            await first.SeedAsync();
            using var client = first.Client();
            var response = await AppFactory.PostAsync(client, "/Account/Login", new() { ["Email"] = AppFactory.Email, ["Password"] = AppFactory.Password });
            cookie = response.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("__Host-MyStatus=")).Split(';')[0];
            using var scope = first.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var owner = (await db.Users.SingleAsync()).Id;
            await scope.ServiceProvider.GetRequiredService<TaxonomyService>().SaveCategoryAsync(owner, null, "Persistent", null, default);
            await scope.ServiceProvider.GetRequiredService<ActivityService>().SaveAsync(owner, null, new ActivityInput
            { Date = new(2026, 1, 1), Description = "Survives restart", CategoryId = (await db.Categories.SingleAsync()).Id });
            var keys = await db.DataProtectionKeys.ToListAsync();
            Assert.NotEmpty(keys);
            if (OperatingSystem.IsWindows()) Assert.Contains("encryptedSecret", keys[0].Xml);
        }
        using var second = new AppFactory(databasePath: path);
        using var reopened = second.Client();
        reopened.DefaultRequestHeaders.Add("Cookie", cookie);
        var page = await reopened.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Survives restart", await page.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/Manage/categories")]
    [InlineData("/Manage/tags")]
    [InlineData("/Report")]
    [InlineData("/Activities/Edit")]
    [InlineData("/Activities/Details/1")]
    [InlineData("/Account/Settings")]
    public async Task Private_pages_require_authentication(string path)
    {
        using var factory = new AppFactory(); using var client = factory.Client();
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("https://localhost/Account/Login", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Single_account_provisioning_registration_antiforgery_and_secure_cookie()
    {
        using var factory = new AppFactory();
        using var client = await factory.SignedInAsync();
        using var scope = factory.Services.CreateScope();
        var provisioner = scope.ServiceProvider.GetRequiredService<AccountProvisioner>();
        await Assert.ThrowsAsync<RuleException>(() => provisioner.ProvisionAsync("second@example.invalid", AppFactory.Password, "UTC"));
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.CountAsync());
        var response = await client.GetAsync("/Account/Register");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/Manage/tags", new FormUrlEncodedContent(new Dictionary<string, string> { ["Name"] = "Unsafe" }))).StatusCode);
        var login = await AppFactory.PostAsync(client, "/Account/Login", new() { ["Email"] = AppFactory.Email, ["Password"] = AppFactory.Password, ["ReturnUrl"] = "https://example.invalid" });
        Assert.Equal("/", login.Headers.Location!.ToString());
        var cookie = login.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("__Host-MyStatus="));
        Assert.Contains("secure", cookie.ToLowerInvariant());
        Assert.Contains("httponly", cookie.ToLowerInvariant());
        Assert.Contains("samesite=lax", cookie.ToLowerInvariant());
        Assert.Equal("no-referrer", (await client.GetAsync("/")).Headers.GetValues("Referrer-Policy").Single());
    }

    [Fact]
    public async Task Password_reset_is_canonical_single_use_and_rejects_expired_tokens()
    {
        using var factory = new AppFactory(); await factory.SeedAsync(); using var client = factory.Client();
        var response = await AppFactory.PostAsync(client, "/Account/ForgotPassword", new() { ["Email"] = AppFactory.Email });
        var known = await response.Content.ReadAsStringAsync();
        Assert.Contains("If this address belongs to the account", known);
        Assert.NotNull(factory.Mail.Link);
        Assert.StartsWith("https://localhost:7143/Account/ResetPassword", factory.Mail.Link);
        var query = QueryHelpers.ParseQuery(new Uri(factory.Mail.Link).Query);
        var fields = new Dictionary<string, string>
        {
            ["UserId"] = query["userId"].ToString(), ["Code"] = query["code"].ToString(),
            ["Password"] = "Changed-test-password-9!", ["ConfirmPassword"] = "Changed-test-password-9!"
        };
        response = await AppFactory.PostAsync(client, "/Account/ResetPassword", fields);
        Assert.Contains("Your password has been reset", await response.Content.ReadAsStringAsync());
        response = await AppFactory.PostAsync(client, "/Account/ResetPassword", fields);
        Assert.Contains("The reset failed", await response.Content.ReadAsStringAsync());
        using var scope = factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await manager.FindByEmailAsync(AppFactory.Email))!;
        var expired = await manager.GeneratePasswordResetTokenAsync(user);
        var options = scope.ServiceProvider.GetRequiredService<IOptions<DataProtectionTokenProviderOptions>>().Value;
        options.TokenLifespan = TimeSpan.FromTicks(-1);
        Assert.False((await manager.ResetPasswordAsync(user, expired, AppFactory.Password)).Succeeded);
    }

    [Fact]
    public async Task Reset_requests_do_not_enumerate_or_disclose_delivery_failures()
    {
        using var factory = new AppFactory(); await factory.SeedAsync(); using var client = factory.Client();
        factory.Mail.Fail = true;
        var known = await AppFactory.PostAsync(client, "/Account/ForgotPassword", new() { ["Email"] = AppFactory.Email });
        var unknown = await AppFactory.PostAsync(client, "/Account/ForgotPassword", new() { ["Email"] = "absent@example.invalid" });
        Assert.Equal(HttpStatusCode.OK, known.StatusCode); Assert.Equal(known.StatusCode, unknown.StatusCode);
        const string message = "If this address belongs to the account";
        Assert.Contains(message, await known.Content.ReadAsStringAsync());
        Assert.Contains(message, await unknown.Content.ReadAsStringAsync());
        Assert.DoesNotContain("Synthetic", await known.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_rate_limit_and_identity_lockout_are_active()
    {
        using var factory = new AppFactory(); await factory.SeedAsync(); using var client = factory.Client();
        var html = await client.GetStringAsync("/Account/Login");
        var token = AppFactory.Field(html, "__RequestVerificationToken");
        for (var i = 0; i < 5; i++)
        {
            var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["Email"] = AppFactory.Email, ["Password"] = "Wrong-test-password", ["__RequestVerificationToken"] = token }));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        using var scope = factory.Services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(AppFactory.Email);
        Assert.True(user!.LockoutEnd > DateTimeOffset.UtcNow);
        await client.GetAsync("/Account/Login"); await client.GetAsync("/Account/Login");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/Account/Login")).StatusCode);
    }

    [Fact]
    public async Task Http_activity_flow_filters_report_markup_and_cross_owner_routes()
    {
        using var factory = new AppFactory(); using var client = await factory.SignedInAsync();
        int categoryId, childId, foreignActivityId, tag1Id, tag2Id;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var owner = (await db.Users.SingleAsync()).Id;
            var service = scope.ServiceProvider.GetRequiredService<TaxonomyService>();
            await service.SaveCategoryAsync(owner, null, "Projects", null, default);
            categoryId = (await db.Categories.SingleAsync()).Id;
            await service.SaveCategoryAsync(owner, null, "Portal", categoryId, default);
            childId = (await db.Categories.SingleAsync(x => x.ParentId != null)).Id;
            await service.SaveTagAsync(owner, null, "Alpha", default);
            await service.SaveTagAsync(owner, null, "Beta", default);
            var tags = await db.Tags.OrderBy(x => x.Id).ToListAsync();
            tag1Id = tags[0].Id; tag2Id = tags[1].Id;
            db.Users.Add(new ApplicationUser { Id = "foreign", UserName = "foreign" });
            await db.SaveChangesAsync();
            var foreignCategory = new Category { OwnerId = "foreign", Name = "Private", NormalizedName = "PRIVATE" };
            db.Categories.Add(foreignCategory); await db.SaveChangesAsync();
            var foreign = new MyStatus.Domain.Activity { OwnerId = "foreign", CategoryId = foreignCategory.Id, Description = "Foreign secret", Date = new(2026, 1, 1) };
            db.Activities.Add(foreign); await db.SaveChangesAsync(); foreignActivityId = foreign.Id;
        }
        var fields = new Dictionary<string, string>
        {
            ["Input.Date"] = "2026-01-01", ["Input.Description"] = "<script>alert('x')</script> shipped",
            ["Input.CategoryId"] = childId.ToString(), ["Input.Status"] = "2",
            ["Input.CompletionDate"] = "2026-05-01", ["Input.IncludeInReport"] = "true", ["Input.TagIds"] = tag1Id.ToString()
        };
        var response = await AppFactory.PostAsync(client, "/Activities/Edit", fields);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var list = await client.GetStringAsync($"/?CategoryId={categoryId}&TagIds={tag1Id}&TagIds={tag2Id}&CompletionDates=true&From=2026-05-01&To=2026-05-01");
        Assert.Contains("1 matching activities", list);
        Assert.Contains("&lt;script&gt;", list); Assert.DoesNotContain("<script>alert", list);
        Assert.DoesNotContain("Foreign secret", list);
        var report = await client.GetStringAsync("/Report?From=2026-05-01&To=2026-05-01");
        Assert.Contains("Projects / Portal", report); Assert.Contains("&lt;script&gt;", report);
        Assert.DoesNotContain("Foreign secret", report);
        var invalid = await client.GetStringAsync("/Report?From=2026-05-02&To=2026-05-01");
        Assert.Contains("start date must be on or before", invalid); Assert.DoesNotContain("data-copy>", invalid);
        Assert.Contains("field is required", await client.GetStringAsync("/Report?From=&To="));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/Activities/Edit/{foreignActivityId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/Activities/Details/{foreignActivityId}")).StatusCode);
        fields["__RequestVerificationToken"] = AppFactory.Field(await client.GetStringAsync("/Activities/Edit"), "__RequestVerificationToken");
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/Activities/Edit/{foreignActivityId}", new FormUrlEncodedContent(fields))).StatusCode);
    }
}
