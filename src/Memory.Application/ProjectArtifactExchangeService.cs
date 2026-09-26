using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Memory.Application;

public sealed class ProjectArtifactExchangeService(
    IApplicationDbContext dbContext,
    IMemoryService memoryService,
    IManagedFileService managedFileService,
    IRequestActorAccessor actorAccessor) : IProjectArtifactExchangeService
{
    public const string SourceType = "project-artifact-exchange";
    private const int ContentPreviewLength = 1200;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> ForbiddenLocatorPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "provider", "bucket", "container", "key", "objectKey", "storageId", "uri", "url", "endpoint",
        "accountId", "credential", "accessKey", "secretKey", "presignedUrl", "directUrl"
    };

    public async Task<ProjectArtifactResult> PublishAsync(ProjectArtifactPublishRequest request, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryWrite);
        var projectId = ProjectContext.Normalize(request.ProjectId);
        ActorAuthorization.EnsureProjectAllowed(actor, projectId, write: true);

        var title = NormalizeRequired(request.Title, nameof(request.Title));
        var summary = NormalizeRequired(request.Summary, nameof(request.Summary));
        var sourceSystem = NormalizeRequired(request.SourceSystem, nameof(request.SourceSystem));
        var logicalReference = await ValidateLogicalReferenceAsync(request, projectId, cancellationToken);
        var content = NormalizeArtifactContent(request, logicalReference);
        var sourceRef = string.IsNullOrWhiteSpace(request.SourceRef)
            ? $"{sourceSystem}:{request.Kind.ToString().ToLowerInvariant()}:{Hash(title, summary, content)[..16]}"
            : request.SourceRef.Trim();
        var externalKey = string.IsNullOrWhiteSpace(request.ExternalKey)
            ? BuildExternalKey(projectId, sourceSystem, sourceRef, request.Kind)
            : request.ExternalKey.Trim();

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
                NormalizeTags(request.Tags, request.Kind, sourceSystem),
                Importance: 0.76m,
                Confidence: 0.88m,
                MetadataJson: BuildMetadataJson(request, sourceSystem, logicalReference),
                ProjectId: projectId),
            cancellationToken);

        return ToResult(document, logicalReference);
    }

    public async Task<IReadOnlyList<ProjectArtifactResult>> ListAsync(ProjectArtifactListRequest request, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryRead);
        var projectId = ProjectContext.Normalize(request.ProjectId);
        ActorAuthorization.EnsureProjectAllowed(actor, projectId, write: false);

        var query = dbContext.MemoryItems.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.MemoryType == MemoryType.Artifact &&
                        x.SourceType == SourceType && x.Status == MemoryStatus.Active);
        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            var text = request.Query.Trim().ToLowerInvariant();
            query = query.Where(x => x.Title.ToLower().Contains(text) || x.Summary.ToLower().Contains(text) ||
                                     x.Content.ToLower().Contains(text) || x.SourceRef.ToLower().Contains(text));
        }

        var rows = await query.OrderByDescending(x => x.UpdatedAt)
            .Take(Math.Clamp(request.Limit * 2, 1, 400)).ToListAsync(cancellationToken);
        var visible = await FilterAuthorizedAsync(rows, cancellationToken);
        return visible
            .Where(x => MatchesFilters(x, request.Kind, request.SourceSystem))
            .Take(Math.Clamp(request.Limit, 1, 200)).ToArray();
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
        var ids = hits.Where(x => x.MemoryType == MemoryType.Artifact && string.Equals(x.SourceType, SourceType, StringComparison.Ordinal))
            .Select(x => x.MemoryId).Distinct().ToArray();
        if (ids.Length == 0) return [];

        var rows = await dbContext.MemoryItems.AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync(cancellationToken);
        var byRank = ids.Select((id, index) => new { id, index }).ToDictionary(x => x.id, x => x.index);
        var visible = await FilterAuthorizedAsync(rows, cancellationToken);
        return visible
            .Where(x => MatchesFilters(x, request.Kind, request.SourceSystem))
            .OrderBy(x => byRank.GetValueOrDefault(x.MemoryId, int.MaxValue))
            .Take(Math.Clamp(request.Limit, 1, 50)).ToArray();
    }

    public async Task<ProjectArtifactResult?> GetAsync(Guid memoryId, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryRead);
        var entity = await dbContext.MemoryItems.AsNoTracking().FirstOrDefaultAsync(
            x => x.Id == memoryId && x.MemoryType == MemoryType.Artifact && x.SourceType == SourceType && x.Status == MemoryStatus.Active,
            cancellationToken);
        if (entity is null) return null;
        ActorAuthorization.EnsureProjectAllowed(actor, entity.ProjectId, write: false);
        return await ToAuthorizedResultAsync(entity, cancellationToken);
    }

    private async Task<LogicalFileReference?> ValidateLogicalReferenceAsync(
        ProjectArtifactPublishRequest request,
        string projectId,
        CancellationToken cancellationToken)
    {
        if (request.Kind != ProjectArtifactKind.FileReference)
        {
            if (request.FileId.HasValue || request.FileVersionId.HasValue)
                throw new InvalidOperationException("Logical file identifiers are valid only for FileReference artifacts.");
            return null;
        }

        if (!request.FileId.HasValue || request.FileId == Guid.Empty || !request.FileVersionId.HasValue || request.FileVersionId == Guid.Empty)
            throw new InvalidOperationException("FileReference requires non-empty FileId and FileVersionId values.");

        var version = await dbContext.FileVersions.AsNoTracking().Include(x => x.FileAsset).SingleOrDefaultAsync(
            x => x.Id == request.FileVersionId.Value && x.FileAssetId == request.FileId.Value && x.FileAsset!.ProjectId == projectId &&
                 x.FileAsset.State == FileAssetState.Active && x.Lifecycle != FileVersionLifecycle.LogicalDeleted,
            cancellationToken) ?? throw new InvalidOperationException("The logical managed file reference is unavailable or does not belong to the requested project.");
        var decision = await managedFileService.AuthorizeOperationAsync(version.Id, FileOperation.Metadata, "artifact-file-reference", cancellationToken);
        if (!decision.Allowed) throw new UnauthorizedAccessException("The current actor cannot reference this managed file version.");
        return new LogicalFileReference(version.FileAssetId, version.Id);
    }

    private static string NormalizeArtifactContent(ProjectArtifactPublishRequest request, LogicalFileReference? logicalReference)
    {
        if (!string.IsNullOrWhiteSpace(request.Content)) return request.Content.Trim();
        if (request.Kind == ProjectArtifactKind.FileReference && logicalReference is not null)
            return "ContextHub managed file reference.";
        throw new InvalidOperationException("Content is required for Summary and Snippet artifacts.");
    }

    private static string BuildMetadataJson(ProjectArtifactPublishRequest request, string sourceSystem, LogicalFileReference? logicalReference)
    {
        var metadata = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["artifactExchange"] = true,
            ["kind"] = request.Kind.ToString(),
            ["sourceSystem"] = sourceSystem,
            ["metadata"] = ParseAndValidateMetadata(request.MetadataJson)
        };
        if (logicalReference is not null)
        {
            metadata["fileId"] = logicalReference.FileId;
            metadata["fileVersionId"] = logicalReference.FileVersionId;
        }
        return JsonSerializer.Serialize(metadata, JsonOptions);
    }

    private static JsonElement ParseAndValidateMetadata(string metadataJson)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(metadataJson) ? "{}" : metadataJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("MetadataJson must contain a JSON object.");
        ValidateMetadataNode(document.RootElement);
        return document.RootElement.Clone();
    }

    private static void ValidateMetadataNode(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (ForbiddenLocatorPropertyNames.Contains(property.Name))
                    throw new InvalidOperationException($"MetadataJson property '{property.Name}' is not allowed on the public artifact contract.");
                ValidateMetadataNode(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) ValidateMetadataNode(item);
        }
    }

    private static ProjectArtifactResult ToResult(MemoryDocument document, LogicalFileReference? logicalReference)
    {
        var metadata = ReadMetadata(document.MetadataJson) ?? throw new InvalidOperationException("Artifact metadata is not a valid logical contract.");
        return new ProjectArtifactResult(document.Id, document.ExternalKey, document.ProjectId, metadata.Kind, document.Title,
            document.Summary, Truncate(document.Content, ContentPreviewLength), metadata.SourceSystem, document.SourceRef,
            document.Tags, logicalReference?.FileId, logicalReference?.FileVersionId, document.CreatedAt, document.UpdatedAt);
    }

    private static ProjectArtifactResult? TryToResult(MemoryItem item)
    {
        var metadata = ReadMetadata(item.MetadataJson);
        if (metadata is null) return null;
        return new ProjectArtifactResult(item.Id, item.ExternalKey, item.ProjectId, metadata.Kind, item.Title, item.Summary,
            Truncate(item.Content, ContentPreviewLength), metadata.SourceSystem, item.SourceRef, item.Tags,
            metadata.FileId, metadata.FileVersionId, item.CreatedAt, item.UpdatedAt);
    }

    private async Task<IReadOnlyList<ProjectArtifactResult>> FilterAuthorizedAsync(
        IReadOnlyList<MemoryItem> items,
        CancellationToken cancellationToken)
    {
        var results = new List<ProjectArtifactResult>(items.Count);
        foreach (var item in items)
        {
            var result = await ToAuthorizedResultAsync(item, cancellationToken);
            if (result is not null) results.Add(result);
        }
        return results;
    }

    private async Task<ProjectArtifactResult?> ToAuthorizedResultAsync(MemoryItem item, CancellationToken cancellationToken)
    {
        var result = TryToResult(item);
        if (result is null || result.Kind != ProjectArtifactKind.FileReference) return result;

        var exists = await dbContext.FileVersions.AsNoTracking().AnyAsync(
            x => x.Id == result.FileVersionId && x.FileAssetId == result.FileId && x.FileAsset!.ProjectId == item.ProjectId &&
                 x.FileAsset.State == FileAssetState.Active && x.Lifecycle != FileVersionLifecycle.LogicalDeleted,
            cancellationToken);
        if (!exists) return null;
        try
        {
            var decision = await managedFileService.AuthorizeOperationAsync(
                result.FileVersionId!.Value,
                FileOperation.Metadata,
                "artifact-file-reference-read",
                cancellationToken);
            return decision.Allowed ? result : null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static ArtifactMetadata? ReadMetadata(string metadataJson)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(metadataJson) ? "{}" : metadataJson);
            var root = document.RootElement;
            ValidateMetadataNode(root);
            if (!root.TryGetProperty("kind", out var kindValue) ||
                !Enum.TryParse<ProjectArtifactKind>(kindValue.GetString(), true, out var kind)) return null;
            var sourceSystem = root.TryGetProperty("sourceSystem", out var sourceSystemValue) ? sourceSystemValue.GetString() ?? string.Empty : string.Empty;
            var fileId = ReadGuid(root, "fileId");
            var fileVersionId = ReadGuid(root, "fileVersionId");
            if (kind == ProjectArtifactKind.FileReference && (!fileId.HasValue || !fileVersionId.HasValue)) return null;
            if (kind != ProjectArtifactKind.FileReference && (fileId.HasValue || fileVersionId.HasValue)) return null;
            return new ArtifactMetadata(kind, sourceSystem, fileId, fileVersionId);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static Guid? ReadGuid(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var parsed)
            ? parsed : null;
    private static bool MatchesFilters(ProjectArtifactResult artifact, ProjectArtifactKind? kind, string? sourceSystem) =>
        (!kind.HasValue || artifact.Kind == kind.Value) &&
        (string.IsNullOrWhiteSpace(sourceSystem) || string.Equals(artifact.SourceSystem, sourceSystem.Trim(), StringComparison.OrdinalIgnoreCase));
    private static IReadOnlyList<string> NormalizeTags(IReadOnlyList<string>? tags, ProjectArtifactKind kind, string sourceSystem) =>
        (tags ?? []).Append("artifact-exchange").Append($"artifact-kind:{kind.ToString().ToLowerInvariant()}")
            .Append($"source-system:{sourceSystem.ToLowerInvariant()}").Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToArray();
    private static string BuildExternalKey(string projectId, string sourceSystem, string sourceRef, ProjectArtifactKind kind) =>
        $"artifact-exchange:{ProjectContext.Normalize(projectId)}:{sourceSystem}:{kind.ToString().ToLowerInvariant()}:{Hash(sourceRef)[..24]}";
    private static string NormalizeRequired(string value, string name)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed)) throw new InvalidOperationException($"{name} is required.");
        return trimmed;
    }
    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength].TrimEnd();
    private static string Hash(params string[] values) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", values))));

    private sealed record LogicalFileReference(Guid FileId, Guid FileVersionId);
    private sealed record ArtifactMetadata(ProjectArtifactKind Kind, string SourceSystem, Guid? FileId, Guid? FileVersionId);
}
