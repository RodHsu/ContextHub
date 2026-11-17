using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Playwright;

namespace Memory.DashboardTests;

public sealed partial class DashboardBrowserUiTests
{
    [Theory]
    [InlineData(1366, 768)]
    [InlineData(390, 844)]
    public async Task Preferences_Content_Edit_Should_Preserve_Loaded_Scores_And_Tags_While_New_Items_Use_Defaults(int width, int height)
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(new("preference-metadata", width, height));
        await context.AddCookiesAsync([new Cookie
        {
            Name = "dashboard-preference-test-session",
            Value = Guid.NewGuid().ToString(),
            Url = _fixture.BaseUri.ToString()
        }]);
        var page = await context.NewPageAsync();
        await LoginAndOpenAsync(page, "/preferences?uiProfile=PreferenceMetadata");
        await page.Locator("[data-dashboard-interactive=true]").WaitForAsync();
        var content = page.GetByLabel("內容 (Content)", new() { Exact = true });
        var tags = page.GetByLabel("標籤 (Tags)", new() { Exact = true });
        (await content.InputValueAsync()).Should().Be("Original metadata fixture content");
        (await tags.InputValueAsync()).Should().Contain("custom-alpha").And.Contain("custom-beta");

        await content.FillAsync("Edited metadata fixture content");
        await SaveAndAssertScoresAsync(0.41m, 0.73m);
        await page.ReloadAsync();
        await page.Locator("[data-dashboard-interactive=true]").WaitForAsync();
        (await content.InputValueAsync()).Should().Be("Edited metadata fixture content");
        (await tags.InputValueAsync()).Should().Contain("custom-alpha").And.Contain("custom-beta");

        await content.FillAsync("Original metadata fixture content");
        await SaveAndAssertScoresAsync(0.41m, 0.73m);
        await page.GetByRole(AriaRole.Button, new() { Name = "建立新偏好", Exact = true }).ClickAsync();
        await page.GetByLabel("鍵值 (Key)", new() { Exact = true }).FillAsync($"preference-metadata-new-{width}");
        await page.GetByLabel("標題 (Title)", new() { Exact = true }).FillAsync("New metadata fixture");
        await content.FillAsync("New synthetic preference");
        await tags.FillAsync("custom-new");
        await SaveAndAssertScoresAsync(0.95m, 0.95m);
        (await tags.InputValueAsync()).Should().Contain("custom-new");

        async Task SaveAndAssertScoresAsync(decimal importance, decimal confidence)
        {
            var before = _fixture.PreferenceMetadataRequests.Count;
            await page.GetByRole(AriaRole.Button, new() { Name = "儲存偏好", Exact = true }).ClickAsync();
            await page.Locator(".toast").Filter(new() { HasText = "偏好已儲存。" }).WaitForAsync();
            for (var attempt = 0; attempt < 50 && _fixture.PreferenceMetadataRequests.Count <= before; attempt++)
                await Task.Delay(100);
            var observations = _fixture.PreferenceMetadataRequests;
            observations.Count.Should().Be(before + 1, "the real Blazor save must call the test API client exactly once");
            observations[^1].Importance.Should().Be(importance, "editing content must preserve the loaded importance");
            observations[^1].Confidence.Should().Be(confidence, "editing content must preserve the loaded confidence");
        }
    }
}

public sealed partial class DashboardBrowserFixture
{
    private readonly ConcurrentQueue<(decimal Importance, decimal Confidence)> _preferenceMetadataRequests = new();
    internal IReadOnlyList<(decimal Importance, decimal Confidence)> PreferenceMetadataRequests => _preferenceMetadataRequests.ToArray();

    private void ObservePreferenceMetadataRequest(string? line)
    {
        const string prefix = "PREFERENCE_METADATA_TEST_REQUEST ";
        if (line is null || line.Length > 160 || !line.StartsWith(prefix, StringComparison.Ordinal) || _preferenceMetadataRequests.Count >= 12) return;
        try
        {
            using var json = JsonDocument.Parse(line[prefix.Length..]);
            var importance = json.RootElement.GetProperty("Importance").GetDecimal();
            var confidence = json.RootElement.GetProperty("Confidence").GetDecimal();
            if (importance is >= 0 and <= 1 && confidence is >= 0 and <= 1)
                _preferenceMetadataRequests.Enqueue((importance, confidence));
        }
        catch (JsonException) { }
    }
}
