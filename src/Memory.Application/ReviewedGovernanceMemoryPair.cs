using Memory.Domain;

namespace Memory.Application;

public sealed record ReviewedGovernanceMemory(Guid Id, int Version, string EvidenceHash);

public sealed record ReviewedGovernanceMemoryPair(
    Guid FindingId,
    string ProjectId,
    GovernanceFindingType FindingType,
    ReviewedGovernanceMemory Left,
    ReviewedGovernanceMemory Right,
    Guid PrimaryId)
{
    internal static bool IsMergeFinding(GovernanceFindingType type)
        => type is GovernanceFindingType.DuplicateCandidate or GovernanceFindingType.DuplicateMemoryCandidate or GovernanceFindingType.MergeMemoryCandidate;

    internal static ReviewedGovernanceMemoryPair? Capture(GovernanceFinding finding, MemoryItem left, MemoryItem right)
    {
        if (left.Id == right.Id || left.Version <= 0 || right.Version <= 0 ||
            !ProjectContext.Matches(finding.ProjectId, left.ProjectId) || !ProjectContext.Matches(finding.ProjectId, right.ProjectId))
            return null;

        var primary = AuthorityScore(left) >= AuthorityScore(right) ? left : right;
        return new(finding.Id, finding.ProjectId, finding.Type,
            CaptureMemory(left), CaptureMemory(right), primary.Id);
    }

    internal bool MatchesFinding(GovernanceFinding finding)
        => FindingId == finding.Id && FindingType == finding.Type && IsMergeFinding(finding.Type) &&
            ProjectContext.Matches(ProjectId, finding.ProjectId) && Left.Id == finding.PrimaryMemoryId &&
            Right.Id == finding.SecondaryMemoryId && Left.Id != Right.Id &&
            (PrimaryId == Left.Id || PrimaryId == Right.Id);

    internal bool MatchesMemory(MemoryItem left, MemoryItem right)
        => Left == CaptureMemory(left) && Right == CaptureMemory(right) &&
            PrimaryId == (AuthorityScore(left) >= AuthorityScore(right) ? left.Id : right.Id);

    private static ReviewedGovernanceMemory CaptureMemory(MemoryItem memory)
        => new(memory.Id, memory.Version,
            GovernanceExceptionIdentity.Hash(new { memory.Scope, Evidence = GovernanceExceptionIdentity.MemoryEvidence(memory) }));

    private static decimal AuthorityScore(MemoryItem memory)
    {
        var score = memory.Confidence * 3m + memory.Importance * 2m + Math.Min(memory.Version, 20) / 20m;
        if (memory.Tags.Any(tag => string.Equals(tag, "authoritative", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(tag, "source-of-truth", StringComparison.OrdinalIgnoreCase)))
        {
            score += 10m;
        }
        return score;
    }
}
