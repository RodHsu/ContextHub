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

public sealed class MemoryEditProposalWorkflowTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [DockerRequiredFact]
    public async Task Same_intent_submission_and_approval_replay_are_bound_to_one_committed_edit_and_audit()
    {
        var (actor, memory) = await SeedAsync();
        const string body = "typed proposal replacement body";
        var command = Edit(memory, body);
        var alias = command with { ProjectId = command.ProjectId.ToUpperInvariant() };
        var proposals = await Task.WhenAll(SubmitAsync(actor, command), SubmitAsync(actor, alias));
        proposals.Select(proposal => proposal.Id).Distinct().Should().ContainSingle();
        var pending = proposals[0];
        pending.ProjectId.Should().Be(memory.ProjectId);
        pending.Status.Should().Be(ChatGptProposalStatus.Pending);
        var persistedCommand = JsonSerializer.Deserialize<MemoryEditCommand>(pending.PayloadJson, Json)!;
        persistedCommand.ExpectedVersion.Should().Be(memory.Version);
        persistedCommand.RequestId.Should().Be(command.RequestId);

        Func<Task> changedSubmission = () => SubmitAsync(actor, command with { Patch = new(Content: "different intent") });
        (await changedSubmission.Should().ThrowAsync<SafeEditConflictException>()).Which.Code.Should()
            .Be(SafeEditConflictCodes.RequestIdentityMismatch);
        Func<Task> forgedDirectReceipt = () => ApplyDirectAsync(actor, command with
        {
            RequestId = MemoryEditProposalContract.ReceiptRequestId(pending.Id)
        });
        await forgedDirectReceipt.Should().ThrowAsync<ArgumentException>();

        var approved = await Task.WhenAll(ApproveAsync(actor, pending.Id), ApproveAsync(actor, pending.Id));
        approved.Should().OnlyContain(result => result.Status == ChatGptProposalStatus.Applied && result.AppliedResourceId == memory.Id);
        await environment.RestartApplicationAsync();
        var replay = await ApproveAsync(actor, pending.Id);
        replay.Should().BeEquivalentTo(approved[0]);
        (await SubmitAsync(actor, alias)).Should().BeEquivalentTo(replay);

        using var scope = NewScope(actor);
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var stored = await db.ConversationInsights.Include(row => row.Checkpoint).SingleAsync(row => row.Id == pending.Id);
        stored.Content.Should().BeEmpty();
        stored.Checkpoint!.MetadataJson.Should().NotContain(body).And.NotContain("payload");
        var current = await db.MemoryItems.SingleAsync(row => row.Id == memory.Id);
        current.Version.Should().Be(memory.Version + 1);
        current.Content.Should().Be(body);
        (await db.MemoryItemRevisions.CountAsync(row => row.MemoryItemId == memory.Id)).Should().Be(2);
        var receipt = await db.MemoryEditReceipts.SingleAsync(row => row.MemoryItemId == memory.Id);
        receipt.RequestId.Should().Be(MemoryEditProposalContract.ReceiptRequestId(pending.Id));
        receipt.RequestHash.Should().Be(MemoryEditContract.Hash(command));
        (await db.MemoryJobs.CountAsync(row => row.ProjectId == memory.ProjectId && row.JobType == MemoryJobType.Reindex)).Should().Be(2);
        var audit = (await ApprovalAuditsAsync(db, actor, pending.Id)).Single();
        using var auditDetails = JsonDocument.Parse(audit.DetailsJson);
        auditDetails.RootElement.GetProperty("receiptId").GetGuid().Should().Be(receipt.Id);
        auditDetails.RootElement.GetProperty("version").GetInt32().Should().Be(receipt.Version);
        audit.ActorUserId.Should().Be(actor.UserId);
    }

    [DockerRequiredFact]
    public async Task Submission_and_approval_recheck_current_project_owner_target_and_original_version()
    {
        var (actor, memory) = await SeedAsync();
        var (_, other) = await SeedAsync();
        var command = Edit(memory, "originally proposed content");
        Func<Task> wrongEnvelope = () => SubmitAsync(actor, command, other.ProjectId);
        await wrongEnvelope.Should().ThrowAsync<ArgumentException>();
        Func<Task> wrongTarget = () => SubmitAsync(actor, command with { MemoryId = other.Id });
        await wrongTarget.Should().ThrowAsync<KeyNotFoundException>();
        Func<Task> wrongOwner = () => SubmitAsync(actor with { UserId = Guid.NewGuid() }, command);
        await wrongOwner.Should().ThrowAsync<KeyNotFoundException>();
        Func<Task> wrongTenant = () => SubmitAsync(actor with { TenantId = Guid.NewGuid() }, command);
        await wrongTenant.Should().ThrowAsync<KeyNotFoundException>();

        var pending = await SubmitAsync(actor, command);
        var direct = await ApplyDirectAsync(actor, Edit(memory, "newer direct edit"));
        Func<Task> staleApproval = () => ApproveAsync(actor, pending.Id);
        var conflict = (await staleApproval.Should().ThrowAsync<SafeEditConflictException>()).Which;
        conflict.Code.Should().Be(SafeEditConflictCodes.StaleReadVersion);
        conflict.CurrentRevision.Should().Be(direct.Version);
        Func<Task> staleSubmit = () => SubmitAsync(actor, command with { RequestId = Guid.NewGuid().ToString("N") });
        (await staleSubmit.Should().ThrowAsync<SafeEditConflictException>()).Which.Code.Should().Be(SafeEditConflictCodes.StaleReadVersion);
        Func<Task> forbiddenProposal = () => ApproveAsync(actor with { AllowedProjectIds = [other.ProjectId] }, pending.Id);
        await forbiddenProposal.Should().ThrowAsync<InvalidOperationException>();

        using var scope = NewScope(actor);
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await db.ConversationInsights.SingleAsync(row => row.Id == pending.Id)).PromotionStatus.Should().Be(ConversationPromotionStatus.Pending);
        (await db.MemoryEditReceipts.CountAsync(row => row.MemoryItemId == memory.Id)).Should().Be(1);
        (await db.MemoryItems.SingleAsync(row => row.Id == memory.Id)).Content.Should().Be("newer direct edit");
    }

    [DockerRequiredFact]
    public async Task Altered_intent_and_old_unversioned_typed_rows_do_not_get_a_later_version_filled_in()
    {
        var (actor, memory) = await SeedAsync();
        var edit = Edit(memory, "existing approved intent");
        var pending = await SubmitAsync(actor, edit);
        using var scope = NewScope(actor);
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var proposal = await db.ConversationInsights.SingleAsync(row => row.Id == pending.Id);
        var metadata = JsonNode.Parse(proposal.MetadataJson)!.AsObject();
        var payload = metadata["payload"]!.AsObject();
        payload["patch"]!["content"] = "substituted intent";
        proposal.MetadataJson = metadata.ToJsonString();
        await db.SaveChangesAsync();

        Func<Task> changedIntent = () => ApproveAsync(actor, pending.Id);
        (await changedIntent.Should().ThrowAsync<SafeEditConflictException>()).Which.Code.Should().Be(SafeEditConflictCodes.RequestIdentityMismatch);
        payload.Remove("expectedVersion");
        proposal.MetadataJson = metadata.ToJsonString();
        await db.SaveChangesAsync();
        Func<Task> oldTypedProposal = () => ApproveAsync(actor, pending.Id);
        (await oldTypedProposal.Should().ThrowAsync<SafeEditConflictException>()).Which.Code.Should().Be(SafeEditConflictCodes.MissingReadVersion);
        metadata["toolName"] = "memory_update";
        proposal.MetadataJson = metadata.ToJsonString();
        await db.SaveChangesAsync();
        Func<Task> retaggedProposal = () => ApproveAsync(actor, pending.Id);
        await retaggedProposal.Should().ThrowAsync<KeyNotFoundException>();
        metadata["toolName"] = MemoryEditProposalContract.ToolName;
        proposal.MetadataJson = metadata.ToJsonString();
        await db.SaveChangesAsync();

        using var cleanup = NewScope(actor);
        var rejection = await cleanup.ServiceProvider.GetRequiredService<IChatGptProposalService>().RejectAsync(new(pending.Id), default);
        rejection.Status.Should().Be(ChatGptProposalStatus.Rejected);
        (await db.MemoryEditReceipts.CountAsync(row => row.MemoryItemId == memory.Id)).Should().Be(0);
        (await db.MemoryItems.SingleAsync(row => row.Id == memory.Id)).Version.Should().Be(memory.Version);
    }

    [DockerRequiredFact]
    public async Task Failed_proposal_decision_rolls_back_every_memory_side_effect_and_can_retry_same_proposal()
    {
        var (actor, memory) = await SeedAsync();
        var pending = await SubmitAsync(actor, Edit(memory, "guarded body"), title: "atomic rollback fixture");
        using var scope = NewScope(actor);
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var originalChunks = await db.MemoryItemChunks.Where(row => row.MemoryItemId == memory.Id).Select(row => row.Id).ToArrayAsync();
        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE conversation_insights ADD CONSTRAINT ck_memory_edit_proposal_rollback_fixture
            CHECK (NOT (source_ref = 'chatgpt-mcp-gateway:memory_edit' AND
                title = 'atomic rollback fixture' AND promotion_status = 'Promoted')) NOT VALID
            """);
        try
        {
            Func<Task> failing = () => ApproveAsync(actor, pending.Id);
            await failing.Should().ThrowAsync<DbUpdateException>();
            (await db.ConversationInsights.AsNoTracking().SingleAsync(row => row.Id == pending.Id)).PromotionStatus
                .Should().Be(ConversationPromotionStatus.Pending);
            (await db.MemoryItems.AsNoTracking().SingleAsync(row => row.Id == memory.Id)).Content.Should().Be(memory.Content);
            (await db.MemoryItemRevisions.CountAsync(row => row.MemoryItemId == memory.Id)).Should().Be(1);
            (await db.MemoryItemChunks.Where(row => row.MemoryItemId == memory.Id).Select(row => row.Id).ToArrayAsync())
                .Should().BeEquivalentTo(originalChunks);
            (await db.MemoryEditReceipts.CountAsync(row => row.MemoryItemId == memory.Id)).Should().Be(0);
            (await db.MemoryJobs.CountAsync(row => row.ProjectId == memory.ProjectId && row.JobType == MemoryJobType.Reindex)).Should().Be(1);
            (await ApprovalAuditsAsync(db, actor, pending.Id)).Should().BeEmpty();
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE conversation_insights DROP CONSTRAINT ck_memory_edit_proposal_rollback_fixture");
        }
        (await ApproveAsync(actor, pending.Id)).Status.Should().Be(ChatGptProposalStatus.Applied);
        (await db.MemoryEditReceipts.CountAsync(row => row.MemoryItemId == memory.Id)).Should().Be(1);
    }

    [DockerRequiredFact]
    public async Task Existing_memory_update_proposals_continue_to_use_the_legacy_contract()
    {
        var (actor, memory) = await SeedAsync();
        using var scope = NewScope(actor);
        var proposals = scope.ServiceProvider.GetRequiredService<IChatGptProposalService>();
        var legacy = new MemoryUpdateRequest(memory.Id, Content: "legacy content", ProjectId: memory.ProjectId);
        var pending = await proposals.CreateAsync(new("memory_update", memory.ProjectId,
            JsonSerializer.Serialize(legacy, Json), "legacy title", "legacy summary"), default);
        (await proposals.ApproveAsync(new(pending.Id), default)).Status.Should().Be(ChatGptProposalStatus.Applied);
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await db.MemoryItems.AsNoTracking().SingleAsync(row => row.Id == memory.Id)).Content.Should().Be("legacy content");
        (await db.MemoryEditReceipts.CountAsync(row => row.MemoryItemId == memory.Id)).Should().Be(0);
    }

    private async Task<(ContextHubRequestActor Actor, MemoryDocument Memory)> SeedAsync()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var user = await db.TenantUsers.SingleAsync(row => row.Username == "contract-test-admin");
        var actor = new ContextHubRequestActor(user.TenantId, user.Id, user.Username, user.Role,
            [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite], [], IsAuthenticated: true);
        scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>().Current = actor;
        var memory = await scope.ServiceProvider.GetRequiredService<IMemoryService>().UpsertAsync(new(
            "proposal-original", MemoryScope.Project, MemoryType.Episode, "original title", "original body", "original summary",
            "document", "synthetic-proposal-fixture", [], 0.5m, 0.5m, ProjectId: $"memory-proposal-{Guid.NewGuid():N}"), default);
        return (actor, memory);
    }

    private async Task<ChatGptProposalResult> SubmitAsync(ContextHubRequestActor actor, MemoryEditCommand command,
        string? envelope = null, string title = "guarded memory proposal")
    {
        using var scope = NewScope(actor);
        return await scope.ServiceProvider.GetRequiredService<IChatGptProposalService>().CreateAsync(new(
            MemoryEditProposalContract.ToolName, envelope ?? command.ProjectId, JsonSerializer.Serialize(command, Json),
            title, "guarded edit review"), default);
    }

    private async Task<ChatGptProposalResult> ApproveAsync(ContextHubRequestActor actor, Guid proposalId)
    {
        using var scope = NewScope(actor);
        return await scope.ServiceProvider.GetRequiredService<IChatGptProposalService>().ApproveAsync(new(proposalId), default);
    }

    private async Task<MemoryEditReceipt> ApplyDirectAsync(ContextHubRequestActor actor, MemoryEditCommand command)
    {
        using var scope = NewScope(actor);
        return await scope.ServiceProvider.GetRequiredService<IMemoryEditingService>().ApplyAsync(command, default);
    }

    private IServiceScope NewScope(ContextHubRequestActor actor)
    {
        var scope = environment.GetFactory().Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>().Current = actor;
        return scope;
    }

    private static MemoryEditCommand Edit(MemoryDocument memory, string body)
        => new(memory.ProjectId, memory.Id, memory.Version, Guid.NewGuid().ToString("N"), new(Content: body));

    private static async Task<SecurityAuditEvent[]> ApprovalAuditsAsync(MemoryDbContext db, ContextHubRequestActor actor, Guid proposalId)
    {
        var events = await db.SecurityAuditEvents.AsNoTracking().Where(row => row.TenantId == actor.TenantId &&
            row.ActorUserId == actor.UserId && row.EventType == SecurityAuditEventType.ConversationInsightGovernanceUpdated &&
            row.Outcome == "Promoted").ToArrayAsync();
        return events.Where(row =>
        {
            using var details = JsonDocument.Parse(row.DetailsJson);
            return details.RootElement.TryGetProperty("proposalId", out var id) && id.GetGuid() == proposalId;
        }).ToArray();
    }
}
