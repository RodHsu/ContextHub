using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Memory.IntegrationTests;

public sealed class PlatformWave5WorkflowTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Connection_profile_snapshot_should_be_immutable_and_current_authority_change_should_block_active_lease()
    {
        var projectId = $"wave5-resource-{Guid.NewGuid():N}";
        var repositoryId = "wave5-resource-repo";
        Guid sourceId;
        Guid workItemId;
        await using (var setup = environment.GetFactory().Services.CreateAsyncScope())
        {
            var actor = UseActor(setup.ServiceProvider);
            var db = setup.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var now = DateTimeOffset.UtcNow;
            var source = new SourceConnection
            {
                TenantId = actor.TenantId,
                OwnerUserId = actor.UserId,
                ProjectId = projectId,
                Name = "Wave 5 logical connection profile",
                SourceKind = SourceKind.LocalDocs,
                ConfigJson = """{"rootPath":"private-provider-location"}""",
                CreatedAt = now,
                UpdatedAt = now
            };
            db.SourceConnections.Add(source);
            await db.SaveChangesAsync();
            sourceId = source.Id;
            var workItem = await setup.ServiceProvider.GetRequiredService<IProjectWorkItemService>().CreateAsync(new(
                projectId, "Resolve logical resources", "Wave 5 integration fixture", ["wave5"], ["fail closed"], 80), CancellationToken.None);
            workItemId = workItem.Id;
            var requirement = new AgentExecutionResourceRequirement(
                Guid.NewGuid(), AgentExecutionResourceKind.ConnectionProfile, source.Id,
                AgentExecutionResourceResolutionMode.LogicalCurrent);
            await setup.ServiceProvider.GetRequiredService<IAgentExecutionService>().PrepareAsync(new(
                workItem.Id, projectId, repositoryId, "Use the logical connection profile", ["snapshot exists"], ["work-item"],
                ["provider locator stays private"], ["read"], ["integration test"], "Codex", [], 50, 3, null,
                $"prepare-{Guid.NewGuid():N}", ResourceRequirements: [requirement],
                ResourceRetryMode: AgentExecutionResourceRetryMode.ReuseSnapshot), CancellationToken.None);
        }

        AgentExecutionClaimResult claim;
        await using (var worker = environment.GetFactory().Services.CreateAsyncScope())
        {
            UseActor(worker.ServiceProvider);
            claim = await worker.ServiceProvider.GetRequiredService<IAgentExecutionService>().ClaimNextAsync(new(
                projectId, repositoryId, "wave5-worker", "Codex", [], 300, $"claim-{Guid.NewGuid():N}"), CancellationToken.None);
            claim.HasExecution.Should().BeTrue();
            claim.ResolutionSnapshot.Should().NotBeNull();
            claim.ResolutionSnapshot!.RetryMode.Should().Be(AgentExecutionResourceRetryMode.ReuseSnapshot);
            claim.ResolutionSnapshot.Items.Should().ContainSingle(x => x.Kind == AgentExecutionResourceKind.ConnectionProfile);
            JsonSerializer.Serialize(claim.ResolutionSnapshot).Should().NotContain("private-provider-location");
        }

        await using (var authorityChange = environment.GetFactory().Services.CreateAsyncScope())
        {
            UseActor(authorityChange.ServiceProvider);
            var db = authorityChange.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var source = await db.SourceConnections.SingleAsync(x => x.Id == sourceId);
            source.ConfigJson = """{"rootPath":"rotated-private-location"}""";
            source.Revision++;
            source.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        await using (var heartbeat = environment.GetFactory().Services.CreateAsyncScope())
        {
            UseActor(heartbeat.ServiceProvider);
            var result = await heartbeat.ServiceProvider.GetRequiredService<IAgentExecutionService>().HeartbeatAsync(new(
                claim.Execution!.Id, "wave5-worker", claim.LeaseToken!, claim.Execution.LeaseVersion, 300,
                $"heartbeat-{Guid.NewGuid():N}"), CancellationToken.None);
            result.Outcome.Should().Be("BlockedByResourceAuthority");
            result.Execution.Status.Should().Be(AgentExecutionStatus.Blocked);
            result.Execution.ClaimedByAgentId.Should().BeEmpty();
        }

        await using var verify = environment.GetFactory().Services.CreateAsyncScope();
        UseActor(verify.ServiceProvider);
        var verifyDb = verify.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await verifyDb.AgentExecutionResolutionSnapshots.CountAsync(x => x.ExecutionId == claim.Execution!.Id)).Should().Be(1);
        (await verifyDb.AgentExecutions.SingleAsync(x => x.WorkItemId == workItemId)).Status.Should().Be(AgentExecutionStatus.Blocked);
    }

    [DockerRequiredFact]
    public async Task Authority_outbox_should_commit_before_monitoring_and_full_run_should_rebuild_deleted_projection()
    {
        var projectId = $"wave5-projection-{Guid.NewGuid():N}";
        Guid authorityEventId;
        Guid? tenantId;
        await using (var authority = environment.GetFactory().Services.CreateAsyncScope())
        {
            var actor = UseActor(authority.ServiceProvider);
            tenantId = actor.TenantId;
            var db = authority.ServiceProvider.GetRequiredService<MemoryDbContext>();
            db.SourceConnections.Add(new SourceConnection
            {
                TenantId = actor.TenantId,
                OwnerUserId = actor.UserId,
                ProjectId = projectId,
                Name = "Projection authority",
                SourceKind = SourceKind.LocalDocs,
                ConfigJson = """{"rootPath":"never-in-outbox"}""",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
            var authorityEvent = await db.AuthorityOutboxEvents.SingleAsync(x => x.ProjectId == projectId);
            authorityEvent.PayloadJson.Should().NotContain("never-in-outbox");
            authorityEventId = authorityEvent.Id;
            (await db.MonitoringActivityProjections.CountAsync(x => x.ProjectId == projectId)).Should().Be(0,
                "monitoring is asynchronous and cannot be authority for the business commit");
        }

        await using (var projection = environment.GetFactory().Services.CreateAsyncScope())
        {
            UseActor(projection.ServiceProvider);
            var service = projection.ServiceProvider.GetRequiredService<IPlatformProjectionService>();
            var incremental = await service.RunAsync(new(tenantId, projectId, PlatformBackgroundMode.Incremental, "integration-worker"), CancellationToken.None);
            incremental.Status.Should().Be(PlatformBackgroundRunStatus.Completed);
            incremental.CoverageComplete.Should().BeTrue();
            var db = projection.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var row = await db.MonitoringActivityProjections.SingleAsync(x => x.OutboxEventId == authorityEventId);
            row.SecurityCritical.Should().BeTrue();
            db.MonitoringActivityProjections.Remove(row);
            await db.SaveChangesAsync();
        }

        await using (var rebuild = environment.GetFactory().Services.CreateAsyncScope())
        {
            UseActor(rebuild.ServiceProvider);
            var result = await rebuild.ServiceProvider.GetRequiredService<IPlatformProjectionService>().RunAsync(new(
                tenantId, projectId, PlatformBackgroundMode.Full, "full-rebuild-worker"), CancellationToken.None);
            result.Status.Should().Be(PlatformBackgroundRunStatus.Completed);
            result.Generation.Should().BePositive();
            result.Rebuilt.Should().BeGreaterThan(0);
            var db = rebuild.ServiceProvider.GetRequiredService<MemoryDbContext>();
            (await db.MonitoringActivityProjections.CountAsync(x => x.OutboxEventId == authorityEventId)).Should().Be(1);
        }
    }

    [DockerRequiredFact]
    public async Task Projection_scope_should_isolate_same_project_id_across_tenants()
    {
        var projectId = $"wave5-tenant-scope-{Guid.NewGuid():N}";
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var eventA = NewAuthorityEvent(tenantA, projectId, "tenant-a");
        var eventB = NewAuthorityEvent(tenantB, projectId, "tenant-b");
        await using var scope = environment.GetFactory().Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        db.AuthorityOutboxEvents.AddRange(eventA, eventB);
        await db.SaveChangesAsync();

        var result = await scope.ServiceProvider.GetRequiredService<IPlatformProjectionService>().RunAsync(new(
            tenantA, projectId, PlatformBackgroundMode.Incremental, "tenant-isolation-worker"), CancellationToken.None);

        result.Status.Should().Be(PlatformBackgroundRunStatus.Completed);
        (await db.MonitoringActivityProjections.SingleAsync(x => x.OutboxEventId == eventA.Id)).TenantId.Should().Be(tenantA);
        (await db.MonitoringActivityProjections.AnyAsync(x => x.OutboxEventId == eventB.Id)).Should().BeFalse();
        var state = await db.MonitoringProjectionStates.SingleAsync(x => x.ProjectId == projectId);
        state.TenantId.Should().Be(tenantA);
        state.TenantScopeKey.Should().Be(tenantA.ToString("D"));
    }

    private static AuthorityOutboxEvent NewAuthorityEvent(Guid tenantId, string projectId, string aggregateId)
        => new()
        {
            TenantId = tenantId,
            ProjectId = projectId,
            Category = "AgentExecution",
            AggregateType = "AgentExecution",
            AggregateId = aggregateId,
            EventType = "Added",
            AuthorityRevision = 1,
            SecurityCritical = true,
            PayloadJson = "{}",
            OccurredAt = DateTimeOffset.UtcNow
        };

    private static ContextHubRequestActor UseActor(IServiceProvider services)
    {
        var db = services.GetRequiredService<MemoryDbContext>();
        var user = db.TenantUsers.Include(x => x.Tenant).Single(x => x.Username == "contract-test-admin");
        var actor = new ContextHubRequestActor(user.TenantId, user.Id, user.Username, user.Role,
            [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite,
             SecurityScopes.AgentExecutionsRead, SecurityScopes.AgentExecutionsClaim,
             SecurityScopes.AgentExecutionsWrite, SecurityScopes.AgentExecutionsManage], [], true);
        services.GetRequiredService<IRequestActorAccessor>().Current = actor;
        return actor;
    }
}
