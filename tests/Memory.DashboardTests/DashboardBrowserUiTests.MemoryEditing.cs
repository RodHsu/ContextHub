using FluentAssertions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Memory.DashboardTests;

public sealed partial class DashboardBrowserUiTests
{
    [Fact]
    public async Task Memory_editor_preserves_a_conflicted_draft_until_review_and_removes_it_on_scope_change()
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(new DashboardViewport("memory-edit", 1366, 768));
        var page = await context.NewPageAsync();
        await LoginAndOpenAsync(page, "/memories?memoryEditFixture=true");
        await page.Locator(".memories-select-button").First.ClickAsync();

        var open = page.GetByTestId("memory-edit-open");
        await Expect(open).ToBeVisibleAsync();
        await open.FocusAsync();
        await open.PressAsync("Enter");
        var content = page.GetByTestId("memory-edit-content");
        var submit = page.GetByTestId("memory-edit-submit");
        await Expect(content).ToBeVisibleAsync();
        (await content.InputValueAsync()).Should().NotBeEmpty();
        await Expect(submit).ToBeDisabledAsync();
        const string draft = "fixture draft kept only in this editor";
        await content.FillAsync(draft);
        await Expect(submit).ToBeEnabledAsync();

        await submit.ClickAsync();
        await Expect(page.GetByTestId("memory-edit-status")).ToContainTextAsync("草稿已保留");
        await Expect(content).ToHaveValueAsync(draft);
        await Expect(submit).ToBeDisabledAsync();
        await page.GetByTestId("memory-edit-reload").ClickAsync();
        await Expect(page.GetByRole(AriaRole.Region, new() { Name = "重新讀取的記憶" }))
            .ToContainTextAsync("其他編輯者的版本");
        await Expect(submit).ToBeDisabledAsync();
        await page.GetByTestId("memory-edit-adopt").ClickAsync();
        await Expect(content).ToHaveValueAsync(draft);
        await Expect(submit).ToBeEnabledAsync();

        await page.Locator("#memory-project-query").FillAsync("another-scope");
        await Expect(content).ToHaveCountAsync(0);
        await Expect(submit).ToHaveCountAsync(0);
        await Expect(page.GetByTestId("memory-edit-recover")).ToHaveCountAsync(0);
    }
}
