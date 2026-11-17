using FluentAssertions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Memory.DashboardTests;

public sealed partial class DashboardBrowserUiTests
{
    [Theory]
    [InlineData(1366, 768)]
    [InlineData(390, 844)]
    public async Task Memories_Should_Select_And_Read_Different_Items_With_Enter_And_Space(int width, int height)
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(new("memory-keyboard", width, height));
        var page = await context.NewPageAsync();
        await LoginAndOpenAsync(page, "/memories?uiProfile=dense");

        var table = page.Locator(".memories-table");
        var titles = table.GetByRole(AriaRole.Button);
        await Expect(titles).ToHaveCountAsync(12);
        await titles.First.FocusAsync();

        foreach (var (index, key) in new[] { (1, "Enter"), (3, "Space") })
        {
            await page.Keyboard.PressAsync("Tab");
            if (index == 3)
            {
                await page.Keyboard.PressAsync("Tab");
            }

            var title = $"Dense Memory Item {index:00} With Long Title For Responsive Layout Validation";
            var select = table.GetByRole(AriaRole.Button, new() { Name = title, Exact = true });
            await Expect(select).ToBeFocusedAsync();
            (await select.EvaluateAsync<bool>("element => element.matches(':focus-visible') && getComputedStyle(element).outlineStyle !== 'none'"))
                .Should().BeTrue("keyboard selection needs a visible focus indicator");
            await page.Keyboard.PressAsync(key);

            await Expect(page.Locator(".memories-table tr.selected-row .memories-select-button")).ToHaveTextAsync(title);
            var reader = page.Locator(".memory-detail-content-block");
            await Expect(reader).ToContainTextAsync($"Dense memory {index:00} line 1:");
            await Expect(reader).Not.ToContainTextAsync($"Dense memory {(index == 1 ? 3 : 1):00} line 1:");
            await reader.ScrollIntoViewIfNeededAsync();
            await Expect(reader).ToBeVisibleAsync();
            (await reader.EvaluateAsync<string>("element => getComputedStyle(element).userSelect")).Should().NotBe("none");
        }
    }
}
