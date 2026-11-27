using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Memory.DashboardTests;

public sealed partial class DashboardBrowserUiTests
{
    [Fact]
    public async Task Secrets_management_tabs_should_page_only_safe_metadata_and_clear_on_selection_change()
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(new DashboardViewport("secret-management", 1366, 768));
        var page = await context.NewPageAsync();
        await LoginAndOpenAsync(page, "/secrets?secretManagementFixture=true");

        var inventory = page.Locator(".domain-list-panel");
        await inventory.GetByRole(AriaRole.Row).Filter(new() { HasText = "deployment-credential" }).ClickAsync();
        var selection = page.Locator(".domain-detail-panel .panel-title");
        await Expect(selection).ToHaveTextAsync("deployment-credential");
        var management = page.GetByRole(AriaRole.Region, new() { Name = "Credential 管理資訊", Exact = true });
        await Expect(management).ToBeVisibleAsync();

        var expiry = management.Locator(".definition-grid");
        var leases = management.Locator("[aria-label='安全租約狀態']");
        var history = management.Locator("[aria-label='遮罩後使用歷史']");
        await Expect(expiry).ToHaveCountAsync(0);
        await Expect(leases).ToHaveCountAsync(0);
        await Expect(history).ToHaveCountAsync(0);

        var expiryTab = management.GetByRole(AriaRole.Button, new() { Name = "版本期限", Exact = true });
        await expiryTab.FocusAsync();
        await expiryTab.PressAsync("Enter");
        var expiryFields = expiry.Locator("dd");
        await Expect(expiryFields.Nth(0)).ToHaveTextAsync("12");
        await Expect(expiryFields.Nth(1)).ToHaveTextAsync("4");
        await Expect(expiryFields.Nth(2)).ToHaveTextAsync("Active");
        await Expect(expiryFields.Nth(3).Locator("time")).ToBeVisibleAsync();

        await management.GetByRole(AriaRole.Button, new() { Name = "租約狀態", Exact = true }).ClickAsync();
        var leaseRows = leases.Locator("tbody tr");
        var previous = management.GetByRole(AriaRole.Button, new() { Name = "上一頁", Exact = true });
        var next = management.GetByRole(AriaRole.Button, new() { Name = "下一頁", Exact = true });
        await Expect(leases.GetByRole(AriaRole.Columnheader))
            .ToHaveTextAsync(new[] { "Kind", "State", "Expires", "Expired", "Used", "Remaining", "Active" });
        await Expect(leaseRows).ToHaveCountAsync(25);
        await Expect(leaseRows.First.GetByRole(AriaRole.Cell).Nth(1)).ToHaveTextAsync("Active");
        await Expect(leaseRows.First.GetByRole(AriaRole.Cell).Nth(4)).ToHaveTextAsync("1");
        await Expect(leaseRows.First.GetByRole(AriaRole.Cell).Nth(5)).ToHaveTextAsync("2");
        await Expect(previous).ToBeDisabledAsync();
        await next.ClickAsync();
        await Expect(leaseRows).ToHaveCountAsync(1);
        await Expect(leaseRows.First.GetByRole(AriaRole.Cell).Nth(1)).ToHaveTextAsync("Revoked");
        await Expect(next).ToBeDisabledAsync();
        await previous.ClickAsync();
        await Expect(leaseRows).ToHaveCountAsync(25);

        await management.GetByRole(AriaRole.Button, new() { Name = "使用歷史", Exact = true }).ClickAsync();
        await Expect(leases).ToHaveCountAsync(0);
        var historyRows = history.Locator("tbody tr");
        await Expect(history.GetByRole(AriaRole.Columnheader)).ToHaveTextAsync(new[] { "Operation", "Time" });
        await Expect(historyRows).ToHaveCountAsync(25);
        await Expect(historyRows.First.GetByRole(AriaRole.Cell).First).ToHaveTextAsync("Use");
        await next.ClickAsync();
        await Expect(historyRows).ToHaveCountAsync(1);
        await Expect(historyRows.First.GetByRole(AriaRole.Cell).First).ToHaveTextAsync("AddVersion");
        await Expect(next).ToBeDisabledAsync();

        await inventory.GetByRole(AriaRole.Row).Filter(new() { HasText = "ssh-signing-authority" }).ClickAsync();
        await Expect(selection).ToHaveTextAsync("ssh-signing-authority");
        await Expect(management).ToBeVisibleAsync();
        await Expect(expiry).ToHaveCountAsync(0);
        await Expect(leases).ToHaveCountAsync(0);
        await Expect(history).ToHaveCountAsync(0);
        await Expect(next).ToHaveCountAsync(0);
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "開始 Reveal", Exact = true })).ToBeDisabledAsync();
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "撤銷 credential", Exact = true })).ToBeDisabledAsync();
    }
}
