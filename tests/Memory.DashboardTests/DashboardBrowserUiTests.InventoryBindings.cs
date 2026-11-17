using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Memory.DashboardTests;

public sealed partial class DashboardBrowserUiTests
{
    [Theory]
    [InlineData("/files", 1366, 768)]
    [InlineData("/files", 390, 844)]
    [InlineData("/secrets", 1366, 768)]
    [InlineData("/secrets", 390, 844)]
    public async Task Inventory_Initial_Scope_Should_Show_Loaded_Default_Project_Missing_From_Catalog(string route, int width, int height)
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(new DashboardViewport("inventory-default-scope", width, height));
        var page = await context.NewPageAsync();
        await LoginAndOpenAsync(page, route + "?uiProfile=normal");

        const string configuredProject = "default";
        var scope = page.Locator(".dashboard-scope-bar select");
        var project = page.Locator(".domain-detail-panel .definition-grid dd").First;
        await Expect(project).ToHaveTextAsync(configuredProject);
        await Expect(scope).ToHaveValueAsync(configuredProject);

        await page.GetByRole(AriaRole.Button, new() { Name = "刷新", Exact = true }).ClickAsync();
        await Expect(scope).ToHaveValueAsync(configuredProject);
        await Expect(project).ToHaveTextAsync(configuredProject);
    }

    [Theory]
    [InlineData("/files", 1366, 768)]
    [InlineData("/files", 390, 844)]
    [InlineData("/secrets", 1366, 768)]
    [InlineData("/secrets", 390, 844)]
    public async Task Inventory_Scope_Should_Match_Loaded_Project_After_Selection_And_Refresh(string route, int width, int height)
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(new DashboardViewport("inventory-scope", width, height));
        var page = await context.NewPageAsync();
        await LoginAndOpenAsync(page, route);

        var scope = page.Locator(".dashboard-scope-bar select");
        var project = page.Locator(".domain-detail-panel .definition-grid dd").First;
        await scope.SelectOptionAsync("dashboard-test-secondary");
        await Expect(project).ToHaveTextAsync("dashboard-test-secondary");
        await Expect(scope).ToHaveValueAsync("dashboard-test-secondary");

        await page.GetByRole(AriaRole.Button, new() { Name = "刷新", Exact = true }).ClickAsync();
        await Expect(project).ToHaveTextAsync("dashboard-test-secondary");
        await Expect(scope).ToHaveValueAsync("dashboard-test-secondary");
    }

    [Theory]
    [InlineData("/files", "release", "release-evidence.pdf", "security-review.zip", 1366, 768)]
    [InlineData("/files", "release", "release-evidence.pdf", "security-review.zip", 390, 844)]
    [InlineData("/secrets", "deployment", "deployment-credential", "ssh-signing-authority", 1366, 768)]
    [InlineData("/secrets", "deployment", "deployment-credential", "ssh-signing-authority", 390, 844)]
    public async Task Inventory_Search_Should_Retain_Filter_Value_After_Parent_Render_And_Refresh(
        string route, string query, string includedName, string excludedName, int width, int height)
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(new DashboardViewport("inventory-search", width, height));
        var page = await context.NewPageAsync();
        await LoginAndOpenAsync(page, route);

        var search = page.Locator(".dashboard-scope-search input");
        var rows = page.Locator(".domain-list-panel tbody tr");
        await Expect(rows).ToHaveCountAsync(2);
        await Expect(search).ToHaveValueAsync(string.Empty);
        await search.FillAsync(query);
        await Expect(rows).ToHaveCountAsync(1);
        await Expect(rows).ToContainTextAsync(includedName);
        await Expect(rows).Not.ToContainTextAsync(excludedName);
        await Expect(search).ToHaveValueAsync(query);

        await page.GetByRole(AriaRole.Button, new() { Name = "刷新", Exact = true }).ClickAsync();
        await Expect(rows).ToHaveCountAsync(1);
        await Expect(search).ToHaveValueAsync(query);
    }

    [Theory]
    [InlineData("/files", 1366, 768)]
    [InlineData("/files", 390, 844)]
    [InlineData("/secrets", 1366, 768)]
    [InlineData("/secrets", 390, 844)]
    public async Task Inventory_Healthy_Refresh_Should_Not_Show_A_Literal_Load_Error_Warning(string route, int width, int height)
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(new DashboardViewport("inventory-refresh", width, height));
        var page = await context.NewPageAsync();
        await LoginAndOpenAsync(page, route);

        await Expect(page.Locator(".domain-list-panel tbody tr")).ToHaveCountAsync(2);
        await page.GetByRole(AriaRole.Button, new() { Name = "刷新", Exact = true }).ClickAsync();
        await Expect(page.Locator(".refresh-status-group .info-popover-warning")).ToHaveCountAsync(0);
        await Expect(page.Locator(".refresh-status-group")).Not.ToContainTextAsync("_loadError");
    }
}
