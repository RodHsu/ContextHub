using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Memory.Domain;

namespace Memory.Application;

public sealed class InstanceBehaviorSettingsAccessor(
    IApplicationDbContext dbContext,
    IOptions<ContextHubOptions> options) : IInstanceBehaviorSettingsAccessor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _instanceId = string.IsNullOrWhiteSpace(options.Value.InstanceId)
        ? ProjectContext.DefaultProjectId
        : options.Value.InstanceId.Trim();

    public async Task<InstanceBehaviorSettingsResult> GetCurrentAsync(CancellationToken cancellationToken)
    {
        var row = await dbContext.InstanceSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.InstanceId == _instanceId &&
                     x.SettingKey == "behavior",
                cancellationToken);

        if (row is null)
        {
            return CreateDefault();
        }

        return InstanceBehaviorSettingsSerializer.DeserializeOrDefault(row.ValueJson, CreateDefault);
    }

    internal static InstanceBehaviorSettingsResult CreateDefault()
        => new(
            ConversationAutomationEnabled: false,
            HostEventIngestionEnabled: true,
            AgentSupplementalIngestionEnabled: true,
            IdleThresholdMinutes: 20,
            PromotionMode: "Automatic",
            ExcerptMaxLength: 240,
            DefaultProjectId: ProjectContext.DefaultProjectId,
            DefaultQueryMode: MemoryQueryMode.CurrentOnly,
            DefaultUseSummaryLayer: false,
            SharedSummaryAutoRefreshEnabled: true,
            SnapshotPolling: DashboardSnapshotPollingDefaults.Create(),
            OverviewPollingSeconds: 10,
            MetricsPollingSeconds: 5,
            JobsPollingSeconds: 8,
            LogsPollingSeconds: 10,
            PerformancePollingSeconds: 30);
}

