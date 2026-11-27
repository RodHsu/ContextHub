using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Memory.Application;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Memory.Infrastructure;

/// <summary>Commits the typed proposal decision and its memory edit in one PostgreSQL transaction.</summary>
public sealed class MemoryEditProposalWorkflow(
    IDbContextFactory<MemoryDbContext> factory,
    MemoryDbContext ambient,
    IRequestActorAccessor actors,
    IMaintenanceCoordinator maintenance,
    MemoryEditStore memoryEdits,
    IClock clock) : IMemoryEditProposalWorkflow
{
    private const string ProposalTag = "chatgpt-proposal";
    private const string SourceSystem = ChatGptProposalService.SourceSystem;
    private const string ToolName = MemoryEditProposalContract.ToolName;
    private const string ToolTag = MemoryEditProposalContract.ToolTag;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ChatGptProposalResult> CreateAsync(ChatGptProposalCreateRequest request, CancellationToken cancellationToken)
    {
        var actor = RequireActor();
        if (request.ToolName.Trim() != ToolName) throw new ArgumentException("Unsupported memory edit proposal tool.");
        var command = MemoryEditProposalContract.ReadCommand(request.ProjectId, request.PayloadJson);
        var scope = RequireProject(actor, command.ProjectId);
        await RequireWriteAllowedAsync(actor, cancellationToken);
        var conversationId = ConversationId(scope, command);
        var fingerprint = ProposalFingerprint(conversationId, command);

        await using var db = await OpenAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        // The target row serializes submissions for its request namespace, including case aliases.
        var target = await MemoryEditStore.LockTargetAsync(db, scope, command.MemoryId, cancellationToken);
        var previous = await db.ConversationInsights.AsNoTracking().SingleOrDefaultAsync(
            row => row.TenantId == scope.TenantId && row.OwnerUserId == scope.OwnerUserId &&
                row.SourceSystem == SourceSystem && row.ConversationId == conversationId, cancellationToken);
        if (previous is not null)
        {
            RequireBoundCommand(previous, scope);
            if (previous.DedupKey != fingerprint)
                throw new SafeEditConflictException(SafeEditConflictCodes.RequestIdentityMismatch);
            await transaction.CommitAsync(cancellationToken);
            return ChatGptProposalProjection.ToResult(previous);
        }
        if (target.Version != command.ExpectedVersion)
            throw new SafeEditConflictException(SafeEditConflictCodes.StaleReadVersion, target.Version);

        var proposal = CreateProposal(request, command with { ProjectId = target.ProjectId }, scope, conversationId, fingerprint, actor);
        db.ConversationInsights.Add(proposal);
        AddAudit(db, proposal, actor, null, "Create");
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ChatGptProposalProjection.ToResult(proposal);
    }

    public async Task<ChatGptProposalResult> ApproveAsync(ChatGptProposalDecisionRequest request, CancellationToken cancellationToken)
    {
        var actor = RequireActor();
        await RequireWriteAllowedAsync(actor, cancellationToken);
        await using var db = await OpenAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var proposal = await LockProposalAsync(db, actor, request.ProposalId, cancellationToken);
        var scope = RequireProject(actor, proposal.ProjectId);
        var intent = RequireBoundCommand(proposal, scope);
        var edit = intent with { RequestId = MemoryEditProposalContract.ReceiptRequestId(proposal.Id) };

        if (proposal.PromotionStatus == ConversationPromotionStatus.Promoted)
        {
            await MemoryEditStore.LockTargetAsync(db, scope, edit.MemoryId, cancellationToken);
            var existing = await MemoryEditStore.FindCommittedReceiptAsync(db, scope, edit, MemoryEditContract.Hash(edit), cancellationToken);
            if (existing is null || proposal.PromotedMemoryId != edit.MemoryId)
                throw new InvalidDataException("The original memory edit proposal receipt is unavailable.");
            await transaction.CommitAsync(cancellationToken);
            return ChatGptProposalProjection.ToResult(proposal);
        }
        if (proposal.PromotionStatus != ConversationPromotionStatus.Pending)
            throw new InvalidOperationException("Memory edit proposal is not pending.");
        var receipt = await memoryEdits.StageAsync(db, scope, edit, cancellationToken);
        if (receipt.Replayed)
            throw new InvalidDataException("Memory edit receipt exists without the original proposal decision.");
        proposal.PromotionStatus = ConversationPromotionStatus.Promoted;
        proposal.PromotedMemoryId = receipt.MemoryId;
        proposal.Error = string.Empty;
        proposal.GovernanceReason = string.IsNullOrWhiteSpace(request.Note) ? "Approved." : request.Note.Trim();
        proposal.UpdatedAt = receipt.CommittedAt;
        AddAudit(db, proposal, actor, ConversationPromotionStatus.Pending, "Approve", receipt);
        await SaveDecisionAsync(db, transaction, cancellationToken);
        return ChatGptProposalProjection.ToResult(proposal);
    }

    public async Task<ChatGptProposalResult> RejectAsync(ChatGptProposalDecisionRequest request, CancellationToken cancellationToken)
    {
        var actor = RequireActor();
        await using var db = await OpenAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var proposal = await LockProposalAsync(db, actor, request.ProposalId, cancellationToken);
        RequireProject(actor, proposal.ProjectId);
        if (proposal.PromotionStatus == ConversationPromotionStatus.Skipped)
        {
            await transaction.CommitAsync(cancellationToken);
            return ChatGptProposalProjection.ToResult(proposal);
        }
        if (proposal.PromotionStatus != ConversationPromotionStatus.Pending)
            throw new InvalidOperationException("Memory edit proposal is not pending.");
        proposal.PromotionStatus = ConversationPromotionStatus.Skipped;
        proposal.Error = string.IsNullOrWhiteSpace(request.Note) ? "Rejected." : request.Note.Trim();
        proposal.GovernanceReason = proposal.Error;
        proposal.UpdatedAt = clock.UtcNow;
        AddAudit(db, proposal, actor, ConversationPromotionStatus.Pending, "Reject");
        await SaveDecisionAsync(db, transaction, cancellationToken);
        return ChatGptProposalProjection.ToResult(proposal);
    }

    private ConversationInsight CreateProposal(ChatGptProposalCreateRequest request, MemoryEditCommand command, MemoryEditScope scope,
        string conversationId, string fingerprint, ContextHubRequestActor actor)
    {
        var now = clock.UtcNow;
        now = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
        var turnId = $"proposal:{Guid.NewGuid():N}";
        var session = new ConversationSession
        {
            TenantId = scope.TenantId,
            OwnerUserId = scope.OwnerUserId,
            ConversationId = conversationId,
            ProjectId = command.ProjectId,
            ProjectName = command.ProjectId,
            SourceSystem = SourceSystem,
            Status = "Active",
            LastTurnId = turnId,
            StartedAt = now,
            LastCheckpointAt = now,
            UpdatedAt = now
        };
        var checkpoint = new ConversationCheckpoint
        {
            Session = session,
            TenantId = scope.TenantId,
            OwnerUserId = scope.OwnerUserId,
            ConversationId = conversationId,
            TurnId = turnId,
            ProjectId = command.ProjectId,
            ProjectName = command.ProjectId,
            SourceSystem = SourceSystem,
            EventType = ConversationEventType.SessionCheckpoint,
            SourceKind = ConversationSourceKind.AgentSupplemental,
            SourceRef = $"{SourceSystem}:{ToolName}",
            AgentMessageSummary = request.Summary.Trim(),
            DedupKey = fingerprint,
            CreatedAt = now,
            MetadataJson = JsonSerializer.Serialize(new { proposal = true, toolName = ToolName }, Json)
        };
        return new ConversationInsight
        {
            Session = session,
            Checkpoint = checkpoint,
            TenantId = scope.TenantId,
            OwnerUserId = scope.OwnerUserId,
            ConversationId = conversationId,
            TurnId = turnId,
            ProjectId = command.ProjectId,
            ProjectName = command.ProjectId,
            SourceSystem = SourceSystem,
            SourceKind = ConversationSourceKind.AgentSupplemental,
            InsightType = ConversationInsightType.Artifact,
            Title = request.Title.Trim(),
            Summary = request.Summary.Trim(),
            SourceRef = $"{SourceSystem}:{ToolName}",
            Tags = [ProposalTag, ToolTag],
            Importance = 0.82m,
            Confidence = 0.78m,
            DedupKey = fingerprint,
            PromotionStatus = ConversationPromotionStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
            // Persist the approved patch once. Session and checkpoint rows carry only lifecycle metadata.
            MetadataJson = JsonSerializer.Serialize(new
            {
                proposal = true,
                toolName = ToolName,
                payload = command,
                oauthSubject = request.OAuthSubject.Trim(),
                oauthEmail = request.OAuthEmail.Trim(),
                oauthName = request.OAuthName.Trim(),
                governanceRunId = request.GovernanceRunId?.Trim(),
                createdBy = actor.Username
            }, Json)
        };
    }

    private static MemoryEditCommand RequireBoundCommand(ConversationInsight proposal, MemoryEditScope scope)
    {
        var metadata = ReadTypedMetadata(proposal);
        var command = MemoryEditProposalContract.ReadCommand(proposal.ProjectId, metadata.PayloadJson);
        var conversationId = ConversationId(scope, command);
        if (proposal.ConversationId != conversationId || proposal.DedupKey != ProposalFingerprint(conversationId, command))
            throw new SafeEditConflictException(SafeEditConflictCodes.RequestIdentityMismatch);
        return command;
    }

    private static async Task<ConversationInsight> LockProposalAsync(MemoryDbContext db, ContextHubRequestActor actor, Guid id, CancellationToken cancellationToken)
    {
        var proposal = await db.ConversationInsights.FromSqlInterpolated($"""
            SELECT * FROM conversation_insights
            WHERE id = {id} AND tenant_id = {actor.TenantId} AND owner_user_id = {actor.UserId}
              AND source_system = {SourceSystem} AND {ProposalTag} = ANY(tags)
            FOR UPDATE
            """).SingleOrDefaultAsync(cancellationToken) ?? throw new KeyNotFoundException("Memory edit proposal is unavailable.");
        ReadTypedMetadata(proposal);
        return proposal;
    }

    private static ChatGptProposalMetadata ReadTypedMetadata(ConversationInsight proposal)
    {
        ChatGptProposalMetadata metadata;
        try { metadata = ChatGptProposalProjection.ReadMetadata(proposal.MetadataJson); }
        catch (InvalidDataException) { throw new InvalidDataException("Stored memory edit proposal is unavailable."); }
        if (metadata.ToolName != ToolName || !proposal.Tags.Contains(ToolTag, StringComparer.Ordinal))
            throw new KeyNotFoundException("Memory edit proposal is unavailable.");
        return metadata;
    }

    private static async Task SaveDecisionAsync(MemoryDbContext db, IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new SafeEditConflictException(SafeEditConflictCodes.StaleReadVersion);
        }
    }

    private void AddAudit(MemoryDbContext db, ConversationInsight proposal, ContextHubRequestActor actor,
        ConversationPromotionStatus? previous, string decision, MemoryEditReceipt? receipt = null)
    {
        var now = clock.UtcNow;
        proposal.GovernanceUpdatedAt = now;
        proposal.GovernanceLastReevaluatedAt = now;
        proposal.GovernanceLastEvidenceChangedAt = now;
        db.SecurityAuditEvents.Add(new SecurityAuditEvent
        {
            TenantId = proposal.TenantId,
            ActorUserId = actor.UserId,
            EventType = SecurityAuditEventType.ConversationInsightGovernanceUpdated,
            Outcome = proposal.PromotionStatus.ToString(),
            CreatedAt = now,
            DetailsJson = JsonSerializer.Serialize(new
            {
                proposalId = proposal.Id,
                proposal.ProjectId,
                sourceSystem = SourceSystem,
                action = "ProposalLifecycleTransition",
                transition = decision,
                fromStatus = previous?.ToString(),
                toStatus = proposal.PromotionStatus.ToString(),
                receiptId = receipt?.ReceiptId,
                memoryId = receipt?.MemoryId,
                version = receipt?.Version
            }, Json)
        });
    }

    private ContextHubRequestActor RequireActor()
    {
        var actor = actors.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryWrite);
        return actor;
    }

    private static MemoryEditScope RequireProject(ContextHubRequestActor actor, string projectId)
    {
        ActorAuthorization.EnsureProjectAllowed(actor, projectId, write: true);
        return new(actor.TenantId!.Value, actor.UserId!.Value, projectId);
    }

    private Task RequireWriteAllowedAsync(ContextHubRequestActor actor, CancellationToken cancellationToken)
        => actor.IsServiceActor ? Task.CompletedTask : maintenance.EnsureWriteAllowedAsync("memory_edit_proposal", cancellationToken);

    private Task<MemoryDbContext> OpenAsync(CancellationToken cancellationToken)
    {
        if (ambient.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Memory edit proposals require their own commit transaction.");
        return factory.CreateDbContextAsync(cancellationToken);
    }

    private static string ConversationId(MemoryEditScope scope, MemoryEditCommand command)
        => SourceSystem + ":" + ToolName + ":" + Hash(scope.TenantId.ToString("N"), scope.OwnerUserId.ToString("N"),
            ProjectContext.IdentityKey(scope.ProjectId), command.MemoryId.ToString("N"), command.RequestId);

    private static string ProposalFingerprint(string conversationId, MemoryEditCommand command)
        => Hash("MemoryEditProposalIntent/v1", conversationId, MemoryEditContract.Hash(command));

    private static string Hash(params string[] values)
        => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(values, Json)));
}
