using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Memory.DashboardTests;

public sealed partial class DashboardBrowserUiTests
{
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
