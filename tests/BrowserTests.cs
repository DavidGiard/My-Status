using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace MyStatus.Tests;

public sealed class BrowserFactAttribute : FactAttribute
{
    public BrowserFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MY_STATUS_BROWSER_TEST") != "1")
            Skip = "Opt-in real Chromium test: install browsers and set MY_STATUS_BROWSER_TEST=1.";
    }
}

public sealed class BrowserTests
{
    [BrowserFact]
    public async Task Real_browser_can_manage_complete_report_copy_print_and_use_mobile_layout()
    {
        using var factory = new AppFactory(browser: true);
        await factory.SeedAsync();
        var url = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()
            .Replace("127.0.0.1", "localhost");
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, BaseURL = url });
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        await page.GotoAsync("/");
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Welcome back" })).ToBeVisibleAsync();
        await page.GetByLabel("Email", new() { Exact = true }).FillAsync(AppFactory.Email);
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(AppFactory.Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Activities", Exact = true })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Categories", Exact = true }).ClickAsync();
        await page.GetByLabel("Name", new() { Exact = true }).FillAsync("Projects");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save category", Exact = true }).ClickAsync();
        await page.GetByLabel("Name", new() { Exact = true }).FillAsync("Customer portal");
        await page.GetByLabel("Parent category").SelectOptionAsync(new SelectOptionValue { Label = "Projects" });
        await page.GetByRole(AriaRole.Button, new() { Name = "Save category", Exact = true }).ClickAsync();
        await Expect(page.GetByText("Projects > Customer portal", new() { Exact = true })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Tags", Exact = true }).ClickAsync();
        await page.GetByLabel("Name", new() { Exact = true }).FillAsync("Delivery");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save tag", Exact = true }).ClickAsync();
        await page.GetByLabel("Name", new() { Exact = true }).FillAsync("Customer");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save tag", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Activities", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "New activity" }).ClickAsync();
        await Expect(page.GetByLabel("Include in status reports")).ToBeCheckedAsync();
        await Expect(page.GetByLabel("Completion date", new() { Exact = true })).ToBeDisabledAsync();
        await page.GetByLabel("Category or subcategory").SelectOptionAsync(new SelectOptionValue { Label = "Projects > Customer portal" });
        await page.GetByLabel("Description", new() { Exact = true }).FillAsync("Released <b>customer portal</b> with accessible navigation.");
        await page.GetByLabel("Delivery", new() { Exact = true }).CheckAsync();
        await page.GetByLabel("Customer", new() { Exact = true }).CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Save activity", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Edit", Exact = true }).ClickAsync();
        await page.GetByLabel("Status", new() { Exact = true }).SelectOptionAsync("2");
        await page.GetByLabel("Completion date", new() { Exact = true }).FillAsync("2026-05-01");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save activity", Exact = true }).ClickAsync();
        await Expect(page.Locator(".activity .badge")).ToHaveTextAsync("Complete");
        Directory.CreateDirectory(".local/browser");
        await page.ScreenshotAsync(new() { Path = ".local/browser/activities.png", FullPage = true });

        await page.GetByRole(AriaRole.Link, new() { Name = "Status report", Exact = true }).ClickAsync();
        await page.GetByLabel("Completion date from (inclusive)").FillAsync("2026-05-01");
        await page.GetByLabel("Completion date to (inclusive)").FillAsync("2026-05-01");
        await page.GetByRole(AriaRole.Button, new() { Name = "Generate report" }).ClickAsync();
        await Expect(page.Locator(".report-group")).ToHaveCountAsync(1);
        await Expect(page.Locator(".report li")).ToHaveCountAsync(1);
        await Expect(page.Locator(".report")).ToContainTextAsync("Released <b>customer portal</b>");
        Assert.Equal(0, await page.Locator(".report b").CountAsync());
        var text = await page.Locator("#report-text").InputValueAsync();
        Assert.Contains("Projects / Customer portal", text);
        Assert.Contains("2026-05-01: Released", text);
        await page.EvaluateAsync("Object.defineProperty(navigator, 'clipboard', { configurable: true, value: undefined });");
        await page.GetByRole(AriaRole.Button, new() { Name = "Copy for email" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Status).Last).ToContainTextAsync("copy it manually");
        await Expect(page.Locator("#report-text")).ToBeVisibleAsync();
        Assert.Equal(text, await page.Locator("#report-text").InputValueAsync());
        await page.ScreenshotAsync(new() { Path = ".local/browser/report.png", FullPage = true });
        await page.EmulateMediaAsync(new() { Media = Media.Print });
        await Expect(page.Locator("header")).ToBeHiddenAsync();
        await Expect(page.Locator("[data-copy]")).ToBeHiddenAsync();
        await Expect(page.Locator(".report")).ToBeVisibleAsync();
        await page.PdfAsync(new() { Path = ".local/browser/report.pdf", Format = "A4" });
        await page.EmulateMediaAsync(new() { Media = Media.Screen });
        await page.SetViewportSizeAsync(375, 812);
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"));
        await page.GetByRole(AriaRole.Link, new() { Name = "Activities", Exact = true }).ClickAsync();
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"));
        await page.ScreenshotAsync(new() { Path = ".local/browser/mobile.png", FullPage = true });
        await page.GotoAsync("/");
        await page.Keyboard.PressAsync("Tab");
        await Expect(page.Locator(".skip")).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Enter");
        await Expect(page.Locator("#main")).ToBeFocusedAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Categories", Exact = true }).ClickAsync();
        await page.Locator(".manage-row").Filter(new() { HasText = "Projects > Customer portal" })
            .GetByRole(AriaRole.Link, new() { Name = "Delete", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Confirm deletion", Exact = true }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("Reassign activities");
        await page.GetByRole(AriaRole.Link, new() { Name = "Tags", Exact = true }).ClickAsync();
        await page.Locator(".manage-row").Filter(new() { HasText = "Delivery" })
            .GetByRole(AriaRole.Link, new() { Name = "Delete", Exact = true }).ClickAsync();
        await Expect(page.GetByText("This removes the tag from all activities, but does not delete any activities.")).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Confirm deletion", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Activities", Exact = true }).ClickAsync();
        await Expect(page.Locator(".activity")).ToHaveCountAsync(1);
        await Expect(page.Locator(".activity .tag")).ToHaveCountAsync(1);
        await page.Locator(".activity h2 a").ClickAsync();
        await page.Locator(".danger-zone summary").ClickAsync();
        await Expect(page.GetByText("This permanently deletes this activity and its tag assignments. This cannot be undone.")).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Confirm permanent deletion" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "No activities to show" })).ToBeVisibleAsync();
        Assert.Empty(errors);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign out", Exact = true }).ClickAsync();
        await page.GotoAsync("/Report");
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Welcome back" })).ToBeVisibleAsync();
    }
}
