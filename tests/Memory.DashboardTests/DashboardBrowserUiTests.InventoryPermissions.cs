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
    public async Task Inventory_Forbidden_Scope_Should_Clear_Previous_Data_And_Allow_Authorized_Recovery(string route, int width, int height)
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(new("inventory-permission", width, height));
        var page = await context.NewPageAsync();
        await LoginAndOpenAsync(page, route + "?uiProfile=InventoryPermissions");
        var scope = page.GetByRole(AriaRole.Combobox, new() { Name = "Project scope", Exact = true });
        await scope.SelectOptionAsync("dashboard-test");
        await Expect(page.Locator(".domain-list-panel tbody tr")).ToHaveCountAsync(2);
        await Expect(page.Locator(".rights-list")).ToBeVisibleAsync();

        await scope.SelectOptionAsync("dashboard-test-restricted");
        await AssertInventoryPermissionDeniedAsync(page, "dashboard-test-restricted");
        await scope.SelectOptionAsync("dashboard-test-secondary");
        await AssertAuthorizedInventoryRecoveryAsync(page, "dashboard-test-secondary");
    }

    [Theory]
    [InlineData("/files", 1366, 768)]
    [InlineData("/files", 390, 844)]
    [InlineData("/secrets", 1366, 768)]
    [InlineData("/secrets", 390, 844)]
    public async Task Inventory_Forbidden_Refresh_Should_Clear_Cached_Data_And_Rights(string route, int width, int height)
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(new("inventory-revocation", width, height));
        var page = await context.NewPageAsync();
        await LoginAndOpenAsync(page, route + "?uiProfile=InventoryPermissions");
        var scope = page.GetByRole(AriaRole.Combobox, new() { Name = "Project scope", Exact = true });
        await scope.SelectOptionAsync("dashboard-test-revoked");
        await Expect(page.Locator(".domain-list-panel tbody tr")).ToHaveCountAsync(2);
        await Expect(page.Locator(".rights-list")).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "刷新", Exact = true }).ClickAsync();
        await AssertInventoryPermissionDeniedAsync(page, "dashboard-test-revoked");

        await scope.SelectOptionAsync("dashboard-test-secondary");
        await AssertAuthorizedInventoryRecoveryAsync(page, "dashboard-test-secondary");
    }

    [Theory]
    [InlineData("/files", 1366, 768)]
    [InlineData("/files", 390, 844)]
    [InlineData("/secrets", 1366, 768)]
    [InlineData("/secrets", 390, 844)]
    public async Task Inventory_Server_Error_Should_Preserve_Same_Scope_Data_But_Not_Show_Previous_Scope_Data(string route, int width, int height)
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(new("inventory-server-error", width, height));
        var page = await context.NewPageAsync();
        await LoginAndOpenAsync(page, route + "?uiProfile=InventoryPermissions");
        var scope = page.GetByRole(AriaRole.Combobox, new() { Name = "Project scope", Exact = true });
        await scope.SelectOptionAsync("dashboard-test-transient");
        await Expect(page.Locator(".domain-list-panel tbody tr")).ToHaveCountAsync(2);
        await page.GetByRole(AriaRole.Button, new() { Name = "刷新", Exact = true }).ClickAsync();
        await Expect(page.Locator(".dashboard-async-state-degraded")).ToBeVisibleAsync();
        await Expect(page.Locator(".domain-list-panel tbody tr")).ToHaveCountAsync(2);
        await Expect(page.Locator(".domain-detail-panel .definition-grid dd").First).ToHaveTextAsync("dashboard-test-transient");
        await Expect(page.Locator(".rights-list")).ToHaveCountAsync(0);
        await Expect(page.Locator(".dashboard-async-state-permission")).ToHaveCountAsync(0);

        await scope.SelectOptionAsync("dashboard-test-unavailable");
        await Expect(page.Locator(".dashboard-async-state-error")).ToBeVisibleAsync();
        await Expect(page.Locator(".domain-list-panel, .domain-detail-panel, .rights-list")).ToHaveCountAsync(0);
        await Expect(page.Locator(".dashboard-async-state-degraded, .dashboard-async-state-empty")).ToHaveCountAsync(0);
    }

    private static async Task AssertInventoryPermissionDeniedAsync(IPage page, string projectId)
    {
        var permission = page.Locator(".dashboard-async-state-permission");
        await Expect(permission).ToBeVisibleAsync();
        await Expect(permission).ToHaveAttributeAsync("role", "status");
        await Expect(permission).ToContainTextAsync("選擇已授權的專案");
        await Expect(permission).ToContainTextAsync("聯絡管理者");
        await Expect(permission.GetByRole(AriaRole.Button)).ToHaveCountAsync(0);
        await Expect(page.GetByRole(AriaRole.Combobox, new() { Name = "Project scope", Exact = true })).ToHaveValueAsync(projectId);
        await Expect(page.GetByRole(AriaRole.Combobox, new() { Name = "Project scope", Exact = true })).ToBeEnabledAsync();
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "刷新", Exact = true })).ToBeDisabledAsync();
        await Expect(page.Locator(".domain-list-panel, .domain-detail-panel, .rights-list, .high-risk-operation")).ToHaveCountAsync(0);
        await Expect(page.Locator(".dashboard-async-state-empty, .dashboard-async-state-degraded, .dashboard-async-state-error")).ToHaveCountAsync(0);
        await Expect(page.Locator("main")).Not.ToContainTextAsync("release-evidence.pdf");
        await Expect(page.Locator("main")).Not.ToContainTextAsync("deployment-credential");
    }

    private static async Task AssertAuthorizedInventoryRecoveryAsync(IPage page, string projectId)
    {
        await Expect(page.Locator(".dashboard-async-state-permission")).ToHaveCountAsync(0);
        await Expect(page.Locator(".domain-list-panel tbody tr")).ToHaveCountAsync(2);
        await Expect(page.Locator(".domain-detail-panel .definition-grid dd").First).ToHaveTextAsync(projectId);
        await Expect(page.Locator(".rights-list")).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "刷新", Exact = true })).ToBeEnabledAsync();
    }
}
