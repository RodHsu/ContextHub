using Microsoft.EntityFrameworkCore;
using Memory.Domain;

namespace Memory.Application;

public sealed class SuggestedActionReconciliationService(
    IApplicationDbContext dbContext,
    IClock clock) : ISuggestedActionReconciliationService
{
    public async Task<int> ReconcileForMemoriesAsync(
        IReadOnlyCollection<Guid> memoryIds,
        IReadOnlyCollection<string> projectIds,
        CancellationToken cancellationToken)
    {
        if (memoryIds.Count == 0 || projectIds.Count == 0)
        {
            return 0;
        }

        var affectedMemoryIds = memoryIds.ToHashSet();
        var affectedProjectIds = projectIds
            .Select(projectId => ProjectContext.Normalize(projectId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actions = await dbContext.SuggestedActions
            .Where(x => affectedProjectIds.Contains(x.ProjectId) &&
                        (x.Status == SuggestedActionStatus.Pending || x.Status == SuggestedActionStatus.Accepted))
            .ToListAsync(cancellationToken);
        var candidates = actions
            .Select(action => new ActionCandidate(action, SuggestedActionEquivalence.GetReferencedMemoryIds(action)))
            .Where(candidate => candidate.MemoryIds.Overlaps(affectedMemoryIds))
            .ToArray();
        if (candidates.Length == 0)
        {
            return 0;
        }

        var referencedMemoryIds = candidates.SelectMany(x => x.MemoryIds).Distinct().ToArray();
        var memories = await dbContext.MemoryItems
            .AsNoTracking()
            .Where(x => referencedMemoryIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        foreach (var trackedMemory in dbContext.MemoryItems.Local.Where(x => referencedMemoryIds.Contains(x.Id)))
        {
            memories[trackedMemory.Id] = trackedMemory;
        }
        var persistedLinks = await dbContext.MemoryLinks
            .AsNoTracking()
            .Where(x => x.LinkType == "replaced_by" &&
                        referencedMemoryIds.Contains(x.FromId) &&
                        referencedMemoryIds.Contains(x.ToId))
            .Select(x => new { x.FromId, x.ToId })
            .ToListAsync(cancellationToken);
        var replacementPairs = persistedLinks
            .Select(x => CanonicalPair(x.FromId, x.ToId))
            .Concat(dbContext.MemoryLinks.Local
                .Where(x => x.LinkType == "replaced_by" &&
                            referencedMemoryIds.Contains(x.FromId) &&
                            referencedMemoryIds.Contains(x.ToId))
                .Select(x => CanonicalPair(x.FromId, x.ToId)))
            .ToHashSet(StringComparer.Ordinal);
        var findingKeys = candidates
            .Select(x => SuggestedActionEquivalence.GetFindingKey(x.Action))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var findings = findingKeys.Length == 0
            ? new Dictionary<string, GovernanceFinding>(StringComparer.Ordinal)
            : (await dbContext.GovernanceFindings
                .AsNoTracking()
                .Where(x => findingKeys.Contains(x.DedupKey))
                .ToListAsync(cancellationToken))
                .ToDictionary(x => x.DedupKey, StringComparer.Ordinal);

        var reconciledCount = 0;
        foreach (var candidate in candidates)
        {
            if (candidate.Action.Status is not (SuggestedActionStatus.Pending or SuggestedActionStatus.Accepted) ||
                !IsTerminal(candidate, memories, replacementPairs, findings))
            {
                continue;
            }

            candidate.Action.Status = SuggestedActionStatus.Superseded;
            candidate.Action.UpdatedAt = clock.UtcNow;
            reconciledCount++;
        }

        return reconciledCount;
    }

    private static bool IsTerminal(
        ActionCandidate candidate,
        IReadOnlyDictionary<Guid, MemoryItem> memories,
        IReadOnlySet<string> replacementPairs,
        IReadOnlyDictionary<string, GovernanceFinding> findings)
    {
        if (candidate.MemoryIds.Any(id => !memories.TryGetValue(id, out var memory) ||
                                          memory.Status == MemoryStatus.Archived ||
                                          !string.Equals(memory.ProjectId, candidate.Action.ProjectId, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var ids = candidate.MemoryIds.ToArray();
        for (var left = 0; left < ids.Length; left++)
        {
            for (var right = left + 1; right < ids.Length; right++)
            {
                if (replacementPairs.Contains(CanonicalPair(ids[left], ids[right])))
                {
                    return true;
                }
            }
        }

        var findingKey = SuggestedActionEquivalence.GetFindingKey(candidate.Action);
        return !string.IsNullOrWhiteSpace(findingKey) &&
               (!findings.TryGetValue(findingKey, out var finding) || finding.Status != GovernanceFindingStatus.Open);
    }

    private static string CanonicalPair(Guid left, Guid right)
        => string.Join(':', new[] { left, right }.OrderBy(x => x).Select(x => x.ToString("N")));

    private sealed record ActionCandidate(SuggestedAction Action, IReadOnlySet<Guid> MemoryIds);
}
