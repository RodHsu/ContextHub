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
        var audit = (await service.ListResolutionsAsync("ContextHub", 20, CancellationToken.None))
            .Single(item => item.ResolutionId == alternative.ResolutionId);
        audit.Candidates.Should().ContainSingle(item => item.SkillVersionId == secondary.Version.Id && item.Pinned && !item.Released);
        audit.Events.Should().Contain(item => item.EventType == SkillTelemetryEventType.SearchImpression);
        audit.Events.Should().Contain(item => item.EventType == SkillTelemetryEventType.Selected);

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
    public async Task Metadata_governance_should_classify_quality_fixtures_and_exclude_runtime_and_policy_events()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(scope.ServiceProvider);
        var service = scope.ServiceProvider.GetRequiredService<ISkillService>();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var actor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>().Current;
        var fixtures = new[]
        {
            (Key: "tag", Reason: SkillRejectionReason.IncorrectTagOrTrigger, Stage: SkillRejectionStage.SearchCandidateRejected, Expected: "TagMismatchSignal", Included: true),
            (Key: "broad", Reason: SkillRejectionReason.LowConfidenceMatch, Stage: SkillRejectionStage.SearchCandidateRejected, Expected: "DescriptionOverbreadthSignal", Included: true),
            (Key: "compat", Reason: SkillRejectionReason.IncorrectCompatibilityMetadata, Stage: SkillRejectionStage.MaterializedRejectedBeforeInvoke, Expected: "CompatibilityMismatchSignal", Included: true),
            (Key: "duplicate", Reason: SkillRejectionReason.DuplicateCoverage, Stage: SkillRejectionStage.PinnedReleasedBeforeMaterialize, Expected: "DuplicateSkillSignal", Included: true),
            (Key: "instruction", Reason: SkillRejectionReason.InstructionMismatch, Stage: SkillRejectionStage.MaterializedRejectedBeforeInvoke, Expected: "InstructionMismatchSignal", Included: true),
            (Key: "runtime", Reason: SkillRejectionReason.ToolUnavailable, Stage: SkillRejectionStage.InvocationAborted, Expected: string.Empty, Included: false),
            (Key: "policy", Reason: SkillRejectionReason.Revoked, Stage: SkillRejectionStage.RevokedOrPolicyCancelled, Expected: string.Empty, Included: false)
        };
        var imported = new Dictionary<string, SkillImportResult>(StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow;
        foreach (var fixture in fixtures)
        {
            var skill = await ImportAndPublishAsync(service, $"fixture-{fixture.Key}-{Guid.NewGuid():N}"[..30], $"Fixture {fixture.Key}", $"Governance {fixture.Key} fixture", [], $"fixture-{fixture.Key}");
            imported[fixture.Key] = skill;
            for (var index = 0; index < 40; index++)
            {
                db.SkillTelemetryEvents.Add(Telemetry(skill, actor, SkillTelemetryEventType.SearchImpression, null, index + fixture.Key.GetHashCode(StringComparison.Ordinal), now));
                if (index < 32)
                {
                    var rejection = Telemetry(skill, actor, SkillTelemetryEventType.Rejected, fixture.Reason, index + 1000 + fixture.Key.GetHashCode(StringComparison.Ordinal), now);
                    rejection.RejectionStage = fixture.Stage;
                    db.SkillTelemetryEvents.Add(rejection);
                }
            }
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var review = await service.ReviewMetadataGovernanceAsync(new(), CancellationToken.None);
        foreach (var fixture in fixtures.Where(item => item.Included))
        {
            review.Findings.Should().Contain(item => item.SkillId == imported[fixture.Key].Skill.Id && item.SignalType == fixture.Expected);
        }
        foreach (var fixture in fixtures.Where(item => !item.Included))
        {
            review.Findings.Should().NotContain(item => item.SkillId == imported[fixture.Key].Skill.Id);
        }
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
    public async Task Skill_management_scopes_should_separate_author_publish_bind_reindex_and_security_authority()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(scope.ServiceProvider);
        var accessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        var owner = accessor.Current;
        var service = scope.ServiceProvider.GetRequiredService<ISkillService>();
        var stableKey = $"roles-{Guid.NewGuid():N}"[..28];
        var markdown = "---\nname: Role matrix\ndescription: Role matrix fixture\n---\n# Role matrix";
        var preview = new SkillImportPreviewRequest(
            stableKey, "Role matrix", "Role matrix fixture", "Use for authorization regression", "1.0.0",
            new PortableSkillBundle([new PortableSkillFile("SKILL.md", Convert.ToBase64String(Encoding.UTF8.GetBytes(markdown)))]),
            SkillSourceKind.Repository, $"https://example.test/{stableKey}", "commit-1", "MIT",
            RiskLevel: SkillRiskLevel.High, TrustLevel: SkillTrustLevel.SourceVerified);

        accessor.Current = owner with { Role = TenantUserRole.Member, Scopes = [SecurityScopes.SkillsManage] };
        var imported = await service.ImportAsync(new(preview, $"import-{Guid.NewGuid():N}"), CancellationToken.None);
        var publishWithoutScope = () => service.PublishAsync(new(imported.Version.Id, imported.Version.ContentHash, true, $"publish-{Guid.NewGuid():N}"), CancellationToken.None);
        await publishWithoutScope.Should().ThrowAsync<UnauthorizedAccessException>();

        accessor.Current = accessor.Current with { Scopes = [SecurityScopes.SkillsPublish] };
        var publishWithoutApproval = () => service.PublishAsync(new(imported.Version.Id, imported.Version.ContentHash, false, $"publish-{Guid.NewGuid():N}"), CancellationToken.None);
        await publishWithoutApproval.Should().ThrowAsync<InvalidOperationException>().WithMessage("*explicit publish approval*");
        await service.PublishAsync(new(imported.Version.Id, imported.Version.ContentHash, true, $"publish-{Guid.NewGuid():N}"), CancellationToken.None);

        accessor.Current = accessor.Current with { Scopes = [SecurityScopes.SkillsBind] };
        var binding = await service.UpsertBindingAsync(new(imported.Skill.Id, SkillBindingScope.Project, "ContextHub", SkillBindingMode.Recommended, "=1.0.0", null, $"bind-{Guid.NewGuid():N}"), CancellationToken.None);
        binding.Scope.Should().Be(SkillBindingScope.Project);

        accessor.Current = accessor.Current with { Scopes = [SecurityScopes.SkillsReindex] };
        (await service.ReindexAsync(new("roles-v1", "deterministic", "1", 0m, true, true, $"reindex-{Guid.NewGuid():N}"), CancellationToken.None)).Activated.Should().BeTrue();

        accessor.Current = accessor.Current with { Scopes = [SecurityScopes.SkillsSecurity] };
        (await service.ChangeLifecycleAsync(new(imported.Version.Id, SkillLifecycleStatus.Revoked, "Security scope fixture", $"revoke-{Guid.NewGuid():N}"), CancellationToken.None)).Status.Should().Be(SkillLifecycleStatus.Revoked);

        accessor.Current = accessor.Current with { Scopes = [SecurityScopes.SkillsExecute] };
        var unauthorizedBinding = () => service.UpsertBindingAsync(new(imported.Skill.Id, SkillBindingScope.Project, "ContextHub", SkillBindingMode.Disabled, "*", binding.Revision, $"bind-{Guid.NewGuid():N}"), CancellationToken.None);
        await unauthorizedBinding.Should().ThrowAsync<UnauthorizedAccessException>();
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

    [DockerRequiredFact]
    public async Task Search_generation_should_support_atomic_validated_rollback_and_replay()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(scope.ServiceProvider);
        var service = scope.ServiceProvider.GetRequiredService<ISkillService>();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        await ImportAndPublishAsync(service, $"index-{Guid.NewGuid():N}"[..28], "Index rollback", "Search generation rollback fixture", [], "index-rollback");
        var first = await service.ReindexAsync(new("skill-search-v1", "deterministic", "1", 0m, true, true, $"reindex-{Guid.NewGuid():N}"), CancellationToken.None);
        var second = await service.ReindexAsync(new("skill-search-v2", "deterministic", "2", 0m, true, true, $"reindex-{Guid.NewGuid():N}"), CancellationToken.None);
        second.GenerationId.Should().NotBe(first.GenerationId);
        db.ChangeTracker.Clear();

        var request = new SkillSearchGenerationActivateRequest(first.GenerationId, "Rollback after shadow benchmark regression", $"activate-{Guid.NewGuid():N}");
        var rollback = await service.ActivateSearchGenerationAsync(request, CancellationToken.None);
        db.ChangeTracker.Clear();
        var replay = await service.ActivateSearchGenerationAsync(request, CancellationToken.None);

        rollback.Activated.Should().BeTrue();
        rollback.Replayed.Should().BeFalse();
        replay.Replayed.Should().BeTrue();
        (await db.SkillSearchGenerations.AsNoTracking().SingleAsync(item => item.Id == first.GenerationId)).Status.Should().Be(SkillSearchGenerationStatus.Active);
        (await db.SkillSearchGenerations.AsNoTracking().SingleAsync(item => item.Id == second.GenerationId)).Status.Should().Be(SkillSearchGenerationStatus.RolledBack);
        (await service.ListSearchGenerationsAsync(CancellationToken.None)).Should().Contain(item => item.GenerationId == first.GenerationId && item.Status == SkillSearchGenerationStatus.Active);
    }

    [DockerRequiredFact]
    public async Task External_source_observation_should_detect_drift_without_mutating_published_content()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(scope.ServiceProvider);
        var service = scope.ServiceProvider.GetRequiredService<ISkillService>();
        var imported = await ImportAndPublishAsync(service, $"source-{Guid.NewGuid():N}"[..29], "Source drift", "External source drift fixture", [], "source-drift");
        var request = new SkillSourceObservationRequest(
            imported.Skill.Id,
            imported.Version.SourceRef,
            "commit-2",
            new string('a', 64),
            SourceAvailable: true,
            SignatureVerified: false,
            CompromiseReported: false,
            EvidenceRef: "provider-refresh:test",
            IdempotencyKey: $"source-{Guid.NewGuid():N}");

        var drift = await service.RecordSourceObservationAsync(request, CancellationToken.None);
        var replay = await service.RecordSourceObservationAsync(request, CancellationToken.None);
        var current = await service.GetAsync(imported.Skill.Id, CancellationToken.None);

        drift.Status.Should().Be(SkillSourceDriftStatus.Changed);
        drift.RequiresNewDraft.Should().BeTrue();
        replay.Replayed.Should().BeTrue();
        current!.Versions.Single(item => item.Id == imported.Version.Id).ContentHash.Should().Be(imported.Version.ContentHash);
        current.Versions.Single(item => item.Id == imported.Version.Id).Status.Should().Be(SkillLifecycleStatus.Published);
        (await service.ListSourceObservationsAsync(imported.Skill.Id, CancellationToken.None)).Should().ContainSingle(item => item.ObservationId == drift.ObservationId);
    }

    [DockerRequiredFact]
    public async Task Telemetry_reconciliation_should_be_idempotent_retain_aggregates_and_protect_revocation_evidence()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(scope.ServiceProvider);
        var service = scope.ServiceProvider.GetRequiredService<ISkillService>();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var actor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>().Current;
        var imported = await ImportAndPublishAsync(service, $"telemetry-{Guid.NewGuid():N}"[..30], "Telemetry retention", "Telemetry reconciliation fixture", [], "telemetry-retention");
        var old = DateTimeOffset.UtcNow.AddDays(-100);
        var impression = Telemetry(imported, actor, SkillTelemetryEventType.SearchImpression, null, 501, old);
        var rejection = Telemetry(imported, actor, SkillTelemetryEventType.Rejected, SkillRejectionReason.NotApplicable, 502, old);
        var revocation = Telemetry(imported, actor, SkillTelemetryEventType.Cancellation, SkillRejectionReason.Revoked, 503, old);
        revocation.RejectionStage = SkillRejectionStage.RevokedOrPolicyCancelled;
        db.SkillTelemetryEvents.AddRange(impression, rejection, revocation);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var request = new SkillTelemetryReconciliationRequest(90, 365, $"reconcile-{Guid.NewGuid():N}");

        var first = await service.ReconcileTelemetryAsync(request, CancellationToken.None);
        db.ChangeTracker.Clear();
        var replay = await service.ReconcileTelemetryAsync(request, CancellationToken.None);
        var analytics = await service.GetAnalyticsAsync(new(SkillId: imported.Skill.Id, WindowDays: 365, Dimension: SkillAnalyticsDimension.SkillVersion), CancellationToken.None);

        first.AggregatedEventCount.Should().BeGreaterThanOrEqualTo(3);
        first.DeletedRawEventCount.Should().Be(2);
        first.ProtectedRawEventCount.Should().Be(1);
        replay.Replayed.Should().BeTrue();
        replay.RunId.Should().Be(first.RunId);
        analytics.Should().ContainSingle(item => item.SkillVersionId == imported.Version.Id && item.SearchImpressionCount == 1 && item.RejectedCount == 1);
        (await db.SkillTelemetryEvents.AsNoTracking().CountAsync(item => item.SkillId == imported.Skill.Id)).Should().Be(1);
        (await db.SkillTelemetryDailyAggregates.AsNoTracking().SumAsync(item => item.SkillId == imported.Skill.Id ? item.EventCount : 0)).Should().Be(3);
    }

    [DockerRequiredFact]
    public async Task Concurrent_telemetry_reconciliation_should_serialize_to_one_exact_run()
    {
        using var firstScope = environment.GetFactory().Services.CreateScope();
        using var secondScope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(firstScope.ServiceProvider);
        UseBootstrapActor(secondScope.ServiceProvider);
        var firstService = firstScope.ServiceProvider.GetRequiredService<ISkillService>();
        var secondService = secondScope.ServiceProvider.GetRequiredService<ISkillService>();
        var key = $"reconcile-race-{Guid.NewGuid():N}";

        var results = await Task.WhenAll(
            firstService.ReconcileTelemetryAsync(new(90, 365, key), CancellationToken.None),
            secondService.ReconcileTelemetryAsync(new(90, 365, key), CancellationToken.None));

        results.Select(item => item.RunId).Distinct().Should().ContainSingle();
        results.Count(item => item.Replayed).Should().Be(1);
        results.Count(item => !item.Replayed).Should().Be(1);
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
