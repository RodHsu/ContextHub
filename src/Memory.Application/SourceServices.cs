using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Memory.Domain;

namespace Memory.Application;

public sealed class SourceConnectionService(
    IApplicationDbContext dbContext,
    IClock clock,
    ISecretProtector secretProtector) : ISourceConnectionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<SourceConnectionResult>> ListAsync(SourceListRequest request, CancellationToken cancellationToken)
    {
        var projectId = ProjectContext.Normalize(request.ProjectId);
        var query = dbContext.SourceConnections
            .AsNoTracking()
            .Where(x => x.ProjectId == projectId);

        if (request.Enabled.HasValue)
        {
            query = query.Where(x => x.Enabled == request.Enabled.Value);
        }

        if (request.SourceKind.HasValue)
        {
            query = query.Where(x => x.SourceKind == request.SourceKind.Value);
        }

        var entities = await query
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
        return entities.Select(Map).ToArray();
    }

    public async Task<SourceConnectionResult> CreateAsync(SourceConnectionCreateRequest request, CancellationToken cancellationToken)
    {
        ValidateJson(request.ConfigJson, "ConfigJson");

        var entity = new SourceConnection
        {
            ProjectId = ProjectContext.Normalize(request.ProjectId),
            Name = request.Name.Trim(),
            SourceKind = request.SourceKind,
            Enabled = request.Enabled,
            ConfigJson = NormalizeJson(request.ConfigJson),
            SecretJsonProtected = string.IsNullOrWhiteSpace(request.SecretJson)
                ? string.Empty
                : secretProtector.Protect(NormalizeJson(request.SecretJson)),
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow
        };

        ValidateSourceConfig(entity.SourceKind, entity.ConfigJson);
        await dbContext.SourceConnections.AddAsync(entity, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    public async Task<SourceConnectionResult> UpdateAsync(SourceConnectionUpdateRequest request, CancellationToken cancellationToken)
    {
        var entity = await dbContext.SourceConnections
            .FirstOrDefaultAsync(x => x.Id == request.Id, cancellationToken)
            ?? throw new InvalidOperationException($"Source connection '{request.Id}' was not found.");

        if (request.ProjectId is not null)
        {
            entity.ProjectId = ProjectContext.Normalize(request.ProjectId, entity.ProjectId);
        }

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            entity.Name = request.Name.Trim();
        }

        if (request.ConfigJson is not null)
        {
            ValidateJson(request.ConfigJson, "ConfigJson");
            entity.ConfigJson = NormalizeJson(request.ConfigJson);
        }

        if (request.SecretJson is not null)
        {
            entity.SecretJsonProtected = string.IsNullOrWhiteSpace(request.SecretJson)
                ? string.Empty
                : secretProtector.Protect(NormalizeJson(request.SecretJson));
        }

        if (request.Enabled.HasValue)
        {
            entity.Enabled = request.Enabled.Value;
        }

        ValidateSourceConfig(entity.SourceKind, entity.ConfigJson);
        entity.UpdatedAt = clock.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    public async Task<EnqueueSourceSyncResult> EnqueueSyncAsync(SourceSyncRequest request, CancellationToken cancellationToken)
    {
        var source = await dbContext.SourceConnections
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == request.SourceConnectionId, cancellationToken)
            ?? throw new InvalidOperationException($"Source connection '{request.SourceConnectionId}' was not found.");

        var payload = JsonSerializer.Serialize(
            new SyncSourceJobPayload(source.Id, source.ProjectId, request.Trigger, request.Force),
            JsonOptions);
        var job = new MemoryJob
        {
            ProjectId = source.ProjectId,
            JobType = MemoryJobType.SyncSource,
            Status = MemoryJobStatus.Pending,
            PayloadJson = payload,
            CreatedAt = clock.UtcNow
        };

        await dbContext.MemoryJobs.AddAsync(job, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new EnqueueSourceSyncResult(job.Id, job.Status);
    }

    public async Task<IReadOnlyList<SourceSyncRunResult>> ListRunsAsync(Guid sourceConnectionId, string? projectId, CancellationToken cancellationToken)
    {
        var normalizedProjectId = projectId is null ? null : ProjectContext.Normalize(projectId);
        var query = dbContext.SourceSyncRuns
            .AsNoTracking()
            .Where(x => x.SourceConnectionId == sourceConnectionId);

        if (normalizedProjectId is not null)
        {
            query = query.Where(x => x.ProjectId == normalizedProjectId);
        }

        var entities = await query
            .OrderByDescending(x => x.StartedAt)
            .Take(50)
            .ToListAsync(cancellationToken);
        return entities.Select(Map).ToArray();
    }

    private static void ValidateJson(string json, string parameterName)
    {
        try
        {
            JsonDocument.Parse(NormalizeJson(json));
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{parameterName} must be valid JSON.", ex);
        }
    }

    private static void ValidateSourceConfig(SourceKind sourceKind, string configJson)
    {
        switch (sourceKind)
        {
            case SourceKind.LocalRepo:
            case SourceKind.LocalDocs:
                var fileConfig = JsonSerializer.Deserialize<FileSourceConfig>(configJson, JsonOptions)
                    ?? throw new InvalidOperationException("File source config is required.");
                if (string.IsNullOrWhiteSpace(fileConfig.RootPath))
                {
                    throw new InvalidOperationException("File source config must include 'rootPath'.");
                }

                break;
            case SourceKind.RuntimeLogRule:
                var logConfig = JsonSerializer.Deserialize<RuntimeLogRuleConfig>(configJson, JsonOptions)
                    ?? throw new InvalidOperationException("RuntimeLogRule config is required.");
                if (logConfig.TimeWindowMinutes <= 0 || logConfig.MinimumOccurrences <= 0)
                {
                    throw new InvalidOperationException("RuntimeLogRule config must have positive timeWindowMinutes and minimumOccurrences.");
                }

                break;
            case SourceKind.GitHubPull:
                var gitHubConfig = JsonSerializer.Deserialize<GitHubPullConfig>(configJson, JsonOptions)
                    ?? throw new InvalidOperationException("GitHubPull config is required.");
                if (string.IsNullOrWhiteSpace(gitHubConfig.Owner) || string.IsNullOrWhiteSpace(gitHubConfig.Repository))
                {
                    throw new InvalidOperationException("GitHubPull config must include 'owner' and 'repository'.");
                }

                break;
        }
    }

    private static string NormalizeJson(string json)
        => string.IsNullOrWhiteSpace(json) ? "{}" : json.Trim();

    private static SourceConnectionResult Map(SourceConnection entity)
        => new(
            entity.Id,
            entity.ProjectId,
            entity.Name,
            entity.SourceKind,
            entity.Enabled,
            entity.ConfigJson,
            !string.IsNullOrWhiteSpace(entity.SecretJsonProtected),
            entity.LastCursor,
            entity.LastSuccessfulSyncAt,
            entity.CreatedAt,
            entity.UpdatedAt);

    private static SourceSyncRunResult Map(SourceSyncRun entity)
        => new(
            entity.Id,
            entity.SourceConnectionId,
            entity.ProjectId,
            entity.Trigger,
            entity.Status,
            entity.ScannedCount,
            entity.UpsertedCount,
            entity.ArchivedCount,
            entity.ErrorCount,
            entity.CursorBefore,
            entity.CursorAfter,
            entity.Error,
            entity.StartedAt,
            entity.CompletedAt);

    private sealed record SyncSourceJobPayload(Guid SourceConnectionId, string ProjectId, SourceSyncTrigger Trigger, bool Force);
    private sealed record FileSourceConfig(string RootPath, string? Scope = null, IReadOnlyList<string>? IncludeExtensions = null, IReadOnlyList<string>? ExcludePatterns = null);
    private sealed record RuntimeLogRuleConfig(string? ServiceName = null, string? Level = null, string? Query = null, int TimeWindowMinutes = 60, int MinimumOccurrences = 1, string? Scope = null);
    private sealed record GitHubPullConfig(string Owner, string Repository, string? Scope = null, int MaxPulls = 20);
}

public sealed class SourceSyncService(
    IApplicationDbContext dbContext,
    IChunkingService chunkingService,
    ICacheVersionStore cacheStore,
    IClock clock,
    ISecretProtector secretProtector) : ISourceSyncService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] DefaultRepoExtensions = [".cs", ".md", ".json", ".yml", ".yaml", ".sql"];
    private static readonly string[] DefaultDocExtensions = [".md", ".txt", ".json"];

    public async Task ProcessSyncJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await dbContext.MemoryJobs
            .FirstOrDefaultAsync(x => x.Id == jobId, cancellationToken)
            ?? throw new InvalidOperationException($"Sync job '{jobId}' was not found.");
        var payload = JsonSerializer.Deserialize<SyncSourceJobPayload>(job.PayloadJson, JsonOptions)
            ?? throw new InvalidOperationException("Sync source job payload is invalid.");
        var source = await dbContext.SourceConnections
            .FirstOrDefaultAsync(x => x.Id == payload.SourceConnectionId, cancellationToken)
            ?? throw new InvalidOperationException($"Source connection '{payload.SourceConnectionId}' was not found.");

        var run = new SourceSyncRun
        {
            SourceConnectionId = source.Id,
            ProjectId = source.ProjectId,
            Trigger = payload.Trigger,
            Status = SourceSyncStatus.Running,
            CursorBefore = source.LastCursor,
            StartedAt = clock.UtcNow
        };

        await dbContext.SourceSyncRuns.AddAsync(run, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            var outcome = source.SourceKind switch
            {
                SourceKind.LocalRepo => await SyncFileSourceAsync(source, DefaultRepoExtensions, true, cancellationToken),
                SourceKind.LocalDocs => await SyncFileSourceAsync(source, DefaultDocExtensions, false, cancellationToken),
                SourceKind.RuntimeLogRule => await SyncRuntimeLogRuleAsync(source, cancellationToken),
                SourceKind.GitHubPull => await SyncGitHubPullAsync(source, cancellationToken),
                _ => throw new InvalidOperationException($"Unsupported source kind '{source.SourceKind}'.")
            };

            run.Status = SourceSyncStatus.Completed;
            run.ScannedCount = outcome.ScannedCount;
            run.UpsertedCount = outcome.UpsertedCount;
            run.ArchivedCount = outcome.ArchivedCount;
            run.ErrorCount = outcome.ErrorCount;
            run.CursorAfter = outcome.CursorAfter ?? source.LastCursor;
            run.CompletedAt = clock.UtcNow;

            source.LastCursor = run.CursorAfter;
            source.LastSuccessfulSyncAt = run.CompletedAt;
            source.UpdatedAt = clock.UtcNow;

            if (outcome.HasMutations)
            {
                await EnqueueProjectJobAsync(source.ProjectId, MemoryJobType.Reindex, new ReindexJobPayload(null, null, source.ProjectId), cancellationToken);
                await EnqueueProjectJobAsync(source.ProjectId, MemoryJobType.AnalyzeGovernance, new GovernanceAnalysisJobPayload(source.ProjectId), cancellationToken);
                await cacheStore.IncrementAsync(cancellationToken);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            run.Status = SourceSyncStatus.Failed;
            run.Error = ex.Message;
            run.ErrorCount = Math.Max(1, run.ErrorCount);
            run.CompletedAt = clock.UtcNow;
            source.UpdatedAt = clock.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
            throw;
        }
    }

    private async Task<ConnectorSyncOutcome> SyncFileSourceAsync(
        SourceConnection source,
        IReadOnlyList<string> defaultExtensions,
        bool applyRepoExclusions,
        CancellationToken cancellationToken)
    {
        var config = JsonSerializer.Deserialize<FileSourceConfig>(source.ConfigJson, JsonOptions)
            ?? throw new InvalidOperationException($"Source '{source.Name}' file config is invalid.");
        var rootPath = Path.GetFullPath(config.RootPath);
        if (!Directory.Exists(rootPath))
        {
            throw new InvalidOperationException($"Source root path '{rootPath}' does not exist.");
        }

        var includeExtensions = (config.IncludeExtensions is { Count: > 0 } ? config.IncludeExtensions : defaultExtensions)
            .Select(NormalizeExtension)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var currentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scannedCount = 0;
        var upsertedCount = 0;
        var latestCursor = source.LastCursor;
        var files = Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
            .Where(path => includeExtensions.Contains(NormalizeExtension(Path.GetExtension(path)), StringComparer.OrdinalIgnoreCase))
            .Where(path => !IsExcludedPath(rootPath, path, applyRepoExclusions, config.ExcludePatterns))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            scannedCount++;
            var normalizedPath = NormalizeRelativePath(rootPath, path);
            var content = await File.ReadAllTextAsync(path, cancellationToken);
            var version = ComputeSha(content);
            var lastWrite = File.GetLastWriteTimeUtc(path).ToString("O");
            latestCursor = string.CompareOrdinal(latestCursor, lastWrite) >= 0 ? latestCursor : lastWrite;
            var externalKey = $"file:{normalizedPath}:{version}";
            currentKeys.Add(externalKey);

            var metadata = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["sourceVersion"] = version,
                ["cursor"] = latestCursor,
                ["originPathOrUrl"] = path,
                ["connectorId"] = source.Id,
                ["syncedAt"] = clock.UtcNow,
                ["lineage"] = new[] { normalizedPath },
                ["missing"] = false,
                ["sourceManaged"] = true
            }, JsonOptions);

            var updated = await UpsertConnectorMemoryAsync(
                source,
                externalKey,
                ResolveScope(config.Scope, MemoryScope.Project),
                normalizedPath,
                content,
                BuildSummary(content, normalizedPath),
                normalizedPath,
                metadata,
                [$"source:{source.SourceKind}", "artifact", $"ext:{Path.GetExtension(path).TrimStart('.').ToLowerInvariant()}"],
                0.62m,
                0.84m,
                cancellationToken);
            if (updated)
            {
                upsertedCount++;
            }
        }

        var archivedCount = await ArchiveMissingConnectorMemoriesAsync(source, currentKeys, cancellationToken);
        return new ConnectorSyncOutcome(scannedCount, upsertedCount, archivedCount, 0, latestCursor);
    }

    private async Task<ConnectorSyncOutcome> SyncRuntimeLogRuleAsync(SourceConnection source, CancellationToken cancellationToken)
    {
        var config = JsonSerializer.Deserialize<RuntimeLogRuleConfig>(source.ConfigJson, JsonOptions)
            ?? throw new InvalidOperationException($"Source '{source.Name}' runtime log config is invalid.");
        var projectId = ProjectContext.Normalize(source.ProjectId);
        var now = clock.UtcNow;
        var from = now.AddMinutes(-config.TimeWindowMinutes);
        var query = dbContext.RuntimeLogEntries
            .Where(x => x.ProjectId == projectId)
            .Where(x => x.CreatedAt >= from && x.CreatedAt <= now);

        if (!string.IsNullOrWhiteSpace(config.ServiceName))
        {
            query = query.Where(x => x.ServiceName == config.ServiceName);
        }

        if (!string.IsNullOrWhiteSpace(config.Level))
        {
            query = query.Where(x => x.Level == config.Level);
        }

        if (!string.IsNullOrWhiteSpace(config.Query))
        {
            query = query.Where(x => x.Message.Contains(config.Query) || x.Exception.Contains(config.Query));
        }

        var entries = await query.OrderBy(x => x.CreatedAt).Take(500).ToListAsync(cancellationToken);
        if (entries.Count < config.MinimumOccurrences)
        {
            return new ConnectorSyncOutcome(entries.Count, 0, 0, 0, now.ToString("O"));
        }

        var bucket = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero).ToString("yyyyMMddHH");
        var externalKey = $"log-rule:{source.Id}:{bucket}";
        var title = string.IsNullOrWhiteSpace(config.ServiceName)
            ? $"Runtime log slice {bucket}"
            : $"{config.ServiceName} log slice {bucket}";
        var content = string.Join(
            Environment.NewLine,
            entries.Select(entry => $"[{entry.CreatedAt:O}] [{entry.Level}] {entry.ServiceName}: {entry.Message}{(string.IsNullOrWhiteSpace(entry.Exception) ? string.Empty : $" | {entry.Exception}")}"));
        var metadata = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["sourceVersion"] = entries.Max(x => x.CreatedAt).ToString("O"),
            ["cursor"] = now.ToString("O"),
            ["originPathOrUrl"] = $"runtime-log-rule:{source.Id}",
            ["connectorId"] = source.Id,
            ["syncedAt"] = clock.UtcNow,
            ["lineage"] = entries.Select(x => $"runtime_log_entries:{x.Id}").ToArray(),
            ["missing"] = false,
            ["sourceManaged"] = true
        }, JsonOptions);

        var updated = await UpsertConnectorMemoryAsync(
            source,
            externalKey,
            ResolveScope(config.Scope, MemoryScope.Project),
            title,
            content,
            $"{entries.Count} 則 runtime log 片段，時間窗 {config.TimeWindowMinutes} 分鐘。",
            $"runtime-log-rule:{source.Id}",
            metadata,
            ["source:RuntimeLogRule", "artifact", "logs"],
            0.58m,
            0.8m,
            cancellationToken);

        return new ConnectorSyncOutcome(entries.Count, updated ? 1 : 0, 0, 0, now.ToString("O"));
    }

    private async Task<ConnectorSyncOutcome> SyncGitHubPullAsync(SourceConnection source, CancellationToken cancellationToken)
    {
        var config = JsonSerializer.Deserialize<GitHubPullConfig>(source.ConfigJson, JsonOptions)
            ?? throw new InvalidOperationException($"Source '{source.Name}' GitHub config is invalid.");
        var secret = string.IsNullOrWhiteSpace(source.SecretJsonProtected)
            ? null
            : JsonSerializer.Deserialize<GitHubPullSecret>(secretProtector.Unprotect(source.SecretJsonProtected), JsonOptions);
        if (string.IsNullOrWhiteSpace(secret?.Token))
        {
            throw new InvalidOperationException("GitHubPull source requires a token in secretJson.");
        }

        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ContextHub", "1.0"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret.Token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        var listUrl = $"https://api.github.com/repos/{config.Owner}/{config.Repository}/pulls?state=all&sort=updated&direction=desc&per_page={Math.Clamp(config.MaxPulls, 1, 50)}";
        using var listResponse = await client.GetAsync(listUrl, cancellationToken);
        listResponse.EnsureSuccessStatusCode();
        using var listDocument = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync(cancellationToken));

        var scannedCount = 0;
        var upsertedCount = 0;
        var latestCursor = source.LastCursor;

        foreach (var pullRequest in listDocument.RootElement.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            scannedCount++;
            var number = pullRequest.GetProperty("number").GetInt32();
            var title = pullRequest.GetProperty("title").GetString() ?? $"PR #{number}";
            var body = pullRequest.TryGetProperty("body", out var bodyElement) ? bodyElement.GetString() ?? string.Empty : string.Empty;
            var state = pullRequest.GetProperty("state").GetString() ?? "unknown";
            var updatedAt = pullRequest.GetProperty("updated_at").GetString() ?? string.Empty;
            var htmlUrl = pullRequest.GetProperty("html_url").GetString() ?? string.Empty;
            latestCursor = string.CompareOrdinal(latestCursor, updatedAt) >= 0 ? latestCursor : updatedAt;

            var commentsUrl = $"https://api.github.com/repos/{config.Owner}/{config.Repository}/pulls/{number}/comments?per_page=20";
            using var commentsResponse = await client.GetAsync(commentsUrl, cancellationToken);
            commentsResponse.EnsureSuccessStatusCode();
            using var commentsDocument = JsonDocument.Parse(await commentsResponse.Content.ReadAsStringAsync(cancellationToken));
            var comments = commentsDocument.RootElement.EnumerateArray()
                .Select(comment =>
                {
                    var author = comment.GetProperty("user").GetProperty("login").GetString() ?? "unknown";
                    var text = comment.TryGetProperty("body", out var commentBody) ? commentBody.GetString() ?? string.Empty : string.Empty;
                    return $"- {author}: {text}";
                })
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Take(20)
                .ToArray();

            var builder = new StringBuilder()
                .AppendLine($"PR #{number}: {title}")
                .AppendLine($"State: {state}")
                .AppendLine($"UpdatedAt: {updatedAt}")
                .AppendLine($"Url: {htmlUrl}")
                .AppendLine()
                .AppendLine(body);
            if (comments.Length > 0)
            {
                builder.AppendLine().AppendLine("Review Comments:").AppendLine(string.Join(Environment.NewLine, comments));
            }

            var metadata = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["sourceVersion"] = updatedAt,
                ["cursor"] = latestCursor,
                ["originPathOrUrl"] = htmlUrl,
                ["connectorId"] = source.Id,
                ["syncedAt"] = clock.UtcNow,
                ["lineage"] = new[] { htmlUrl },
                ["missing"] = false,
                ["sourceManaged"] = true
            }, JsonOptions);

            var updated = await UpsertConnectorMemoryAsync(
                source,
                $"github-pr:{config.Owner}/{config.Repository}:{number}",
                ResolveScope(config.Scope, MemoryScope.Project),
                $"PR #{number} {title}",
                builder.ToString().Trim(),
                $"PR #{number} / {state} / {comments.Length} 則 review comments",
                htmlUrl,
                metadata,
                ["source:GitHubPull", "artifact", $"repo:{config.Owner}/{config.Repository}"],
                0.63m,
                0.81m,
                cancellationToken);
            if (updated)
            {
                upsertedCount++;
            }
        }

        return new ConnectorSyncOutcome(scannedCount, upsertedCount, 0, 0, latestCursor);
    }

    private async Task<bool> UpsertConnectorMemoryAsync(
        SourceConnection source,
        string externalKey,
        MemoryScope scope,
        string title,
        string content,
        string summary,
        string sourceRef,
        string metadataJson,
        IReadOnlyList<string> tags,
        decimal importance,
        decimal confidence,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.MemoryItems
            .FirstOrDefaultAsync(x => x.ProjectId == source.ProjectId && x.ExternalKey == externalKey, cancellationToken);
        var created = entity is null;
        var changed = created;

        if (entity is null)
        {
            entity = new MemoryItem
            {
                ProjectId = source.ProjectId,
                ExternalKey = externalKey,
                Scope = scope,
                MemoryType = MemoryType.Artifact,
                CreatedAt = clock.UtcNow,
                UpdatedAt = clock.UtcNow
            };
            await dbContext.MemoryItems.AddAsync(entity, cancellationToken);
        }
        else
        {
            changed |= entity.Scope != scope ||
                       entity.Title != title ||
                       entity.Content != content ||
                       entity.Summary != summary ||
                       entity.SourceType != source.SourceKind.ToString() ||
                       entity.SourceRef != sourceRef ||
                       entity.Importance != importance ||
                       entity.Confidence != confidence ||
                       entity.Status != MemoryStatus.Active ||
                       entity.MetadataJson != metadataJson ||
                       !entity.Tags.SequenceEqual(tags);
        }

        entity.Scope = scope;
        entity.MemoryType = MemoryType.Artifact;
        entity.Title = title;
        entity.Content = content;
        entity.Summary = summary;
        entity.Tags = tags.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        entity.SourceType = source.SourceKind.ToString();
        entity.SourceRef = sourceRef;
        entity.Importance = importance;
        entity.Confidence = confidence;
        entity.Status = MemoryStatus.Active;
        entity.IsReadOnly = true;
        entity.MetadataJson = metadataJson;
        entity.UpdatedAt = clock.UtcNow;
        entity.Version = created ? 1 : (changed ? entity.Version + 1 : entity.Version);

        if (changed)
        {
            await dbContext.MemoryItemRevisions.AddAsync(new MemoryItemRevision
            {
                MemoryItemId = entity.Id,
                Version = entity.Version,
                Title = entity.Title,
                Content = entity.Content,
                Summary = entity.Summary,
                MetadataJson = entity.MetadataJson,
                ChangedBy = "source-sync",
                CreatedAt = clock.UtcNow
            }, cancellationToken);

            await ReplaceChunksAsync(entity, cancellationToken);
        }

        return changed;
    }

    private async Task ReplaceChunksAsync(MemoryItem entity, CancellationToken cancellationToken)
    {
        var existingChunks = await dbContext.MemoryItemChunks
            .Where(x => x.MemoryItemId == entity.Id)
            .ToListAsync(cancellationToken);
        if (existingChunks.Count > 0)
        {
            dbContext.MemoryItemChunks.RemoveRange(existingChunks);
        }

        var chunks = chunkingService.Chunk(entity.MemoryType, entity.SourceType, entity.Content);
        foreach (var draft in chunks)
        {
            await dbContext.MemoryItemChunks.AddAsync(new MemoryItemChunk
            {
                MemoryItemId = entity.Id,
                ChunkKind = draft.Kind,
                ChunkIndex = draft.Index,
                ChunkText = draft.Text,
                MetadataJson = draft.MetadataJson,
                CreatedAt = clock.UtcNow
            }, cancellationToken);
        }
    }

    private async Task<int> ArchiveMissingConnectorMemoriesAsync(
        SourceConnection source,
        IReadOnlySet<string> currentKeys,
        CancellationToken cancellationToken)
    {
        var marker = $"\"connectorId\":\"{source.Id}\"";
        var existing = await dbContext.MemoryItems
            .Where(x => x.ProjectId == source.ProjectId)
            .Where(x => x.SourceType == source.SourceKind.ToString())
            .Where(x => x.MetadataJson.Contains(marker))
            .Where(x => x.Status != MemoryStatus.Archived)
            .ToListAsync(cancellationToken);
        var archivedCount = 0;
        foreach (var entity in existing.Where(item => !currentKeys.Contains(item.ExternalKey)))
        {
            archivedCount++;
            entity.Status = MemoryStatus.Archived;
            entity.UpdatedAt = clock.UtcNow;
            entity.MetadataJson = MergeMetadata(entity.MetadataJson, new Dictionary<string, object?>
            {
                ["missing"] = true,
                ["missingDetectedAt"] = clock.UtcNow
            });
            entity.Version += 1;

            await dbContext.MemoryItemRevisions.AddAsync(new MemoryItemRevision
            {
                MemoryItemId = entity.Id,
                Version = entity.Version,
                Title = entity.Title,
                Content = entity.Content,
                Summary = entity.Summary,
                MetadataJson = entity.MetadataJson,
                ChangedBy = "source-sync-missing",
                CreatedAt = clock.UtcNow
            }, cancellationToken);
        }

        return archivedCount;
    }

    private async Task EnqueueProjectJobAsync<TPayload>(string projectId, MemoryJobType jobType, TPayload payload, CancellationToken cancellationToken)
    {
        await dbContext.MemoryJobs.AddAsync(new MemoryJob
        {
            ProjectId = projectId,
            JobType = jobType,
            Status = MemoryJobStatus.Pending,
            PayloadJson = JsonSerializer.Serialize(payload, JsonOptions),
            CreatedAt = clock.UtcNow
        }, cancellationToken);
    }

    private static MemoryScope ResolveScope(string? configuredScope, MemoryScope fallback)
        => Enum.TryParse<MemoryScope>(configuredScope, true, out var parsed) ? parsed : fallback;

    private static string NormalizeRelativePath(string rootPath, string fullPath)
        => Path.GetRelativePath(rootPath, fullPath).Replace('\\', '/').TrimStart('/');

    private static bool IsExcludedPath(string rootPath, string fullPath, bool applyRepoExclusions, IReadOnlyList<string>? additionalPatterns)
    {
        var relative = NormalizeRelativePath(rootPath, fullPath);
        if (applyRepoExclusions &&
            (relative.StartsWith(".git/", StringComparison.OrdinalIgnoreCase) ||
             relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) ||
             relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) ||
             relative.StartsWith("artifacts/", StringComparison.OrdinalIgnoreCase) ||
             relative.StartsWith("deploy/release-", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return additionalPatterns?.Any(pattern =>
            !string.IsNullOrWhiteSpace(pattern) &&
            relative.Contains(pattern.Replace('\\', '/').Trim(), StringComparison.OrdinalIgnoreCase)) == true;
    }

    private static string NormalizeExtension(string extension)
        => string.IsNullOrWhiteSpace(extension)
            ? string.Empty
            : extension.StartsWith('.') ? extension.ToLowerInvariant() : $".{extension.ToLowerInvariant()}";

    private static string BuildSummary(string content, string fallback)
    {
        var line = content
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(static entry => !string.IsNullOrWhiteSpace(entry));
        if (string.IsNullOrWhiteSpace(line))
        {
            return fallback;
        }

        return line.Length <= 180 ? line : $"{line[..177]}...";
    }

    private static string ComputeSha(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static string MergeMetadata(string metadataJson, IReadOnlyDictionary<string, object?> updates)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(metadataJson) ? "{}" : metadataJson);
        var merged = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            merged[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.GetRawText();
        }

        foreach (var pair in updates)
        {
            merged[pair.Key] = pair.Value;
        }

        return JsonSerializer.Serialize(merged, JsonOptions);
    }

    private sealed record ConnectorSyncOutcome(int ScannedCount, int UpsertedCount, int ArchivedCount, int ErrorCount, string? CursorAfter)
    {
        public bool HasMutations => UpsertedCount > 0 || ArchivedCount > 0;
    }

    private sealed record SyncSourceJobPayload(Guid SourceConnectionId, string ProjectId, SourceSyncTrigger Trigger, bool Force);
    private sealed record ReindexJobPayload(string? ModelKey, Guid? MemoryItemId, string ProjectId);
    private sealed record GovernanceAnalysisJobPayload(string ProjectId);
    private sealed record FileSourceConfig(string RootPath, string? Scope = null, IReadOnlyList<string>? IncludeExtensions = null, IReadOnlyList<string>? ExcludePatterns = null);
    private sealed record RuntimeLogRuleConfig(string? ServiceName = null, string? Level = null, string? Query = null, int TimeWindowMinutes = 60, int MinimumOccurrences = 1, string? Scope = null);
    private sealed record GitHubPullConfig(string Owner, string Repository, string? Scope = null, int MaxPulls = 20);
    private sealed record GitHubPullSecret(string Token);
}
