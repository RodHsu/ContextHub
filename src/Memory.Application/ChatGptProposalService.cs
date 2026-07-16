using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Memory.Application;

public sealed class ChatGptProposalService(
    IApplicationDbContext dbContext,
    IMemoryService memoryService,
    IProjectArtifactExchangeService artifactExchangeService,
    ISuggestedActionService suggestedActionService,
    IRequestActorAccessor actorAccessor,
    IClock clock) : IChatGptProposalService
{
    public const string SourceSystem = "chatgpt-mcp-gateway";
    private const string ProposalTag = "chatgpt-proposal";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> SupportedProposalTools = new(StringComparer.Ordinal)
    {
        "memory_upsert",
        "memory_update",
        "user_preference_upsert",
        "user_preference_archive",
        "suggested_action_accept",
        "suggested_action_dismiss",
        "promote_log_slice_to_memory",
        "project_artifact_publish",
        "project_artifact_upload_object"
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
        var turnId = $"proposal:{Guid.NewGuid():N}";
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
            createdBy = actor.Username
        }, JsonOptions);
        var dedupKey = Hash(SourceSystem, conversationId, turnId, request.ToolName.Trim(), projectId, request.PayloadJson.Trim());
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
        if (proposal.PromotionStatus != ConversationPromotionStatus.Pending)
        {
            throw new InvalidOperationException($"Proposal '{request.ProposalId}' is not pending.");
        }

        try
        {
            var metadata = ParseMetadata(proposal.MetadataJson);
            var appliedId = await ApplyAsync(metadata.ToolName, metadata.PayloadJson, cancellationToken);
            proposal.PromotionStatus = ConversationPromotionStatus.Promoted;
            proposal.PromotedMemoryId = appliedId;
            proposal.Error = string.Empty;
        }
        catch (Exception ex)
        {
            proposal.PromotionStatus = ConversationPromotionStatus.Failed;
            proposal.Error = ex.Message;
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
            "user_preference_upsert" => (await memoryService.UpsertUserPreferenceAsync(Deserialize<UserPreferenceUpsertRequest>(payloadJson), cancellationToken)).Id,
            "user_preference_archive" => (await memoryService.ArchiveUserPreferenceAsync(Deserialize<UserPreferenceArchiveRequest>(payloadJson), cancellationToken)).Id,
            "suggested_action_accept" => (await suggestedActionService.AcceptAsync(Deserialize<HubActionRequest>(payloadJson).Id, cancellationToken)).Action.Id,
            "suggested_action_dismiss" => (await suggestedActionService.DismissAsync(Deserialize<HubActionRequest>(payloadJson).Id, cancellationToken)).Id,
            "promote_log_slice_to_memory" => (await memoryService.PromoteLogSliceAsync(Deserialize<PromoteLogSliceRequest>(payloadJson), cancellationToken)).Id,
            "project_artifact_publish" => (await artifactExchangeService.PublishAsync(Deserialize<ProjectArtifactPublishRequest>(payloadJson), cancellationToken)).MemoryId,
            "project_artifact_upload_object" => (await artifactExchangeService.UploadManagedObjectAsync(Deserialize<ProjectArtifactManagedObjectPublishRequest>(payloadJson), cancellationToken)).MemoryId,
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

        _ = ParsePayload(request.PayloadJson);
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
            root.TryGetProperty("oauthName", out var name) ? name.GetString() ?? string.Empty : string.Empty);
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
            item.UpdatedAt);
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

    private sealed record ChatGptProposalMetadata(
        string ToolName,
        string PayloadJson,
        string OAuthSubject,
        string OAuthEmail,
        string OAuthName);
}
