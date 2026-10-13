using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Memory.Application;

public sealed class MemoryManifestService(
    IApplicationDbContext dbContext, IGovernanceProjectScopeResolver scopeResolver,
    IRequestActorAccessor actorAccessor, IClock clock) : IMemoryManifestService
{
    // Existing release/rollback governance APIs reject Run IDs over 128 characters.
    // Keep read-storage keys outside that authority domain even on readers that ignore ReadManifest.
    internal static readonly string StoragePrefix = "metadata-manifest:" + new string('_', 129) + ":";
    private static readonly char[] WhiteSpaceCharacters = Enumerable.Range(0, char.MaxValue + 1)
        .Select(x => (char)x).Where(char.IsWhiteSpace).ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<MemoryManifestPage> ReadAsync(MemoryManifestReadRequest request, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryRead);
        var project = RequireProject(request.ProjectId);
        ActorAuthorization.EnsureProjectAllowed(actor, project, write: false);
        if (request.PageSize is < 1 or > 100 || request.RequestId == Guid.Empty)
            throw new MemoryManifestException("invalid-manifest-request");
        if (request.Cursor is not null && request.SnapshotId is null)
            throw new MemoryManifestException("cursor-snapshot-required");

        var storageId = StoragePrefix + request.RequestId.ToString("N");
        var row = await OwnedSnapshots(actor)
            .FirstOrDefaultAsync(x => request.SnapshotId.HasValue
                ? x.Id == request.SnapshotId.Value : x.GovernanceRunId == storageId, cancellationToken);
        if (row is null)
        {
            if (request.SnapshotId.HasValue)
                throw new MemoryManifestException("manifest-not-found");
            if (dbContext is not DbContext context || context.ChangeTracker.HasChanges())
                throw new MemoryManifestException("manifest-context-not-clean");
            var requested = DurableMemoryGovernancePolicy.NormalizeGovernanceProjectIds(
                new[] { project }.Concat(request.IncludedProjectIds ?? []));
            var projects = await ResolveAsync(request.AllAuthorizedProjects ? null : requested, cancellationToken);
            var identity = await CurrentAccessIdentityAsync(actor, projects, cancellationToken);
            var entries = await ProjectEntries(actor, projects).ToListAsync(cancellationToken);
            // Sorting the materialized projection gives provider-independent, immutable positions.
            entries = entries.OrderBy(x => x.MemoryId).ToList();
            // PostgreSQL timestamps retain microseconds; first response and persisted replay must agree.
            var now = clock.UtcNow.ToUniversalTime();
            now = now.AddTicks(-(now.Ticks % 10));
            var manifest = new MemoryManifestData(project, request.AllAuthorizedProjects, requested, projects,
                identity, now.AddHours(24), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), entries);
            var id = Guid.NewGuid();
            var coverage = new KnowledgeGovernanceCoverageResult(id, "manifest:" + id.ToString("N"), now,
                entries.Count, entries.Count, entries.Count(x => x.Status == MemoryStatus.Active),
                entries.Count(x => x.Status == MemoryStatus.Archived), entries.Count(x => !ProjectContext.IsShared(x.ProjectId)),
                entries.Count(x => ProjectContext.IsShared(x.ProjectId)), true, false, null)
            {
                AuthorizedGovernanceDurableMemoryCount = entries.Count,
                GovernanceCoveredDurableMemoryCount = entries.Count,
                GovernanceProjectIds = projects,
                SystemMetadataCount = entries.Count(x => x.Classification.StartsWith("System", StringComparison.Ordinal)),
                NonRetrievalSystemMetadataCount = entries.Count(x => x.Classification == "SystemNonRetrieval")
            };
            row = new KnowledgeGovernanceSnapshot
            {
                Id = id,
                TenantId = actor.TenantId!.Value,
                OwnerUserId = actor.UserId!.Value,
                GovernanceRunId = storageId,
                ProjectSetHash = manifest.AccessIdentity,
                ProjectIdsJson = JsonSerializer.Serialize(projects, JsonOptions),
                ResultJson = JsonSerializer.Serialize(new DurableMemoryGovernanceSnapshotResult(coverage, [], [])
                { ReadManifest = manifest }, JsonOptions),
                TotalCount = entries.Count,
                ScannedCount = entries.Count,
                CoverageComplete = true,
                CreatedAt = now,
                CompletedAt = now
            };
            await dbContext.KnowledgeGovernanceSnapshots.AddAsync(row, cancellationToken);
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Unique owner/request key makes timeout retries and competing freezes converge.
                context.Entry(row).State = EntityState.Detached;
                row = await OwnedSnapshots(actor).FirstOrDefaultAsync(x => x.GovernanceRunId == storageId, cancellationToken)
                    ?? throw new MemoryManifestException("manifest-freeze-failed");
            }
        }

        var data = await ValidateAsync(row, project, cancellationToken);
        if (row.GovernanceRunId != storageId || data.AllAuthorizedProjects != request.AllAuthorizedProjects ||
            !data.RequestedProjects.SequenceEqual(DurableMemoryGovernancePolicy.NormalizeGovernanceProjectIds(
                new[] { project }.Concat(request.IncludedProjectIds ?? [])), ProjectContext.IdentityComparer))
            throw new MemoryManifestException("manifest-request-mismatch");
        return Page(row, data, request.Cursor, request.PageSize);
    }

    public async Task<IReadOnlyList<MemoryManifestVerification>> VerifyAsync(
        MemoryManifestVerifyRequest request, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryRead);
        if (request.MemoryIds is null || request.MemoryIds.Count is < 1 or > 100 || request.MemoryIds.Distinct().Count() != request.MemoryIds.Count)
            throw new MemoryManifestException("invalid-manifest-members");
        var row = await OwnedSnapshots(actor).FirstOrDefaultAsync(x => x.Id == request.SnapshotId, cancellationToken)
            ?? throw new MemoryManifestException("manifest-not-found");
        var data = await ValidateAsync(row, RequireProject(request.ProjectId), cancellationToken);
        var saved = data.Entries.Where(x => request.MemoryIds.Contains(x.MemoryId)).ToDictionary(x => x.MemoryId);
        if (saved.Count != request.MemoryIds.Count)
            throw new MemoryManifestException("memory-not-in-snapshot");
        var current = await ProjectEntries(actor, data.Projects, request.MemoryIds)
            .ToDictionaryAsync(x => x.MemoryId, cancellationToken);
        return request.MemoryIds.Select(id => current.TryGetValue(id, out var entry)
            ? new MemoryManifestVerification(id, entry == saved[id] ? "Unchanged" : "Changed", entry.Version)
            : new MemoryManifestVerification(id, "RemovedOrMoved", null)).ToArray();
    }

    private IQueryable<KnowledgeGovernanceSnapshot> OwnedSnapshots(ContextHubRequestActor actor)
        => dbContext.KnowledgeGovernanceSnapshots.AsNoTracking()
            .Where(x => x.TenantId == actor.TenantId && x.OwnerUserId == actor.UserId);

    private async Task<IReadOnlyList<string>> ResolveAsync(IReadOnlyList<string>? requested, CancellationToken cancellationToken)
    {
        var resolved = await scopeResolver.ResolveAsync(requested, cancellationToken);
        if (requested is not null && resolved.Any(x => !x.CanRead))
            throw new MemoryManifestException("manifest-access-changed");
        var projects = DurableMemoryGovernancePolicy.NormalizeGovernanceProjectIds(resolved.Where(x => x.CanRead).Select(x => x.ProjectId));
        // Shared belongs to the same resolver/grant checks, never an implicit authorization bypass.
        var checkedProjects = await scopeResolver.ResolveAsync(projects, cancellationToken);
        if (checkedProjects.Any(x => !x.CanRead))
            throw new MemoryManifestException("manifest-access-changed");
        return projects;
    }

    private async Task<MemoryManifestData> ValidateAsync(KnowledgeGovernanceSnapshot row, string project,
        CancellationToken cancellationToken)
    {
        var result = JsonSerializer.Deserialize<DurableMemoryGovernanceSnapshotResult>(row.ResultJson, JsonOptions)
            ?? throw new MemoryManifestException("manifest-invalid");
        var data = result.ReadManifest ?? throw new MemoryManifestException("legacy-manifest-unavailable");
        if (data.ScopeContractVersion != DurableMemoryGovernancePolicy.ScopeContractVersion)
            throw new MemoryManifestException("manifest-scope-contract-changed");
        if (!ProjectContext.Matches(project, data.ContextProjectId))
            throw new MemoryManifestException("manifest-context-mismatch");
        if (clock.UtcNow >= data.ExpiresAt)
            throw new MemoryManifestException("manifest-expired");
        IReadOnlyList<string> projects;
        try
        {
            projects = await ResolveAsync(data.Projects, cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            throw new MemoryManifestException("manifest-access-changed");
        }
        if (await CurrentAccessIdentityAsync(actorAccessor.Current, projects, cancellationToken) != data.AccessIdentity)
            throw new MemoryManifestException("manifest-access-changed");
        if (data.Entries.Count != row.TotalCount || data.Entries.Select(x => x.MemoryId).Distinct().Count() != row.TotalCount)
            throw new MemoryManifestException("manifest-count-invariant-failed");
        return data;
    }

    internal IQueryable<MemoryManifestEntry> ProjectEntries(ContextHubRequestActor actor, IReadOnlyList<string> projects,
        IReadOnlyList<Guid>? ids = null)
        => (ids is null
                ? dbContext.MemoryItems.AsNoTracking().ForActor(actor).Where(x => projects.Contains(x.ProjectId))
                : dbContext.MemoryItems.AsNoTracking().ForActor(actor).Where(x => projects.Contains(x.ProjectId) && ids.Contains(x.Id)))
            .Select(x => new MemoryManifestEntry(x.Id, x.ProjectId, x.MemoryType, x.Scope, x.Status, x.Version,
                x.UpdatedAt, x.IsReadOnly, x.Revisions.Where(r => r.Version == x.Version).Select(r => (Guid?)r.Id).FirstOrDefault(),
                x.Id.ToString() + ":v" + x.Version,
                x.ExternalKey == DurableMemoryGovernancePolicy.ProjectInformationExternalKey
                    ? (x.Content.Trim(WhiteSpaceCharacters) == "" && x.Summary.Trim(WhiteSpaceCharacters) == ""
                        ? "SystemNonRetrieval" : "SystemMetadata") : "Knowledge",
                x.SourceType == "Manual" || x.SourceType == "Conversation" || x.SourceType == "Repo" || x.SourceType == "GitHub"
                    ? x.SourceType : (x.SourceType == "" ? "Empty" : "Other"), x.SourceRef != ""));

    internal static string AccessIdentity(ContextHubRequestActor actor, IReadOnlyList<string> projects)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            actor.TenantId,
            actor.UserId,
            actor.Role,
            Scopes = actor.Scopes.Select(x => x.ToLowerInvariant()).Distinct().Order().ToArray(),
            Allowed = actor.AllowedProjectIds.Select(x => x.ToLowerInvariant()).Distinct().Order().ToArray(),
            Projects = projects.Select(x => x.ToLowerInvariant()).Distinct().Order().ToArray()
        })));

    private async Task<string> CurrentAccessIdentityAsync(ContextHubRequestActor actor, IReadOnlyList<string> projects,
        CancellationToken cancellationToken)
    {
        var grants = await dbContext.TenantProjectGrants.AsNoTracking().Where(x => x.TenantId == actor.TenantId)
            .Select(x => new { x.Id, x.ProjectId, x.CanRead, x.CanWrite, x.UpdatedAt }).ToListAsync(cancellationToken);
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Actor = AccessIdentity(actor, projects),
            Grants = grants.Where(x => projects.Contains(x.ProjectId, ProjectContext.IdentityComparer))
                .OrderBy(x => x.ProjectId, StringComparer.OrdinalIgnoreCase)
                .Select(x => new
                {
                    x.Id,
                    Project = x.ProjectId.ToLowerInvariant(),
                    x.CanRead,
                    x.CanWrite,
                    UpdatedAt = x.UpdatedAt.ToUniversalTime()
                }).ToArray()
        })));
    }

    internal static MemoryManifestPage Page(KnowledgeGovernanceSnapshot row, MemoryManifestData data, string? cursor, int pageSize)
    {
        var position = 0;
        if (cursor is not null)
        {
            if (cursor.Length > 512) throw new MemoryManifestException("invalid-manifest-cursor");
            var parts = cursor.Split('.');
            if (parts.Length != 2) throw new MemoryManifestException("invalid-manifest-cursor");
            try
            {
                var bytes = Convert.FromBase64String(parts[0]);
                var signature = Convert.FromBase64String(parts[1]);
                if (!CryptographicOperations.FixedTimeEquals(signature,
                        HMACSHA256.HashData(Convert.FromBase64String(data.CursorKey), bytes)))
                    throw new MemoryManifestException("invalid-manifest-cursor");
                var body = Encoding.UTF8.GetString(bytes).Split(':');
                if (body.Length != 3 || body[0] != row.Id.ToString("N") ||
                    !int.TryParse(body[1], NumberStyles.None, CultureInfo.InvariantCulture, out position) ||
                    body[2] != pageSize.ToString(CultureInfo.InvariantCulture) || position < 0 || position > data.Entries.Count)
                    throw new MemoryManifestException("invalid-manifest-cursor");
            }
            catch (FormatException) { throw new MemoryManifestException("invalid-manifest-cursor"); }
        }
        var items = data.Entries.Skip(position).Take(pageSize).ToArray();
        var nextPosition = position + items.Length;
        var end = nextPosition == data.Entries.Count;
        var partitions = data.Entries.GroupBy(x => "status:" + x.Status).ToDictionary(x => x.Key, x => x.Count());
        foreach (var group in data.Entries.GroupBy(x => "project:" + x.ProjectId)) partitions.Add(group.Key, group.Count());
        foreach (var group in data.Entries.GroupBy(x => "classification:" + x.Classification)) partitions.Add(group.Key, group.Count());
        string? next = null;
        if (!end)
        {
            var bytes = Encoding.UTF8.GetBytes(FormattableString.Invariant($"{row.Id:N}:{nextPosition}:{pageSize}"));
            next = Convert.ToBase64String(bytes) + "." + Convert.ToBase64String(HMACSHA256.HashData(Convert.FromBase64String(data.CursorKey), bytes));
        }
        return new(row.Id, row.CreatedAt, data.ExpiresAt, data.ScopeContractVersion,
            data.AccessIdentity, row.TotalCount, data.Entries.Select(x => x.MemoryId).Distinct().Count(),
            row.TotalCount == data.Entries.Count && row.TotalCount == data.Entries.Select(x => x.MemoryId).Distinct().Count(),
            partitions, position, items, end, next);
    }

    private static string RequireProject(string project)
    {
        if (string.IsNullOrWhiteSpace(project) || ProjectContext.Matches(project, ProjectContext.DefaultProjectId) || ProjectContext.IsUser(project))
            throw new MemoryManifestException("explicit-project-required");
        return ProjectContext.Normalize(project);
    }
}
