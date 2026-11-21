using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Memory.IntegrationTests;

public sealed class ConversationAdjudicationTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Receipt_collision_should_roll_back_the_disposition_and_audit_atomically()
    {
        using var scope = CreateScope(out var db, out var service, out var actor);
        var first = await SeedAsync(db, actor);
        var firstRequest = Request(first, (await Preview(service, first)).Items.Single(), ConversationAdjudicationAction.ResolveSkipped);
        await service.AdjudicateAsync(firstRequest, default);
        var second = await SeedAsync(db, actor);
        db.ChangeTracker.Clear();
        var request = Request(second, (await Preview(service, second)).Items.Single(), ConversationAdjudicationAction.ResolveSkipped)
            with
        { OperationId = firstRequest.OperationId };
        await FluentActions.Invoking(() => service.AdjudicateAsync(request, default)).Should().ThrowAsync<DbUpdateException>();
        (await db.ConversationInsights.AsNoTracking().SingleAsync(x => x.Id == second.Id)).PromotionStatus
            .Should().Be(ConversationPromotionStatus.RequiresUserDecision);
        (await db.ConversationAdjudications.CountAsync(x => x.ProjectId == second.ProjectId)).Should().Be(0);
        var audits = await db.SecurityAuditEvents.AsNoTracking().Where(x =>
            x.TenantId == actor.TenantId && x.EventType == SecurityAuditEventType.ConversationInsightGovernanceUpdated).ToArrayAsync();
        audits.Should().NotContain(x => x.DetailsJson.Contains(second.Id.ToString()));
    }

    [DockerRequiredFact]
    public async Task Concurrent_replays_should_commit_only_one_receipt_and_stale_competing_decision_should_fail()
    {
        using var seedScope = CreateScope(out var db, out var service, out var actor);
        var row = await SeedAsync(db, actor);
        var candidate = (await Preview(service, row)).Items.Single();
        var request = Request(row, candidate, ConversationAdjudicationAction.ResolveSkipped);
        using var first = CreateScope(out _, out var firstService, out _);
        using var second = CreateScope(out _, out var secondService, out _);
        async Task<ConversationAdjudicationReceipt?> Attempt(IConversationAdjudicationService target)
        {
            try { return await target.AdjudicateAsync(request, default); }
            catch (Exception ex) when (ex.GetBaseException() is Npgsql.PostgresException { SqlState: "40001" or "23505" }) { return null; }
        }
        var results = await Task.WhenAll(Attempt(firstService), Attempt(secondService));
        results.Count(x => x is { IsReplay: false }).Should().Be(1);
        (await db.ConversationAdjudications.CountAsync(x => x.InsightId == row.Id)).Should().Be(1);
        using var retry = CreateScope(out _, out var retryService, out _);
        (await retryService.AdjudicateAsync(request, default)).IsReplay.Should().BeTrue();
        await FluentActions.Invoking(() => retryService.AdjudicateAsync(request with { OperationId = Guid.NewGuid() }, default))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [DockerRequiredFact]
    public async Task Rejected_proposal_reconciliation_should_require_an_exact_scoped_link()
    {
        using var scope = CreateScope(out var db, out var service, out var actor);
        var row = await SeedAsync(db, actor);
        var proposal = new ConversationInsight
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = row.ProjectId,
            SessionId = row.SessionId,
            CheckpointId = row.CheckpointId,
            SourceSystem = "chatgpt-mcp-gateway",
            Tags = ["chatgpt-proposal"],
            DedupKey = Guid.NewGuid().ToString(),
            PromotionStatus = ConversationPromotionStatus.Skipped,
            CreatedAt = row.CreatedAt,
            UpdatedAt = row.UpdatedAt
        };
        db.ConversationInsights.Add(proposal);
        row.MetadataJson = JsonSerializer.Serialize(new { proposalId = proposal.Id });
        await db.SaveChangesAsync();
        (await Preview(service, row)).Items.Single().CanReconcile.Should().BeFalse();
        row.SourceRef = $"chatgpt-proposal:{proposal.Id:D}";
        await db.SaveChangesAsync();
        var candidate = (await Preview(service, row)).Items.Single();
        candidate.Classification.Should().Be("ResolvedRejectedProposal");
        (await service.AdjudicateAsync(Request(row, candidate, ConversationAdjudicationAction.Reconcile), default)).Outcome
            .Should().Be("ResolvedRejectedProposal");
        proposal.PromotionStatus.Should().Be(ConversationPromotionStatus.Skipped);
    }

    [DockerRequiredFact]
    public async Task Resolution_replay_reopen_and_ledger_should_preserve_audit_and_require_changed_evidence()
    {
        using var scope = CreateScope(out var db, out var service, out var actor);
        var row = await SeedAsync(db, actor);
        var preview = await Preview(service, row);
        var request = Request(row, preview.Items.Single(), ConversationAdjudicationAction.ResolveSkipped);
        var alias = row.ProjectId.ToUpperInvariant();
        (await service.PreviewAsync(new(alias), default)).Items.Single().EvidenceToken.Should().Be(request.ExpectedEvidenceToken);
        var receipt = await service.AdjudicateAsync(request with { ProjectId = alias }, default);
        receipt.ProjectId.Should().Be(row.ProjectId);
        receipt.ResultStatus.Should().Be("Skipped");
        (await service.AdjudicateAsync(request, default)).IsReplay.Should().BeTrue();
        (await db.ConversationAdjudications.CountAsync(x => x.InsightId == row.Id)).Should().Be(1);
        (await Preview(service, row)).ResolvedHumanDecisionCount.Should().Be(1);
        (await Preview(service, row)).UnresolvedHumanDecisionCount.Should().Be(0);
        await FluentActions.Invoking(() => service.AdjudicateAsync(request with { Reason = "different" }, default))
            .Should().ThrowAsync<InvalidOperationException>();
        var terminal = (await Preview(service, row)).Items.Single();
        terminal.CanReopen.Should().BeFalse();
        await FluentActions.Invoking(() => service.AdjudicateAsync(Request(row, terminal, ConversationAdjudicationAction.Reopen), default))
            .Should().ThrowAsync<InvalidOperationException>();
        row = await db.ConversationInsights.SingleAsync(x => x.Id == row.Id);
        row.Content = "Materially changed evidence";
        await db.SaveChangesAsync();
        var changed = (await Preview(service, row)).Items.Single();
        changed.CanReopen.Should().BeTrue();
        (await service.AdjudicateAsync(Request(row, changed, ConversationAdjudicationAction.Reopen), default)).ResultStatus
            .Should().Be("RequiresUserDecision");
        (await Preview(service, row)).UnresolvedHumanDecisionCount.Should().Be(1);
        var counters = await service.PreviewAsync(new(row.ProjectId, GovernanceRunId: "test-adjudication"), default);
        counters.NewlyResolvedCount.Should().Be(1);
        counters.ReopenedCount.Should().Be(1);
        counters.UnchangedCount.Should().Be(0);
        (await service.PreviewAsync(new(row.ProjectId, GovernanceRunId: "another-run"), default)).UnchangedCount.Should().Be(1);
        await db.ConversationInsights.Where(x => x.Id == row.Id).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.GovernanceEvidenceFingerprint, "previous-evidence")
            .SetProperty(x => x.GovernancePolicyVersion, "previous-policy"));
        var emptyCoverage = new KnowledgeGovernanceCoverageResult(Guid.NewGuid(), "empty", DateTimeOffset.UtcNow,
            0, 0, 0, 0, 0, 0, true, false, null);
        var plan = await scope.ServiceProvider.GetRequiredService<IFullGovernancePlanService>().BuildAsync(
            [row.ProjectId], "test-adjudication", new(emptyCoverage, [], []), default);
        plan.ResolvedHumanDecisionCount.Should().Be(0);
        plan.NewlyResolvedHumanDecisionCount.Should().Be(1);
        plan.ReopenedHumanDecisionCount.Should().Be(1);
        (await Preview(service, row)).Items.Single().Status.Should().Be(ConversationPromotionStatus.RequiresUserDecision);
        var audits = await db.SecurityAuditEvents.AsNoTracking().Where(x =>
            x.TenantId == actor.TenantId && x.EventType == SecurityAuditEventType.ConversationInsightGovernanceUpdated).ToArrayAsync();
        audits.Count(x => x.DetailsJson.Contains(row.Id.ToString())).Should().Be(2);
        var assembly = typeof(MemoryDbContext).Assembly;
        var migrationName = assembly.GetManifestResourceNames().Single(x => x.EndsWith(".052_conversation_adjudication.sql", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(migrationName)!);
        await db.Database.ExecuteSqlRawAsync(await reader.ReadToEndAsync());
        (await db.ConversationAdjudications.CountAsync(x => x.InsightId == row.Id)).Should().Be(2);
        await FluentActions.Invoking(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE conversation_adjudications SET reason = 'overwrite' WHERE id = {receipt.OperationId}"))
            .Should().ThrowAsync<Exception>().WithMessage("*append-only*");
        await FluentActions.Invoking(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM conversation_adjudications WHERE id = {receipt.OperationId}"))
            .Should().ThrowAsync<Exception>().WithMessage("*append-only*");
    }

    [DockerRequiredFact]
    public async Task Archived_delete_proposal_should_close_without_deleting_target_and_stale_token_should_fail()
    {
        using var scope = CreateScope(out var db, out var service, out var actor);
        var row = await SeedAsync(db, actor);
        var target = new MemoryItem
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = row.ProjectId,
            Title = "Archived evidence",
            Content = "Retain original",
            Status = MemoryStatus.Archived,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.MemoryItems.Add(target);
        row.SourceSystem = "chatgpt-mcp-gateway";
        row.Tags = ["chatgpt-proposal"];
        row.MetadataJson = JsonSerializer.Serialize(new { proposal = true, toolName = "memory_delete", payload = new { id = target.Id, projectId = row.ProjectId } });
        await db.SaveChangesAsync();
        var candidate = (await Preview(service, row)).Items.Single();
        candidate.Classification.Should().Be("ResolvedWithoutDelete");
        target.Content = "Changed archived evidence";
        await db.SaveChangesAsync();
        await FluentActions.Invoking(() => service.AdjudicateAsync(Request(row, candidate, ConversationAdjudicationAction.Reconcile), default))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*changed*");
        candidate = (await Preview(service, row)).Items.Single();
        var receipt = await service.AdjudicateAsync(Request(row, candidate, ConversationAdjudicationAction.Reconcile), default);
        receipt.Outcome.Should().Be("ResolvedWithoutDelete");
        (await db.MemoryItems.AsNoTracking().SingleAsync(x => x.Id == target.Id)).Content.Should().Be("Changed archived evidence");
    }

    [DockerRequiredFact]
    public async Task Holds_host_blocks_and_scheduled_or_wrong_owner_actors_should_fail_closed()
    {
        using var scope = CreateScope(out var db, out var service, out var actor);
        var row = await SeedAsync(db, actor);
        foreach (var metadata in new[] { "[]", "{\"legalHold\":true}", "{\"securityHold\":\"unknown\"}" })
        {
            row = await db.ConversationInsights.SingleAsync(x => x.Id == row.Id);
            row.MetadataJson = metadata;
            await db.SaveChangesAsync();
            var candidate = (await Preview(service, row)).Items.Single();
            candidate.Classification.Should().Be("ProtectedHold");
            await FluentActions.Invoking(() => service.AdjudicateAsync(Request(row, candidate, ConversationAdjudicationAction.ResolveSkipped), default))
                .Should().ThrowAsync<InvalidOperationException>();
        }
        row = await db.ConversationInsights.SingleAsync(x => x.Id == row.Id);
        row.MetadataJson = "{}";
        row.PromotionStatus = ConversationPromotionStatus.HostBlocked;
        await db.SaveChangesAsync();
        var blocked = (await Preview(service, row)).Items.Single();
        await FluentActions.Invoking(() => service.AdjudicateAsync(Request(row, blocked, ConversationAdjudicationAction.ResolveSkipped), default))
            .Should().ThrowAsync<InvalidOperationException>();
        var actors = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        foreach (var denied in new[] { actor with { IsServiceActor = true }, actor with { Role = TenantUserRole.Member },
            actor with { Scopes = [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite, SecurityScopes.ScheduledGovernance] },
            actor with { AllowedProjectIds = ["another-project"] } })
        {
            actors.Current = denied;
            await FluentActions.Invoking(() => Preview(service, row)).Should().ThrowAsync<UnauthorizedAccessException>();
        }
        actors.Current = actor with { UserId = Guid.NewGuid() };
        (await Preview(service, row)).TotalCount.Should().Be(0);
        await FluentActions.Invoking(() => service.AdjudicateAsync(Request(row, blocked, ConversationAdjudicationAction.ResolveSkipped), default))
            .Should().ThrowAsync<KeyNotFoundException>();
        actors.Current = actor;
        (await db.ConversationAdjudications.CountAsync(x => x.InsightId == row.Id)).Should().Be(0);
    }

    [DockerRequiredFact]
    public async Task Dry_run_should_page_more_than_one_thousand_exceptions_without_mutation()
    {
        using var scope = CreateScope(out var db, out var service, out var actor);
        var seed = await SeedAsync(db, actor);
        var rows = Enumerable.Range(0, 1006).Select(i => new ConversationInsight
        {
            SessionId = seed.SessionId,
            CheckpointId = seed.CheckpointId,
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = seed.ProjectId,
            Title = $"historical {i}",
            Content = "Unbound historical record",
            DedupKey = Guid.NewGuid().ToString(),
            PromotionStatus = ConversationPromotionStatus.RequiresUserDecision,
            CreatedAt = seed.CreatedAt,
            UpdatedAt = seed.UpdatedAt
        }).ToArray();
        db.ConversationInsights.AddRange(rows);
        await db.SaveChangesAsync();
        var ids = new HashSet<Guid>();
        var offset = 0;
        while (true)
        {
            var page = await service.PreviewAsync(new(seed.ProjectId, offset, 200), default);
            page.TotalCount.Should().Be(1007);
            page.UnresolvedHumanDecisionCount.Should().Be(1007);
            foreach (var item in page.Items) ids.Add(item.InsightId).Should().BeTrue();
            if (!page.HasMore) break;
            offset += page.Items.Count;
        }
        ids.Count.Should().Be(1007);
        (await db.ConversationAdjudications.CountAsync(x => x.ProjectId == seed.ProjectId)).Should().Be(0);
        var firstPage = await service.PreviewAsync(new(seed.ProjectId, 0, 200), default);
        var selected = firstPage.Items.First();
        await service.AdjudicateAsync(new(seed.ProjectId, selected.InsightId, Guid.NewGuid(),
            ConversationAdjudicationAction.ResolveSkipped, selected.EvidenceToken, "Explicit historical adjudication", "backfill-test"), default);
        var readBack = await service.PreviewAsync(new(seed.ProjectId, GovernanceRunId: "backfill-test"), default);
        readBack.TotalCount.Should().Be(1007);
        readBack.UnresolvedHumanDecisionCount.Should().Be(1006);
        readBack.ResolvedHumanDecisionCount.Should().Be(1);
        readBack.NewlyResolvedCount.Should().Be(1);
        readBack.UnchangedCount.Should().Be(1006);
        (await db.ConversationInsights.CountAsync(x => x.ProjectId == seed.ProjectId && x.PromotionStatus == ConversationPromotionStatus.Promoted)).Should().Be(0);
    }

    private IServiceScope CreateScope(out MemoryDbContext db, out IConversationAdjudicationService service, out ContextHubRequestActor actor)
    {
        var scope = environment.GetFactory().Services.CreateScope();
        db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var user = db.TenantUsers.Single(x => x.Username == "contract-test-admin");
        actor = new(user.TenantId, user.Id, user.Username, TenantUserRole.Owner,
            [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite, SecurityScopes.SkillsRead], [], true);
        scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>().Current = actor;
        service = scope.ServiceProvider.GetRequiredService<IConversationAdjudicationService>();
        return scope;
    }

    private static async Task<ConversationInsight> SeedAsync(MemoryDbContext db, ContextHubRequestActor actor)
    {
        var now = DateTimeOffset.UtcNow;
        var project = $"adjudication-{Guid.NewGuid():N}";
        var session = new ConversationSession
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = project,
            ConversationId = project,
            SourceSystem = "test",
            StartedAt = now,
            LastCheckpointAt = now,
            UpdatedAt = now
        };
        var checkpoint = new ConversationCheckpoint
        {
            SessionId = session.Id,
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = project,
            ConversationId = project,
            TurnId = "1",
            SourceSystem = "test",
            DedupKey = project,
            CreatedAt = now
        };
        var row = new ConversationInsight
        {
            SessionId = session.Id,
            CheckpointId = checkpoint.Id,
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = project,
            ConversationId = project,
            Title = "Historical exception",
            Content = "Original evidence",
            DedupKey = project,
            PromotionStatus = ConversationPromotionStatus.RequiresUserDecision,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.AddRange(session, checkpoint, row);
        await db.SaveChangesAsync();
        return row;
    }

    private static Task<ConversationAdjudicationPreviewResult> Preview(IConversationAdjudicationService service, ConversationInsight row)
        => service.PreviewAsync(new(row.ProjectId), default);
    private static ConversationAdjudicationRequest Request(ConversationInsight row, ConversationAdjudicationCandidate candidate, ConversationAdjudicationAction action)
        => new(row.ProjectId, row.Id, Guid.NewGuid(), action, candidate.EvidenceToken, "Reviewed current evidence", "test-adjudication");
}
