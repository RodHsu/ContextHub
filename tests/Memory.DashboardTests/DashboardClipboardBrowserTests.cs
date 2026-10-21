using System.Text.Json;
using FluentAssertions;
using Microsoft.Playwright;

namespace Memory.DashboardTests;

public sealed partial class DashboardBrowserUiTests
{
    [Theory]
    [InlineData("modern-success", true, 1, 0)]
    [InlineData("modern-denied", false, 1, 0)]
    [InlineData("legacy-success", true, 0, 1)]
    [InlineData("legacy-false", false, 0, 1)]
    [InlineData("legacy-throw", false, 0, 1)]
    [InlineData("select-throw", false, 0, 0)]
    public async Task Clipboard_Branches_Should_Report_Outcome_And_Remove_Temporary_Text(
        string mode, bool expectedSuccess, int expectedModernCalls, int expectedLegacyCalls)
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(Viewports[0]);
        var page = await context.NewPageAsync();
        await LoginAndOpenAsync(page, "/logs?uiProfile=normal");

        var resultJson = await page.EvaluateAsync<string>(
            """
            async mode => {
                let modernCalls = 0;
                let legacyCalls = 0;
                Object.defineProperty(navigator, 'clipboard', {
                    configurable: true,
                    value: mode.startsWith('modern-') ? {
                        writeText: async () => {
                            modernCalls++;
                            if (mode === 'modern-denied') throw new DOMException('test denial', 'NotAllowedError');
                        }
                    } : undefined
                });
                document.execCommand = () => {
                    legacyCalls++;
                    if (mode === 'legacy-throw') throw new Error('test legacy error');
                    return mode === 'legacy-success';
                };
                if (mode === 'select-throw') {
                    HTMLTextAreaElement.prototype.select = () => { throw new Error('test select error'); };
                }
                let succeeded = false;
                try {
                    await window.contextHubDashboard.copyText('synthetic clipboard regression');
                    succeeded = true;
                } catch { }
                return JSON.stringify({
                    succeeded, modernCalls, legacyCalls,
                    temporaryTextareas: Array.from(document.querySelectorAll('textarea'))
                        .filter(element => element.style.top === '-1000px').length
                });
            }
            """, mode);

