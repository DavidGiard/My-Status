using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
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

public sealed class AppFactory : WebApplicationFactory<Program>
{
    private readonly string database;
    private readonly bool keepDatabase;
    public const string Username = "test-owner";
    public const string Password = "Synthetic-test-password-9!";
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
            using (var setup = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite($"Data Source={database}").Options))
                setup.Database.Migrate();
            services.RemoveAll<DbContextOptions<LocalDbContext>>();
            services.AddDbContext<LocalDbContext>(o => o.UseSqlite($"Data Source={database}"));
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
        await scope.ServiceProvider.GetRequiredService<AccountProvisioner>().ProvisionAsync(Username, Password);
    }
    public async Task<HttpClient> SignedInAsync()
    {
        await SeedAsync();
        var client = Client();
        var response = await CompleteSignInAsync(client);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location!.ToString());
        return client;
    }
    public async Task<HttpResponseMessage> CompleteSignInAsync(HttpClient client, string? returnUrl = null)
    {
        var fields = new Dictionary<string, string> { ["Username"] = Username, ["Password"] = Password };
        if (returnUrl is not null) fields["ReturnUrl"] = returnUrl;
        return await PostAsync(client, "/Account/Login", fields);
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
            var response = await first.CompleteSignInAsync(client);
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
    [InlineData("/Account/ChangePassword")]
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
        await Assert.ThrowsAsync<RuleException>(() => provisioner.ProvisionAsync(AppFactory.Username, AppFactory.Password));
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.CountAsync());
        var response = await client.GetAsync("/Account/Register");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/Manage/tags", new FormUrlEncodedContent(new Dictionary<string, string> { ["Name"] = "Unsafe" }))).StatusCode);
        var login = await factory.CompleteSignInAsync(client, "https://example.invalid");
        Assert.Equal("/", login.Headers.Location!.ToString());
        var cookie = login.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("__Host-MyStatus="));
        Assert.Contains("secure", cookie.ToLowerInvariant());
        Assert.Contains("httponly", cookie.ToLowerInvariant());
        Assert.Contains("samesite=lax", cookie.ToLowerInvariant());
        Assert.Equal("no-referrer", (await client.GetAsync("/")).Headers.GetValues("Referrer-Policy").Single());
    }

    [Fact]
    public async Task Local_password_is_hashed_and_public_registration_and_external_sign_in_are_removed()
    {
        using var factory = new AppFactory(); using var client = await factory.SignedInAsync();
        foreach (var route in new[] { "/Account/ForgotPassword", "/Account/ResetPassword", "/Account/Register" })
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(route)).StatusCode);
        var page = await client.GetStringAsync("/Account/Login");
        Assert.Contains("type=\"password\"", page);
        Assert.DoesNotContain("Sign in with Microsoft", page);
        using var scope = factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await manager.Users.SingleAsync();
        Assert.True(await manager.HasPasswordAsync(user));
        Assert.NotEqual(AppFactory.Password, user.PasswordHash);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/signin-oidc")).StatusCode);
    }

    [Fact]
    public async Task Incorrect_or_unknown_credentials_have_the_same_error_and_cannot_create_accounts()
    {
        using var factory = new AppFactory(); await factory.SeedAsync(); using var client = factory.Client();
        foreach (var username in new[] { AppFactory.Username, "unknown" })
        {
            var response = await AppFactory.PostAsync(client, "/Account/Login", new()
                { ["Username"] = username, ["Password"] = "Incorrect-password-9!" });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("Sign-in failed. Check your username and password or try again later.", await response.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/")).StatusCode);
        }
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Single(await db.Users.ToListAsync());
        Assert.Empty(await db.UserLogins.ToListAsync());
    }

    [Fact]
    public async Task Login_requires_antiforgery()
    {
        using var factory = new AppFactory(); await factory.SeedAsync(); using var client = factory.Client();
        var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["Username"] = AppFactory.Username, ["Password"] = AppFactory.Password }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Explicit_credential_replacement_preserves_data_and_removes_external_binding()
    {
        using var factory = new AppFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = "legacy@example.invalid", Email = "legacy@example.invalid", TimeZoneId = "America/Chicago" };
        Assert.True((await manager.CreateAsync(user)).Succeeded);
        Assert.True((await manager.AddLoginAsync(user, new UserLoginInfo("MicrosoftEntra", "synthetic-old-binding", "Microsoft"))).Succeeded);
        var stamp = user.SecurityStamp;
        var taxonomy = scope.ServiceProvider.GetRequiredService<TaxonomyService>();
        await taxonomy.SaveCategoryAsync(user.Id, null, "Existing work", null, default);
        await taxonomy.SaveTagAsync(user.Id, null, "Keep tag", default);
        await scope.ServiceProvider.GetRequiredService<ActivityService>().SaveAsync(user.Id, null, new ActivityInput
        {
            Date = new(2026, 1, 1), Description = "Keep my data", CategoryId = (await db.Categories.SingleAsync()).Id,
            TagIds = [(await db.Tags.SingleAsync()).Id]
        });
        var provisioner = scope.ServiceProvider.GetRequiredService<AccountProvisioner>();
        await Assert.ThrowsAsync<RuleException>(() => provisioner.ProvisionAsync(AppFactory.Username, AppFactory.Password));
        await provisioner.ProvisionAsync(AppFactory.Username, AppFactory.Password, replaceExisting: true);
        Assert.Equal(user.Id, (await db.Users.SingleAsync()).Id);
        Assert.Equal("America/Chicago", user.TimeZoneId);
        Assert.NotEqual(stamp, user.SecurityStamp);
        Assert.True(await manager.CheckPasswordAsync(user, AppFactory.Password));
        Assert.Empty(await manager.GetLoginsAsync(user));
        Assert.Equal("Keep my data", (await db.Activities.SingleAsync()).Description);
        Assert.Single(await db.ActivityTags.ToListAsync());
        using var client = factory.Client();
        var response = await factory.CompleteSignInAsync(client);
        Assert.Equal("/", response.Headers.Location!.ToString());
        Assert.Contains("Keep my data", await client.GetStringAsync("/"));
    }

    [Fact]
    public async Task Credential_replacement_revokes_sessions_and_rejects_old_password()
    {
        using var factory = new AppFactory(); using var client = await factory.SignedInAsync();
        factory.Services.GetRequiredService<IOptions<SecurityStampValidatorOptions>>().Value.ValidationInterval = TimeSpan.Zero;
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AccountProvisioner>()
            .ProvisionAsync(AppFactory.Username, "Replacement-password-8!", replaceExisting: true);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/")).StatusCode);
        using var fresh = factory.Client();
        Assert.Equal(HttpStatusCode.OK, (await factory.CompleteSignInAsync(fresh)).StatusCode);
        var response = await AppFactory.PostAsync(fresh, "/Account/Login", new()
            { ["Username"] = AppFactory.Username, ["Password"] = "Replacement-password-8!" });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task Weak_replacement_rolls_back_without_losing_existing_credentials()
    {
        using var factory = new AppFactory(); await factory.SeedAsync();
        using (var scope = factory.Services.CreateScope())
        {
            await Assert.ThrowsAsync<RuleException>(() => scope.ServiceProvider.GetRequiredService<AccountProvisioner>()
                .ProvisionAsync("different-name", "weak", replaceExisting: true));
        }
        using var client = factory.Client();
        Assert.Equal(HttpStatusCode.Redirect, (await factory.CompleteSignInAsync(client)).StatusCode);
    }

    [Fact]
    public async Task Login_rate_limit_and_account_lockout_are_active()
    {
        using var factory = new AppFactory(); await factory.SeedAsync(); using var client = factory.Client();
        var html = await client.GetStringAsync("/Account/Login");
        var token = AppFactory.Field(html, "__RequestVerificationToken");
        for (var i = 0; i < 5; i++)
        {
            var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["Username"] = AppFactory.Username, ["Password"] = "Incorrect-password-9!", ["__RequestVerificationToken"] = token }));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        using var scope = factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.True(await manager.IsLockedOutAsync((await manager.FindByNameAsync(AppFactory.Username))!));
        await client.GetAsync("/Account/Login"); await client.GetAsync("/Account/Login");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/Account/Login")).StatusCode);
    }

    [Fact]
    public async Task Password_change_requires_current_password_and_revokes_other_sessions()
    {
        using var factory = new AppFactory(); using var client = await factory.SignedInAsync();
        using var other = factory.Client();
        await factory.CompleteSignInAsync(other);
        factory.Services.GetRequiredService<IOptions<SecurityStampValidatorOptions>>().Value.ValidationInterval = TimeSpan.Zero;
        var fields = new Dictionary<string, string>
        {
            ["CurrentPassword"] = "Incorrect-password-9!", ["NewPassword"] = "Changed-password-4!",
            ["ConfirmPassword"] = "Changed-password-4!"
        };
        var response = await AppFactory.PostAsync(client, "/Account/ChangePassword", fields);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Incorrect password", await response.Content.ReadAsStringAsync());
        fields["CurrentPassword"] = AppFactory.Password;
        response = await AppFactory.PostAsync(client, "/Account/ChangePassword", fields);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await other.GetAsync("/")).StatusCode);
        using var fresh = factory.Client();
        Assert.Equal(HttpStatusCode.OK, (await factory.CompleteSignInAsync(fresh)).StatusCode);
        response = await AppFactory.PostAsync(fresh, "/Account/Login", new()
            { ["Username"] = AppFactory.Username, ["Password"] = fields["NewPassword"] });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
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
