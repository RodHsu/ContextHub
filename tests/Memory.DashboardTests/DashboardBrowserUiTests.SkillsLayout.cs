using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Memory.DashboardTests;

public sealed partial class DashboardBrowserUiTests
{
    [Theory]
    [InlineData(1366, 768)]
    [InlineData(390, 844)]
    public async Task Skills_import_preview_should_receive_pointer_input_after_expanding_the_registry(int width, int height)
    {
        const string skill = "Execution audit fixture skill";
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(new DashboardViewport("skills-preview-pointer", width, height));
        var page = await context.NewPageAsync();
        await LoginAndOpenAsync(page, "/skills?skillsExecutionFixture=true");

        var registry = page.Locator("section.panel").Filter(new()
        {
            Has = page.GetByText("Registry / versions / bindings", new() { Exact = true })
        });
        var import = page.Locator("section.panel").Filter(new()
        {
            Has = page.GetByText("Portable import", new() { Exact = true })
        });
        await registry.Locator("article.stack-item").Filter(new() { HasText = skill }).ClickAsync();
        await Expect(registry.GetByText($"{skill} details", new() { Exact = true })).ToBeVisibleAsync();

        var importBounds = await import.BoundingBoxAsync();
        var registryBounds = await registry.BoundingBoxAsync();
        Assert.NotNull(importBounds);
        Assert.NotNull(registryBounds);
        Assert.True(importBounds.Y + importBounds.Height <= registryBounds.Y + 1, "The expanded registry overlaps Portable import.");

        await import.GetByRole(AriaRole.Button, new() { Name = "Preview", Exact = true }).ClickAsync(new() { Trial = true });
    }
}
