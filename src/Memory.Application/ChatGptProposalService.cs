using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Memory.Application;

public sealed class ChatGptProposalService(
    IApplicationDbContext dbContext,
    IMemoryService memoryService,
    IProjectInformationService projectInformationService,
    IProjectArtifactExchangeService artifactExchangeService,
    ISuggestedActionService suggestedActionService,
    IRequestActorAccessor actorAccessor,
    IClock clock,
    ILogger<ChatGptProposalService> logger) : IChatGptProposalService
{
    public const string SourceSystem = "chatgpt-mcp-gateway";
    private const string ProposalTag = "chatgpt-proposal";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions StrictJsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly HashSet<string> SupportedProposalTools = new(StringComparer.Ordinal)
    {
        "memory_upsert",
        "memory_update",
        "memory_archive",
        "memory_move",
        "memory_delete",
        "project_cleanup_apply",
        "user_preference_upsert",
        "user_preference_archive",
        "suggested_action_accept",
        "suggested_action_dismiss",
        "promote_log_slice_to_memory",
        "project_artifact_publish",
        "project_artifact_upload_object",
        "project_information_upsert",
        "project_information_update_lifecycle",
        "enqueue_reindex"
    };

    public async Task<ChatGptProposalResult> CreateAsync(ChatGptProposalCreateRequest request, CancellationToken cancellationToken)
    {
        ValidateCreate(request);
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryWrite);
        var projectId = ProjectContext.Normalize(request.ProjectId);
        ActorAuthorization.EnsureProjectAllowed(actor, projectId, write: true);

        var now = clock.UtcNow;
        var conversationId = $"{SourceSystem}:{request.OAuthSubject.Trim()}:{projectId}";
        var governanceRunId = NormalizeGovernanceRunId(request.GovernanceRunId);
        var turnId = governanceRunId.Length == 0
            ? $"proposal:{Guid.NewGuid():N}"
            : $"governance:{Hash(governanceRunId)[..24]}";
        var dedupKey = governanceRunId.Length == 0
            ? Hash(SourceSystem, conversationId, turnId, request.ToolName.Trim(), projectId, request.PayloadJson.Trim())
            : Hash(SourceSystem, actor.TenantId?.ToString("D") ?? string.Empty, actor.UserId?.ToString("D") ?? string.Empty, governanceRunId, request.ToolName.Trim(), projectId, request.PayloadJson.Trim());
        if (governanceRunId.Length > 0)
        {
            var existing = await dbContext.ConversationInsights.AsNoTracking()
                .Where(x => x.DedupKey == dedupKey && x.SourceSystem == SourceSystem && x.Tags.Contains(ProposalTag))
                .Where(x => !actor.HasUser || (x.TenantId == actor.TenantId && x.OwnerUserId == actor.UserId))
                .FirstOrDefaultAsync(cancellationToken);
            if (existing is not null)
            {
                return ToResult(existing);
            }
        }
        var session = await dbContext.ConversationSessions.FirstOrDefaultAsync(
            x => x.SourceSystem == SourceSystem && x.ConversationId == conversationId,
            cancellationToken);

        if (session is null)
        {
            session = new ConversationSession
            {
                TenantId = actor.TenantId,
                OwnerUserId = actor.UserId,
                ConversationId = conversationId,
                ProjectId = projectId,
                ProjectName = projectId,
                SourceSystem = SourceSystem,
                Status = "Active",
                LastTurnId = turnId,
                StartedAt = now,
                LastCheckpointAt = now,
                UpdatedAt = now
            };
            await dbContext.ConversationSessions.AddAsync(session, cancellationToken);
        }
        else
        {
            session.ProjectId = projectId;
            session.ProjectName = projectId;
            session.LastTurnId = turnId;
            session.LastCheckpointAt = now;
            session.UpdatedAt = now;
            session.TenantId ??= actor.TenantId;
            session.OwnerUserId ??= actor.UserId;
        }

        var payload = ParsePayload(request.PayloadJson);
        var metadataJson = JsonSerializer.Serialize(new
        {
            proposal = true,
            toolName = request.ToolName.Trim(),
            payload,
            oauthSubject = request.OAuthSubject.Trim(),
            oauthEmail = request.OAuthEmail.Trim(),
            oauthName = request.OAuthName.Trim(),
            governanceRunId,
            createdBy = actor.Username
        }, JsonOptions);
        var checkpoint = new ConversationCheckpoint
        {
            Session = session,
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ConversationId = conversationId,
            TurnId = turnId,
            ProjectId = projectId,
            ProjectName = projectId,
            SourceSystem = SourceSystem,
            EventType = ConversationEventType.SessionCheckpoint,
            SourceKind = ConversationSourceKind.AgentSupplemental,
            SourceRef = $"{SourceSystem}:{request.ToolName.Trim()}",
            AgentMessageSummary = request.Summary.Trim(),
            ShortExcerpt = Truncate(request.Summary.Trim(), 240),
            DedupKey = dedupKey,
            MetadataJson = metadataJson,
            CreatedAt = now
        };
        await dbContext.ConversationCheckpoints.AddAsync(checkpoint, cancellationToken);

        var insight = new ConversationInsight
        {
            Session = session,
            Checkpoint = checkpoint,
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ConversationId = conversationId,
            TurnId = turnId,
            ProjectId = projectId,
            ProjectName = projectId,
            SourceSystem = SourceSystem,
            SourceKind = ConversationSourceKind.AgentSupplemental,
            InsightType = ConversationInsightType.Artifact,
            Title = request.Title.Trim(),
            Content = request.PayloadJson.Trim(),
            Summary = request.Summary.Trim(),
            SourceRef = $"{SourceSystem}:{request.ToolName.Trim()}",
            Tags = [ProposalTag, $"tool:{request.ToolName.Trim()}"],
            Importance = 0.82m,
            Confidence = 0.78m,
            DedupKey = dedupKey,
            PromotionStatus = ConversationPromotionStatus.Pending,
            MetadataJson = metadataJson,
            CreatedAt = now,
            UpdatedAt = now
        };
        await dbContext.ConversationInsights.AddAsync(insight, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToResult(insight);
    }

    public async Task<IReadOnlyList<ChatGptProposalResult>> ListAsync(ChatGptProposalListRequest request, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryRead);

        var query = dbContext.ConversationInsights.AsNoTracking()
            .Where(x => x.SourceSystem == SourceSystem && x.Tags.Contains(ProposalTag));

        if (actor.HasUser)
        {
            query = query.Where(x => x.TenantId == actor.TenantId && x.OwnerUserId == actor.UserId);
        }

        if (!string.IsNullOrWhiteSpace(request.ProjectId))
        {
            var projectId = ProjectContext.Normalize(request.ProjectId);
            ActorAuthorization.EnsureProjectAllowed(actor, projectId, write: false);
            query = query.Where(x => x.ProjectId == projectId);
        }
        else if (actor.HasUser && actor.AllowedProjectIds.Count > 0)
        {
            query = query.Where(x => actor.AllowedProjectIds.Contains(x.ProjectId));
        }

        if (request.Status.HasValue)
        {
            query = query.Where(x => x.PromotionStatus == ToPromotionStatus(request.Status.Value));
        }

        var rows = await query
            .OrderByDescending(x => x.UpdatedAt)
            .Skip(Math.Max(0, request.Offset))
            .Take(Math.Clamp(request.Limit, 1, 200))
            .ToListAsync(cancellationToken);

        return rows.Select(ToResult).ToArray();
    }

    public async Task<ChatGptProposalResult> ApproveAsync(ChatGptProposalDecisionRequest request, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryWrite);
        var proposal = await LoadProposalForWriteAsync(request.ProposalId, cancellationToken);
        ActorAuthorization.EnsureProjectAllowed(actor, proposal.ProjectId, write: true);
        if (proposal.PromotionStatus == ConversationPromotionStatus.Promoted)
        {
            return ToResult(proposal);
        }
        if (proposal.PromotionStatus != ConversationPromotionStatus.Pending)
        {
            throw new InvalidOperationException($"Proposal '{request.ProposalId}' is not pending.");
        }

        var toolName = string.Empty;
        try
        {
            var metadata = ParseMetadata(proposal.MetadataJson);
            toolName = metadata.ToolName;
            var appliedId = await ApplyAsync(metadata.ToolName, metadata.PayloadJson, cancellationToken);
            proposal.PromotionStatus = ConversationPromotionStatus.Promoted;
            proposal.PromotedMemoryId = appliedId;
            proposal.Error = string.Empty;
        }
        catch (Exception ex)
        {
            proposal.PromotionStatus = ConversationPromotionStatus.Failed;
            proposal.Error = ex.Message;
            logger.LogWarning(
                ex,
                "ChatGPT proposal {ProposalId} approval failed while applying tool {ToolName} for project {ProjectId}.",
                proposal.Id,
                toolName,
                proposal.ProjectId);
        }

        proposal.UpdatedAt = clock.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToResult(proposal);
    }

    public async Task<ChatGptProposalResult> RejectAsync(ChatGptProposalDecisionRequest request, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryWrite);
        var proposal = await LoadProposalForWriteAsync(request.ProposalId, cancellationToken);
        ActorAuthorization.EnsureProjectAllowed(actor, proposal.ProjectId, write: true);
        if (proposal.PromotionStatus != ConversationPromotionStatus.Pending)
        {
            throw new InvalidOperationException($"Proposal '{request.ProposalId}' is not pending.");
        }

        proposal.PromotionStatus = ConversationPromotionStatus.Skipped;
        proposal.Error = string.IsNullOrWhiteSpace(request.Note) ? "Rejected." : request.Note.Trim();
        proposal.UpdatedAt = clock.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToResult(proposal);
    }

    private async Task<Guid?> ApplyAsync(string toolName, string payloadJson, CancellationToken cancellationToken)
    {
        return toolName switch
        {
            "memory_upsert" => (await memoryService.UpsertAsync(Deserialize<MemoryUpsertRequest>(payloadJson), cancellationToken)).Id,
            "memory_update" => (await memoryService.UpdateAsync(Deserialize<MemoryUpdateRequest>(payloadJson), cancellationToken)).Id,
            "memory_archive" => (await memoryService.ArchiveAsync(Deserialize<MemoryArchiveRequest>(payloadJson), cancellationToken)).Id,
            "memory_move" => (await memoryService.MoveAsync(Deserialize<MemoryMoveRequest>(payloadJson), cancellationToken)).Id,
            "memory_delete" => (await memoryService.DeleteAsync(Deserialize<MemoryDeleteRequest>(payloadJson), cancellationToken)).Id,
            "project_cleanup_apply" => (await memoryService.ApplyProjectCleanupAsync(Deserialize<ProjectCleanupApplyRequest>(payloadJson), cancellationToken)).AppliedMemoryIds.FirstOrDefault(),
            "user_preference_upsert" => (await memoryService.UpsertUserPreferenceAsync(Deserialize<UserPreferenceUpsertRequest>(payloadJson), cancellationToken)).Id,
            "user_preference_archive" => (await memoryService.ArchiveUserPreferenceAsync(Deserialize<UserPreferenceArchiveRequest>(payloadJson), cancellationToken)).Id,
            "suggested_action_accept" => (await suggestedActionService.AcceptAsync(Deserialize<HubActionRequest>(payloadJson).Id, cancellationToken)).Action.Id,
            "suggested_action_dismiss" => (await suggestedActionService.DismissAsync(Deserialize<HubActionRequest>(payloadJson).Id, cancellationToken)).Id,
            "promote_log_slice_to_memory" => (await memoryService.PromoteLogSliceAsync(Deserialize<PromoteLogSliceRequest>(payloadJson), cancellationToken)).Id,
            "project_artifact_publish" => (await artifactExchangeService.PublishAsync(Deserialize<ProjectArtifactPublishRequest>(payloadJson), cancellationToken)).MemoryId,
            "project_artifact_upload_object" => (await artifactExchangeService.UploadManagedObjectAsync(Deserialize<ProjectArtifactManagedObjectPublishRequest>(payloadJson), cancellationToken)).MemoryId,
            "project_information_upsert" => (await projectInformationService.UpdateFromAgentAsync(Deserialize<ProjectInformationAgentUpdateRequest>(payloadJson), cancellationToken)).MemoryId,
            "project_information_update_lifecycle" => (await projectInformationService.UpdateLifecycleAsync(Deserialize<ProjectLifecycleUpdateRequest>(payloadJson), cancellationToken)).MemoryId,
            "enqueue_reindex" => (await memoryService.EnqueueReindexAsync(Deserialize<EnqueueReindexRequest>(payloadJson), cancellationToken)).JobId,
            _ => throw new InvalidOperationException($"Tool '{toolName}' is not supported for proposal approval.")
        };
    }

    private async Task<ConversationInsight> LoadProposalForWriteAsync(Guid id, CancellationToken cancellationToken)
        => await dbContext.ConversationInsights
            .FirstOrDefaultAsync(x => x.Id == id && x.SourceSystem == SourceSystem && x.Tags.Contains(ProposalTag), cancellationToken)
            ?? throw new InvalidOperationException($"ChatGPT proposal '{id}' was not found.");

    private static void ValidateCreate(ChatGptProposalCreateRequest request)
    {
        if (!SupportedProposalTools.Contains(request.ToolName.Trim()))
        {
            throw new InvalidOperationException($"Tool '{request.ToolName}' is not supported for ChatGPT proposals.");
        }

        if (string.IsNullOrWhiteSpace(request.ProjectId))
        {
            throw new InvalidOperationException("ProjectId is required.");
        }

        ValidatePayload(request.ToolName.Trim(), request.PayloadJson);
    }

    private static void ValidatePayload(string toolName, string payloadJson)
    {
        _ = ParsePayload(payloadJson);
        switch (toolName)
        {
            case "memory_upsert": _ = DeserializeStrict<MemoryUpsertRequest>(payloadJson); break;
            case "memory_update": EnsureId(DeserializeStrict<MemoryUpdateRequest>(payloadJson).Id, "Id"); break;
            case "memory_archive": EnsureId(DeserializeStrict<MemoryArchiveRequest>(payloadJson).Id, "Id"); break;
            case "memory_move": EnsureId(DeserializeStrict<MemoryMoveRequest>(payloadJson).Id, "Id"); break;
            case "memory_delete": EnsureId(DeserializeStrict<MemoryDeleteRequest>(payloadJson).Id, "Id"); break;
            case "project_cleanup_apply": _ = DeserializeStrict<ProjectCleanupApplyRequest>(payloadJson); break;
            case "user_preference_upsert": _ = DeserializeStrict<UserPreferenceUpsertRequest>(payloadJson); break;
            case "user_preference_archive": EnsureId(DeserializeStrict<UserPreferenceArchiveRequest>(payloadJson).Id, "Id"); break;
            case "suggested_action_accept": EnsureId(DeserializeStrict<HubActionRequest>(payloadJson).Id, "Id"); break;
            case "suggested_action_dismiss": EnsureId(DeserializeStrict<HubActionRequest>(payloadJson).Id, "Id"); break;
            case "promote_log_slice_to_memory": _ = DeserializeStrict<PromoteLogSliceRequest>(payloadJson); break;
            case "project_artifact_publish": _ = DeserializeStrict<ProjectArtifactPublishRequest>(payloadJson); break;
            case "project_artifact_upload_object": _ = DeserializeStrict<ProjectArtifactManagedObjectPublishRequest>(payloadJson); break;
            case "project_information_upsert": _ = DeserializeStrict<ProjectInformationAgentUpdateRequest>(payloadJson); break;
            case "project_information_update_lifecycle": _ = DeserializeStrict<ProjectLifecycleUpdateRequest>(payloadJson); break;
            case "enqueue_reindex":
                {
                    var request = DeserializeStrict<EnqueueReindexRequest>(payloadJson);
                    if (!request.MemoryItemId.HasValue && string.IsNullOrWhiteSpace(request.ProjectId))
                    {
                        throw new InvalidOperationException("Proposal payload for enqueue_reindex requires memoryItemId or projectId.");
                    }
                    if (request.MemoryItemId == Guid.Empty)
                    {
                        throw new InvalidOperationException("Proposal payload field 'memoryItemId' must be a non-empty UUID.");
                    }
                    break;
                }
            default: throw new InvalidOperationException($"Tool '{toolName}' is not supported for proposal validation.");
        }
    }

    private static void EnsureId(Guid id, string propertyName)
    {
        if (id == Guid.Empty) throw new InvalidOperationException($"Proposal payload field '{propertyName}' must be a non-empty UUID.");
    }

    private static T DeserializeStrict<T>(string json)
    {
        try
        {
            var result = JsonSerializer.Deserialize<T>(json, StrictJsonOptions)
                         ?? throw new InvalidOperationException($"Payload could not be deserialized as {typeof(T).Name}.");
            ValidateRequiredConstructorArguments<T>(json);
            return result;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Payload does not match the {typeof(T).Name} schema: {ex.Message}", ex);
        }
    }

    private static void ValidateRequiredConstructorArguments<T>(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"Payload for {typeof(T).Name} must be a JSON object.");
        var constructor = typeof(T).GetConstructors()
            .OrderByDescending(x => x.GetParameters().Length)
            .FirstOrDefault();
        if (constructor is null) return;
        foreach (var parameter in constructor.GetParameters().Where(x => !x.HasDefaultValue))
        {
            var jsonName = JsonNamingPolicy.CamelCase.ConvertName(parameter.Name!);
            if (!TryGetProperty(document.RootElement, jsonName, out var value) || value.ValueKind == JsonValueKind.Null)
                throw new InvalidOperationException($"Proposal payload is missing required field '{jsonName}'.");
            if (parameter.ParameterType == typeof(string) && string.IsNullOrWhiteSpace(value.GetString()))
                throw new InvalidOperationException($"Proposal payload field '{jsonName}' must not be blank.");
            if (parameter.ParameterType == typeof(Guid) && (!value.TryGetGuid(out var id) || id == Guid.Empty))
                throw new InvalidOperationException($"Proposal payload field '{jsonName}' must be a non-empty UUID.");
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static T Deserialize<T>(string json)
        => JsonSerializer.Deserialize<T>(json, JsonOptions)
           ?? throw new InvalidOperationException($"Payload could not be deserialized as {typeof(T).Name}.");

    private static JsonElement ParsePayload(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            throw new InvalidOperationException("PayloadJson is required.");
        }

        using var document = JsonDocument.Parse(payloadJson);
        return document.RootElement.Clone();
    }

    private static ChatGptProposalMetadata ParseMetadata(string metadataJson)
    {
        using var document = JsonDocument.Parse(metadataJson);
        var root = document.RootElement;
        var payload = root.GetProperty("payload").GetRawText();
        return new ChatGptProposalMetadata(
            root.GetProperty("toolName").GetString() ?? string.Empty,
            payload,
            root.TryGetProperty("oauthSubject", out var subject) ? subject.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("oauthEmail", out var email) ? email.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("oauthName", out var name) ? name.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("governanceRunId", out var governanceRunId) ? governanceRunId.GetString() ?? string.Empty : string.Empty);
    }

    private static ChatGptProposalResult ToResult(ConversationInsight item)
    {
        var metadata = ParseMetadata(item.MetadataJson);
        return new ChatGptProposalResult(
            item.Id,
            metadata.ToolName,
            ToProposalStatus(item.PromotionStatus),
            item.ProjectId,
            item.ProjectName,
            item.Title,
            item.Summary,
            metadata.PayloadJson,
            metadata.OAuthSubject,
            metadata.OAuthEmail,
            metadata.OAuthName,
            item.PromotedMemoryId,
            item.Error,
            item.CreatedAt,
            item.UpdatedAt,
            metadata.GovernanceRunId);
    }

    private static ConversationPromotionStatus ToPromotionStatus(ChatGptProposalStatus status)
        => status switch
        {
            ChatGptProposalStatus.Pending => ConversationPromotionStatus.Pending,
            ChatGptProposalStatus.Applied => ConversationPromotionStatus.Promoted,
            ChatGptProposalStatus.Rejected => ConversationPromotionStatus.Skipped,
            ChatGptProposalStatus.Failed => ConversationPromotionStatus.Failed,
            _ => ConversationPromotionStatus.Pending
        };

    private static ChatGptProposalStatus ToProposalStatus(ConversationPromotionStatus status)
        => status switch
        {
            ConversationPromotionStatus.Pending => ChatGptProposalStatus.Pending,
            ConversationPromotionStatus.Promoted => ChatGptProposalStatus.Applied,
            ConversationPromotionStatus.Skipped => ChatGptProposalStatus.Rejected,
            ConversationPromotionStatus.Failed => ChatGptProposalStatus.Failed,
            _ => ChatGptProposalStatus.Pending
        };

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength].TrimEnd();

    private static string Hash(params string[] values)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", values))));

    private static string NormalizeGovernanceRunId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim();
        if (normalized.Length > 128)
        {
            throw new InvalidOperationException("GovernanceRunId must not exceed 128 characters.");
        }

        return normalized;
    }

    private sealed record ChatGptProposalMetadata(
        string ToolName,
        string PayloadJson,
        string OAuthSubject,
        string OAuthEmail,
        string OAuthName,
        string GovernanceRunId);
}
