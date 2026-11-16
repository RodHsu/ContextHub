using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Memory.IntegrationTests;

public sealed class GovernanceFindingCacheTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Accept_and_dismiss_should_invalidate_warm_details_for_both_referenced_projects()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<MemoryDbContext>();
        var actor = UseActor(services, db);
        var first = Item(actor, "finding-primary");
        var second = Item(actor, "finding-secondary");
        db.MemoryItems.AddRange(first, second);
        await db.SaveChangesAsync();
        var accepted = Finding(actor, first, second);
        var dismissed = Finding(actor, first, second);
        db.GovernanceFindings.AddRange(accepted, dismissed);
        await db.SaveChangesAsync();
        var dashboard = services.GetRequiredService<IDashboardQueryService>();
        var telemetry = services.GetRequiredService<IRedisCacheTelemetry>();
        var versions = services.GetRequiredService<ICacheVersionStore>();
        foreach (var item in new[] { first, second })
        {
            (await dashboard.GetMemoryDetailsAsync(item.Id, default))!.Findings.Should().HaveCount(2);
            await dashboard.GetMemoryDetailsAsync(item.Id, default);
        }
        telemetry.GetSnapshot().Kinds["dashboard-memory-details"].Hits.Should().BeGreaterThanOrEqualTo(2);
        var before = await versions.GetVersionStampAsync([first.ProjectId, second.ProjectId], actor, false, default);
        var governance = services.GetRequiredService<IGovernanceService>();
        await governance.AcceptAsync(accepted.Id, default);
        await governance.DismissAsync(dismissed.Id, default);
        var after = await versions.GetVersionStampAsync([first.ProjectId, second.ProjectId], actor, false, default);
        after.ProjectVersions[first.ProjectId].Should().BeGreaterThan(before.ProjectVersions[first.ProjectId]);
        after.ProjectVersions[second.ProjectId].Should().BeGreaterThan(before.ProjectVersions[second.ProjectId]);
        foreach (var item in new[] { first, second })
        {
            var details = await dashboard.GetMemoryDetailsAsync(item.Id, default);
            details!.Findings!.Single(x => x.Id == accepted.Id).Status.Should().Be(GovernanceFindingStatus.Accepted);
            details.Findings!.Single(x => x.Id == dismissed.Id).Status.Should().Be(GovernanceFindingStatus.Dismissed);
        }
    }

    [DockerRequiredFact]
    public async Task Changing_finding_references_should_invalidate_old_and_new_projects_only_on_commit()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<MemoryDbContext>();
        var actor = UseActor(services, db);
        var original = Item(actor, "finding-old");
        var secondary = Item(actor, "finding-secondary");
        var replacement = Item(actor, "finding-new");
        db.MemoryItems.AddRange(original, secondary, replacement);
        await db.SaveChangesAsync();
        var finding = Finding(actor, original, secondary);
        db.GovernanceFindings.Add(finding);
        await db.SaveChangesAsync();
        var dashboard = services.GetRequiredService<IDashboardQueryService>();
        foreach (var item in new[] { original, secondary, replacement })
            await dashboard.GetMemoryDetailsAsync(item.Id, default);
        var versions = services.GetRequiredService<ICacheVersionStore>();
        var projects = new[] { original.ProjectId, secondary.ProjectId, replacement.ProjectId };
        var before = await versions.GetVersionStampAsync(projects, actor, false, default);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE governance_findings SET primary_memory_id = {replacement.Id} WHERE id = {finding.Id}");
            // The independent reader sees the old committed revision while the writer holds its transaction.
            (await versions.GetVersionStampAsync(projects, actor, false, default)).Value.Should().Be(before.Value);
            await transaction.RollbackAsync();
        }
        (await versions.GetVersionStampAsync(projects, actor, false, default)).Value.Should().Be(before.Value);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE governance_findings SET primary_memory_id = {replacement.Id} WHERE id = {finding.Id}");
        var after = await versions.GetVersionStampAsync(projects, actor, false, default);
        foreach (var project in projects)
            after.ProjectVersions[project].Should().BeGreaterThan(before.ProjectVersions[project]);
        (await dashboard.GetMemoryDetailsAsync(original.Id, default))!.Findings.Should().BeEmpty();
        (await dashboard.GetMemoryDetailsAsync(replacement.Id, default))!.Findings.Should().ContainSingle(x => x.Id == finding.Id);
        (await dashboard.GetMemoryDetailsAsync(secondary.Id, default))!.Findings.Should().ContainSingle(x => x.Id == finding.Id);
    }

    private static ContextHubRequestActor UseActor(IServiceProvider services, MemoryDbContext db)
    {
        var user = db.TenantUsers.Single(x => x.Username == "contract-test-admin");
        var actor = new ContextHubRequestActor(user.TenantId, user.Id, user.Username, user.Role,
            [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite], [], true);
        services.GetRequiredService<IRequestActorAccessor>().Current = actor;
        return actor;
    }

    private static MemoryItem Item(ContextHubRequestActor actor, string prefix) => new()
    {
        TenantId = actor.TenantId,
        OwnerUserId = actor.UserId,
        ProjectId = $"{prefix}-{Guid.NewGuid():N}",
        ExternalKey = Guid.NewGuid().ToString("N"),
        Title = "Finding cache fixture",
        Content = "Fixture",
        Summary = "Fixture",
        Scope = MemoryScope.Project,
        MemoryType = MemoryType.Fact,
        Status = MemoryStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static GovernanceFinding Finding(ContextHubRequestActor actor, MemoryItem primary, MemoryItem secondary) => new()
    {
        TenantId = actor.TenantId,
        OwnerUserId = actor.UserId,
        ProjectId = primary.ProjectId,
        PrimaryMemoryId = primary.Id,
        SecondaryMemoryId = secondary.Id,
        Type = GovernanceFindingType.DuplicateCandidate,
        Status = GovernanceFindingStatus.Open,
        Title = "Finding cache fixture",
        Summary = "Fixture",
        DedupKey = Guid.NewGuid().ToString("N"),
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };
}
