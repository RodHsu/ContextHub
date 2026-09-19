using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Memory.IntegrationTests;

public sealed class AgentExecutionWorkflowTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Claim_checkpoint_complete_should_replay_exactly_and_leave_work_item_business_state_unchanged()
    {
        var projectId = $"agent-execution-{Guid.NewGuid():N}";
        Guid workItemId;
        await using (var setup = environment.GetFactory().Services.CreateAsyncScope())
        {
            UseAgentActor(setup.ServiceProvider);
            var workItems = setup.ServiceProvider.GetRequiredService<IProjectWorkItemService>();
            var workItem = await workItems.CreateAsync(new(projectId, "Implement isolated task", "Agent execution fixture", ["agent-execution"], ["preserve business state"], 80), CancellationToken.None);
            workItemId = workItem.Id;
            var service = setup.ServiceProvider.GetRequiredService<IAgentExecutionService>();
            await service.PrepareAsync(Prepare(workItemId, projectId, "repo-one", "prepare-1"), CancellationToken.None);
        }

        AgentExecutionClaimResult claimed;
        await using (var worker = environment.GetFactory().Services.CreateAsyncScope())
        {
            UseAgentActor(worker.ServiceProvider);
            var service = worker.ServiceProvider.GetRequiredService<IAgentExecutionService>();
            var request = Claim(projectId, "repo-one", "worker-a", "claim-1");
            claimed = await service.ClaimNextAsync(request, CancellationToken.None);
            claimed.HasExecution.Should().BeTrue();
            claimed.Execution!.Status.Should().Be(AgentExecutionStatus.Claimed);

            var replay = await service.ClaimNextAsync(request, CancellationToken.None);
            replay.Replayed.Should().BeTrue();
            replay.Execution!.Id.Should().Be(claimed.Execution.Id);
            replay.LeaseToken.Should().Be(claimed.LeaseToken);

            var checkpoint = await service.CheckpointAsync(new(
                claimed.Execution.Id, "worker-a", claimed.LeaseToken!, claimed.Execution.LeaseVersion,
                "tests", "focused validation passed", ["test:focused"], 300, "checkpoint-1"), CancellationToken.None);
            checkpoint.Execution.Status.Should().Be(AgentExecutionStatus.Running);
            checkpoint.Replayed.Should().BeFalse();

            var checkpointReplay = await service.CheckpointAsync(new(
                claimed.Execution.Id, "worker-a", claimed.LeaseToken!, claimed.Execution.LeaseVersion,
                "tests", "focused validation passed", ["test:focused"], 300, "checkpoint-1"), CancellationToken.None);
            checkpointReplay.Replayed.Should().BeTrue();

            var completed = await service.CompleteAsync(new(
                claimed.Execution.Id, "worker-a", claimed.LeaseToken!, claimed.Execution.LeaseVersion,
                "Completed", "Execution evidence recorded", ["test:full"], false, "complete-1"), CancellationToken.None);
            completed.Execution.Status.Should().Be(AgentExecutionStatus.Completed);
        }

        await using var verify = environment.GetFactory().Services.CreateAsyncScope();
        UseAgentActor(verify.ServiceProvider);
        var db = verify.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await db.ProjectWorkItems.SingleAsync(x => x.Id == workItemId)).Status.Should().Be(ProjectWorkItemStatus.Pending);
        var events = await db.AgentExecutionEvents.Where(x => x.ExecutionId == claimed.Execution!.Id).OrderBy(x => x.Sequence).ToListAsync();
        events.Select(x => x.EventType).Should().ContainInOrder(
            AgentExecutionEventType.Prepared,
            AgentExecutionEventType.Claimed,
            AgentExecutionEventType.Checkpoint,
            AgentExecutionEventType.Completed);
        events.Select(x => x.Sequence).Should().OnlyHaveUniqueItems();
    }

    [DockerRequiredFact]
    public async Task Concurrent_workers_should_have_one_winner_and_stale_or_foreign_lease_should_fail_closed()
    {
        var projectId = $"agent-race-{Guid.NewGuid():N}";
        await using (var setup = environment.GetFactory().Services.CreateAsyncScope())
        {
            UseAgentActor(setup.ServiceProvider);
            var workItem = await setup.ServiceProvider.GetRequiredService<IProjectWorkItemService>().CreateAsync(
                new(projectId, "Race fixture", string.Empty, [], [], 100), CancellationToken.None);
            await setup.ServiceProvider.GetRequiredService<IAgentExecutionService>().PrepareAsync(
                Prepare(workItem.Id, projectId, "repo-race", "prepare-race"), CancellationToken.None);
        }

        async Task<AgentExecutionClaimResult> ClaimFromAsync(string worker, string key)
        {
            await using var scope = environment.GetFactory().Services.CreateAsyncScope();
            UseAgentActor(scope.ServiceProvider);
            return await scope.ServiceProvider.GetRequiredService<IAgentExecutionService>().ClaimNextAsync(
                Claim(projectId, "repo-race", worker, key), CancellationToken.None);
        }

        var results = await Task.WhenAll(ClaimFromAsync("worker-a", "race-a"), ClaimFromAsync("worker-b", "race-b"));
        results.Count(x => x.HasExecution).Should().Be(1);
        results.Count(x => x.Outcome == "NoEligibleWork").Should().Be(1);
        var winner = results.Single(x => x.HasExecution);

        await using var mutationScope = environment.GetFactory().Services.CreateAsyncScope();
        UseAgentActor(mutationScope.ServiceProvider);
        var service = mutationScope.ServiceProvider.GetRequiredService<IAgentExecutionService>();
        var foreign = () => service.HeartbeatAsync(new(
            winner.Execution!.Id, "foreign-worker", winner.LeaseToken!, winner.Execution.LeaseVersion, 300, "foreign-heartbeat"), CancellationToken.None);
        await foreign.Should().ThrowAsync<UnauthorizedAccessException>();

        var staleVersion = () => service.HeartbeatAsync(new(
            winner.Execution!.Id, winner.Execution.ClaimedByAgentId, winner.LeaseToken!, winner.Execution.LeaseVersion + 1, 300, "stale-heartbeat"), CancellationToken.None);
        await staleVersion.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [DockerRequiredFact]
    public async Task Concurrent_prepare_replay_should_create_one_execution_and_management_cancel_should_be_idempotent()
    {
        var projectId = $"agent-prepare-{Guid.NewGuid():N}";
        Guid workItemId;
        await using (var setup = environment.GetFactory().Services.CreateAsyncScope())
        {
            UseAgentActor(setup.ServiceProvider);
            var workItem = await setup.ServiceProvider.GetRequiredService<IProjectWorkItemService>().CreateAsync(
                new(projectId, "Prepare replay fixture", string.Empty, [], [], 70), CancellationToken.None);
            workItemId = workItem.Id;
        }

        var request = Prepare(workItemId, projectId, "repo-prepare", "same-prepare-key");
        async Task<AgentExecutionResult> PrepareFromAsync()
        {
            await using var scope = environment.GetFactory().Services.CreateAsyncScope();
            UseAgentActor(scope.ServiceProvider);
            return await scope.ServiceProvider.GetRequiredService<IAgentExecutionService>().PrepareAsync(request, CancellationToken.None);
        }

        var prepared = await Task.WhenAll(PrepareFromAsync(), PrepareFromAsync());
        prepared.Select(item => item.Id).Distinct().Should().ContainSingle();

        await using var manager = environment.GetFactory().Services.CreateAsyncScope();
        UseAgentActor(manager.ServiceProvider);
        var service = manager.ServiceProvider.GetRequiredService<IAgentExecutionService>();
        var cancelRequest = new AgentExecutionCancelRequest(
            prepared[0].Id,
            "SupersededByAuthority",
            "Management cancellation fixture",
            ["test:cancel"],
            "cancel-prepare");
        var cancelled = await service.CancelAsync(cancelRequest, CancellationToken.None);
        cancelled.Execution.Status.Should().Be(AgentExecutionStatus.Cancelled);
        cancelled.Replayed.Should().BeFalse();
        var replay = await service.CancelAsync(cancelRequest, CancellationToken.None);
        replay.Replayed.Should().BeTrue();
        replay.Execution.Id.Should().Be(cancelled.Execution.Id);

        var db = manager.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await db.AgentExecutions.CountAsync(item => item.WorkItemId == workItemId)).Should().Be(1);
        (await db.ProjectWorkItems.SingleAsync(item => item.Id == workItemId)).Status.Should().Be(ProjectWorkItemStatus.Pending);
        (await db.AgentExecutionEvents.CountAsync(item => item.ExecutionId == cancelled.Execution.Id && item.EventType == AgentExecutionEventType.Cancelled)).Should().Be(1);
    }

    [DockerRequiredFact]
    public async Task Revoked_pinned_skill_should_block_execution_on_heartbeat_without_changing_work_item_status()
    {
        var projectId = $"agent-skill-{Guid.NewGuid():N}";
        var repositoryId = $"repo-skill-{Guid.NewGuid():N}";
        var executionId = Guid.NewGuid();
        Guid workItemId;
        Guid skillVersionId;
        Guid resolutionId;

        await using (var setup = environment.GetFactory().Services.CreateAsyncScope())
        {
            UseAgentActor(setup.ServiceProvider);
            var workItems = setup.ServiceProvider.GetRequiredService<IProjectWorkItemService>();
            var workItem = await workItems.CreateAsync(new(
                projectId,
                "Execute with a pinned Skill",
                "AgentExecution and Skills runtime integration fixture",
                ["agent-execution", "skills"],
                ["revocation blocks execution"],
                90), CancellationToken.None);
            workItemId = workItem.Id;

            var skills = setup.ServiceProvider.GetRequiredService<ISkillService>();
            var imported = await ImportAndPublishSkillAsync(skills);
            skillVersionId = imported.Version.Id;
            await skills.ReindexAsync(new(
                "skill-search-v1",
                "deterministic",
                "1",
                0m,
                true,
                true,
                $"reindex-{Guid.NewGuid():N}"), CancellationToken.None);
            var search = await skills.SearchForExecutionAsync(new(
                executionId,
                workItemId,
                projectId,
                repositoryId,
                "Codex",
                "Use the exact AgentExecution integration fixture Skill",
                ["agent", "execution", "integration"],
                ["agent-execution"],
                [],
                ["git"],
                [],
                [],
                new SkillSearchPolicy(TopN: 5, Threshold: 0m, MaxSearchRounds: 2, MaxSelectedSkills: 2),
                $"search-{Guid.NewGuid():N}",
                ExplicitSkillVersionId: skillVersionId), CancellationToken.None);
            search.Candidates.Should().Contain(item => item.SkillVersionId == skillVersionId);
            var selected = await skills.SelectAsync(new(
                search.ResolutionId,
                [skillVersionId],
                "Exact runtime integration fixture",
                $"select-{Guid.NewGuid():N}"), CancellationToken.None);
            selected.PinnedVersions.Should().ContainSingle(item => item.SkillVersionId == skillVersionId);
            resolutionId = search.ResolutionId;

            setup.ServiceProvider.GetRequiredService<MemoryDbContext>().ChangeTracker.Clear();
            var executions = setup.ServiceProvider.GetRequiredService<IAgentExecutionService>();
            var prepared = await executions.PrepareAsync(
                Prepare(workItemId, projectId, repositoryId, "prepare-skill") with
                {
                    ExecutionId = executionId,
                    SkillResolutionId = resolutionId,
                    AllowedActions = []
                },
                CancellationToken.None);
            prepared.Package.SkillSnapshot.Should().NotBeNull();
            prepared.Package.SkillSnapshot!.PinnedVersions.Should().ContainSingle(item => item.SkillVersionId == skillVersionId);
        }

        AgentExecutionClaimResult claim;
        await using (var worker = environment.GetFactory().Services.CreateAsyncScope())
        {
            UseAgentActor(worker.ServiceProvider);
            var executions = worker.ServiceProvider.GetRequiredService<IAgentExecutionService>();
            var missingTool = await executions.ClaimNextAsync(
                Claim(projectId, repositoryId, "worker-without-tool", "claim-skill-missing-tool"), CancellationToken.None);
            missingTool.Outcome.Should().Be("NoEligibleWork");
            claim = await executions.ClaimNextAsync(
                Claim(projectId, repositoryId, "worker-skill", "claim-skill") with { AvailableTools = ["git"] }, CancellationToken.None);
            claim.HasExecution.Should().BeTrue();
        }

        await using (var revoker = environment.GetFactory().Services.CreateAsyncScope())
        {
            UseAgentActor(revoker.ServiceProvider);
            await revoker.ServiceProvider.GetRequiredService<ISkillService>().ChangeLifecycleAsync(new(
                skillVersionId,
                SkillLifecycleStatus.Revoked,
                "Mid-execution security revocation fixture",
                $"revoke-{Guid.NewGuid():N}"), CancellationToken.None);
        }

        await using (var heartbeat = environment.GetFactory().Services.CreateAsyncScope())
        {
            UseAgentActor(heartbeat.ServiceProvider);
            var result = await heartbeat.ServiceProvider.GetRequiredService<IAgentExecutionService>().HeartbeatAsync(new(
                executionId,
                "worker-skill",
                claim.LeaseToken!,
                claim.Execution!.LeaseVersion,
                300,
                "heartbeat-after-revoke",
                CurrentCapabilities: [],
                CurrentTools: ["git"]), CancellationToken.None);
            result.Outcome.Should().Be("BlockedBySkillPolicy");
            result.Execution.Status.Should().Be(AgentExecutionStatus.Blocked);
            result.SkillDecision.Should().Be(SkillExecutionSnapshotDecision.StopRevoked);
            result.SkillIssues.Should().Contain(item => item.SkillVersionId == skillVersionId && item.Reason == SkillRejectionReason.Revoked);
        }

        await using var verify = environment.GetFactory().Services.CreateAsyncScope();
        UseAgentActor(verify.ServiceProvider);
        var db = verify.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await db.ProjectWorkItems.SingleAsync(item => item.Id == workItemId)).Status.Should().Be(ProjectWorkItemStatus.Pending);
        (await db.AgentExecutionEvents.Where(item => item.ExecutionId == executionId).ToListAsync())
            .Should().Contain(item => item.EventType == AgentExecutionEventType.SkillSnapshotRevalidated);
    }

    [DockerRequiredFact]
    public async Task Skill_reresolve_should_block_heartbeat_without_renewing_stale_worker_authority()
    {
        var projectId = $"agent-skill-drift-{Guid.NewGuid():N}";
        var repositoryId = $"repo-skill-drift-{Guid.NewGuid():N}";
        var executionId = Guid.NewGuid();
        var (workItemId, skillVersionId) = await PreparePinnedSkillExecutionAsync(
            projectId,
            repositoryId,
            executionId,
            "Runtime tool drift must fail closed");

        AgentExecutionClaimResult claim;
        await using (var worker = environment.GetFactory().Services.CreateAsyncScope())
        {
            UseAgentActor(worker.ServiceProvider);
            claim = await worker.ServiceProvider.GetRequiredService<IAgentExecutionService>().ClaimNextAsync(
                Claim(projectId, repositoryId, "worker-drift", "claim-skill-drift") with { AvailableTools = ["git"] },
                CancellationToken.None);
            claim.HasExecution.Should().BeTrue();
        }

        await using (var heartbeat = environment.GetFactory().Services.CreateAsyncScope())
        {
            UseAgentActor(heartbeat.ServiceProvider);
            var result = await heartbeat.ServiceProvider.GetRequiredService<IAgentExecutionService>().HeartbeatAsync(new(
                executionId,
                "worker-drift",
                claim.LeaseToken!,
                claim.Execution!.LeaseVersion,
                300,
                "heartbeat-after-tool-drift",
                CurrentCapabilities: [],
                CurrentTools: []), CancellationToken.None);

            result.Outcome.Should().Be("BlockedBySkillPolicy");
            result.Execution.Status.Should().Be(AgentExecutionStatus.Blocked);
            result.Execution.LeaseExpiresAt.Should().BeNull();
            result.Execution.ClaimedByAgentId.Should().BeEmpty();
            result.SkillDecision.Should().Be(SkillExecutionSnapshotDecision.ReResolve);
            result.SkillIssues.Should().Contain(item => item.SkillVersionId == skillVersionId);
        }

        await using var verify = environment.GetFactory().Services.CreateAsyncScope();
        UseAgentActor(verify.ServiceProvider);
        var db = verify.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await db.ProjectWorkItems.SingleAsync(item => item.Id == workItemId)).Status.Should().Be(ProjectWorkItemStatus.Pending);
        var execution = await db.AgentExecutions.SingleAsync(item => item.Id == executionId);
        execution.Status.Should().Be(AgentExecutionStatus.Blocked);
        execution.LeaseTokenHash.Should().BeEmpty();
    }

    [DockerRequiredFact]
    public async Task Queue_with_2001_items_should_apply_priority_and_capability_filters_with_bounded_claim_latency()
    {
        var projectId = $"agent-scale-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        await using (var setup = environment.GetFactory().Services.CreateAsyncScope())
        {
            var actor = UseAgentActor(setup.ServiceProvider);
            var db = setup.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var workItems = Enumerable.Range(0, 2001).Select(index => new ProjectWorkItem
            {
                TenantId = actor.TenantId,
                OwnerUserId = actor.UserId,
                ProjectId = projectId,
                Title = $"Scale item {index}",
                Priority = index == 2000 ? 100 : index % 50,
                CreatedAt = now.AddMilliseconds(index),
                UpdatedAt = now
            }).ToArray();
            await db.ProjectWorkItems.AddRangeAsync(workItems);
            var executions = workItems.Select((item, index) => Execution(
                item,
                projectId,
                index == 0 ? now.AddHours(-101) : now,
                index == 2000 ? 100 : index % 50,
                index is 0 or 2000 ? ["gpu"] : [])).ToArray();
            await db.AgentExecutions.AddRangeAsync(executions);
            await db.SaveChangesAsync();
        }

        await using var worker = environment.GetFactory().Services.CreateAsyncScope();
        UseAgentActor(worker.ServiceProvider);
        var stopwatch = Stopwatch.StartNew();
        var result = await worker.ServiceProvider.GetRequiredService<IAgentExecutionService>().ClaimNextAsync(
            Claim(projectId, "repo-scale", "scale-worker", "scale-claim") with { Capabilities = ["gpu"] }, CancellationToken.None);
        stopwatch.Stop();
        result.HasExecution.Should().BeTrue();
        result.Execution!.Priority.Should().Be(0, "bounded aging must prevent an old low-priority item from starving behind fresh high-priority work");
        result.Execution.Package.RequiredCapabilities.Should().ContainSingle().Which.Should().Be("gpu");
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));

        var verifyDb = worker.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var duplicateOwners = await verifyDb.AgentExecutions
            .Where(x => x.ProjectId == projectId && (x.Status == AgentExecutionStatus.Claimed || x.Status == AgentExecutionStatus.Running))
            .GroupBy(x => x.WorkItemId).Where(x => x.Count() > 1).CountAsync();
        duplicateOwners.Should().Be(0);
    }

    private static AgentExecutionPrepareRequest Prepare(Guid workItemId, string projectId, string repositoryId, string key)
        => new(workItemId, projectId, repositoryId, "Implement the authoritative work item", ["tests pass"], ["work-item"],
            ["do not change business lifecycle"], ["read", "write"], ["focused tests", "full regression"], "Codex", [], 50, 3, null, key);

    private static AgentExecutionClaimRequest Claim(string projectId, string repositoryId, string worker, string key)
        => new(projectId, repositoryId, worker, "Codex", [], 300, key);

    private static async Task<SkillImportResult> ImportAndPublishSkillAsync(ISkillService service)
    {
        var unique = Guid.NewGuid().ToString("N");
        var stableKey = $"agent-exec-{unique}"[..31];
        var name = $"Agent execution integration {unique}";
        var markdown = $"---\nname: {name}\ndescription: Exercise exact AgentExecution runtime pinning\n---\n# Integration\nUse bounded evidence.";
        var preview = new SkillImportPreviewRequest(
            stableKey,
            name,
            "Exercise exact AgentExecution runtime pinning",
            "Use for the AgentExecution integration fixture",
            "1.0.0",
            new PortableSkillBundle([new PortableSkillFile("SKILL.md", Convert.ToBase64String(Encoding.UTF8.GetBytes(markdown)))]),
            SkillSourceKind.Repository,
            $"https://example.test/{stableKey}",
            "commit-1",
            "MIT",
            ["agent-execution"],
            [],
            ["tests"],
            RequiredTools: ["git"],
            AllowedActions: [],
            TrustLevel: SkillTrustLevel.SourceVerified);
        var imported = await service.ImportAsync(new(preview, $"import-{unique}"), CancellationToken.None);
        await service.PublishAsync(new(imported.Version.Id, imported.Version.ContentHash, false, $"publish-{unique}"), CancellationToken.None);
        var current = await service.GetAsync(imported.Skill.Id, CancellationToken.None);
        return imported with { Skill = current!, Version = current!.Versions.Single(item => item.Id == imported.Version.Id) };
    }

    private async Task<(Guid WorkItemId, Guid SkillVersionId)> PreparePinnedSkillExecutionAsync(
        string projectId,
        string repositoryId,
        Guid executionId,
        string title)
    {
        await using var setup = environment.GetFactory().Services.CreateAsyncScope();
        UseAgentActor(setup.ServiceProvider);
        var workItem = await setup.ServiceProvider.GetRequiredService<IProjectWorkItemService>().CreateAsync(new(
            projectId,
            title,
            "AgentExecution Skill policy-drift fixture",
            ["agent-execution", "skills"],
            ["non-Continue revalidation blocks execution"],
            90), CancellationToken.None);

        var skills = setup.ServiceProvider.GetRequiredService<ISkillService>();
        var imported = await ImportAndPublishSkillAsync(skills);
        await skills.ReindexAsync(new(
            "skill-search-v1",
            "deterministic",
            "1",
            0m,
            true,
            true,
            $"reindex-{Guid.NewGuid():N}"), CancellationToken.None);
        var search = await skills.SearchForExecutionAsync(new(
            executionId,
            workItem.Id,
            projectId,
            repositoryId,
            "Codex",
            "Use the exact AgentExecution integration fixture Skill",
            ["agent", "execution", "integration"],
            ["agent-execution"],
            [],
            ["git"],
            [],
            [],
            new SkillSearchPolicy(TopN: 5, Threshold: 0m, MaxSearchRounds: 2, MaxSelectedSkills: 2),
            $"search-{Guid.NewGuid():N}",
            ExplicitSkillVersionId: imported.Version.Id), CancellationToken.None);
        var selected = await skills.SelectAsync(new(
            search.ResolutionId,
            [imported.Version.Id],
            "Exact runtime policy-drift fixture",
            $"select-{Guid.NewGuid():N}"), CancellationToken.None);
        selected.PinnedVersions.Should().ContainSingle(item => item.SkillVersionId == imported.Version.Id);

        var prepared = await setup.ServiceProvider.GetRequiredService<IAgentExecutionService>().PrepareAsync(
            Prepare(workItem.Id, projectId, repositoryId, $"prepare-{Guid.NewGuid():N}") with
            {
                ExecutionId = executionId,
                SkillResolutionId = search.ResolutionId,
                AllowedActions = []
            },
            CancellationToken.None);
        prepared.Package.SkillSnapshot.Should().NotBeNull();
        return (workItem.Id, imported.Version.Id);
    }

    private static AgentExecution Execution(ProjectWorkItem workItem, string projectId, DateTimeOffset now, int priority, string[] capabilities)
    {
        var id = Guid.NewGuid();
        var package = new AgentExecutionPackage(AgentExecutionContract.Version, id, workItem.Id, projectId, "repo-scale", "scale", [], [], [], [], [], "Codex", capabilities, AgentExecutionContract.Version, null);
        var packageJson = JsonSerializer.Serialize(package, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new AgentExecution
        {
            Id = id,
            TenantId = workItem.TenantId,
            OwnerUserId = workItem.OwnerUserId,
            WorkItemId = workItem.Id,
            ProjectId = projectId,
            RepositoryId = "repo-scale",
            AgentType = "Codex",
            RequiredCapabilitiesJson = JsonSerializer.Serialize(capabilities),
            AllowedActionsJson = "[]",
            PackageJson = packageJson,
            PackageHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(packageJson))).ToLowerInvariant(),
            PackageContextVersion = AgentExecutionContract.Version,
            Priority = priority,
            EligibleAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static ContextHubRequestActor UseAgentActor(IServiceProvider services)
    {
        var db = services.GetRequiredService<MemoryDbContext>();
        var user = db.TenantUsers.Include(x => x.Tenant).Single(x => x.Username == "contract-test-admin");
        var actor = new ContextHubRequestActor(user.TenantId, user.Id, user.Username, user.Role,
            [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite, SecurityScopes.SkillsExecute,
             SecurityScopes.SkillsRead, SecurityScopes.SkillsManage, SecurityScopes.SkillsPublish,
             SecurityScopes.SkillsSecurity, SecurityScopes.SkillsBind, SecurityScopes.SkillsReindex,
             SecurityScopes.AgentExecutionsRead, SecurityScopes.AgentExecutionsClaim, SecurityScopes.AgentExecutionsWrite, SecurityScopes.AgentExecutionsManage],
            [], true);
        services.GetRequiredService<IRequestActorAccessor>().Current = actor;
        return actor;
    }
}
