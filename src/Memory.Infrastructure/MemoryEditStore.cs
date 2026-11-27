using System.Data;
using System.Text.Json;
using Memory.Application;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Memory.Infrastructure;

public sealed class MemoryEditStore(
    IDbContextFactory<MemoryDbContext> factory,
    MemoryDbContext ambient,
    IChunkingService chunking,
    IEmbeddingProvider embedding,
    IInstanceBehaviorSettingsAccessor behavior,
    IClock clock) : IMemoryEditStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<MemoryEditReceipt> ApplyAsync(MemoryEditScope scope, MemoryEditCommand command, CancellationToken cancellationToken)
    {
        if (ambient.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Memory edits require their own commit transaction.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var receipt = await StageAsync(db, scope, command, cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new SafeEditConflictException(SafeEditConflictCodes.StaleReadVersion);
        }
        return receipt;
    }

    internal static async Task<MemoryItem> LockTargetAsync(MemoryDbContext db, MemoryEditScope scope, Guid memoryId, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("A memory edit transaction is required.");
        // PostgreSQL only locks a row that still belongs to the expected owner and project after waiting.
        var memory = await db.MemoryItems.FromSqlInterpolated($"""
            SELECT *
            FROM memory_items
            WHERE id = {memoryId}
              AND tenant_id = {scope.TenantId}
              AND owner_user_id = {scope.OwnerUserId}
              AND public.project_identity_equals(project_id, {scope.ProjectId})
            FOR UPDATE
            """).SingleOrDefaultAsync(cancellationToken) ?? throw Unavailable();
        if (memory.IsReadOnly || memory.MemoryType == MemoryType.Preference) throw Unavailable();
        return memory;
    }

    internal async Task<MemoryEditReceipt> StageAsync(MemoryDbContext db, MemoryEditScope scope, MemoryEditCommand command, CancellationToken cancellationToken)
    {
        if (!ProjectContext.Matches(command.ProjectId, scope.ProjectId)) throw Unavailable();
        var hash = MemoryEditContract.Hash(command);
        var memory = await LockTargetAsync(db, scope, command.MemoryId, cancellationToken);
        var replay = await FindCommittedReceiptAsync(db, scope, command, hash, cancellationToken);
        if (replay is not null) return replay;

        if (memory.Version != command.ExpectedVersion)
            throw new SafeEditConflictException(SafeEditConflictCodes.StaleReadVersion, memory.Version);
        if (memory.Version == int.MaxValue) throw new InvalidOperationException("Memory version limit reached.");

        var previousVersion = memory.Version;
        var patch = command.Patch;
        var now = clock.UtcNow;
        now = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
        memory.Title = patch.Title ?? memory.Title;
        memory.Content = patch.Content ?? memory.Content;
        memory.Summary = patch.Summary ?? memory.Summary;
        memory.Tags = patch.Tags?.ToArray() ?? memory.Tags;
        memory.Importance = patch.Importance ?? memory.Importance;
        memory.Confidence = patch.Confidence ?? memory.Confidence;
        memory.MetadataJson = patch.MetadataJson ?? memory.MetadataJson;
        memory.Version++;
        memory.UpdatedAt = now;
        var revision = new MemoryItemRevision
        {
            MemoryItemId = memory.Id,
            Version = memory.Version,
            Title = memory.Title,
            Content = memory.Content,
            Summary = memory.Summary,
            MetadataJson = memory.MetadataJson,
            ChangedBy = "versioned-update",
            CreatedAt = now
        };
        db.MemoryItemRevisions.Add(revision);

        var existingChunks = await db.MemoryItemChunks.Where(row => row.MemoryItemId == memory.Id).ToListAsync(cancellationToken);
        db.MemoryItemChunks.RemoveRange(existingChunks);
        foreach (var chunk in chunking.Chunk(memory.MemoryType, memory.SourceType, memory.Content))
        {
            db.MemoryItemChunks.Add(new MemoryItemChunk
            {
                MemoryItemId = memory.Id,
                ChunkKind = chunk.Kind,
                ChunkIndex = chunk.Index,
                ChunkText = chunk.Text,
                MetadataJson = chunk.MetadataJson,
                CreatedAt = now
            });
        }
        await QueueJobsAsync(db, memory, now, cancellationToken);
        var receipt = new MemoryEditReceiptRecord
        {
            TenantId = scope.TenantId,
            OwnerUserId = scope.OwnerUserId,
            ProjectId = memory.ProjectId,
            ProjectKey = ProjectContext.IdentityKey(memory.ProjectId),
            MemoryItemId = memory.Id,
            RequestId = command.RequestId,
            RequestHash = hash,
            HashVersion = MemoryEditContract.IntentHashVersion,
            PreviousVersion = previousVersion,
            Version = memory.Version,
            RevisionId = revision.Id,
            CommittedAt = now
        };
        db.MemoryEditReceipts.Add(receipt);
        return Map(receipt, replayed: false);
    }

    internal static async Task<MemoryEditReceipt?> FindCommittedReceiptAsync(MemoryDbContext db, MemoryEditScope scope,
        MemoryEditCommand command, string hash, CancellationToken cancellationToken)
    {
        var record = await ReceiptQuery(db, scope, command.MemoryId, command.RequestId).SingleOrDefaultAsync(cancellationToken);
        return record is null ? null : ReadBoundReceipt(record, hash);
    }

    public async Task<MemoryEditReceipt?> ReadReceiptAsync(MemoryEditScope scope, MemoryEditRecoveryRequest request, CancellationToken cancellationToken)
    {
        if (!ProjectContext.Matches(request.ProjectId, scope.ProjectId)) return null;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        // Read receipt and current resource authority in one statement. History cannot restore expired access.
        var receipt = await ReceiptQuery(db, scope, request.MemoryId, request.RequestId)
            .SingleOrDefaultAsync(record => db.MemoryItems.Any(memory =>
                memory.Id == record.MemoryItemId && memory.TenantId == scope.TenantId && memory.OwnerUserId == scope.OwnerUserId &&
                ProjectContext.Matches(memory.ProjectId, scope.ProjectId) && !memory.IsReadOnly && memory.MemoryType != MemoryType.Preference),
                cancellationToken);
        return receipt is null ? null : ReadBoundReceipt(receipt, request.ExpectedRequestHash);
    }

    private async Task QueueJobsAsync(MemoryDbContext db, MemoryItem memory, DateTimeOffset now, CancellationToken cancellationToken)
    {
        db.MemoryJobs.Add(NewJob(memory, MemoryJobType.Reindex, new
        {
            modelKey = EmbeddingModelIdentity.RequireConfigured(null, embedding.ModelKey),
            memoryItemId = memory.Id,
            projectId = memory.ProjectId
        }, now));
        if (memory.MemoryType is not (MemoryType.Fact or MemoryType.Decision or MemoryType.Artifact)) return;
        var settings = await behavior.GetCurrentAsync(cancellationToken);
        if (!settings.SharedSummaryAutoRefreshEnabled) return;
        var hasPending = await db.MemoryJobs.AnyAsync(row => row.TenantId == memory.TenantId && row.OwnerUserId == memory.OwnerUserId &&
            ProjectContext.Matches(row.ProjectId, memory.ProjectId) && row.JobType == MemoryJobType.RefreshSummary &&
            row.Status == MemoryJobStatus.Pending, cancellationToken);
        if (!hasPending)
        {
            db.MemoryJobs.Add(NewJob(memory, MemoryJobType.RefreshSummary, new
            {
                projectId = memory.ProjectId,
                includedProjectIds = Array.Empty<string>(),
                rebuildAll = false
            }, now));
        }
    }

    private static MemoryJob NewJob<TPayload>(MemoryItem memory, MemoryJobType type, TPayload payload, DateTimeOffset now)
        => new()
        {
            TenantId = memory.TenantId,
            OwnerUserId = memory.OwnerUserId,
            ProjectId = memory.ProjectId,
            JobType = type,
            Status = MemoryJobStatus.Pending,
            PayloadJson = JsonSerializer.Serialize(payload, Json),
            CreatedAt = now
        };

    private static IQueryable<MemoryEditReceiptRecord> ReceiptQuery(MemoryDbContext db, MemoryEditScope scope, Guid memoryId, string requestId)
    {
        var projectKey = ProjectContext.IdentityKey(scope.ProjectId);
        return db.MemoryEditReceipts.AsNoTracking().Where(row => row.TenantId == scope.TenantId && row.OwnerUserId == scope.OwnerUserId &&
            row.ProjectKey == projectKey && row.MemoryItemId == memoryId && row.RequestId == requestId);
    }

    private static MemoryEditReceipt ReadBoundReceipt(MemoryEditReceiptRecord receipt, string requestHash)
    {
        if (receipt.HashVersion != MemoryEditContract.IntentHashVersion || receipt.RequestHash != requestHash)
            throw new SafeEditConflictException(SafeEditConflictCodes.RequestIdentityMismatch);
        return Map(receipt, replayed: true);
    }

    private static MemoryEditReceipt Map(MemoryEditReceiptRecord record, bool replayed)
        => new(record.Id, record.ProjectId, record.MemoryItemId, record.RequestId, record.RequestHash, record.HashVersion,
            record.PreviousVersion, record.Version, record.RevisionId, record.CommittedAt, replayed);

    private static KeyNotFoundException Unavailable() => new("Memory edit is unavailable.");
}

public sealed class MemoryEditReceiptRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid OwnerUserId { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public string ProjectKey { get; set; } = string.Empty;
    public Guid MemoryItemId { get; set; }
    public string RequestId { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public string HashVersion { get; set; } = string.Empty;
    public int PreviousVersion { get; set; }
    public int Version { get; set; }
    public Guid RevisionId { get; set; }
    public DateTimeOffset CommittedAt { get; set; }
}
