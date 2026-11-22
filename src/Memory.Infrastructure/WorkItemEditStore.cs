using System.Data;
using System.Text.Json;
using Memory.Application;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Memory.Infrastructure;

/// <summary>
/// Owns the entire edit commit in a fresh context. It is not routed to legacy writers
/// until their adapters participate in the revision protocol. Ambient composition is
/// rejected rather than returning a receipt for an outer transaction not yet committed.
/// </summary>
public sealed class WorkItemEditStore(IDbContextFactory<MemoryDbContext> factory, MemoryDbContext ambient, IClock clock) : IWorkItemEditStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<WorkItemEditReceipt> ApplyAsync(WorkItemEditScope scope, WorkItemEditCommand command, CancellationToken ct)
    {
        if (ambient.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Work item edits require an explicit commit owner; ambient transaction composition is not enabled.");
        var normalized = WorkItemEditContract.Normalize(command);
        if (!ProjectContext.Matches(scope.ProjectId, normalized.ProjectId)) throw Unavailable();
        var hash = WorkItemEditContract.Hash(normalized);
        await using var db = await factory.CreateDbContextAsync(ct);
        if (db.Database.ProviderName != "Npgsql.EntityFrameworkCore.PostgreSQL")
            throw new NotSupportedException("Work item edit persistence requires the PostgreSQL transaction-lock implementation.");

        // ReadCommitted gives the query after a contended advisory lock a fresh snapshot,
        // allowing the second identical request to see the first committed receipt.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        // Do not let a caller lock an item outside its current resource scope. Recheck
        // the same predicate after waiting, because ownership/scope could have changed.
        if (!await Visible(db.ProjectWorkItems.AsNoTracking(), scope).AnyAsync(x => x.Id == normalized.WorkItemId, ct))
            throw Unavailable();
        await db.AcquireTransactionLockAsync($"work-item-edit:{scope.TenantId:N}:{normalized.WorkItemId:N}", ct);
        var entity = await Visible(db.ProjectWorkItems.Include(x => x.ChecklistItems), scope)
            .SingleOrDefaultAsync(x => x.Id == normalized.WorkItemId, ct) ?? throw Unavailable();
        var projectKey = ProjectContext.IdentityKey(entity.ProjectId);
        var previous = await ReceiptQuery(db, scope, normalized.WorkItemId, projectKey, normalized.RequestId).SingleOrDefaultAsync(ct);
        if (previous is not null)
        {
            var replay = ReadBoundReceipt(previous, hash) with { Replayed = true };
            await transaction.CommitAsync(ct);
            return replay;
        }

        WorkItemEditContract.RequireRevision(normalized.ExpectedRevision, entity.Revision);
        var now = clock.UtcNow;
        var beforeRevision = entity.Revision;
        var before = WorkItemEditProjection.ToResult(entity);
        var originalChildren = entity.ChecklistItems.ToDictionary(x => x.Id);
        var changed = WorkItemEditAggregate.Apply(entity, normalized, now);
        var after = WorkItemEditProjection.ToResult(entity);
        Guid? historyId = null;
        if (changed)
        {
            TrackChecklistChanges(db, entity, originalChildren);
            var history = new WorkItemEditHistory
            {
                TenantId = scope.TenantId,
                ActorUserId = scope.ActorUserId,
                OwnerUserId = entity.OwnerUserId,
                WorkItemId = entity.Id,
                ProjectId = entity.ProjectId,
                PreviousRevision = beforeRevision,
                Revision = entity.Revision,
                Operation = normalized.Operation.ToString(),
                RequestId = normalized.RequestId,
                BeforeJson = JsonSerializer.Serialize(before, Json),
                AfterJson = JsonSerializer.Serialize(after, Json),
                CreatedAt = now
            };
            historyId = history.Id;
            db.WorkItemEditHistories.Add(history);
            db.AuthorityOutboxEvents.Add(new AuthorityOutboxEvent
            {
                TenantId = scope.TenantId,
                ProjectId = entity.ProjectId,
                Category = "WorkItems",
                AggregateType = nameof(ProjectWorkItem),
                AggregateId = entity.Id.ToString("D"),
                EventType = normalized.Operation.ToString(),
                AuthorityRevision = entity.Revision,
                PayloadJson = JsonSerializer.Serialize(new { schemaVersion = "1.0", workItemId = entity.Id, revision = entity.Revision, historyId }, Json),
                OccurredAt = now
            });
        }
        var receiptId = Guid.NewGuid();
        var receipt = new WorkItemEditReceipt(receiptId, historyId, entity.ProjectId, entity.Id, normalized.RequestId,
            hash, WorkItemEditContract.IntentHashVersion, normalized.Operation, beforeRevision, entity.Revision, changed, now, after);
        db.WorkItemEditReceipts.Add(new WorkItemEditReceiptRecord
        {
            Id = receiptId,
            TenantId = scope.TenantId,
            ActorUserId = scope.ActorUserId,
            WorkItemId = entity.Id,
            ProjectId = entity.ProjectId,
            ProjectKey = projectKey,
            RequestId = normalized.RequestId,
            RequestHash = hash,
            ResultJson = JsonSerializer.Serialize(receipt, Json),
            CreatedAt = now
        });
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new SafeEditConflictException(SafeEditConflictCodes.StaleReadVersion);
        }
        // A cancellation/transport failure at commit is not automatically replayed. The
        // caller must recover this exact request identity through the read-only lookup.
        return receipt;
    }

    public async Task<WorkItemEditReceipt?> ReadReceiptAsync(WorkItemEditScope scope, WorkItemEditRecoveryRequest request, CancellationToken ct)
    {
        var normalized = WorkItemEditRecovery.Normalize(request);
        if (!ProjectContext.Matches(scope.ProjectId, normalized.ProjectId)) throw Unavailable();
        await using var db = await factory.CreateDbContextAsync(ct);
        // Reauthorize the current resource scope before revealing a historical receipt.
        // Archived rows remain recoverable; no mutable data is restored or replayed.
        var project = await Visible(db.ProjectWorkItems.AsNoTracking(), scope).Where(x => x.Id == normalized.WorkItemId)
            .Select(x => x.ProjectId).SingleOrDefaultAsync(ct);
        if (project is null) return null;
        var row = await ReceiptQuery(db, scope, normalized.WorkItemId, ProjectContext.IdentityKey(project), normalized.RequestId).SingleOrDefaultAsync(ct);
        return row is null ? null : ReadBoundReceipt(row, normalized.ExpectedRequestHash);
    }

    private static IQueryable<ProjectWorkItem> Visible(IQueryable<ProjectWorkItem> query, WorkItemEditScope scope)
        => query.Where(x => x.TenantId == scope.TenantId && (scope.IsServiceActor || x.OwnerUserId == scope.ActorUserId)
            && ProjectContext.Matches(x.ProjectId, scope.ProjectId));

    private static IQueryable<WorkItemEditReceiptRecord> ReceiptQuery(MemoryDbContext db, WorkItemEditScope scope,
        Guid workItemId, string projectKey, string requestId)
        => db.WorkItemEditReceipts.AsNoTracking().Where(x => x.TenantId == scope.TenantId && x.ActorUserId == scope.ActorUserId
            && x.ProjectKey == projectKey && x.WorkItemId == workItemId && x.RequestId == requestId);

    internal static WorkItemEditReceipt ReadBoundReceipt(WorkItemEditReceiptRecord row, string expectedHash)
    {
        if (!string.Equals(row.RequestHash, expectedHash, StringComparison.Ordinal))
            throw new SafeEditConflictException(SafeEditConflictCodes.RequestIdentityMismatch);
        var value = JsonSerializer.Deserialize<WorkItemEditReceipt>(row.ResultJson, Json)
            ?? throw new InvalidDataException("Stored work item receipt is unavailable.");
        if (value.Result is null || string.IsNullOrWhiteSpace(value.ProjectId) || value.ReceiptId != row.Id || value.WorkItemId != row.WorkItemId || value.RequestId != row.RequestId ||
            value.RequestHash != row.RequestHash || value.HashVersion != WorkItemEditContract.IntentHashVersion ||
            ProjectContext.IdentityKey(value.ProjectId) != row.ProjectKey ||
            !ProjectContext.Matches(value.ProjectId, row.ProjectId) || !ProjectContext.Matches(value.Result.ProjectId, row.ProjectId) ||
            value.Result.Id != row.WorkItemId || value.Result.Revision != value.Revision || value.PreviousRevision <= 0 || value.Revision <= 0 ||
            !Enum.IsDefined(value.Operation) ||
            (value.Changed ? value.HistoryId is null || value.HistoryId == Guid.Empty || value.PreviousRevision == long.MaxValue || value.Revision != value.PreviousRevision + 1
                : value.HistoryId is not null || value.Revision != value.PreviousRevision))
            throw new InvalidDataException("Stored work item receipt identity is inconsistent.");
        return value;
    }

    internal static void TrackChecklistChanges(MemoryDbContext db, ProjectWorkItem entity,
        IReadOnlyDictionary<Guid, ProjectWorkItemChecklistItem> originalChildren)
    {
        var retained = entity.ChecklistItems.Select(x => x.Id).ToHashSet();
        db.ProjectWorkItemChecklistItems.RemoveRange(originalChildren.Values.Where(x => !retained.Contains(x.Id)));
        // Caller-assigned nondefault keys MUST be inserts. Relationship discovery can
        // otherwise select Modified/UPDATE and risk changing an unrelated existing row.
        db.ProjectWorkItemChecklistItems.AddRange(entity.ChecklistItems.Where(x => !originalChildren.ContainsKey(x.Id)));
    }
    private static KeyNotFoundException Unavailable() => new("Work item is unavailable.");
}
