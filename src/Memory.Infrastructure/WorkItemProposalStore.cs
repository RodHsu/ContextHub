using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Memory.Application;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Memory.Infrastructure;

/// <summary>
/// Commits proposal dispatch/finalization in owned fresh-context transactions. The core
/// edit store owns the separate mutation transaction; no pre-commit receipt or absence
/// of a receipt grants permission to dispatch a second time.
/// </summary>
public sealed class WorkItemProposalStore(IDbContextFactory<MemoryDbContext> factory, MemoryDbContext ambient,
    IRequestActorAccessor actors, IClock clock) : IWorkItemProposalStore
{
    private const string ProposalTag = "chatgpt-proposal";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions StrictJson = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public async Task<WorkItemProposalPreparation> PrepareAsync(Guid proposalId, CancellationToken ct)
    {
        var actor = RequireActor();
        RejectAmbient();
        await using var db = await OpenAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var proposal = await LockProposalAsync(db, actor, proposalId, ct);
        var command = ReadCommand(proposal);
        var hash = WorkItemEditContract.Hash(command);
        var operation = await Operations(db, actor).SingleOrDefaultAsync(x => x.ProposalId == proposalId, ct);
        if (operation is null)
        {
            if (proposal.PromotionStatus != ConversationPromotionStatus.Pending) throw InvalidState();
            await RequireTargetAsync(db, actor, command, ct);
            var now = clock.UtcNow;
            operation = new WorkItemProposalOperation
            {
                ProposalId = proposal.Id,
                TenantId = actor.TenantId!.Value,
                ActorUserId = actor.UserId!.Value,
                ProjectId = proposal.ProjectId,
                ProjectKey = ProjectContext.IdentityKey(proposal.ProjectId),
                WorkItemId = command.WorkItemId,
                OriginalRevision = command.ExpectedRevision!.Value,
                RequestId = command.RequestId,
                RequestHash = hash,
                HashVersion = WorkItemEditContract.IntentHashVersion,
                Archived = command.Operation == WorkItemEditOperation.Archive,
                State = WorkItemProposalOperationState.Dispatched,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Set<WorkItemProposalOperation>().Add(operation);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Preparation(operation, command, WorkItemProposalPreparationMode.Dispatch);
        }

        RequireIntent(operation, proposal.Id, actor, command, hash);
        RequireShape(operation);
        if (operation.State == WorkItemProposalOperationState.Applied)
        {
            await RequireTargetAsync(db, actor, command, ct);
            await RequireAppliedAsync(db, proposal, operation, command, ct);
            var result = Preparation(operation, command, WorkItemProposalPreparationMode.Applied,
                ChatGptProposalProjection.ToResult(proposal));
            await transaction.CommitAsync(ct);
            return result;
        }
        if (proposal.PromotionStatus != ConversationPromotionStatus.Pending) throw InvalidState();
        var mode = operation.State switch
        {
            WorkItemProposalOperationState.Dispatched => WorkItemProposalPreparationMode.Recover,
            WorkItemProposalOperationState.Conflict when IsKnownConflict(operation.ConflictCode) => WorkItemProposalPreparationMode.Conflict,
            _ => throw InvalidState()
        };
        await transaction.CommitAsync(ct);
        return Preparation(operation, command, mode, conflictCode: operation.ConflictCode);
    }

    public async Task<ChatGptProposalResult> FinalizeAsync(WorkItemProposalPreparation original,
        WorkItemEditReceipt receipt, string note, CancellationToken ct)
    {
        var actor = RequireActor();
        RejectAmbient();
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(receipt);
        await using var db = await OpenAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var proposal = await LockProposalAsync(db, actor, original.ProposalId, ct);
        var command = ReadCommand(proposal);
        await RequireTargetAsync(db, actor, command, ct);
        var operation = await Operations(db, actor).SingleOrDefaultAsync(x => x.ProposalId == proposal.Id, ct)
            ?? throw new WorkItemProposalOutcomeUnknownException();
        RequireIntent(operation, proposal.Id, actor, command, WorkItemEditContract.Hash(command));
        RequireOriginal(operation, original, command);
        WorkItemProposalContract.RequireSameIntent(original, receipt);

        var stored = await ReadCoreReceiptAsync(db, operation, command, receipt.ReceiptId, ct);
        RequireEquivalentReceipt(stored, receipt);
        if (operation.State == WorkItemProposalOperationState.Applied)
        {
            if (operation.ReceiptId != stored.ReceiptId) throw ReceiptUnavailable();
            await RequireAppliedAsync(db, proposal, operation, command, ct);
            await transaction.CommitAsync(ct);
            return ChatGptProposalProjection.ToResult(proposal);
        }
        if (operation.State != WorkItemProposalOperationState.Dispatched || proposal.PromotionStatus != ConversationPromotionStatus.Pending)
            throw InvalidState();

        MarkApplied(operation, stored.ReceiptId, clock.UtcNow);
        var previous = proposal.PromotionStatus;
        proposal.PromotionStatus = ConversationPromotionStatus.Promoted;
        proposal.PromotedMemoryId = operation.WorkItemId;
        proposal.Error = string.Empty;
        proposal.GovernanceReason = string.IsNullOrWhiteSpace(note) ? "Approved." : note.Trim();
        proposal.UpdatedAt = operation.UpdatedAt;
        AddLifecycleAudit(db, proposal, actor, previous,
            original.Mode == WorkItemProposalPreparationMode.Recover || receipt.Replayed ? "Recover" : "Approve", operation.UpdatedAt);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return ChatGptProposalProjection.ToResult(proposal);
    }

    public async Task RecordConflictAsync(WorkItemProposalPreparation original, string conflictCode, CancellationToken ct)
    {
        var actor = RequireActor();
        RejectAmbient();
        ArgumentNullException.ThrowIfNull(original);
        if (!IsKnownConflict(conflictCode)) throw new ArgumentException("A recognized edit conflict code is required.");
        if (original.Mode != WorkItemProposalPreparationMode.Dispatch) throw new WorkItemProposalOutcomeUnknownException();
        await using var db = await OpenAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var proposal = await LockProposalAsync(db, actor, original.ProposalId, ct);
        var command = ReadCommand(proposal);
        await RequireTargetAsync(db, actor, command, ct);
        var operation = await Operations(db, actor).SingleOrDefaultAsync(x => x.ProposalId == proposal.Id, ct)
            ?? throw new WorkItemProposalOutcomeUnknownException();
        RequireIntent(operation, proposal.Id, actor, command, WorkItemEditContract.Hash(command));
        RequireOriginal(operation, original, command);
        if (proposal.PromotionStatus != ConversationPromotionStatus.Pending) throw InvalidState();
        if (MarkConflict(operation, conflictCode, clock.UtcNow)) await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<ChatGptProposalResult> RejectAsync(ChatGptProposalDecisionRequest request, CancellationToken ct)
    {
        var actor = RequireActor();
        RejectAmbient();
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await OpenAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var proposal = await LockProposalAsync(db, actor, request.ProposalId, ct);
        var metadata = ChatGptProposalProjection.ReadMetadata(proposal.MetadataJson);
        if (metadata.ToolName != WorkItemProposalContract.ToolName) throw Unavailable();
        var operation = await Operations(db, actor).SingleOrDefaultAsync(x => x.ProposalId == proposal.Id, ct);
        if (operation is not null)
        {
            var command = ReadCommand(proposal);
            RequireIntent(operation, proposal.Id, actor, command, WorkItemEditContract.Hash(command));
            if (operation.State == WorkItemProposalOperationState.Dispatched) throw new WorkItemProposalOutcomeUnknownException();
            if (operation.State != WorkItemProposalOperationState.Conflict || !IsKnownConflict(operation.ConflictCode)) throw InvalidState();
        }
        if (proposal.PromotionStatus == ConversationPromotionStatus.Skipped)
        {
            await transaction.CommitAsync(ct);
            return ChatGptProposalProjection.ToResult(proposal);
        }
        if (proposal.PromotionStatus != ConversationPromotionStatus.Pending) throw InvalidState();
        var previous = proposal.PromotionStatus;
        var now = clock.UtcNow;
        proposal.PromotionStatus = ConversationPromotionStatus.Skipped;
        proposal.GovernanceReason = string.IsNullOrWhiteSpace(request.Note) ? "Rejected." : request.Note.Trim();
        proposal.Error = proposal.GovernanceReason;
        proposal.UpdatedAt = now;
        AddLifecycleAudit(db, proposal, actor, previous, "Reject", now);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return ChatGptProposalProjection.ToResult(proposal);
    }

    internal static bool IsKnownConflict(string? code) => code is SafeEditConflictCodes.MissingReadVersion or
        SafeEditConflictCodes.StaleReadVersion or SafeEditConflictCodes.RequestIdentityMismatch or SafeEditConflictCodes.RequiresReopen;

    internal static bool MarkConflict(WorkItemProposalOperation operation, string code, DateTimeOffset now)
    {
        if (!IsKnownConflict(code)) throw new ArgumentException("A recognized edit conflict code is required.");
        RequireShape(operation);
        if (operation.State == WorkItemProposalOperationState.Conflict && operation.ConflictCode == code) return false;
        if (operation.State != WorkItemProposalOperationState.Dispatched) throw InvalidState();
        operation.State = WorkItemProposalOperationState.Conflict;
        operation.ConflictCode = code;
        operation.UpdatedAt = now;
        return true;
    }

    internal static bool MarkApplied(WorkItemProposalOperation operation, Guid receiptId, DateTimeOffset now)
    {
        if (receiptId == Guid.Empty) throw ReceiptUnavailable();
        RequireShape(operation);
        if (operation.State == WorkItemProposalOperationState.Applied && operation.ReceiptId == receiptId) return false;
        if (operation.State != WorkItemProposalOperationState.Dispatched) throw InvalidState();
        operation.State = WorkItemProposalOperationState.Applied;
        operation.ReceiptId = receiptId;
        operation.FinalizedAt = now;
        operation.UpdatedAt = now;
        return true;
    }

    internal static void RequireEquivalentReceipt(WorkItemEditReceipt stored, WorkItemEditReceipt received)
    {
        if (JsonSerializer.Serialize(stored with { Replayed = false }, Json) !=
            JsonSerializer.Serialize(received with { Replayed = false }, Json)) throw ReceiptUnavailable();
    }

    internal static void RequireShape(WorkItemProposalOperation operation)
    {
        var valid = operation.State switch
        {
            WorkItemProposalOperationState.Dispatched => operation.ConflictCode is null && operation.ReceiptId is null && operation.FinalizedAt is null,
            WorkItemProposalOperationState.Conflict => IsKnownConflict(operation.ConflictCode) && operation.ReceiptId is null && operation.FinalizedAt is null,
            WorkItemProposalOperationState.Applied => operation.ConflictCode is null && operation.ReceiptId.HasValue
                && operation.ReceiptId.Value != Guid.Empty && operation.FinalizedAt.HasValue,
            _ => false
        };
        if (!valid) throw InvalidState();
    }

    internal static IQueryable<Guid> TargetIdentities(IQueryable<ProjectWorkItem> query, ContextHubRequestActor actor, WorkItemEditCommand command)
        => query.Where(x => x.Id == command.WorkItemId && x.TenantId == actor.TenantId
            && (actor.IsServiceActor || x.OwnerUserId == actor.UserId) && ProjectContext.Matches(x.ProjectId, command.ProjectId))
            .Select(x => x.Id);

    internal static IQueryable<WorkItemEditReceiptRecord> CoreReceipts(IQueryable<WorkItemEditReceiptRecord> query,
        WorkItemProposalOperation operation, Guid receiptId)
        => query.Where(x => x.Id == receiptId && x.TenantId == operation.TenantId
            && x.ActorUserId == operation.ActorUserId && x.ProjectKey == operation.ProjectKey && x.WorkItemId == operation.WorkItemId
            && x.RequestId == operation.RequestId && x.RequestHash == operation.RequestHash);

    internal static IQueryable<ConversationInsight> VisibleProposals(IQueryable<ConversationInsight> query, ContextHubRequestActor actor)
    {
        if (!actor.IsAuthenticated || !actor.HasUser) return query.Where(_ => false);
        query = query.Where(x => x.SourceSystem == ChatGptProposalService.SourceSystem && x.Tags.Contains(ProposalTag)
            && x.TenantId == actor.TenantId && x.OwnerUserId == actor.UserId
            && (x.PromotionStatus == ConversationPromotionStatus.Pending || x.PromotionStatus == ConversationPromotionStatus.Promoted
                || x.PromotionStatus == ConversationPromotionStatus.Skipped || x.PromotionStatus == ConversationPromotionStatus.Failed));
        if (actor.AllowedProjectIds.Count > 0)
        {
            var projects = ProjectContext.IdentityKeys(actor.AllowedProjectIds);
            query = query.Where(x => projects.Contains(ProjectContext.IdentityKey(x.ProjectId)) ||
                ProjectContext.Matches(x.ProjectId, ProjectContext.SharedProjectId) || ProjectContext.Matches(x.ProjectId, ProjectContext.UserProjectId));
        }
        return query;
    }

    private ContextHubRequestActor RequireActor()
    {
        var actor = actors.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryWrite);
        return actor;
    }

    private void RejectAmbient()
    {
        if (ambient.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Proposal coordination requires an explicit commit owner; ambient transaction composition is not enabled.");
    }

    private async Task<MemoryDbContext> OpenAsync(CancellationToken ct)
    {
        var db = await factory.CreateDbContextAsync(ct);
        if (db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL") return db;
        await db.DisposeAsync();
        throw new NotSupportedException("Proposal coordination requires the PostgreSQL transaction-lock implementation.");
    }

    private static async Task<ConversationInsight> LockProposalAsync(MemoryDbContext db, ContextHubRequestActor actor, Guid proposalId, CancellationToken ct)
    {
        if (proposalId == Guid.Empty || !await VisibleProposals(db.ConversationInsights.AsNoTracking(), actor).AnyAsync(x => x.Id == proposalId, ct)) throw Unavailable();
        await db.AcquireTransactionLockAsync($"work-item-proposal:{actor.TenantId:N}:{proposalId:N}", ct);
        // Generic insight writers do not share this advisory lock. Serialize their
        // row updates before reading the immutable intent and creating its marker.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM conversation_insights WHERE id = {proposalId} FOR UPDATE", ct);
        var proposal = await VisibleProposals(db.ConversationInsights, actor).SingleOrDefaultAsync(x => x.Id == proposalId, ct) ?? throw Unavailable();
        ActorAuthorization.EnsureProjectAllowed(actor, proposal.ProjectId, write: true);
        return proposal;
    }

    private static WorkItemEditCommand ReadCommand(ConversationInsight proposal)
    {
        var metadata = ChatGptProposalProjection.ReadMetadata(proposal.MetadataJson);
        if (metadata.ToolName != WorkItemProposalContract.ToolName) throw Unavailable();
        return WorkItemProposalContract.Command(proposal.ProjectId, ParseArchivePayload(metadata.PayloadJson));
    }

    internal static ProjectWorkItemArchiveRequest ParseArchivePayload(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("A saved versioned WorkItem archive intent is required.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (document.RootElement.EnumerateObject().Any(property => !names.Add(property.Name)))
                throw new ArgumentException("A saved WorkItem archive intent must be unambiguous.");
            return JsonSerializer.Deserialize<ProjectWorkItemArchiveRequest>(json, StrictJson)
                ?? throw new ArgumentException("A saved versioned WorkItem archive intent is required.");
        }
        catch (JsonException) { throw new ArgumentException("A saved versioned WorkItem archive intent is required."); }
    }

    private static async Task RequireTargetAsync(MemoryDbContext db, ContextHubRequestActor actor, WorkItemEditCommand command, CancellationToken ct)
    {
        var id = await TargetIdentities(db.ProjectWorkItems.AsNoTracking(), actor, command).SingleOrDefaultAsync(ct);
        if (id == Guid.Empty) throw Unavailable();
    }

    private static IQueryable<WorkItemProposalOperation> Operations(MemoryDbContext db, ContextHubRequestActor actor)
        => db.Set<WorkItemProposalOperation>().Where(x => x.TenantId == actor.TenantId && x.ActorUserId == actor.UserId);

    private static void RequireIntent(WorkItemProposalOperation operation, Guid proposalId, ContextHubRequestActor actor,
        WorkItemEditCommand command, string hash)
    {
        RequireShape(operation);
        if (operation.ProposalId != proposalId || operation.TenantId != actor.TenantId || operation.ActorUserId != actor.UserId
            || operation.ProjectKey != ProjectContext.IdentityKey(command.ProjectId) || !ProjectContext.Matches(operation.ProjectId, command.ProjectId)
            || operation.WorkItemId != command.WorkItemId || operation.OriginalRevision != command.ExpectedRevision
            || operation.RequestId != command.RequestId || operation.RequestHash != hash || operation.HashVersion != WorkItemEditContract.IntentHashVersion
            || operation.Archived != (command.Operation == WorkItemEditOperation.Archive)) throw ReceiptUnavailable();
    }

    private static void RequireOriginal(WorkItemProposalOperation operation, WorkItemProposalPreparation original, WorkItemEditCommand command)
    {
        if (original.ProposalId != operation.ProposalId || original.TenantId != operation.TenantId || original.ActorUserId != operation.ActorUserId
            || !Enum.IsDefined(original.Mode)
            || original.RequestHash != operation.RequestHash || WorkItemEditContract.Hash(original.Command) != operation.RequestHash
            || original.Command.WorkItemId != command.WorkItemId || original.Command.ExpectedRevision != command.ExpectedRevision
            || !ProjectContext.Matches(original.Command.ProjectId, command.ProjectId) || original.Command.RequestId != command.RequestId)
            throw ReceiptUnavailable();
    }

    private static WorkItemProposalPreparation Preparation(WorkItemProposalOperation operation, WorkItemEditCommand command,
        WorkItemProposalPreparationMode mode, ChatGptProposalResult? applied = null, string? conflictCode = null)
        => new(operation.ProposalId, operation.TenantId, operation.ActorUserId, command, operation.RequestHash, mode, applied, conflictCode);

    private static async Task<WorkItemEditReceipt> ReadCoreReceiptAsync(MemoryDbContext db, WorkItemProposalOperation operation,
        WorkItemEditCommand command, Guid receiptId, CancellationToken ct)
    {
        var row = await CoreReceipts(db.WorkItemEditReceipts.AsNoTracking(), operation, receiptId).SingleOrDefaultAsync(ct)
            ?? throw ReceiptUnavailable();
        var receipt = WorkItemEditStore.ReadBoundReceipt(row, operation.RequestHash);
        WorkItemProposalContract.RequireSameIntent(Preparation(operation, command, WorkItemProposalPreparationMode.Recover), receipt);
        return receipt;
    }

    private static async Task RequireAppliedAsync(MemoryDbContext db, ConversationInsight proposal, WorkItemProposalOperation operation,
        WorkItemEditCommand command, CancellationToken ct)
    {
        if (operation.State != WorkItemProposalOperationState.Applied || operation.ReceiptId is not Guid receiptId
            || operation.FinalizedAt is null || operation.ConflictCode is not null || proposal.PromotionStatus != ConversationPromotionStatus.Promoted
            || proposal.PromotedMemoryId != operation.WorkItemId) throw ReceiptUnavailable();
        await ReadCoreReceiptAsync(db, operation, command, receiptId, ct);
    }

    private static void AddLifecycleAudit(MemoryDbContext db, ConversationInsight proposal, ContextHubRequestActor actor,
        ConversationPromotionStatus previous, string transition, DateTimeOffset now)
    {
        proposal.GovernanceUpdatedAt = now;
        proposal.GovernanceLastReevaluatedAt = now;
        proposal.GovernanceLastEvidenceChangedAt = now;
        var metadata = ChatGptProposalProjection.ReadMetadata(proposal.MetadataJson);
        db.SecurityAuditEvents.Add(new SecurityAuditEvent
        {
            TenantId = proposal.TenantId,
            ActorUserId = actor.UserId,
            EventType = SecurityAuditEventType.ConversationInsightGovernanceUpdated,
            Outcome = proposal.PromotionStatus.ToString(),
            DetailsJson = JsonSerializer.Serialize(new
            {
                proposalId = proposal.Id,
                proposal.ProjectId,
                sourceSystem = ChatGptProposalService.SourceSystem,
                action = "ProposalLifecycleTransition",
                transition,
                fromStatus = previous.ToString(),
                toStatus = proposal.PromotionStatus.ToString(),
                governanceRunId = metadata.GovernanceRunId
            }, Json),
            CreatedAt = now
        });
    }

    private static KeyNotFoundException Unavailable() => new("Work item proposal is unavailable.");
    private static InvalidOperationException InvalidState() => new("The work item proposal is not in an eligible state.");
    private static InvalidDataException ReceiptUnavailable() => new("The stored proposal execution could not be bound to its original intent.");
}
