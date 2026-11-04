using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authentication.Cookies;
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
    public TestMicrosoftProvider Provider { get; } = new();
    public MicrosoftAccountOptions Account { get; } = TestMicrosoftProvider.Account();
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
            services.RemoveAll<MicrosoftAccountOptions>();
            services.AddSingleton(Account);
            services.PostConfigure<OpenIdConnectOptions>(MicrosoftAccountOptions.Scheme, Provider.Configure);
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
        await scope.ServiceProvider.GetRequiredService<AccountProvisioner>().ProvisionAsync("UTC");
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
        var fields = new Dictionary<string, string>();
        if (returnUrl is not null) fields["ReturnUrl"] = returnUrl;
        var challenge = await PostAsync(client, "/Account/Login", fields);
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        return await client.GetAsync(Provider.Callback(challenge.Headers.Location!.ToString()));
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
        if (disposing) Provider.Dispose();
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
        await Assert.ThrowsAsync<RuleException>(() => provisioner.ProvisionAsync("UTC"));
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
    public async Task Local_password_and_recovery_routes_are_removed()
    {
        using var factory = new AppFactory(); using var client = await factory.SignedInAsync();
        foreach (var route in new[] { "/Account/ForgotPassword", "/Account/ResetPassword", "/Account/Register" })
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(route)).StatusCode);
        var page = await client.GetStringAsync("/Account/Login");
        Assert.Contains("Sign in with Microsoft", page);
        Assert.DoesNotContain("type=\"password\"", page);
        using var scope = factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.False(await manager.HasPasswordAsync((await manager.Users.SingleAsync())));
    }

    [Fact]
    public async Task Missing_configuration_disables_sign_in_and_fails_closed()
    {
        using var factory = new AppFactory();
        factory.Account.ClientSecret = "";
        using var client = factory.Client();
        var html = await client.GetStringAsync("/Account/Login");
        Assert.Contains("Microsoft sign-in needs setup", html);
        Assert.Contains("disabled=\"disabled\"", html);
        var response = await AppFactory.PostAsync(client, "/Account/Login", new());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Null(response.Headers.Location);
        response = await client.GetAsync("/signin-oidc?code=not-real&state=not-real");
        Assert.Equal("/Account/Login?signInFailed=true", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Valid_Microsoft_identity_cannot_automatically_create_or_claim_an_unlinked_account()
    {
        using var factory = new AppFactory(); using var client = factory.Client();
        var response = await factory.CompleteSignInAsync(client);
        Assert.Equal("/Account/Login?signInFailed=true", response.Headers.Location!.ToString());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await db.Users.ToListAsync());
        db.Users.Add(new ApplicationUser { UserName = "same-email@example.invalid", Email = "same-email@example.invalid" });
        await db.SaveChangesAsync();
        response = await factory.CompleteSignInAsync(client);
        Assert.Equal("/Account/Login?signInFailed=true", response.Headers.Location!.ToString());
        Assert.Empty(await db.UserLogins.ToListAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Microsoft_callback_requires_original_state_and_correlation(bool tamperState)
    {
        using var factory = new AppFactory(); await factory.SeedAsync(); using var client = factory.Client();
        var challenge = await AppFactory.PostAsync(client, "/Account/Login", new());
        var callback = factory.Provider.Callback(challenge.Headers.Location!.ToString());
        using var noCookies = factory.Client();
        var response = tamperState
            ? await client.GetAsync(callback[..callback.IndexOf("&state=", StringComparison.Ordinal)] + "&state=invalid")
            : await noCookies.GetAsync(callback);
        Assert.Equal("/Account/Login?signInFailed=true", response.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Explicit_link_preserves_existing_owner_data_and_removes_password()
    {
        using var factory = new AppFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = "legacy@example.invalid", Email = "legacy@example.invalid", TimeZoneId = "America/Chicago" };
        Assert.True((await manager.CreateAsync(user, "Synthetic-legacy-password-9!")).Succeeded);
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
        await Assert.ThrowsAsync<RuleException>(() => provisioner.ProvisionAsync("UTC"));
        Assert.True(await manager.HasPasswordAsync(user));
        Assert.Empty(await db.UserLogins.ToListAsync());
        await provisioner.ProvisionAsync("UTC", linkExisting: true);
        Assert.Equal(user.Id, (await db.Users.SingleAsync()).Id);
        Assert.Equal("America/Chicago", user.TimeZoneId);
        Assert.NotEqual(stamp, user.SecurityStamp);
        Assert.False(await manager.HasPasswordAsync(user));
        Assert.Equal(factory.Account.ProviderKey, (await manager.GetLoginsAsync(user)).Single().ProviderKey);
        Assert.Equal("Keep my data", (await db.Activities.SingleAsync()).Description);
        Assert.Single(await db.ActivityTags.ToListAsync());
        await Assert.ThrowsAsync<RuleException>(() => provisioner.ProvisionAsync("UTC", linkExisting: true));
        using var client = factory.Client();
        var response = await factory.CompleteSignInAsync(client);
        Assert.Equal("/", response.Headers.Location!.ToString());
        Assert.Contains("Keep my data", await client.GetStringAsync("/"));
    }

    [Fact]
    public async Task Refreshed_cookies_retain_binding_and_revoked_logins_and_legacy_cookies_are_rejected()
    {
        using var factory = new AppFactory(); using var client = await factory.SignedInAsync();
        factory.Services.GetRequiredService<IOptions<SecurityStampValidatorOptions>>().Value.ValidationInterval = TimeSpan.Zero;
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
        using var scope = factory.Services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().Users.SingleAsync();
        var legacyPrincipal = await scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>().CreateUserPrincipalAsync(user);
        var options = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
        var legacyTicket = options.TicketDataFormat.Protect(new AuthenticationTicket(legacyPrincipal,
            new AuthenticationProperties { IssuedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1) },
            IdentityConstants.ApplicationScheme));
        using var oldClient = factory.Client();
        oldClient.DefaultRequestHeaders.Add("Cookie", $"__Host-MyStatus={legacyTicket}");
        Assert.Equal(HttpStatusCode.Redirect, (await oldClient.GetAsync("/")).StatusCode);
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.True((await manager.RemoveLoginAsync(user, MicrosoftAccountOptions.Scheme, factory.Account.ProviderKey)).Succeeded);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/")).StatusCode);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("tenant")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("expiry")]
    [InlineData("nonce")]
    [InlineData("signature")]
    [InlineData("missing-object")]
    public async Task Microsoft_callback_rejects_untrusted_tokens_and_unauthorized_identities(string invalid)
    {
        using var factory = new AppFactory(); await factory.SeedAsync(); using var client = factory.Client();
        switch (invalid)
        {
            case "owner": factory.Provider.TokenObject = Guid.NewGuid().ToString(); break;
            case "tenant": factory.Provider.TokenTenant = Guid.NewGuid().ToString(); break;
            case "issuer": factory.Provider.TokenIssuer = "https://example.invalid"; break;
            case "audience": factory.Provider.TokenAudience = Guid.NewGuid().ToString(); break;
            case "expiry": factory.Provider.Expired = true; break;
            case "nonce": factory.Provider.InvalidNonce = true; break;
            case "signature": factory.Provider.InvalidSignature = true; break;
            case "missing-object": factory.Provider.IncludeObject = false; break;
        }
        var response = await factory.CompleteSignInAsync(client);
        Assert.Equal("/Account/Login?signInFailed=true", response.Headers.Location!.ToString());
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out var cookies)
            && cookies.Any(x => x.StartsWith("__Host-MyStatus=") && !x.StartsWith("__Host-MyStatus=;")));
        var denied = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.CountAsync());
    }

    [Fact]
    public async Task Login_rate_limit_is_active()
    {
        using var factory = new AppFactory(); await factory.SeedAsync(); using var client = factory.Client();
        var html = await client.GetStringAsync("/Account/Login");
        var token = AppFactory.Field(html, "__RequestVerificationToken");
        for (var i = 0; i < 5; i++)
        {
            var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["__RequestVerificationToken"] = token }));
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        }
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
