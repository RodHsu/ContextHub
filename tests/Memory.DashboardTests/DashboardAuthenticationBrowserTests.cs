using FluentAssertions;
using Microsoft.Playwright;

namespace Memory.DashboardTests;

public sealed class DashboardAuthenticationBrowserTests(DashboardBrowserFixture fixture)
    : IClassFixture<DashboardBrowserFixture>
{
    [Theory]
    [InlineData("chromium")]
    [InlineData("firefox")]
    public async Task Login_Post_Should_Enter_Interactive_Graph_Without_Aborting_A_Login_Circuit(string engine)
    {
        await fixture.EnsureDashboardRunningAsync();
        using var playwright = await Playwright.CreateAsync();
        var browserType = engine == "firefox" ? playwright.Firefox : playwright.Chromium;
        await using var browser = await browserType.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var pageErrors = 0;
        var negotiations = 0;
        var sockets = 0;
        var loginStatus = 0;
        page.PageError += (_, _) => Interlocked.Increment(ref pageErrors);
        page.WebSocket += (_, _) => Interlocked.Increment(ref sockets);
        page.Response += (_, response) =>
        {
            var path = new Uri(response.Url).AbsolutePath;
            if (path == "/_blazor/negotiate") Interlocked.Increment(ref negotiations);
            if (path == "/account/login" && response.Request.Method == "POST") loginStatus = response.Status;
        };

        await page.GotoAsync(new Uri(fixture.BaseUri, "/login?returnUrl=%2Fgraph").ToString());
        await page.WaitForTimeoutAsync(500);
        negotiations.Should().Be(0, "the cookie login page must not create an interactive circuit");
        sockets.Should().Be(0);
        (await page.Locator("input[name='__RequestVerificationToken']").GetAttributeAsync("value"))
            .Should().NotBeNullOrWhiteSpace();

        await page.Locator("input[name='Username']").FillAsync("admin");
        await page.Locator("input[name='Password']").FillAsync("ContextHub!123");
        await Task.WhenAll(
            page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/graph", new PageWaitForURLOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded
            }),
            page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "登入", Exact = true }).ClickAsync());
        await page.Locator(".dashboard-shell[data-dashboard-interactive='true']")
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

        loginStatus.Should().Be(302);
        negotiations.Should().BeGreaterThan(0, "the protected Graph must retain interactive server rendering");
        sockets.Should().BeGreaterThan(0);
        var timestamp = page.Locator(".page-actions-status time.client-local-time").First;
        var before = await timestamp.GetAttributeAsync("datetime");
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "更新圖譜", Exact = true }).ClickAsync();
        await page.WaitForFunctionAsync(
            "before => document.querySelector('.page-actions-status time.client-local-time')?.getAttribute('datetime') !== before",
            before);
        pageErrors.Should().Be(0, "login and the subsequent interactive refresh must not emit unhandled browser errors");
    }
}
