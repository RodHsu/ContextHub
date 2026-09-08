using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Memory.IntegrationTests;

public sealed class ProposalLifecycleParityIntegrationTests(ContainerTestEnvironment environment)
    : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Proposal_score_contract_should_fail_fast_on_create_and_approve()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var services = scope.ServiceProvider;
        UseBootstrapActor(services);
        var db = services.GetRequiredService<MemoryDbContext>();
        var proposals = services.GetRequiredService<IChatGptProposalService>();
        var projectId = $"proposal-score-contract-{Guid.NewGuid():N}";

        var invalidCreate = BuildCreateRequest(
            projectId, "invalid-create", $"proposal-score-create-{Guid.NewGuid():N}");
        invalidCreate = invalidCreate with { PayloadJson = ReplaceImportance(invalidCreate.PayloadJson, 95m) };
        var create = () => proposals.CreateAsync(invalidCreate, CancellationToken.None);
        var createException = await create.Should().ThrowAsync<MemoryScoreValidationException>();
        createException.Which.Code.Should().Be(MemoryScoreContract.InvalidRangeCode);
        createException.Which.ReasonClass.Should().Be(MemoryScoreContract.InvalidRangeReasonClass);
        createException.Which.Field.Should().Be("importance");

        var pending = await proposals.CreateAsync(
            BuildCreateRequest(projectId, "invalid-approve", $"proposal-score-approve-{Guid.NewGuid():N}"),
            CancellationToken.None);
        var persisted = await db.ConversationInsights.SingleAsync(x => x.Id == pending.Id);
        var metadata = JsonNode.Parse(persisted.MetadataJson)!.AsObject();
        metadata["payload"]!.AsObject()["importance"] = 95m;
        persisted.MetadataJson = metadata.ToJsonString();
        await db.SaveChangesAsync(CancellationToken.None);

        var approve = () => proposals.ApproveAsync(
            new ChatGptProposalDecisionRequest(pending.Id), CancellationToken.None);
        var approveException = await approve.Should().ThrowAsync<MemoryScoreValidationException>();
        approveException.Which.Code.Should().Be(MemoryScoreContract.InvalidRangeCode);
        approveException.Which.ReasonClass.Should().Be(MemoryScoreContract.InvalidRangeReasonClass);
        approveException.Which.Field.Should().Be("importance");

        var unchanged = await db.ConversationInsights.AsNoTracking().SingleAsync(x => x.Id == pending.Id);
        unchanged.PromotionStatus.Should().Be(ConversationPromotionStatus.Pending);
        unchanged.PromotedMemoryId.Should().BeNull();
    }

    [DockerRequiredFact]
    public async Task Pending_projection_should_match_direct_list_and_exclude_terminal_history()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var services = scope.ServiceProvider;
        var actor = UseBootstrapActor(services);
        var actorAccessor = services.GetRequiredService<IRequestActorAccessor>();
        var db = services.GetRequiredService<MemoryDbContext>();
        var proposals = services.GetRequiredService<IChatGptProposalService>();
        var projectId = $"proposal-parity-{Guid.NewGuid():N}";
        var runId = $"proposal-parity-run-{Guid.NewGuid():N}";

        var pending = await proposals.CreateAsync(
            BuildCreateRequest(projectId, "pending", $"{runId}-pending"), CancellationToken.None);
        var applied = await proposals.CreateAsync(
            BuildCreateRequest(projectId, "applied", $"{runId}-applied"), CancellationToken.None);
        var rejected = await proposals.CreateAsync(
            BuildCreateRequest(projectId, "rejected", $"{runId}-rejected"), CancellationToken.None);
        var failed = await proposals.CreateAsync(
            BuildCreateRequest(projectId, "failed", $"{runId}-failed"), CancellationToken.None);

        var terminalRows = await db.ConversationInsights
            .Where(x => x.Id == applied.Id || x.Id == rejected.Id || x.Id == failed.Id)
            .ToListAsync(CancellationToken.None);
        terminalRows.Single(x => x.Id == applied.Id).PromotionStatus = ConversationPromotionStatus.Promoted;
        terminalRows.Single(x => x.Id == rejected.Id).PromotionStatus = ConversationPromotionStatus.Skipped;
        terminalRows.Single(x => x.Id == failed.Id).PromotionStatus = ConversationPromotionStatus.Failed;
        await db.SaveChangesAsync(CancellationToken.None);

        var directPending = await proposals.ListAsync(
            new ChatGptProposalListRequest(projectId, ChatGptProposalStatus.Pending, 100), CancellationToken.None);
        directPending.Should().ContainSingle(x => x.Id == pending.Id);
        directPending.Should().NotContain(x => x.Id == applied.Id || x.Id == rejected.Id || x.Id == failed.Id);

        var review = await services.GetRequiredService<IKnowledgeReviewService>().ReviewAsync(
            new KnowledgeReviewRequest([projectId], LimitPerSection: 200, GovernanceRunId: runId),
            CancellationToken.None);

        review.PendingProposals.Should().ContainSingle(x => x.Id == pending.Id);
        review.GovernanceCoverage!.ProposalCoverage.TotalCount.Should().Be(1);
        review.GovernanceCoverage.ProposalCoverage.CandidateCount.Should().Be(1);
        review.GovernancePlan.Should().ContainSingle(x =>
            x.ItemKind == GovernanceItemKind.Proposal && x.AuthorityResourceId == pending.Id);
        review.GovernancePlan.Should().NotContain(x =>
            x.ItemKind == GovernanceItemKind.Proposal &&
            (x.AuthorityResourceId == applied.Id || x.AuthorityResourceId == rejected.Id || x.AuthorityResourceId == failed.Id));

        var allHistory = await proposals.ListAsync(
            new ChatGptProposalListRequest(projectId, Status: null, 100), CancellationToken.None);
        allHistory.Select(x => x.Id).Should().Contain([pending.Id, applied.Id, rejected.Id, failed.Id]);
        actorAccessor.Current.Should().Be(actor);
    }

    [DockerRequiredFact]
    public async Task Proposal_list_and_mutation_should_fail_closed_across_tenants_with_same_project()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var services = scope.ServiceProvider;
        var ownerActor = UseBootstrapActor(services);
        var actorAccessor = services.GetRequiredService<IRequestActorAccessor>();
        var db = services.GetRequiredService<MemoryDbContext>();
        var proposals = services.GetRequiredService<IChatGptProposalService>();
        var projectId = $"proposal-acl-{Guid.NewGuid():N}";

        var ownerProposal = await proposals.CreateAsync(
            BuildCreateRequest(projectId, "owner", $"proposal-acl-owner-{Guid.NewGuid():N}"), CancellationToken.None);
        var foreignActor = ownerActor with
        {
            TenantId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            Username = $"foreign-{Guid.NewGuid():N}"[..28],
            Role = TenantUserRole.Member
        };
        var now = DateTimeOffset.UtcNow;
        db.Tenants.Add(new Tenant
        {
            Id = foreignActor.TenantId!.Value,
            Slug = $"proposal-{foreignActor.TenantId.Value:N}"[..28],
            DisplayName = "Proposal isolation tenant",
            Status = TenantStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        });
        db.TenantUsers.Add(new TenantUser
        {
            Id = foreignActor.UserId!.Value,
            TenantId = foreignActor.TenantId.Value,
            Username = foreignActor.Username,
            DisplayName = "Proposal isolation user",
            Role = foreignActor.Role ?? TenantUserRole.Member,
            Status = TenantUserStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync(CancellationToken.None);
        actorAccessor.Current = foreignActor;
        var foreignProposal = await proposals.CreateAsync(
            BuildCreateRequest(projectId, "foreign", $"proposal-acl-foreign-{Guid.NewGuid():N}"), CancellationToken.None);

        actorAccessor.Current = ownerActor;
        var ownerPending = await proposals.ListAsync(
            new ChatGptProposalListRequest(projectId, ChatGptProposalStatus.Pending, 100), CancellationToken.None);
        ownerPending.Should().ContainSingle(x => x.Id == ownerProposal.Id);
        ownerPending.Should().NotContain(x => x.Id == foreignProposal.Id);

        var deniedApprove = () => proposals.ApproveAsync(
            new ChatGptProposalDecisionRequest(foreignProposal.Id), CancellationToken.None);
        await deniedApprove.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"ChatGPT proposal '{foreignProposal.Id}' was not found.");

        var review = await services.GetRequiredService<IKnowledgeReviewService>().ReviewAsync(
            new KnowledgeReviewRequest([projectId], LimitPerSection: 200,
                GovernanceRunId: $"proposal-acl-run-{Guid.NewGuid():N}"), CancellationToken.None);
        review.PendingProposals.Should().ContainSingle(x => x.Id == ownerProposal.Id);
        review.PendingProposals.Should().NotContain(x => x.Id == foreignProposal.Id);
        review.GovernanceCoverage!.ProposalCoverage.TotalCount.Should().Be(1);
    }

    [DockerRequiredFact]
    public async Task Reject_should_reconcile_same_run_review_preserve_history_and_be_replay_safe()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var services = scope.ServiceProvider;
        UseBootstrapActor(services);
        var db = services.GetRequiredService<MemoryDbContext>();
        var proposals = services.GetRequiredService<IChatGptProposalService>();
        var projectId = $"proposal-lifecycle-{Guid.NewGuid():N}";
        var runId = $"proposal-lifecycle-run-{Guid.NewGuid():N}";
        var proposal = await proposals.CreateAsync(
            BuildCreateRequest(projectId, "lifecycle", runId), CancellationToken.None);

        var initial = await services.GetRequiredService<IKnowledgeReviewService>().ReviewAsync(
            new KnowledgeReviewRequest([projectId], LimitPerSection: 200, GovernanceRunId: runId),
            CancellationToken.None);
        initial.PendingProposals.Should().ContainSingle(x => x.Id == proposal.Id);
        initial.GovernanceCoverage!.ProposalCoverage.CandidateCount.Should().Be(1);

        var rejected = await proposals.RejectAsync(
            new ChatGptProposalDecisionRequest(proposal.Id, "Owner rejected this proposal."), CancellationToken.None);
        rejected.Status.Should().Be(ChatGptProposalStatus.Rejected);
        var replay = await proposals.RejectAsync(
            new ChatGptProposalDecisionRequest(proposal.Id, "Owner rejected this proposal."), CancellationToken.None);
        replay.Status.Should().Be(ChatGptProposalStatus.Rejected);
        replay.UpdatedAt.Should().Be(rejected.UpdatedAt);

        var reReview = await services.GetRequiredService<IKnowledgeReviewService>().ReviewAsync(
            new KnowledgeReviewRequest([projectId], LimitPerSection: 200, GovernanceRunId: runId, IsReReview: true),
            CancellationToken.None);
        reReview.PendingProposals.Should().BeEmpty();
        reReview.GovernanceCoverage!.ProposalCoverage.TotalCount.Should().Be(0);
        reReview.GovernanceCoverage.ProposalCoverage.CandidateCount.Should().Be(0);
        reReview.GovernancePlan.Should().NotContain(x =>
            x.ItemKind == GovernanceItemKind.Proposal && x.AuthorityResourceId == proposal.Id);

        var history = await proposals.ListAsync(
            new ChatGptProposalListRequest(projectId, Status: null, 100), CancellationToken.None);
        history.Should().ContainSingle(x => x.Id == proposal.Id && x.Status == ChatGptProposalStatus.Rejected);
        var persisted = await db.ConversationInsights.AsNoTracking().SingleAsync(x => x.Id == proposal.Id);
        persisted.GovernanceLastReevaluatedAt.Should().NotBeNull();
        persisted.GovernanceLastEvidenceChangedAt.Should().NotBeNull();

        var audits = await db.SecurityAuditEvents.AsNoTracking()
            .Where(x => x.EventType == SecurityAuditEventType.ConversationInsightGovernanceUpdated)
            .Select(x => x.DetailsJson)
            .ToListAsync(CancellationToken.None);
        audits = audits.Where(x => x.Contains(proposal.Id.ToString(), StringComparison.Ordinal)).ToList();
        var transitions = audits
            .Select(x => JsonNode.Parse(x)!["transition"]!.GetValue<string>())
            .ToArray();
        transitions.Should().Contain(["Create", "Reject"]);
    }

    [DockerRequiredFact]
    public async Task Interactive_Governance_Must_Not_Strand_A_Pending_Proposal()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var services = scope.ServiceProvider;
        UseBootstrapActor(services);
        var db = services.GetRequiredService<MemoryDbContext>();
        var proposals = services.GetRequiredService<IChatGptProposalService>();
        var projectId = $"proposal-governance-boundary-{Guid.NewGuid():N}";
        var runId = $"proposal-governance-boundary-run-{Guid.NewGuid():N}";
        var proposal = await proposals.CreateAsync(
            BuildCreateRequest(projectId, "governance-boundary", runId),
            CancellationToken.None);
        var review = await services.GetRequiredService<IKnowledgeReviewService>().ReviewAsync(
            new KnowledgeReviewRequest([projectId], LimitPerSection: 200, GovernanceRunId: runId),
            CancellationToken.None);
        var request = new GovernanceBatchExecuteRequest(
            runId,
            [projectId],
            review.DurableMemoryCoverage!.SnapshotToken,
            AllowedActionTypes: [GovernanceBatchActionType.ProposalApply],
            ExecutionMode: GovernanceBatchExecutionMode.Interactive);

        var execution = await services.GetRequiredService<IGovernanceBatchExecutor>()
            .ExecuteAsync(request, CancellationToken.None);

        execution.ErrorCode.Should().Be(GovernanceBatchErrorCode.None);
        execution.Items.Should().ContainSingle(x =>
            x.ResourceId == proposal.Id &&
            x.Disposition == GovernanceBatchItemDisposition.RequiresUserDecision);
        var pending = await proposals.ListAsync(
            new ChatGptProposalListRequest(projectId, ChatGptProposalStatus.Pending, 100),
            CancellationToken.None);
        pending.Should().ContainSingle(x => x.Id == proposal.Id);
        var persisted = await db.ConversationInsights.AsNoTracking().SingleAsync(x => x.Id == proposal.Id);
        persisted.PromotionStatus.Should().Be(ConversationPromotionStatus.Pending);
        persisted.PromotedMemoryId.Should().BeNull();

        var approved = await proposals.ApproveAsync(
            new ChatGptProposalDecisionRequest(proposal.Id),
            CancellationToken.None);
        approved.Status.Should().Be(ChatGptProposalStatus.Applied);
    }

    [DockerRequiredFact]
    public async Task Terminal_business_work_item_should_not_become_a_convergence_candidate()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var services = scope.ServiceProvider;
        UseBootstrapActor(services);
        var db = services.GetRequiredService<MemoryDbContext>();
        var workItems = services.GetRequiredService<IProjectWorkItemService>();
        var projectId = $"proposal-work-item-boundary-{Guid.NewGuid():N}";
        var terminal = await workItems.CreateAsync(
            new ProjectWorkItemCreateRequest(projectId, "Ordinary terminal business work"), CancellationToken.None);
        terminal = await workItems.UpdateAsync(
            new ProjectWorkItemUpdateRequest(terminal.Id, Status: ProjectWorkItemStatus.Completed),
            CancellationToken.None);
        var terminalEntity = await db.ProjectWorkItems.SingleAsync(x => x.Id == terminal.Id);
        terminalEntity.UpdatedAt = DateTimeOffset.UtcNow.AddDays(-120);
        await db.SaveChangesAsync(CancellationToken.None);

        var review = await services.GetRequiredService<IKnowledgeReviewService>().ReviewAsync(
            new KnowledgeReviewRequest([projectId], LimitPerSection: 200,
                GovernanceRunId: $"proposal-work-item-boundary-run-{Guid.NewGuid():N}"), CancellationToken.None);

        review.GovernancePlan.Should().NotContain(x =>
            x.ItemKind == GovernanceItemKind.WorkItem && x.AuthorityResourceId == terminal.Id);
        review.GovernanceCoverage!.WorkItemCoverage.CandidateCount.Should().Be(0);
        review.Convergence.BusinessWorkItemActionableCount.Should().Be(0);
        (await db.ProjectWorkItems.AsNoTracking().SingleAsync(x => x.Id == terminal.Id)).ArchivedAt.Should().BeNull();
    }

    private static ChatGptProposalCreateRequest BuildCreateRequest(
        string projectId,
        string suffix,
        string governanceRunId)
    {
        var payload = JsonSerializer.Serialize(new MemoryUpsertRequest(
            $"proposal-lifecycle:{suffix}:{Guid.NewGuid():N}",
            MemoryScope.Project,
            MemoryType.Fact,
            $"Proposal {suffix}",
            "Proposal payload retained for lifecycle parity testing.",
            "Lifecycle parity test proposal.",
            "integration-test",
            $"integration://proposal/{suffix}",
            ["proposal-lifecycle", suffix],
            .80m,
            .90m,
            ProjectId: projectId));
        return new ChatGptProposalCreateRequest(
            "memory_upsert",
            projectId,
            payload,
            $"Proposal {suffix}",
            "Lifecycle parity test proposal.",
            $"oauth-{suffix}",
            GovernanceRunId: governanceRunId);
    }

    private static string ReplaceImportance(string payloadJson, decimal importance)
    {
        var payload = JsonNode.Parse(payloadJson)!.AsObject();
        payload["importance"] = importance;
        return payload.ToJsonString();
    }

    private static ContextHubRequestActor UseBootstrapActor(IServiceProvider services)
    {
        var db = services.GetRequiredService<MemoryDbContext>();
        var user = db.TenantUsers.Single(x => x.Username == "contract-test-admin");
        var actor = new ContextHubRequestActor(
            user.TenantId,
            user.Id,
            user.Username,
            user.Role,
            [
                SecurityScopes.MemoryRead,
                SecurityScopes.MemoryWrite,
                SecurityScopes.PreferencesRead,
                SecurityScopes.PreferencesWrite,
                SecurityScopes.LogsRead
            ],
            [],
            IsAuthenticated: true);
        services.GetRequiredService<IRequestActorAccessor>().Current = actor;
        return actor;
    }
}
