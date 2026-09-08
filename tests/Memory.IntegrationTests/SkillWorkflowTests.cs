using System.Text;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Memory.IntegrationTests;

public sealed class SkillWorkflowTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Registry_search_research_pinning_and_revocation_should_fail_closed()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(scope.ServiceProvider);
        var service = scope.ServiceProvider.GetRequiredService<ISkillService>();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var primary = await ImportAndPublishAsync(service, "incident-triage", "Incident triage", "Triage production incidents and logs", [], "incident-triage");
        var secondary = await ImportAndPublishAsync(service, "log-review", "Log review", "Analyze bounded application log evidence", [], "log-review");

        var reindex = await service.ReindexAsync(new("skill-search-v1", "deterministic", "1", 0m, true, true, $"reindex-{Guid.NewGuid():N}"), CancellationToken.None);
        reindex.Activated.Should().BeTrue();
        reindex.IndexedVersionCount.Should().BeGreaterThanOrEqualTo(2);

        var executionId = Guid.NewGuid();
        var first = await SearchAsync(service, executionId, [], [], "search-first");
        first.Candidates.Should().Contain(x => x.SkillVersionId == primary.Version.Id);
        first.Candidates.Should().Contain(x => x.SkillVersionId == secondary.Version.Id);

        var rejected = await service.RecordFeedbackAsync(new(
            first.ResolutionId, primary.Version.Id, SkillRejectionStage.SearchCandidateRejected,
            SkillRejectionReason.IncorrectTagOrTrigger, "Prefer direct log evidence", ["test:evidence"], [], ["logs"], ["logs"],
            $"feedback-{Guid.NewGuid():N}"), CancellationToken.None);
        rejected.ReSearchAllowed.Should().BeTrue();

        var alternative = await SearchAsync(service, executionId, [primary.Version.Id], [], "search-alternative");
        alternative.Round.Should().Be(2);
        alternative.Candidates.Should().ContainSingle(x => x.SkillVersionId == secondary.Version.Id);
        db.ChangeTracker.Clear(); // HTTP/MCP calls use a fresh request scope.
        var selected = await service.SelectAsync(new(alternative.ResolutionId, [secondary.Version.Id], "Best available evidence match", $"select-{Guid.NewGuid():N}"), CancellationToken.None);
        selected.PinnedVersions.Should().ContainSingle();
        selected.PinnedVersions[0].ContentHash.Should().Be(secondary.Version.ContentHash);

        db.ChangeTracker.Clear();
        var materialized = await service.MaterializeAsync(new(executionId, alternative.ResolutionId, secondary.Version.Id,
            secondary.Version.ContentHash, $"materialize-{Guid.NewGuid():N}"), CancellationToken.None);
        materialized.Status.Should().Be(SkillMaterializationStatus.Active);

        db.ChangeTracker.Clear();
        await service.ChangeLifecycleAsync(new(secondary.Version.Id, SkillLifecycleStatus.Revoked, "Security revocation fixture", $"revoke-{Guid.NewGuid():N}"), CancellationToken.None);
        db.ChangeTracker.Clear();
        (await db.SkillMaterializations.SingleAsync(x => x.Id == materialized.MaterializationId)).Status.Should().Be(SkillMaterializationStatus.Revoked);
        var staleIndexSearch = await SearchAsync(service, Guid.NewGuid(), [], [], "search-after-revoke");
        staleIndexSearch.Candidates.Should().NotContain(x => x.SkillVersionId == secondary.Version.Id);
        var getPinned = () => service.GetPinnedVersionAsync(new(executionId, alternative.ResolutionId, secondary.Version.Id, secondary.Version.ContentHash), CancellationToken.None);
        await getPinned.Should().ThrowAsync<UnauthorizedAccessException>();
        var cleanupKey = $"cleanup-{Guid.NewGuid():N}";
        var cleanup = await service.CleanupMaterializationsAsync(new(executionId, "execution completed", cleanupKey), CancellationToken.None);
        cleanup.CleanedCount.Should().Be(1);
        var cleanupReplay = await service.CleanupMaterializationsAsync(new(executionId, "execution completed", cleanupKey), CancellationToken.None);
        cleanupReplay.Replayed.Should().BeTrue();
        cleanupReplay.MaterializationIds.Should().BeEquivalentTo(cleanup.MaterializationIds);
    }

    [DockerRequiredFact]
    public async Task Required_dependency_should_be_pinned_with_multi_skill_selection()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(scope.ServiceProvider);
        var service = scope.ServiceProvider.GetRequiredService<ISkillService>();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var dependency = await ImportAndPublishAsync(service, $"base-{Guid.NewGuid():N}"[..26], "Base analysis", "Common analysis primitives", [], "base-analysis");
        var dependent = await ImportAndPublishAsync(service, $"parent-{Guid.NewGuid():N}"[..28], "Parent analysis", "Orchestrates common analysis primitives",
            [new SkillDependencyInput(dependency.Skill.Id, SkillDependencyKind.Requires, $"={dependency.Version.Version}")], "parent-analysis");
        await service.ReindexAsync(new("skill-search-v1", "deterministic", "1", 0m, true, true, $"reindex-{Guid.NewGuid():N}"), CancellationToken.None);

        var search = await SearchAsync(service, Guid.NewGuid(), [], [], "dependency-search");
        db.ChangeTracker.Clear(); // HTTP/MCP calls use a fresh request scope.
        var selected = await service.SelectAsync(new(search.ResolutionId, [dependent.Version.Id], "Required dependency closure", $"select-{Guid.NewGuid():N}"), CancellationToken.None);

        selected.PinnedVersions.Should().Contain(x => x.SkillVersionId == dependent.Version.Id && !x.IsDependency);
        selected.PinnedVersions.Should().Contain(x => x.SkillVersionId == dependency.Version.Id && x.IsDependency);
    }

    [DockerRequiredFact]
    public async Task Conflicting_skills_should_be_rejected_before_exact_version_pinning()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(scope.ServiceProvider);
        var service = scope.ServiceProvider.GetRequiredService<ISkillService>();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var baseSkill = await ImportAndPublishAsync(service, $"conflict-base-{Guid.NewGuid():N}"[..30], "Conflict base", "Mutually exclusive base workflow", [], "conflict-base");
        var conflicting = await ImportAndPublishAsync(service, $"conflict-peer-{Guid.NewGuid():N}"[..30], "Conflict peer", "Mutually exclusive peer workflow",
            [new SkillDependencyInput(baseSkill.Skill.Id, SkillDependencyKind.ConflictsWith, "*")], "conflict-peer");
        await service.ReindexAsync(new("skill-search-v1", "deterministic", "1", 0m, true, true, $"reindex-{Guid.NewGuid():N}"), CancellationToken.None);

        var search = await SearchAsync(service, Guid.NewGuid(), [], [], "conflict-search");
        db.ChangeTracker.Clear();
        var select = () => service.SelectAsync(new(
            search.ResolutionId,
            [baseSkill.Version.Id, conflicting.Version.Id],
            "Conflict fixture",
            $"select-{Guid.NewGuid():N}"), CancellationToken.None);

        await select.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("SkillSetConflict:*");
    }

    [DockerRequiredFact]
    public async Task Metadata_governance_should_converge_to_a_hash_pinned_pending_proposal_exception()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(scope.ServiceProvider);
        var service = scope.ServiceProvider.GetRequiredService<ISkillService>();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var imported = await ImportAndPublishAsync(service, $"govern-{Guid.NewGuid():N}"[..28], "Governance target", "Metadata quality fixture", [], "metadata-governance");
        var actor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>().Current;
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < 40; index++)
        {
            db.SkillTelemetryEvents.Add(Telemetry(imported, actor, SkillTelemetryEventType.SearchImpression, null, index, now));
            if (index < 32)
            {
                db.SkillTelemetryEvents.Add(Telemetry(imported, actor, SkillTelemetryEventType.Rejected,
                    SkillRejectionReason.IncorrectTagOrTrigger, index + 100, now));
            }
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var review = await service.ReviewMetadataGovernanceAsync(new(), CancellationToken.None);
        review.Findings.Should().ContainSingle(x => x.SkillId == imported.Skill.Id);
        var finding = review.Findings.Single(x => x.SkillId == imported.Skill.Id);
        db.SkillMetadataProposals.Add(new SkillMetadataProposal
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            SkillId = imported.Skill.Id,
            ExpectedMetadataVersion = imported.Skill.MetadataVersion,
            ExpectedMetadataHash = finding.CurrentMetadataHash,
            ProposedPatchJson = "{}",
            EvidenceJson = "{}",
            Confidence = finding.Confidence,
            Status = SkillMetadataProposalStatus.Pending,
            GovernanceRunId = $"run-{Guid.NewGuid():N}",
            IdempotencyKey = $"proposal-{Guid.NewGuid():N}",
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var rereview = await service.ReviewMetadataGovernanceAsync(new(), CancellationToken.None);
        rereview.Findings.Should().NotContain(x => x.SkillId == imported.Skill.Id);
        rereview.ExceptionCount.Should().BeGreaterThanOrEqualTo(1);

        var pending = (await service.ListMetadataProposalsAsync(SkillMetadataProposalStatus.Pending, CancellationToken.None))
            .Single(x => x.SkillId == imported.Skill.Id);
        var stale = await service.DecideMetadataProposalAsync(new(
            pending.Id, true, pending.ExpectedMetadataVersion + 1, pending.ExpectedMetadataHash,
            Description: "must not apply stale metadata"), CancellationToken.None);
        stale.Status.Should().Be(SkillMetadataProposalStatus.Stale);
        (await service.GetAsync(imported.Skill.Id, CancellationToken.None))!.Description.Should().Be(imported.Skill.Description);
    }

    [DockerRequiredFact]
    public async Task Skill_registry_should_enforce_explicit_read_and_management_scopes()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(scope.ServiceProvider);
        var accessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        var owner = accessor.Current;
        var service = scope.ServiceProvider.GetRequiredService<ISkillService>();
        accessor.Current = owner with { Role = TenantUserRole.Member, Scopes = [], IsAuthenticated = true };

        var read = () => service.ListAsync("ContextHub", false, CancellationToken.None);
        await read.Should().ThrowAsync<UnauthorizedAccessException>();
        var import = () => service.ImportAsync(new(new(
            "denied", "Denied", "Denied scope", "Use never", "1.0.0",
            new PortableSkillBundle([]), SkillSourceKind.LocalUpload, "tests", "1", "MIT"), "denied-import-key"), CancellationToken.None);
        await import.Should().ThrowAsync<UnauthorizedAccessException>();

        accessor.Current = accessor.Current with { Scopes = [SecurityScopes.SkillsRead] };
        (await service.ListAsync("ContextHub", false, CancellationToken.None)).Should().NotBeNull();
    }

    [DockerRequiredFact]
    public async Task Default_version_should_support_optimistic_rollback_between_published_versions()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(scope.ServiceProvider);
        var service = scope.ServiceProvider.GetRequiredService<ISkillService>();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var stableKey = $"rollback-{Guid.NewGuid():N}"[..30];
        var first = await ImportAndPublishAsync(service, stableKey, "Rollback Skill", "Version one", [], "rollback-v1", "1.0.0");
        db.ChangeTracker.Clear();
        var second = await ImportAndPublishAsync(service, stableKey, "Rollback Skill", "Version two", [], "rollback-v2", "2.0.0");
        var before = (await service.GetAsync(first.Skill.Id, CancellationToken.None))!;
        before.DefaultVersionId.Should().Be(first.Version.Id);

        db.ChangeTracker.Clear();
        var promoted = await service.SetDefaultVersionAsync(new(first.Skill.Id, second.Version.Id, before.MetadataVersion, $"default-{Guid.NewGuid():N}"), CancellationToken.None);
        db.ChangeTracker.Clear();
        var rolledBack = await service.SetDefaultVersionAsync(new(first.Skill.Id, first.Version.Id, promoted.MetadataVersion, $"rollback-{Guid.NewGuid():N}"), CancellationToken.None);

        rolledBack.DefaultVersionId.Should().Be(first.Version.Id);
        rolledBack.MetadataVersion.Should().Be(promoted.MetadataVersion + 1);
    }

    private static Task<SkillSearchForExecutionResult> SearchAsync(ISkillService service, Guid executionId, IReadOnlyList<Guid> excluded, IReadOnlyList<Guid> selected, string prefix)
        => service.SearchForExecutionAsync(new(
            executionId, null, "ContextHub", "ContextHub", "Codex", "incident log analysis primitives",
            ["incident", "log", "analysis"], [], [], [], [], excluded,
            new SkillSearchPolicy(TopN: 20, Threshold: 0m, MaxSearchRounds: 3, MaxSelectedSkills: 10),
            $"{prefix}-{Guid.NewGuid():N}"), CancellationToken.None);

    private static async Task<SkillImportResult> ImportAndPublishAsync(
        ISkillService service,
        string stableKey,
        string name,
        string description,
        IReadOnlyList<SkillDependencyInput> dependencies,
        string unique,
        string version = "1.0.0")
    {
        var markdown = $"---\nname: {name}\ndescription: {description}\n---\n# {name}\nUse bounded evidence.";
        var preview = new SkillImportPreviewRequest(
            stableKey, name, description, $"Use when {description.ToLowerInvariant()}", version,
            new PortableSkillBundle([new PortableSkillFile("SKILL.md", Convert.ToBase64String(Encoding.UTF8.GetBytes(markdown)))]),
            SkillSourceKind.Repository, $"https://example.test/{unique}", "commit-1", "MIT", ["incident", "log", "analysis"], [], ["tests"],
            Dependencies: dependencies, TrustLevel: SkillTrustLevel.SourceVerified);
        var imported = await service.ImportAsync(new(preview, $"import-{unique}-{Guid.NewGuid():N}"), CancellationToken.None);
        await service.PublishAsync(new(imported.Version.Id, imported.Version.ContentHash, false, $"publish-{unique}-{Guid.NewGuid():N}"), CancellationToken.None);
        var current = await service.GetAsync(imported.Skill.Id, CancellationToken.None);
        return imported with { Skill = current!, Version = current!.Versions.Single(x => x.Id == imported.Version.Id) };
    }

    private static SkillTelemetryEvent Telemetry(
        SkillImportResult imported,
        ContextHubRequestActor actor,
        SkillTelemetryEventType eventType,
        SkillRejectionReason? reason,
        int index,
        DateTimeOffset now) => new()
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            SkillId = imported.Skill.Id,
            SkillVersionId = imported.Version.Id,
            ExecutionId = Guid.NewGuid(),
            ResolutionRound = 1,
            EventType = eventType,
            RejectionStage = reason.HasValue ? SkillRejectionStage.SearchCandidateRejected : null,
            ReasonClass = reason,
            ProjectId = "ContextHub",
            RepositoryId = "ContextHub",
            AgentType = "tests",
            IdempotencyKey = $"metadata-{eventType}-{index}-{Guid.NewGuid():N}",
            OccurredAt = now,
            CreatedAt = now
        };

    private static void UseBootstrapActor(IServiceProvider services)
    {
        var db = services.GetRequiredService<MemoryDbContext>();
        var user = db.TenantUsers.Include(x => x.Tenant).Single(x => x.Username == "contract-test-admin");
        services.GetRequiredService<IRequestActorAccessor>().Current = new ContextHubRequestActor(
            user.TenantId, user.Id, user.Username, user.Role,
            [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite, SecurityScopes.SkillsRead, SecurityScopes.SkillsExecute,
             SecurityScopes.SkillsManage, SecurityScopes.SkillsPublish, SecurityScopes.SkillsSecurity, SecurityScopes.SkillsBind, SecurityScopes.SkillsReindex],
            [], true);
    }
}
