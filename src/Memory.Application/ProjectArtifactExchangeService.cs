using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Memory.Application;

public sealed class ProjectArtifactExchangeService(
    IApplicationDbContext dbContext,
    IMemoryService memoryService,
    IProjectArtifactObjectStore objectStore,
    IRequestActorAccessor actorAccessor,
    IClock clock) : IProjectArtifactExchangeService
{
    public const string SourceType = "project-artifact-exchange";
    private const int ContentPreviewLength = 1200;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ProjectArtifactResult> PublishAsync(ProjectArtifactPublishRequest request, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryWrite);

        var projectId = ProjectContext.Normalize(request.ProjectId);
        ActorAuthorization.EnsureProjectAllowed(actor, projectId, write: true);

        var title = NormalizeRequired(request.Title, nameof(request.Title));
        var summary = NormalizeRequired(request.Summary, nameof(request.Summary));
        var content = NormalizeArtifactContent(request);
        var sourceSystem = NormalizeRequired(request.SourceSystem, nameof(request.SourceSystem));
        var sourceRef = string.IsNullOrWhiteSpace(request.SourceRef)
            ? $"{sourceSystem}:{request.Kind.ToString().ToLowerInvariant()}:{Hash(title, summary, content)[..16]}"
            : request.SourceRef.Trim();
        var externalKey = string.IsNullOrWhiteSpace(request.ExternalKey)
            ? BuildExternalKey(projectId, sourceSystem, sourceRef, request.Kind)
            : request.ExternalKey.Trim();
        var tags = NormalizeTags(request.Tags, request.Kind, sourceSystem);
        var expiresAt = request.ExpiresAt ?? request.ObjectRef?.ExpiresAt;
        var metadataJson = BuildMetadataJson(request, sourceSystem, expiresAt);

        var document = await memoryService.UpsertAsync(
            new MemoryUpsertRequest(
                externalKey,
                MemoryScope.Project,
                MemoryType.Artifact,
                title,
                content,
                summary,
                SourceType,
                sourceRef,
                tags,
                Importance: 0.76m,
                Confidence: 0.88m,
                MetadataJson: metadataJson,
                ProjectId: projectId),
            cancellationToken);

        return ToResult(document, clock.UtcNow);
    }

    public async Task<ProjectArtifactResult> UploadManagedObjectAsync(ProjectArtifactManagedObjectPublishRequest request, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryWrite);

        var projectId = ProjectContext.Normalize(request.ProjectId);
        ActorAuthorization.EnsureProjectAllowed(actor, projectId, write: true);

        if (request.ExpiresAt <= clock.UtcNow)
        {
            throw new InvalidOperationException("ExpiresAt must be a future timestamp for managed object artifacts.");
        }

        var content = DecodeBase64(request.ContentBase64);
        var sourceSystem = NormalizeRequired(request.SourceSystem, nameof(request.SourceSystem));
        var sourceRef = string.IsNullOrWhiteSpace(request.SourceRef)
            ? $"{sourceSystem}:managed-object:{Hash(request.Title, request.Summary, request.FileName, request.ExpiresAt.ToString("O"))[..16]}"
            : request.SourceRef.Trim();
        var objectRef = await objectStore.UploadAsync(
            new ProjectArtifactObjectUploadRequest(
                projectId,
                NormalizeRequired(request.FileName, nameof(request.FileName)),
                NormalizeContentType(request.ContentType),
                content,
                request.ExpiresAt,
                sourceSystem,
                sourceRef,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["projectId"] = projectId,
                    ["title"] = NormalizeRequired(request.Title, nameof(request.Title)),
                    ["sourceSystem"] = sourceSystem,
                    ["sourceRef"] = sourceRef,
                    ["expiresAt"] = request.ExpiresAt.ToString("O")
                }),
            cancellationToken);

        return await PublishAsync(
            new ProjectArtifactPublishRequest(
                projectId,
                request.Title,
                request.Summary,
                Content: string.Empty,
                Kind: ProjectArtifactKind.ExternalObject,
                SourceSystem: sourceSystem,
                SourceRef: sourceRef,
                Tags: request.Tags,
                ExternalKey: request.ExternalKey,
                ObjectRef: objectRef,
                ExpiresAt: request.ExpiresAt,
                MetadataJson: request.MetadataJson),
            cancellationToken);
    }

    public async Task<IReadOnlyList<ProjectArtifactResult>> ListAsync(ProjectArtifactListRequest request, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryRead);
        var projectId = ProjectContext.Normalize(request.ProjectId);
        ActorAuthorization.EnsureProjectAllowed(actor, projectId, write: false);

        var query = dbContext.MemoryItems.AsNoTracking()
            .Where(x => x.ProjectId == projectId &&
                        x.MemoryType == MemoryType.Artifact &&
                        x.SourceType == SourceType &&
                        x.Status == MemoryStatus.Active);

        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            var text = request.Query.Trim().ToLowerInvariant();
            query = query.Where(x =>
                x.Title.ToLower().Contains(text) ||
                x.Summary.ToLower().Contains(text) ||
                x.Content.ToLower().Contains(text) ||
                x.SourceRef.ToLower().Contains(text));
        }

        var rows = await query
            .OrderByDescending(x => x.UpdatedAt)
            .Take(Math.Clamp(request.Limit, 1, 200))
            .ToListAsync(cancellationToken);

        return rows
            .Select(x => ToResult(x, clock.UtcNow))
            .Where(x => MatchesFilters(x, request.Kind, request.SourceSystem, request.IncludeExpired))
            .Take(Math.Clamp(request.Limit, 1, 200))
            .ToArray();
    }

    public async Task<IReadOnlyList<ProjectArtifactResult>> SearchAsync(ProjectArtifactSearchRequest request, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryRead);
        var projectId = ProjectContext.Normalize(request.ProjectId);
        ActorAuthorization.EnsureProjectAllowed(actor, projectId, write: false);

        var hits = await memoryService.SearchAsync(
            new MemorySearchRequest(
                request.Query,
                Math.Clamp(request.Limit * 3, 3, 60),
                IncludeArchived: false,
                ProjectId: projectId,
                Telemetry: new RetrievalTelemetryContext("project_artifact_search", "artifact-exchange", "Cross-agent project artifact exchange")),
            cancellationToken);

        var ids = hits
            .Where(x => x.MemoryType == MemoryType.Artifact && string.Equals(x.SourceType, SourceType, StringComparison.Ordinal))
            .Select(x => x.MemoryId)
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        var rows = await dbContext.MemoryItems.AsNoTracking()
            .Where(x => ids.Contains(x.Id))
            .ToListAsync(cancellationToken);
        var byRank = ids.Select((id, index) => new { id, index }).ToDictionary(x => x.id, x => x.index);

        return rows
            .Select(x => ToResult(x, clock.UtcNow))
            .Where(x => MatchesFilters(x, request.Kind, request.SourceSystem, request.IncludeExpired))
            .OrderBy(x => byRank.GetValueOrDefault(x.MemoryId, int.MaxValue))
            .Take(Math.Clamp(request.Limit, 1, 50))
            .ToArray();
    }

    public async Task<ProjectArtifactResult?> GetAsync(Guid memoryId, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryRead);
        var entity = await dbContext.MemoryItems.AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.Id == memoryId &&
                     x.MemoryType == MemoryType.Artifact &&
                     x.SourceType == SourceType &&
                     x.Status == MemoryStatus.Active,
                cancellationToken);
        if (entity is null)
        {
            return null;
        }

        ActorAuthorization.EnsureProjectAllowed(actor, entity.ProjectId, write: false);
        return ToResult(entity, clock.UtcNow);
    }

    public async Task<ProjectArtifactExpiredObjectPruneResult> PruneExpiredObjectsAsync(
        ProjectArtifactExpiredObjectPruneRequest request,
        CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryWrite);

        var projectId = string.IsNullOrWhiteSpace(request.ProjectId)
            ? null
            : ProjectContext.Normalize(request.ProjectId);
        if (projectId is not null)
        {
            ActorAuthorization.EnsureProjectAllowed(actor, projectId, write: true);
        }

        var limit = Math.Clamp(request.Limit, 1, 500);
        var now = clock.UtcNow;
        var query = dbContext.MemoryItems
            .Where(x => x.MemoryType == MemoryType.Artifact &&
                        x.SourceType == SourceType &&
                        x.Status == MemoryStatus.Active);

        if (projectId is not null)
        {
            query = query.Where(x => x.ProjectId == projectId);
        }
        else if (actor.AllowedProjectIds.Count > 0)
        {
            query = query.Where(x => actor.AllowedProjectIds.Contains(x.ProjectId));
        }

        var rows = await query
            .OrderBy(x => x.UpdatedAt)
            .Take(Math.Clamp(limit * 5, limit, 2500))
            .ToListAsync(cancellationToken);

        var expired = rows
            .Select(x => new { Item = x, Metadata = ReadMetadata(x.MetadataJson) })
            .Where(x => x.Metadata.ObjectRef is not null &&
                        x.Metadata.ExpiresAt.HasValue &&
                        x.Metadata.ExpiresAt.Value <= now)
            .Take(limit)
            .ToArray();

        var results = new List<ProjectArtifactExpiredObjectPruneItem>(expired.Length);
        foreach (var candidate in expired)
        {
            var item = candidate.Item;
            var objectRef = candidate.Metadata.ObjectRef!;
            ActorAuthorization.EnsureProjectAllowed(actor, item.ProjectId, write: true);

            if (request.DryRun)
            {
                results.Add(ToPruneItem(item, candidate.Metadata, objectRef, deletedObject: false, archivedArtifact: false, error: string.Empty));
                continue;
            }

            try
            {
                await objectStore.DeleteAsync(objectRef, cancellationToken);
                item.Status = MemoryStatus.Archived;
                item.UpdatedAt = now;
                item.Tags = item.Tags
                    .Append("artifact-object-expired")
                    .Append("artifact-object-pruned")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                results.Add(ToPruneItem(item, candidate.Metadata, objectRef, deletedObject: true, archivedArtifact: true, error: string.Empty));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results.Add(ToPruneItem(item, candidate.Metadata, objectRef, deletedObject: false, archivedArtifact: false, error: ex.Message));
            }
        }

        if (!request.DryRun && results.Any(x => x.ArchivedArtifact))
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return new ProjectArtifactExpiredObjectPruneResult(
            expired.Length,
            results.Count(x => x.DeletedObject),
            results.Count(x => x.ArchivedArtifact),
            results.Count(x => !string.IsNullOrWhiteSpace(x.Error)),
            results);
    }

    private static bool MatchesFilters(ProjectArtifactResult artifact, ProjectArtifactKind? kind, string? sourceSystem, bool includeExpired)
    {
        if (!includeExpired && artifact.IsExpired)
        {
            return false;
        }

        if (kind.HasValue && artifact.Kind != kind.Value)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(sourceSystem) ||
               string.Equals(artifact.SourceSystem, sourceSystem.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeArtifactContent(ProjectArtifactPublishRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Content))
        {
            return request.Content.Trim();
        }

        if ((request.Kind is ProjectArtifactKind.ExternalObject or ProjectArtifactKind.FileReference) && request.ObjectRef is not null)
        {
            return $"External object reference: {request.ObjectRef.Provider}/{request.ObjectRef.Bucket}/{request.ObjectRef.Key}";
        }

        throw new InvalidOperationException("Content is required unless an external object reference is supplied.");
    }

    private static byte[] DecodeBase64(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("ContentBase64 is required.");
        }

        try
        {
            return Convert.FromBase64String(value.Trim());
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("ContentBase64 must be valid base64.", ex);
        }
    }

    private static string NormalizeContentType(string value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(trimmed)
            ? "application/octet-stream"
            : trimmed;
    }

    private static string BuildMetadataJson(ProjectArtifactPublishRequest request, string sourceSystem, DateTimeOffset? expiresAt)
    {
        var baseMetadata = ParseMetadata(request.MetadataJson);
        var metadata = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["artifactExchange"] = true,
            ["kind"] = request.Kind.ToString(),
            ["sourceSystem"] = sourceSystem,
            ["objectRef"] = request.ObjectRef,
            ["expiresAt"] = expiresAt,
            ["metadata"] = baseMetadata
        };
        return JsonSerializer.Serialize(metadata, JsonOptions);
    }

    private static JsonElement ParseMetadata(string metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            metadataJson = "{}";
        }

        using var document = JsonDocument.Parse(metadataJson);
        return document.RootElement.Clone();
    }

    private static ProjectArtifactResult ToResult(MemoryDocument document, DateTimeOffset now)
        => ToResult(
            new MemoryItem
            {
                Id = document.Id,
                ExternalKey = document.ExternalKey,
                ProjectId = document.ProjectId,
                Title = document.Title,
                Summary = document.Summary,
                Content = document.Content,
                SourceRef = document.SourceRef,
                Tags = document.Tags.ToArray(),
                MetadataJson = document.MetadataJson,
                CreatedAt = document.CreatedAt,
                UpdatedAt = document.UpdatedAt
            },
            now);

    private static ProjectArtifactResult ToResult(MemoryItem item, DateTimeOffset now)
    {
        var metadata = ReadMetadata(item.MetadataJson);
        var isExpired = metadata.ExpiresAt.HasValue && metadata.ExpiresAt.Value <= now;
        return new ProjectArtifactResult(
            item.Id,
            item.ExternalKey,
            item.ProjectId,
            metadata.Kind,
            item.Title,
            item.Summary,
            Truncate(item.Content, ContentPreviewLength),
            metadata.SourceSystem,
            item.SourceRef,
            item.Tags,
            metadata.ObjectRef,
            metadata.ExpiresAt,
            isExpired,
            item.CreatedAt,
            item.UpdatedAt);
    }

    private static ArtifactMetadata ReadMetadata(string metadataJson)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(metadataJson) ? "{}" : metadataJson);
        var root = document.RootElement;
        var kind = root.TryGetProperty("kind", out var kindValue) &&
                   Enum.TryParse<ProjectArtifactKind>(kindValue.GetString(), ignoreCase: true, out var parsedKind)
            ? parsedKind
            : ProjectArtifactKind.Summary;
        var sourceSystem = root.TryGetProperty("sourceSystem", out var sourceSystemValue)
            ? sourceSystemValue.GetString() ?? string.Empty
            : string.Empty;
        var expiresAt = root.TryGetProperty("expiresAt", out var expiresAtValue) &&
                        expiresAtValue.ValueKind == JsonValueKind.String &&
                        DateTimeOffset.TryParse(expiresAtValue.GetString(), out var parsedExpiresAt)
            ? parsedExpiresAt
            : (DateTimeOffset?)null;
        var objectRef = root.TryGetProperty("objectRef", out var objectRefValue) &&
                        objectRefValue.ValueKind == JsonValueKind.Object
            ? objectRefValue.Deserialize<ProjectArtifactObjectRef>(JsonOptions)
            : null;

        return new ArtifactMetadata(kind, sourceSystem, objectRef, expiresAt);
    }

    private static ProjectArtifactExpiredObjectPruneItem ToPruneItem(
        MemoryItem item,
        ArtifactMetadata metadata,
        ProjectArtifactObjectRef objectRef,
        bool deletedObject,
        bool archivedArtifact,
        string error)
        => new(
            item.Id,
            item.ProjectId,
            item.Title,
            metadata.Kind,
            objectRef.Bucket,
            objectRef.Key,
            metadata.ExpiresAt,
            deletedObject,
            archivedArtifact,
            error);

    private static IReadOnlyList<string> NormalizeTags(IReadOnlyList<string>? tags, ProjectArtifactKind kind, string sourceSystem)
    {
        return (tags ?? [])
            .Append("artifact-exchange")
            .Append($"artifact-kind:{kind.ToString().ToLowerInvariant()}")
            .Append($"source-system:{sourceSystem.ToLowerInvariant()}")
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(32)
            .ToArray();
    }

    private static string BuildExternalKey(string projectId, string sourceSystem, string sourceRef, ProjectArtifactKind kind)
        => $"artifact-exchange:{ProjectContext.Normalize(projectId)}:{sourceSystem}:{kind.ToString().ToLowerInvariant()}:{Hash(sourceRef)[..24]}";

    private static string NormalizeRequired(string value, string name)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new InvalidOperationException($"{name} is required.");
        }

        return trimmed;
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength].TrimEnd();

    private static string Hash(params string[] values)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", values))));

    private sealed record ArtifactMetadata(
        ProjectArtifactKind Kind,
        string SourceSystem,
        ProjectArtifactObjectRef? ObjectRef,
        DateTimeOffset? ExpiresAt);
}

public sealed class DisabledProjectArtifactObjectStore : IProjectArtifactObjectStore
{
    public Task<ProjectArtifactObjectRef> UploadAsync(ProjectArtifactObjectUploadRequest request, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Project artifact object storage is not enabled.");

    public Task DeleteAsync(ProjectArtifactObjectRef objectRef, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Project artifact object storage is not enabled.");
}
