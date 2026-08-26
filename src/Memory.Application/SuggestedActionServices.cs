using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Memory.Domain;

namespace Memory.Application;

public sealed class SuggestedActionService(
    IApplicationDbContext dbContext,
    IMemoryService memoryService,
    IGovernanceService governanceService,
    IBackgroundJobQueue jobQueue,
    IRequestActorAccessor actorAccessor,
    IClock clock) : ISuggestedActionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<SuggestedActionResult>> ListAsync(SuggestedActionListRequest request, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryRead);
        var projectId = ProjectContext.Normalize(request.ProjectId);
        ActorAuthorization.EnsureProjectAllowed(actor, projectId, write: false);
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
            .Skip(Math.Max(0, request.Offset))
            .Take(Math.Clamp(request.Limit, 1, 200))
            .ToListAsync(cancellationToken);
        return entities.Select(Map).ToArray();
    }

    public async Task<SuggestedActionMutationResult> AcceptAsync(Guid id, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryWrite);
        var entity = await dbContext.SuggestedActions
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Suggested action '{id}' was not found.");
        ActorAuthorization.EnsureProjectAllowed(actor, entity.ProjectId, write: true);
        if (entity.Status is SuggestedActionStatus.Executed or SuggestedActionStatus.Dismissed or SuggestedActionStatus.Superseded)
        {
            return new SuggestedActionMutationResult(Map(entity));
        }

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
                            if (entity.Type == SuggestedActionType.MergeDuplicateCandidate)
                            {
                                await EnsureReplacementRelationshipAsync(finding, cancellationToken);
                            }
                            await governanceService.AcceptAsync(finding.Id, cancellationToken);
                        }
                    }

                    break;
            }

            entity.Status = SuggestedActionStatus.Executed;
            entity.ExecutedAt = clock.UtcNow;
            entity.UpdatedAt = clock.UtcNow;
            await SupersedeEquivalentActionsAsync(entity, cancellationToken);
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
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryWrite);
        var entity = await dbContext.SuggestedActions
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Suggested action '{id}' was not found.");
        ActorAuthorization.EnsureProjectAllowed(actor, entity.ProjectId, write: true);
        if (entity.Status is SuggestedActionStatus.Dismissed or SuggestedActionStatus.Executed or SuggestedActionStatus.Superseded)
        {
            return Map(entity);
        }
        entity.Status = SuggestedActionStatus.Dismissed;
        entity.UpdatedAt = clock.UtcNow;
        var payload = DeserializePayload(entity.PayloadJson);
        if (TryReadString(payload, "findingId") is { } findingKey)
        {
            var finding = await dbContext.GovernanceFindings.FirstOrDefaultAsync(x => x.DedupKey == findingKey, cancellationToken);
            if (finding is not null)
            {
                await governanceService.DismissAsync(finding.Id, cancellationToken);
            }
        }
        await SupersedeEquivalentActionsAsync(entity, cancellationToken);
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

        return await jobQueue.EnqueueAsync(job, cancellationToken);
    }

    private async Task ArchiveMemoryAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await dbContext.MemoryItems.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null || entity.Status == MemoryStatus.Archived)
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

    private async Task EnsureReplacementRelationshipAsync(GovernanceFinding finding, CancellationToken cancellationToken)
    {
        if (!finding.PrimaryMemoryId.HasValue || !finding.SecondaryMemoryId.HasValue)
        {
            return;
        }

        var pair = new[] { finding.PrimaryMemoryId.Value, finding.SecondaryMemoryId.Value };
        var memories = await dbContext.MemoryItems
            .Where(x => pair.Contains(x.Id))
            .ToListAsync(cancellationToken);
        if (memories.Count != 2)
        {
            return;
        }

        var authoritative = memories
            .OrderByDescending(AuthorityScore)
            .ThenByDescending(x => x.UpdatedAt)
            .ThenBy(x => x.Id)
            .First();
        var replaced = memories.Single(x => x.Id != authoritative.Id);
        var exists = await dbContext.MemoryLinks.AnyAsync(
            x => x.LinkType == "replaced_by" &&
                 ((x.FromId == replaced.Id && x.ToId == authoritative.Id) ||
                  (x.FromId == authoritative.Id && x.ToId == replaced.Id)),
            cancellationToken);
        if (exists)
        {
            return;
        }

        await dbContext.MemoryLinks.AddAsync(new MemoryLink
        {
            Id = DeterministicLinkId(replaced.Id, authoritative.Id),
            FromId = replaced.Id,
            ToId = authoritative.Id,
            LinkType = "replaced_by",
            CreatedAt = clock.UtcNow
        }, cancellationToken);
    }

    private async Task SupersedeEquivalentActionsAsync(SuggestedAction terminal, CancellationToken cancellationToken)
    {
        var dedupKey = GetDedupKey(terminal);
        if (string.IsNullOrWhiteSpace(dedupKey))
        {
            return;
        }

        var candidates = await dbContext.SuggestedActions
            .Where(x => x.Id != terminal.Id &&
                        x.ProjectId == terminal.ProjectId &&
                        x.Type == terminal.Type &&
                        (x.Status == SuggestedActionStatus.Pending || x.Status == SuggestedActionStatus.Accepted))
            .ToListAsync(cancellationToken);
        foreach (var candidate in candidates.Where(x => string.Equals(GetDedupKey(x), dedupKey, StringComparison.Ordinal)))
        {
            candidate.Status = SuggestedActionStatus.Superseded;
            candidate.UpdatedAt = clock.UtcNow;
        }
    }

    private static decimal AuthorityScore(MemoryItem memory)
    {
        var score = memory.Confidence * 3m + memory.Importance * 2m + Math.Min(memory.Version, 20) / 20m;
        if (memory.MemoryType == MemoryType.Decision) score += 2m;
        if (memory.Tags.Any(x => string.Equals(x, "authoritative", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(x, "source-of-truth", StringComparison.OrdinalIgnoreCase))) score += 10m;
        return score;
    }

    private static Guid DeterministicLinkId(Guid replacedId, Guid authoritativeId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"replaced_by:{replacedId:N}:{authoritativeId:N}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static string GetDedupKey(SuggestedAction action)
        => !string.IsNullOrWhiteSpace(action.DedupKey)
            ? action.DedupKey
            : TryReadString(DeserializePayload(action.PayloadJson), "dedupKey") ?? string.Empty;

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
            entity.ExecutedAt)
        {
            DedupKey = GetDedupKey(entity)
        };
}
