using Memory.Domain;

namespace Memory.Application;

public sealed record MemoryManifestReadRequest(
    string ProjectId,
    Guid RequestId,
    Guid? SnapshotId = null,
    string? Cursor = null,
    int PageSize = 100,
    bool AllAuthorizedProjects = true,
    IReadOnlyList<string>? IncludedProjectIds = null);

public sealed record MemoryManifestVerifyRequest(string ProjectId, Guid SnapshotId, IReadOnlyList<Guid> MemoryIds);

public sealed record MemoryManifestEntry(
    Guid MemoryId, string ProjectId, MemoryType MemoryType, MemoryScope Scope,
    MemoryStatus Status, int Version, DateTimeOffset UpdatedAt, bool IsReadOnly,
    Guid? RevisionId, string RevisionIdentity, string Classification,
    string SourceType, bool SourceRefPresent);

public sealed record MemoryManifestPage(
    Guid SnapshotId, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
    string ScopeContractVersion, string AccessScopeIdentity,
    int AuthorizedTotal, int UniqueCount, bool CountInvariantSatisfied,
    IReadOnlyDictionary<string, int> PartitionCounts, int Position,
    IReadOnlyList<MemoryManifestEntry> Items, bool EndOfSnapshot, string? NextCursor);

public sealed record MemoryManifestVerification(Guid MemoryId, string State, int? CurrentVersion);

public interface IMemoryManifestService
{
    Task<MemoryManifestPage> ReadAsync(MemoryManifestReadRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<MemoryManifestVerification>> VerifyAsync(MemoryManifestVerifyRequest request, CancellationToken cancellationToken);
}

public sealed class MemoryManifestException(string code) : InvalidOperationException(code)
{
    public string Code { get; } = code;
}

// Persisted only in the existing snapshot JSON envelope; never published as a tool response.
public sealed record MemoryManifestData(
    string ContextProjectId, bool AllAuthorizedProjects, IReadOnlyList<string> RequestedProjects,
    IReadOnlyList<string> Projects, string AccessIdentity, DateTimeOffset ExpiresAt,
    string CursorKey, IReadOnlyList<MemoryManifestEntry> Entries)
{
    public string ScopeContractVersion { get; init; } = DurableMemoryGovernancePolicy.ScopeContractVersion;
}
