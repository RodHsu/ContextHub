using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Memory.DashboardTests;

public sealed partial class DashboardBrowserUiTests
{
    [Theory]
    [InlineData("success", "switch", "current-beta-revision")]
    [InlineData("error", "switch", "current-beta-revision")]
    [InlineData("empty", "refresh", "current-alpha-revision")]
    [InlineData("success", "reselect", "current-alpha-revision")]
    public async Task Skills_Should_Ignore_Superseded_Source_Observation_Results(
        string staleOutcome, string selectionChange, string expectedRevision)
    {
        const string firstSkill = "Execution audit fixture skill";
        const string secondSkill = "Source observation fixture beta";
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(new DashboardViewport("skills-observations", 1366, 768));
        var page = await context.NewPageAsync();
        await LoginAndOpenAsync(page, $"/skills?skillsObservationFixture={staleOutcome}");

        var registry = page.Locator("section.panel").Filter(new()
        {
            Has = page.GetByText("Registry / versions / bindings", new() { Exact = true })
        });
        var firstRow = registry.Locator("article.stack-item").Filter(new() { HasText = firstSkill });
        await firstRow.ClickAsync();
        await Expect(registry.GetByText($"{firstSkill} details", new() { Exact = true })).ToBeVisibleAsync();

        if (selectionChange == "refresh")
        {
            await page.GetByRole(AriaRole.Button, new() { Name = "重新整理", Exact = true }).ClickAsync();
        }
        else if (selectionChange == "switch")
        {
            await registry.Locator("article.stack-item").Filter(new() { HasText = secondSkill }).ClickAsync();
        }
        else
        {
            await firstRow.ClickAsync();
        }

        var details = registry.Locator(".subpanel");
        await Expect(details).ToContainTextAsync(expectedRevision);

        // Preview releases the fixture's blocked first read and renders after its continuation.
        await page.GetByRole(AriaRole.Button, new() { Name = "Preview", Exact = true }).PressAsync("Enter");
        await Expect(page.GetByText("Import preview 已完成。", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(details).ToContainTextAsync(expectedRevision);
        await Expect(details).Not.ToContainTextAsync("stale-alpha-revision");
        await Expect(page.Locator(".inline-error")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Skills_And_Executions_Should_Read_Back_The_Same_Execution_In_Audit_Details()
    {
        const string fixtureQuery = "?skillsExecutionFixture=true";
        const string executionId = "483d7042-fcab-4351-94bc-142ea3435546";
        const string skillName = "Execution audit fixture skill";
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(new DashboardViewport("skills-executions", 1366, 768));
        var page = await context.NewPageAsync();
        await LoginAndOpenAsync(page, "/agent-executions" + fixtureQuery);

        var recentExecution = page.GetByRole(AriaRole.Row).Filter(new() { HasText = "browser-audit-repository" });
        await Expect(recentExecution).ToContainTextAsync("Completed");
        await recentExecution.GetByRole(AriaRole.Button, new() { Name = "檢視", Exact = true }).ClickAsync();

        var executionAudit = page.Locator("section.panel").Filter(new()
        {
            Has = page.GetByText("Execution audit drill-down", new() { Exact = true })
        });
        await Expect(executionAudit).ToContainTextAsync(executionId);
        await Expect(executionAudit).ToContainTextAsync(new string('b', 64));
        await Expect(executionAudit.GetByRole(AriaRole.Row).Filter(new() { HasText = "browser-audit-completion" }))
            .ToContainTextAsync("Completed");
        await Expect(recentExecution).Not.ToContainTextAsync(new Regex(@"lease[-_\s]?token", RegexOptions.IgnoreCase));
        await Expect(executionAudit).Not.ToContainTextAsync(new Regex(@"lease[-_\s]?token", RegexOptions.IgnoreCase));

        var skillsLink = page.Locator(".domain-surface-nav").GetByRole(AriaRole.Link, new() { Name = "Agent Skills", Exact = true });
        await Expect(skillsLink).ToHaveAttributeAsync("href", "/skills");
        await page.GotoAsync(new Uri(_fixture.BaseUri, "/skills" + fixtureQuery).ToString());
        await Expect(page.Locator(".dashboard-shell")).ToHaveAttributeAsync("data-dashboard-interactive", "true");

        var skillAudit = page.Locator("section.panel").Filter(new()
        {
            Has = page.GetByText("Execution resolution audit", new() { Exact = true })
        }).Locator("article.stack-item").Filter(new() { HasText = executionId });
        await Expect(skillAudit).ToContainTextAsync(skillName);
        await Expect(skillAudit).ToContainTextAsync("1.2.3");
        await Expect(skillAudit).ToContainTextAsync("Pinned");
        await Expect(skillAudit).ToContainTextAsync(new string('a', 12));

        var registry = page.Locator("section.panel").Filter(new()
        {
            Has = page.GetByText("Registry / versions / bindings", new() { Exact = true })
        });
        await registry.Locator("article.stack-item").Filter(new() { HasText = skillName }).ClickAsync();
        var version = registry.GetByRole(AriaRole.Row).Filter(new() { HasText = "1.2.3" });
        await Expect(version).ToContainTextAsync("Published");
        await Expect(version).ToContainTextAsync("Default");
        await Expect(version).ToContainTextAsync(new string('a', 12));
    }
}