        using var result = JsonDocument.Parse(resultJson);
        result.RootElement.GetProperty("succeeded").GetBoolean().Should().Be(expectedSuccess);
        result.RootElement.GetProperty("modernCalls").GetInt32().Should().Be(expectedModernCalls);
        result.RootElement.GetProperty("legacyCalls").GetInt32().Should().Be(expectedLegacyCalls);
        result.RootElement.GetProperty("temporaryTextareas").GetInt32().Should().Be(0);
    }

    [Theory]
    [InlineData("logs", 1)]
    [InlineData("jobs", 2)]
    [InlineData("retention", 2)]
    public async Task Clipboard_Existing_Consumers_Should_Report_Each_Successful_Copy(string route, int expectedCalls)
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(Viewports[0]);
        var page = await context.NewPageAsync();
        await LoginAndOpenAsync(page, $"/{route}?uiProfile=normal");
        await page.EvaluateAsync("""
            () => {
                window.__clipboardConsumerCalls = 0;
                Object.defineProperty(navigator, 'clipboard', {
                    configurable: true,
                    value: { writeText: async () => { window.__clipboardConsumerCalls++; } }
                });
            }
            """);

        if (route == "logs")
        {
            await page.Locator(".data-table-clickable tbody tr").First.ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "複製 JSON", Exact = true }).ClickAsync();
            await AssertClipboardToastAsync(page, "已複製日誌 #");
        }
        else if (route == "jobs")
        {
            await page.Locator(".jobs-table tbody tr").First.ClickAsync();
            await page.Locator(".detail-panel").GetByRole(AriaRole.Button, new() { Name = "複製 JSON", Exact = true }).ClickAsync();
            await AssertClipboardToastAsync(page, "已複製工作 ");
            var retentionPanel = page.Locator(".jobs-retention-panel");
            await retentionPanel.GetByRole(AriaRole.Button, new() { Name = "快速產生清單", Exact = true }).ClickAsync();
            await retentionPanel.GetByRole(AriaRole.Button, new() { Name = "複製結果", Exact = true }).ClickAsync();
            await AssertClipboardToastAsync(page, "已複製 retention 結果，可交給 agent 進一步判斷。");
        }
        else
        {
            await page.GetByRole(AriaRole.Button, new() { Name = "產生整理清單", Exact = true }).ClickAsync();
            var commandPanel = page.Locator(".retention-command-panel");
            await commandPanel.GetByRole(AriaRole.Button, new() { Name = "複製整理計畫", Exact = true }).ClickAsync();
            await AssertClipboardToastAsync(page, "已複製整理計畫，可交給 agent 或人工審核後套用。");
            await commandPanel.GetByRole(AriaRole.Button, new() { Name = "複製原始結果", Exact = true }).ClickAsync();
            await AssertClipboardToastAsync(page, "已複製 retention 原始結果。");
        }

        (await page.EvaluateAsync<int>("() => window.__clipboardConsumerCalls")).Should().Be(expectedCalls);
        (await page.Locator("#blazor-error-ui").IsVisibleAsync()).Should().BeFalse();
    }

    private static async Task AssertClipboardToastAsync(IPage page, string message)
    {
        await page.Locator(".toast").Filter(new() { HasText = message }).WaitForAsync(new()
        {
            State = WaitForSelectorState.Visible,
            Timeout = 5000
        });
    }

    [Theory]
    [InlineData("memories", "複製內容", "已複製 memory 內容")]
    [InlineData("account/tokens", "複製存取權杖", "已複製完整 Token。")]
    [InlineData("security", "複製 Token", "已複製完整 Token。")]
    public async Task Clipboard_Failure_Should_Show_Safe_Feedback_And_Keep_Circuit_Interactive(
        string route, string copyButtonName, string successMessage)
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(Viewports[0]);
        var page = await context.NewPageAsync();
        await LoginAndOpenAsync(page, $"/{route}?uiProfile=normal");

        if (route == "memories")
        {
            await page.Locator(".memories-table tbody tr").First.ClickAsync();
        }
        else if (route == "account/tokens")
        {
            await page.GetByLabel("存取權杖名稱", new() { Exact = true }).FillAsync("clipboard regression");
            await page.GetByRole(AriaRole.Button, new() { Name = "建立存取權杖", Exact = true }).ClickAsync();
        }
        else
        {
            var tokenPanel = page.Locator("section.panel").Filter(new()
            {
                Has = page.GetByText("Token 管理", new() { Exact = true })
            });
            var ownerSelect = tokenPanel.Locator("label:has(> span:text-is(\"Owner\")) > select");
            (await ownerSelect.CountAsync()).Should().Be(1);
            await ownerSelect.SelectOptionAsync(new SelectOptionValue { Index = 1 });
            await tokenPanel.GetByLabel("Token 名稱", new() { Exact = true }).FillAsync("clipboard regression");
            await tokenPanel.GetByRole(AriaRole.Button, new() { Name = "建立 Token", Exact = true }).ClickAsync();
        }

        // The existing fixture selects Testing AND the synthetic browser-test API client.
        // Replace only this page's JS boundary; never read or save clipboard/token contents.
        await page.EvaluateAsync("""
            () => {
                window.__clipboardFailureEnabled = true;
                window.__clipboardFailureCalls = 0;
                window.contextHubDashboard.copyText = async () => {
                    window.__clipboardFailureCalls++;
                    if (window.__clipboardFailureEnabled) throw new Error('clipboard-failure-canary');
                };
            }
            """);
        var copyButton = page.GetByRole(AriaRole.Button, new() { Name = copyButtonName, Exact = true });
        await copyButton.ClickAsync();
        var errorToast = page.Locator(".toast").Filter(new() { HasText = "複製失敗" });
        await errorToast.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5000 });
        (await errorToast.InnerTextAsync()).Should().NotContain("clipboard-failure-canary");
        (await page.Locator(".toast").AllTextContentsAsync()).Should().NotContain(text => text.Contains("已複製", StringComparison.Ordinal));
        (await page.Locator("#blazor-error-ui").IsVisibleAsync()).Should().BeFalse();
        (await page.EvaluateAsync<int>("() => window.__clipboardFailureCalls")).Should().Be(1);

        await page.EvaluateAsync("() => { window.__clipboardFailureEnabled = false; }");
        await copyButton.ClickAsync();
        await page.Locator(".toast").Filter(new() { HasText = successMessage }).WaitForAsync(new()
        {
            State = WaitForSelectorState.Visible,
            Timeout = 5000
        });
        (await errorToast.CountAsync()).Should().Be(0);
        (await page.Locator("#blazor-error-ui").IsVisibleAsync()).Should().BeFalse();
        (await page.EvaluateAsync<int>("() => window.__clipboardFailureCalls")).Should().Be(2);
    }
}
