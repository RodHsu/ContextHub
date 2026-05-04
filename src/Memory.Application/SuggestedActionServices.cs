using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Memory.Domain;

namespace Memory.Application;

public sealed class SuggestedActionService(
    IApplicationDbContext dbContext,
    IMemoryService memoryService,
    IGovernanceService governanceService,
    IClock clock) : ISuggestedActionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<SuggestedActionResult>> ListAsync(SuggestedActionListRequest request, CancellationToken cancellationToken)
    {
        var projectId = ProjectContext.Normalize(request.ProjectId);
        var query = dbContext.SuggestedActions.AsNoTracking().Where(x => x.ProjectId == projectId);

        if (request.Status.HasValue)
        {
            query = query.Where(x => x.Status == request.Status.Value);
        }

        if (request.Type.HasValue)
        {
            query = query.Where(x => x.Type == request.Type.Value);
        }

        var entities = await query
            .OrderByDescending(x => x.UpdatedAt)
            .Take(Math.Clamp(request.Limit, 1, 200))
            .ToListAsync(cancellationToken);
        return entities.Select(Map).ToArray();
    }

    public async Task<SuggestedActionMutationResult> AcceptAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await dbContext.SuggestedActions
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Suggested action '{id}' was not found.");

        entity.Status = SuggestedActionStatus.Accepted;
        entity.UpdatedAt = clock.UtcNow;
        Guid? jobId = null;
        var payload = DeserializePayload(entity.PayloadJson);

        try
        {
            switch (entity.Type)
            {
                case SuggestedActionType.SyncSourceNow:
                    jobId = await EnqueueJobAsync(
                        entity.ProjectId,
                        MemoryJobType.SyncSource,
                        new
                        {
                            sourceConnectionId = ReadGuid(payload, "sourceConnectionId"),
                            projectId = entity.ProjectId,
                            trigger = SourceSyncTrigger.Action,
                            force = true
                        },
                        cancellationToken);
                    break;
                case SuggestedActionType.ReindexProject:
                    jobId = (await memoryService.EnqueueReindexAsync(new EnqueueReindexRequest(ProjectId: entity.ProjectId), cancellationToken)).JobId;
                    break;
                case SuggestedActionType.RefreshSharedSummary:
                    jobId = (await memoryService.EnqueueSummaryRefreshAsync(new EnqueueSummaryRefreshRequest(entity.ProjectId), cancellationToken)).JobId;
                    break;
                case SuggestedActionType.ArchiveStaleMemory:
                    if (TryReadGuid(payload, "primaryMemoryId") is Guid memoryId)
                    {
                        await ArchiveMemoryAsync(memoryId, cancellationToken);
                    }

                    break;
                case SuggestedActionType.MergeDuplicateCandidate:
                case SuggestedActionType.ReviewConflictCandidate:
                    if (TryReadString(payload, "findingId") is { } findingKey)
                    {
                        var finding = await dbContext.GovernanceFindings.FirstOrDefaultAsync(x => x.DedupKey == findingKey, cancellationToken);
                        if (finding is not null)
                        {
                            await governanceService.AcceptAsync(finding.Id, cancellationToken);
                        }
                    }

                    break;
            }

            entity.Status = SuggestedActionStatus.Executed;
            entity.ExecutedAt = clock.UtcNow;
            entity.UpdatedAt = clock.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            entity.Status = SuggestedActionStatus.Failed;
            entity.Error = ex.Message;
            entity.UpdatedAt = clock.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
            throw;
        }

        return new SuggestedActionMutationResult(Map(entity), jobId);
    }

    public async Task<SuggestedActionResult> DismissAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await dbContext.SuggestedActions
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Suggested action '{id}' was not found.");
        entity.Status = SuggestedActionStatus.Dismissed;
        entity.UpdatedAt = clock.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    private async Task<Guid> EnqueueJobAsync<TPayload>(string projectId, MemoryJobType jobType, TPayload payload, CancellationToken cancellationToken)
    {
        var job = new MemoryJob
        {
            ProjectId = projectId,
            JobType = jobType,
            Status = MemoryJobStatus.Pending,
            PayloadJson = JsonSerializer.Serialize(payload, JsonOptions),
            CreatedAt = clock.UtcNow
        };

        await dbContext.MemoryJobs.AddAsync(job, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return job.Id;
    }

    private async Task ArchiveMemoryAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await dbContext.MemoryItems.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null)
        {
            return;
        }

        entity.Status = MemoryStatus.Archived;
        entity.UpdatedAt = clock.UtcNow;
        entity.Version += 1;
        await dbContext.MemoryItemRevisions.AddAsync(new MemoryItemRevision
        {
            MemoryItemId = entity.Id,
            Version = entity.Version,
            Title = entity.Title,
            Content = entity.Content,
            Summary = entity.Summary,
            MetadataJson = entity.MetadataJson,
            ChangedBy = "suggested-action",
            CreatedAt = clock.UtcNow
        }, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static Dictionary<string, JsonElement> DeserializePayload(string payloadJson)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson, JsonOptions)
            ?? [];

    private static Guid ReadGuid(IReadOnlyDictionary<string, JsonElement> payload, string key)
        => TryReadGuid(payload, key) ?? throw new InvalidOperationException($"Suggested action payload is missing '{key}'.");

    private static Guid? TryReadGuid(IReadOnlyDictionary<string, JsonElement> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return Guid.TryParse(value.GetString(), out var id) ? id : null;
    }

    private static string? TryReadString(IReadOnlyDictionary<string, JsonElement> payload, string key)
        => payload.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static SuggestedActionResult Map(SuggestedAction entity)
        => new(
            entity.Id,
            entity.ProjectId,
            entity.Type,
            entity.Status,
            entity.Title,
            entity.Summary,
            entity.PayloadJson,
            entity.Error,
            entity.CreatedAt,
            entity.UpdatedAt,
            entity.ExecutedAt);
}
