using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Memory.Domain;

namespace Memory.Application;

public sealed class DurableMemoryGovernanceService(
    IApplicationDbContext dbContext,
    IGovernanceService governanceService,
    IRequestActorAccessor actorAccessor,
    IClock clock) : IDurableMemoryGovernanceService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<DurableMemoryGovernanceSnapshotResult> GetOrCreateSnapshotAsync(
        IReadOnlyList<string> projectIds,
        string governanceRunId,
        bool isReReview,
        CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryRead);
        var tenantId = actor.TenantId ?? throw new UnauthorizedAccessException("Knowledge governance requires a tenant actor.");
        var ownerUserId = actor.UserId ?? throw new UnauthorizedAccessException("Knowledge governance requires a tenant user.");
        var normalizedProjects = projectIds
            .Append(ProjectContext.SharedProjectId)
            .Where(x => !ProjectContext.IsUser(x))
            .Select(x => ProjectContext.Normalize(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        ActorAuthorization.EnsureProjectsAllowed(actor, normalizedProjects, write: false);
        var projectSetHash = Hash(string.Join('\n', normalizedProjects).ToLowerInvariant());

        var existing = await dbContext.KnowledgeGovernanceSnapshots
            .AsNoTracking()
            .FirstOrDefaultAsync(x =>
                x.TenantId == tenantId &&
                x.OwnerUserId == ownerUserId &&
                x.GovernanceRunId == governanceRunId &&
                x.IsReReview == isReReview,
                cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.ProjectSetHash, projectSetHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("GovernanceRunId cannot be replayed with a different authorized ProjectId set.");
            }

            return await ApplyLiveLifecycleOverlayAsync(Deserialize(existing.ResultJson), cancellationToken);
        }

        foreach (var projectId in normalizedProjects)
        {
            await governanceService.AnalyzeAsync(projectId, cancellationToken);
        }

        // One materializing query is the coverage snapshot boundary. Inserts committed after this
        // query belong to the next review and cannot make this snapshot appear partially covered.
        var memories = await dbContext.MemoryItems
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.OwnerUserId == ownerUserId)
            .Where(x => normalizedProjects.Contains(x.ProjectId))
            .OrderBy(x => x.ProjectId)
            .ThenBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Select(x => new CoverageMemory(x.Id, x.ProjectId, x.Status))
            .ToListAsync(cancellationToken);
        var memoryIds = memories.Select(x => x.Id).ToArray();
        var findings = memoryIds.Length == 0
            ? []
            : await dbContext.GovernanceFindings
                .AsNoTracking()
                .Where(x => x.PrimaryMemoryId.HasValue &&
                            memoryIds.Contains(x.PrimaryMemoryId.Value))
                .OrderBy(x => x.ProjectId)
                .ThenBy(x => x.Type)
                .ThenBy(x => x.PrimaryMemoryId)
                .ThenBy(x => x.Id)
                .ToListAsync(cancellationToken);

        var candidates = findings
            .Where(x => x.Status == GovernanceFindingStatus.Open)
            .Select(MapCandidate)
            .ToArray();
        var projectCandidates = candidates.Where(x => !ProjectContext.IsShared(x.ProjectId)).ToArray();
        var sharedCandidates = candidates.Where(x => ProjectContext.IsShared(x.ProjectId)).ToArray();
        var snapshotId = Guid.NewGuid();
        var now = clock.UtcNow;
        var token = $"kg:{snapshotId:N}:{projectSetHash[..16]}:{(isReReview ? "r" : "i")}";
        var coverage = new KnowledgeGovernanceCoverageResult(
            snapshotId,
            token,
            now,
            memories.Count,
            memories.Count,
            memories.Count(x => x.Status == MemoryStatus.Active),
            memories.Count(x => x.Status == MemoryStatus.Archived),
            memories.Count(x => !ProjectContext.IsShared(x.ProjectId)),
            memories.Count(x => ProjectContext.IsShared(x.ProjectId)),
            CoverageComplete: true,
            HasMore: false,
            Continuation: null);
        var result = new DurableMemoryGovernanceSnapshotResult(coverage, projectCandidates, sharedCandidates)
        {
            DeferredCount = findings.Count(x => x.Status == GovernanceFindingStatus.Deferred),
            RequiresUserDecisionCount = findings.Count(x => x.Status == GovernanceFindingStatus.RequiresUserDecision),
            HostBlockedCount = findings.Count(x => x.Status == GovernanceFindingStatus.HostBlocked),
            FindingIds = findings.Select(x => x.Id).ToArray()
        };
        var resultJson = JsonSerializer.Serialize(result, JsonOptions);
        await dbContext.KnowledgeGovernanceSnapshots.AddAsync(new KnowledgeGovernanceSnapshot
        {
            Id = snapshotId,
            TenantId = tenantId,
            OwnerUserId = ownerUserId,
            GovernanceRunId = governanceRunId,
            IsReReview = isReReview,
            ProjectSetHash = projectSetHash,
            ProjectIdsJson = JsonSerializer.Serialize(normalizedProjects, JsonOptions),
            ResultJson = resultJson,
            TotalCount = coverage.TotalCount,
            ScannedCount = coverage.ScannedCount,
            CoverageComplete = coverage.CoverageComplete,
            CreatedAt = now,
            CompletedAt = now
        }, cancellationToken);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return result;
        }
        catch (DbUpdateException)
        {
            dbContext.ClearTrackedChanges();
            var winner = await dbContext.KnowledgeGovernanceSnapshots
                .AsNoTracking()
                .FirstOrDefaultAsync(x =>
                    x.TenantId == tenantId &&
                    x.OwnerUserId == ownerUserId &&
                    x.GovernanceRunId == governanceRunId &&
                    x.IsReReview == isReReview,
                    cancellationToken);
            if (winner is null || !string.Equals(winner.ProjectSetHash, projectSetHash, StringComparison.Ordinal))
            {
                throw;
            }

            return await ApplyLiveLifecycleOverlayAsync(Deserialize(winner.ResultJson), cancellationToken);
        }
    }

    private async Task<DurableMemoryGovernanceSnapshotResult> ApplyLiveLifecycleOverlayAsync(
        DurableMemoryGovernanceSnapshotResult snapshot,
        CancellationToken cancellationToken)
    {
        var persistedFindingIds = snapshot.FindingIds.Count > 0
            ? snapshot.FindingIds
            : snapshot.ProjectCandidates.Concat(snapshot.SharedCandidates).Select(x => x.FindingId).ToArray();
        if (persistedFindingIds.Count == 0)
        {
            return snapshot;
        }

        var liveFindings = await dbContext.GovernanceFindings
            .AsNoTracking()
            .Where(x => persistedFindingIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var orderedLiveFindings = persistedFindingIds
            .Where(liveFindings.ContainsKey)
            .Select(id => liveFindings[id])
            .ToArray();
        var candidates = orderedLiveFindings
            .Where(x => x.Status == GovernanceFindingStatus.Open && x.PrimaryMemoryId.HasValue)
            .Select(MapCandidate)
            .ToArray();

        var usesCompleteFindingMembership = snapshot.FindingIds.Count > 0;
        var deferredCount = orderedLiveFindings.Count(x => x.Status == GovernanceFindingStatus.Deferred);
        var requiresUserDecisionCount = orderedLiveFindings.Count(x => x.Status == GovernanceFindingStatus.RequiresUserDecision);
        var hostBlockedCount = orderedLiveFindings.Count(x => x.Status == GovernanceFindingStatus.HostBlocked);
        if (!usesCompleteFindingMembership)
        {
            deferredCount += snapshot.DeferredCount;
            requiresUserDecisionCount += snapshot.RequiresUserDecisionCount;
            hostBlockedCount += snapshot.HostBlockedCount;
        }

        return snapshot with
        {
            ProjectCandidates = candidates.Where(x => !ProjectContext.IsShared(x.ProjectId)).ToArray(),
            SharedCandidates = candidates.Where(x => ProjectContext.IsShared(x.ProjectId)).ToArray(),
            DeferredCount = deferredCount,
            RequiresUserDecisionCount = requiresUserDecisionCount,
            HostBlockedCount = hostBlockedCount
        };
    }

    private static DurableMemoryGovernanceSnapshotResult Deserialize(string resultJson)
        => JsonSerializer.Deserialize<DurableMemoryGovernanceSnapshotResult>(resultJson, JsonOptions)
           ?? throw new InvalidOperationException("Persisted knowledge governance snapshot is invalid.");

    private static KnowledgeGovernanceCandidateResult MapCandidate(GovernanceFinding finding)
    {
        var targetProjectId = ReadString(finding.DetailsJson, "targetProjectId");
        var reasonCodes = ReadStrings(finding.DetailsJson, "reasonCodes");
        return new KnowledgeGovernanceCandidateResult(
            finding.Id,
            finding.PrimaryMemoryId!.Value,
            finding.SecondaryMemoryId,
            finding.ProjectId,
            finding.Type,
            finding.Title,
            finding.Summary,
            RecommendedAction(finding.Type),
            targetProjectId,
            reasonCodes,
            RequiresExplicitApproval: finding.Type is GovernanceFindingType.MoveMemoryCandidate or
                GovernanceFindingType.SharedKnowledgePromotionCandidate or
                GovernanceFindingType.SharedKnowledgeDemotionCandidate or
                GovernanceFindingType.InvalidMemoryCandidate,
            finding.UpdatedAt);
    }

    private static string RecommendedAction(GovernanceFindingType type)
        => type switch
        {
            GovernanceFindingType.DuplicateCandidate or GovernanceFindingType.DuplicateMemoryCandidate or GovernanceFindingType.MergeMemoryCandidate => "ProposeMerge",
            GovernanceFindingType.MisplacedProjectCandidate or GovernanceFindingType.MoveMemoryCandidate or GovernanceFindingType.SharedKnowledgePromotionCandidate or GovernanceFindingType.SharedKnowledgeDemotionCandidate => "ProposeMove",
            GovernanceFindingType.StaleMemoryCandidate or GovernanceFindingType.LowSignalEpisodeCandidate or GovernanceFindingType.ObsoleteMemoryCandidate or GovernanceFindingType.LowValueMemoryCandidate or GovernanceFindingType.ArchiveMemoryCandidate => "ProposeArchive",
            GovernanceFindingType.SupersededMemoryCandidate or GovernanceFindingType.ReplacementChainCandidate or GovernanceFindingType.AuthoritativeSourceCandidate => "ReviewReplacementChain",
            GovernanceFindingType.ConflictCandidate or GovernanceFindingType.InvalidMemoryCandidate => "RequiresUserDecision",
            _ => "Review"
        };

    private static string? ReadString(string json, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> ReadStrings(string json, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record CoverageMemory(Guid Id, string ProjectId, MemoryStatus Status);
}