public sealed class ConversationAutomationService(
    IApplicationDbContext dbContext,
    IMemoryService memoryService,
    ICacheVersionStore cacheStore,
    IClock clock,
    IInstanceBehaviorSettingsAccessor behaviorSettingsAccessor) : IConversationAutomationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ConversationIngestResult> IngestAsync(ConversationIngestRequest request, CancellationToken cancellationToken)
    {
        Validate(request);

        var behavior = await behaviorSettingsAccessor.GetCurrentAsync(cancellationToken);
        var effectiveProjectId = ProjectContext.Normalize(request.ProjectId, behavior.DefaultProjectId);
        var projectName = request.ProjectName?.Trim() ?? string.Empty;
        var session = await dbContext.ConversationSessions
            .FirstOrDefaultAsync(
                x => x.ConversationId == request.ConversationId.Trim() &&
                     x.SourceSystem == request.SourceSystem.Trim(),
                cancellationToken);

        if (session is null)
        {
            session = new ConversationSession
            {
                ConversationId = request.ConversationId.Trim(),
                ProjectId = effectiveProjectId,
                ProjectName = projectName,
                TaskId = request.TaskId?.Trim() ?? string.Empty,
                SourceSystem = request.SourceSystem.Trim(),
                Status = "Active",
                LastTurnId = request.TurnId.Trim(),
                StartedAt = clock.UtcNow,
                LastCheckpointAt = clock.UtcNow,
                UpdatedAt = clock.UtcNow
            };
            await dbContext.ConversationSessions.AddAsync(session, cancellationToken);
        }
        else
        {
            session.ProjectId = effectiveProjectId;
            session.ProjectName = projectName;
            session.TaskId = request.TaskId?.Trim() ?? session.TaskId;
            session.LastTurnId = request.TurnId.Trim();
            session.LastCheckpointAt = clock.UtcNow;
            session.UpdatedAt = clock.UtcNow;
        }

        var checkpointDedupKey = Hash(
            request.SourceSystem.Trim(),
            request.ConversationId.Trim(),
            request.TurnId.Trim(),
            request.EventType.ToString(),
            request.SourceKind.ToString());

        var existingCheckpoint = await dbContext.ConversationCheckpoints
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.DedupKey == checkpointDedupKey, cancellationToken);

        if (existingCheckpoint is not null)
        {
            var existingJobId = await FindExistingCheckpointJobIdAsync(existingCheckpoint.Id, cancellationToken);

            return new ConversationIngestResult(
                session.Id,
                existingCheckpoint.Id,
                existingJobId,
                existingCheckpoint.ProjectId,
                existingCheckpoint.ProjectName,
                existingJobId.HasValue);
        }

        var metadata = new ConversationCheckpointMetadata(
            BuildCandidateProjectIds(request, effectiveProjectId),
            BuildProjectHints(request));

        var checkpoint = new ConversationCheckpoint
        {
            SessionId = session.Id,
            ConversationId = session.ConversationId,
            TurnId = request.TurnId.Trim(),
            ProjectId = effectiveProjectId,
            ProjectName = projectName,
            TaskId = request.TaskId?.Trim() ?? string.Empty,
            SourceSystem = session.SourceSystem,
            EventType = request.EventType,
            SourceKind = request.SourceKind,
            SourceRef = request.SourceRef.Trim(),
            UserMessageSummary = NormalizeText(request.UserMessageSummary),
            AgentMessageSummary = NormalizeText(request.AgentMessageSummary),
            ToolCallsJson = JsonSerializer.Serialize(request.ToolCalls ?? [], JsonOptions),
            SessionSummary = NormalizeText(request.SessionSummary),
            ShortExcerpt = BuildShortExcerpt(request, behavior.ExcerptMaxLength),
            DedupKey = checkpointDedupKey,
            MetadataJson = JsonSerializer.Serialize(metadata, JsonOptions),
            CreatedAt = clock.UtcNow
        };

        await dbContext.ConversationCheckpoints.AddAsync(checkpoint, cancellationToken);

        Guid? jobId = null;
        if (ShouldScheduleAutomation(request.SourceKind, behavior))
        {
            var job = new MemoryJob
            {
                ProjectId = effectiveProjectId,
                JobType = MemoryJobType.IngestConversation,
                Status = MemoryJobStatus.Pending,
                PayloadJson = JsonSerializer.Serialize(new ConversationCheckpointJobPayload(checkpoint.Id), JsonOptions),
                CreatedAt = clock.UtcNow
            };

            await dbContext.MemoryJobs.AddAsync(job, cancellationToken);
            jobId = job.Id;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        if (jobId.HasValue)
        {
            await cacheStore.PublishJobSignalAsync(jobId.Value, cancellationToken);
        }

        return new ConversationIngestResult(
            session.Id,
            checkpoint.Id,
            jobId,
            effectiveProjectId,
            projectName,
            jobId.HasValue);
    }

    public async Task<IReadOnlyList<ConversationSessionResult>> ListSessionsAsync(ConversationSessionListRequest request, CancellationToken cancellationToken)
    {
        var query = dbContext.ConversationSessions.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.ProjectId))
        {
            var projectId = ProjectContext.Normalize(request.ProjectId);
            query = query.Where(x => x.ProjectId == projectId);
        }

        if (!string.IsNullOrWhiteSpace(request.SourceSystem))
        {
            query = query.Where(x => x.SourceSystem == request.SourceSystem.Trim());
        }

        if (!string.IsNullOrWhiteSpace(request.ConversationId))
        {
            query = query.Where(x => x.ConversationId == request.ConversationId.Trim());
        }

        return await query
            .OrderByDescending(x => x.UpdatedAt)
            .Take(Math.Clamp(request.Limit, 1, 200))
            .Select(x => new ConversationSessionResult(
                x.Id,
                x.ConversationId,
                x.ProjectId,
                x.ProjectName,
                x.TaskId,
                x.SourceSystem,
                x.Status,
                x.LastTurnId,
                x.StartedAt,
                x.LastCheckpointAt,
                x.UpdatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ConversationInsightResult>> ListInsightsAsync(ConversationInsightListRequest request, CancellationToken cancellationToken)
    {
        var query = dbContext.ConversationInsights.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.ProjectId))
        {
            var projectId = ProjectContext.Normalize(request.ProjectId);
            query = query.Where(x => x.ProjectId == projectId);
        }

        if (!string.IsNullOrWhiteSpace(request.ConversationId))
        {
            query = query.Where(x => x.ConversationId == request.ConversationId.Trim());
        }

        if (request.PromotionStatus.HasValue)
        {
            query = query.Where(x => x.PromotionStatus == request.PromotionStatus.Value);
        }

        if (request.InsightType.HasValue)
        {
            query = query.Where(x => x.InsightType == request.InsightType.Value);
        }

        return await query
            .OrderByDescending(x => x.UpdatedAt)
            .Take(Math.Clamp(request.Limit, 1, 400))
            .Select(x => new ConversationInsightResult(
                x.Id,
                x.SessionId,
                x.CheckpointId,
                x.ConversationId,
                x.TurnId,
                x.ProjectId,
                x.ProjectName,
                x.TaskId,
                x.SourceSystem,
                x.SourceKind,
                x.InsightType,
                x.Title,
                x.Content,
                x.Summary,
                x.SourceRef,
                x.Tags,
                x.Importance,
                x.Confidence,
                x.DedupKey,
                x.PromotionStatus,
                x.PromotedMemoryId,
                x.Error,
                x.CreatedAt,
                x.UpdatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<ConversationAutomationStatusResult> GetAutomationStatusAsync(CancellationToken cancellationToken)
    {
        var since = clock.UtcNow.AddHours(-24);
        var recentCheckpoints = await dbContext.ConversationCheckpoints.CountAsync(x => x.CreatedAt >= since, cancellationToken);
        var pendingInsights = await dbContext.ConversationInsights.CountAsync(x => x.PromotionStatus == ConversationPromotionStatus.Pending, cancellationToken);
        var pendingPromotions = await dbContext.MemoryJobs.CountAsync(
            x => x.JobType == MemoryJobType.PromoteConversationInsights &&
                 (x.Status == MemoryJobStatus.Pending || x.Status == MemoryJobStatus.Running),
            cancellationToken);
        var lastPromotionError = await dbContext.MemoryJobs
            .Where(x => x.JobType == MemoryJobType.PromoteConversationInsights && x.Status == MemoryJobStatus.Failed)
            .OrderByDescending(x => x.CompletedAt ?? x.CreatedAt)
            .Select(x => x.Error)
            .FirstOrDefaultAsync(cancellationToken);

        return new ConversationAutomationStatusResult(
            recentCheckpoints,
            pendingInsights,
            pendingPromotions,
            lastPromotionError ?? string.Empty);
    }

    public async Task ProcessCheckpointJobAsync(Guid checkpointId, CancellationToken cancellationToken)
    {
        var checkpoint = await dbContext.ConversationCheckpoints
            .Include(x => x.Session)
            .FirstOrDefaultAsync(x => x.Id == checkpointId, cancellationToken)
            ?? throw new InvalidOperationException($"Conversation checkpoint '{checkpointId}' was not found.");

        var behavior = await behaviorSettingsAccessor.GetCurrentAsync(cancellationToken);
        var toolCalls = DeserializeToolCalls(checkpoint.ToolCallsJson);
        var candidates = ConversationInsightClassifier.Extract(
            checkpoint,
            toolCalls,
            behavior.ExcerptMaxLength);

        foreach (var candidate in candidates)
        {
            var dedupKey = BuildInsightDedupKey(checkpoint, candidate);
            var exists = await dbContext.ConversationInsights.AnyAsync(x => x.DedupKey == dedupKey, cancellationToken);
            if (exists)
            {
                continue;
            }

            await dbContext.ConversationInsights.AddAsync(new ConversationInsight
            {
                SessionId = checkpoint.SessionId,
                CheckpointId = checkpoint.Id,
                ConversationId = checkpoint.ConversationId,
                TurnId = checkpoint.TurnId,
                ProjectId = candidate.ProjectId,
                ProjectName = candidate.ProjectName,
                TaskId = checkpoint.TaskId,
                SourceSystem = checkpoint.SourceSystem,
                SourceKind = checkpoint.SourceKind,
                InsightType = candidate.InsightType,
                Title = candidate.Title,
                Content = candidate.Content,
                Summary = candidate.Summary,
                SourceRef = candidate.SourceRef,
                Tags = candidate.Tags.ToArray(),
                Importance = candidate.Importance,
                Confidence = candidate.Confidence,
                DedupKey = dedupKey,
                PromotionStatus = ConversationPromotionStatus.Pending,
                MetadataJson = candidate.MetadataJson,
                CreatedAt = clock.UtcNow,
                UpdatedAt = clock.UtcNow
            }, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        if (ShouldEnqueuePromotion(checkpoint.EventType))
        {
            await EnqueuePromotionJobIfNeededAsync(checkpoint.ConversationId, checkpoint.ProjectId, cancellationToken);
        }
    }

    public async Task PromotePendingInsightsAsync(string? conversationId, string? projectId, CancellationToken cancellationToken)
    {
        var query = dbContext.ConversationInsights
            .Where(x => x.PromotionStatus == ConversationPromotionStatus.Pending)
            .OrderBy(x => x.CreatedAt)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(conversationId))
        {
            query = query.Where(x => x.ConversationId == conversationId.Trim());
        }

        if (string.IsNullOrWhiteSpace(conversationId) && !string.IsNullOrWhiteSpace(projectId))
        {
            var normalizedProjectId = ProjectContext.Normalize(projectId);
            query = query.Where(x => x.ProjectId == normalizedProjectId);
        }

        var items = await query.ToListAsync(cancellationToken);
        foreach (var item in items)
        {
            try
            {
                if (item.InsightType == ConversationInsightType.PreferenceCandidate)
                {
                    var preferenceResult = await memoryService.UpsertUserPreferenceAsync(
                        new UserPreferenceUpsertRequest(
                            Key: $"conversation-auto:{item.DedupKey[..Math.Min(24, item.DedupKey.Length)]}",
                            Kind: InferPreferenceKind(item),
                            Title: item.Title,
                            Content: item.Content,
                            Rationale: item.Summary,
                            Tags: item.Tags.Append("conversation-auto").Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                            Importance: item.Importance,
                            Confidence: item.Confidence),
                        cancellationToken);

                    item.PromotedMemoryId = preferenceResult.Id;
                }
                else
                {
                    var document = await memoryService.UpsertAsync(
                        new MemoryUpsertRequest(
                            ExternalKey: $"conversation-insight:{item.DedupKey}",
                            Scope: item.InsightType == ConversationInsightType.Episode && !string.IsNullOrWhiteSpace(item.TaskId)
                                ? MemoryScope.Task
                                : MemoryScope.Project,
                            MemoryType: ToMemoryType(item.InsightType),
                            Title: item.Title,
                            Content: item.Content,
                            Summary: item.Summary,
                            SourceType: item.SourceRef.StartsWith("tool:", StringComparison.OrdinalIgnoreCase)
                                ? "conversation-tool-call"
                                : "conversation-auto",
                            SourceRef: item.SourceRef,
                            Tags: item.Tags.Append("conversation-auto").Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                            Importance: item.Importance,
                            Confidence: item.Confidence,
                            MetadataJson: item.MetadataJson,
                            ProjectId: item.ProjectId),
                        cancellationToken);

                    item.PromotedMemoryId = document.Id;
                }

                item.PromotionStatus = ConversationPromotionStatus.Promoted;
                item.Error = string.Empty;
            }
            catch (Exception ex)
            {
                item.PromotionStatus = ConversationPromotionStatus.Failed;
                item.Error = ex.ToString();
            }

            item.UpdatedAt = clock.UtcNow;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task EnqueuePromotionJobIfNeededAsync(string conversationId, string projectId, CancellationToken cancellationToken)
    {
        var hasPending = await HasPendingPromotionJobAsync(conversationId, cancellationToken);

        if (hasPending)
        {
            return;
        }

        var job = new MemoryJob
        {
            ProjectId = ProjectContext.Normalize(projectId),
            JobType = MemoryJobType.PromoteConversationInsights,
            Status = MemoryJobStatus.Pending,
            PayloadJson = JsonSerializer.Serialize(new ConversationPromotionJobPayload(conversationId, projectId), JsonOptions),
            CreatedAt = clock.UtcNow
        };

        await dbContext.MemoryJobs.AddAsync(job, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await cacheStore.PublishJobSignalAsync(job.Id, cancellationToken);
    }

    private static bool ShouldScheduleAutomation(ConversationSourceKind sourceKind, InstanceBehaviorSettingsResult behavior)
    {
        if (!behavior.ConversationAutomationEnabled)
        {
            return false;
        }

        return sourceKind switch
        {
            ConversationSourceKind.HostEvent => behavior.HostEventIngestionEnabled,
            ConversationSourceKind.AgentSupplemental => behavior.AgentSupplementalIngestionEnabled,
            _ => false
        };
    }

    private static bool ShouldEnqueuePromotion(ConversationEventType eventType)
        => eventType is ConversationEventType.SessionCheckpoint or
                        ConversationEventType.Idle or
                        ConversationEventType.ProjectSwitched or
                        ConversationEventType.TaskSwitched;

    private static IReadOnlyList<string> BuildCandidateProjectIds(ConversationIngestRequest request, string effectiveProjectId)
    {
        return (request.ToolCalls ?? [])
            .Select(x => x.ProjectId)
            .Append(request.ProjectId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => ProjectContext.Normalize(x, effectiveProjectId))
            .Append(effectiveProjectId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> BuildProjectHints(ConversationIngestRequest request)
    {
        return (request.ToolCalls ?? [])
            .Select(x => x.ProjectName)
            .Append(request.ProjectName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<ConversationToolCallRequest> DeserializeToolCalls(string toolCallsJson)
        => JsonSerializer.Deserialize<IReadOnlyList<ConversationToolCallRequest>>(toolCallsJson, JsonOptions) ?? [];

    private async Task<Guid?> FindExistingCheckpointJobIdAsync(Guid checkpointId, CancellationToken cancellationToken)
    {
        var jobs = await dbContext.MemoryJobs
            .AsNoTracking()
            .Where(x => x.JobType == MemoryJobType.IngestConversation)
            .OrderByDescending(x => x.CreatedAt)
            .Take(200)
            .ToListAsync(cancellationToken);

        foreach (var job in jobs)
        {
            var payload = TryDeserialize<ConversationCheckpointJobPayload>(job.PayloadJson);
            if (payload?.CheckpointId == checkpointId)
            {
                return job.Id;
            }
        }

        return null;
    }

    private async Task<bool> HasPendingPromotionJobAsync(string conversationId, CancellationToken cancellationToken)
    {
        var jobs = await dbContext.MemoryJobs
            .AsNoTracking()
            .Where(x => x.JobType == MemoryJobType.PromoteConversationInsights &&
                        x.Status == MemoryJobStatus.Pending)
            .OrderByDescending(x => x.CreatedAt)
            .Take(200)
            .ToListAsync(cancellationToken);

        foreach (var job in jobs)
        {
            var payload = TryDeserialize<ConversationPromotionJobPayload>(job.PayloadJson);
            if (string.Equals(payload?.ConversationId, conversationId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static T? TryDeserialize<T>(string json)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string BuildShortExcerpt(ConversationIngestRequest request, int maxLength)
    {
        var source = request.ShortExcerpt
            ?? request.SessionSummary
            ?? request.AgentMessageSummary
            ?? request.UserMessageSummary
            ?? request.SourceRef;
        return Truncate(source?.Trim() ?? string.Empty, maxLength);
    }

    private static string BuildInsightDedupKey(ConversationCheckpoint checkpoint, ConversationInsightCandidate candidate)
        => Hash(
            checkpoint.SourceSystem,
            checkpoint.ConversationId,
            candidate.ProjectId,
            candidate.InsightType.ToString(),
            candidate.SourceRef,
            NormalizeForDedup(candidate.Title),
            NormalizeForDedup(candidate.Content));

    private static UserPreferenceKind InferPreferenceKind(ConversationInsight item)
    {
        var text = $"{item.Title} {item.Content} {item.Summary}".ToLowerInvariant();
        if (ContainsAny(text, "語言", "回覆", "風格", "trad", "中文", "english"))
        {
            return UserPreferenceKind.CommunicationStyle;
        }

        if (ContainsAny(text, "docker", "compose", "tool", "terminal", "cli", "vscode", "工具", "編輯器"))
        {
            return UserPreferenceKind.ToolingPreference;
        }

        if (ContainsAny(text, "禁止", "不要", "必須", "限制", "avoid", "must not"))
        {
            return UserPreferenceKind.Constraint;
        }

        return UserPreferenceKind.EngineeringPrinciple;
    }

    private static MemoryType ToMemoryType(ConversationInsightType value)
        => value switch
        {
            ConversationInsightType.Fact => MemoryType.Fact,
            ConversationInsightType.Decision => MemoryType.Decision,
            ConversationInsightType.Artifact => MemoryType.Artifact,
            ConversationInsightType.Episode => MemoryType.Episode,
            _ => MemoryType.Fact
        };

    private static string NormalizeText(string? value)
        => value?.Trim() ?? string.Empty;

    private static string NormalizeForDedup(string value)
        => string.Join(' ', value
            .Trim()
            .ToLowerInvariant()
            .Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength].TrimEnd();

    private static string Hash(params string[] values)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", values))));

    private static bool ContainsAny(string source, params string[] values)
        => values.Any(value => source.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static void Validate(ConversationIngestRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ConversationId))
        {
            throw new InvalidOperationException("ConversationId is required.");
        }

        if (string.IsNullOrWhiteSpace(request.TurnId))
        {
            throw new InvalidOperationException("TurnId is required.");
        }

        if (string.IsNullOrWhiteSpace(request.SourceSystem))
        {
            throw new InvalidOperationException("SourceSystem is required.");
        }

        if (string.IsNullOrWhiteSpace(request.SourceRef))
        {
            throw new InvalidOperationException("SourceRef is required.");
        }
    }

    private sealed record ConversationCheckpointMetadata(
        IReadOnlyList<string> CandidateProjectIds,
        IReadOnlyList<string> ProjectHints);

    private sealed record ConversationCheckpointJobPayload(Guid CheckpointId);
    private sealed record ConversationPromotionJobPayload(string ConversationId, string ProjectId);
}

internal static class ConversationInsightClassifier
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<ConversationInsightCandidate> Extract(
        ConversationCheckpoint checkpoint,
        IReadOnlyList<ConversationToolCallRequest> toolCalls,
        int excerptMaxLength)
    {
        var candidates = new List<ConversationInsightCandidate>();

        var preference = TryBuildPreferenceCandidate(checkpoint);
        if (preference is not null)
        {
            candidates.Add(preference);
        }

        AddKnowledgeCandidate(candidates, checkpoint, checkpoint.AgentMessageSummary, excerptMaxLength, "agent-summary");
        if (checkpoint.EventType != ConversationEventType.TurnCompleted)
        {
            AddKnowledgeCandidate(candidates, checkpoint, checkpoint.SessionSummary, excerptMaxLength, "session-summary");
        }

        foreach (var toolCall in toolCalls)
        {
            var content = BuildToolCallContent(toolCall);
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            var toolProjectId = ProjectContext.Normalize(toolCall.ProjectId, checkpoint.ProjectId);
            var toolProjectName = string.IsNullOrWhiteSpace(toolCall.ProjectName) ? checkpoint.ProjectName : toolCall.ProjectName.Trim();
            candidates.Add(new ConversationInsightCandidate(
                toolProjectId,
                toolProjectName,
                ConversationInsightType.Episode,
                $"{toolCall.ToolName} {(toolCall.Success ? "completed" : "failed")}",
                content,
                Truncate(toolCall.OutputSummary ?? toolCall.InputSummary ?? toolCall.ToolName, excerptMaxLength),
                toolCall.SourceRef?.Trim() ?? $"tool:{toolCall.ToolName}",
                ["conversation-auto", "tool-call", $"tool:{toolCall.ToolName}"],
                0.72m,
                toolCall.Success ? 0.86m : 0.8m,
                JsonSerializer.Serialize(new { toolCall.ToolName, toolCall.Success }, JsonOptions)));
        }

        return candidates;
    }

    private static ConversationInsightCandidate? TryBuildPreferenceCandidate(ConversationCheckpoint checkpoint)
    {
        var text = checkpoint.UserMessageSummary;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var normalized = text.ToLowerInvariant();
        if (!ContainsAny(normalized,
                "偏好", "希望", "要求", "習慣", "固定", "回覆", "語言",
                "prefer", "preferred", "always", "must", "should use"))
        {
            return null;
        }

        return new ConversationInsightCandidate(
            ProjectContext.UserProjectId,
            "User Preferences",
            ConversationInsightType.PreferenceCandidate,
            BuildTitle(text, "User preference"),
            text.Trim(),
            Truncate(text.Trim(), 240),
            checkpoint.SourceRef,
            ["conversation-auto", "user-preference-candidate"],
            0.9m,
            0.85m,
            JsonSerializer.Serialize(new { origin = "user-message-summary" }, JsonOptions));
    }

    private static void AddKnowledgeCandidate(
        List<ConversationInsightCandidate> candidates,
        ConversationCheckpoint checkpoint,
        string text,
        int excerptMaxLength,
        string origin)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (!TryResolveInsightType(text, out var insightType))
        {
            return;
        }

        candidates.Add(new ConversationInsightCandidate(
            checkpoint.ProjectId,
            checkpoint.ProjectName,
            insightType,
            BuildTitle(text, insightType.ToString()),
            text.Trim(),
            Truncate(text.Trim(), excerptMaxLength),
            checkpoint.SourceRef,
            ["conversation-auto", origin, insightType.ToString().ToLowerInvariant()],
            insightType == ConversationInsightType.Decision ? 0.86m : 0.78m,
            insightType == ConversationInsightType.Decision ? 0.82m : 0.76m,
            JsonSerializer.Serialize(new { origin }, JsonOptions)));
    }

    private static bool TryResolveInsightType(string text, out ConversationInsightType insightType)
    {
        var normalized = text.ToLowerInvariant();
        if (ContainsAny(normalized, "決定", "決策", "採用", "改用", "選擇", "統一", "decide", "decision", "adopt", "switch to"))
        {
            insightType = ConversationInsightType.Decision;
            return true;
        }

        if (ContainsAny(normalized, "script", "compose", "migration", "api", "dashboard", "ui", "程式", "實作", "功能", "修正", "檔案"))
        {
            insightType = ConversationInsightType.Artifact;
            return true;
        }

        if (ContainsAny(normalized, "架構", "設定", "流程", "用途", "規則", "支援", "現況", "責任", "方式", "ability", "supports", "architecture"))
        {
            insightType = ConversationInsightType.Fact;
            return true;
        }

        insightType = default;
        return false;
    }

    private static string BuildToolCallContent(ConversationToolCallRequest toolCall)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(toolCall.InputSummary))
        {
            parts.Add($"Input: {toolCall.InputSummary.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(toolCall.OutputSummary))
        {
            parts.Add($"Output: {toolCall.OutputSummary.Trim()}");
        }

        return string.Join(Environment.NewLine, parts);
    }

    private static string BuildTitle(string text, string fallback)
    {
        var candidate = text.Trim()
            .Split(['。', '.', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return fallback;
        }

        return candidate.Length <= 80 ? candidate : candidate[..80].TrimEnd();
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength].TrimEnd();

    private static bool ContainsAny(string source, params string[] values)
        => values.Any(value => source.Contains(value, StringComparison.OrdinalIgnoreCase));
}

internal sealed record ConversationInsightCandidate(
    string ProjectId,
    string ProjectName,
    ConversationInsightType InsightType,
    string Title,
    string Content,
    string Summary,
    string SourceRef,
    IReadOnlyList<string> Tags,
    decimal Importance,
    decimal Confidence,
    string MetadataJson);
