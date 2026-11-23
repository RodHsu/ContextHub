using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Memory.Application;

public sealed class ConversationAdjudicationService(
    IApplicationDbContext db,
    IRequestActorAccessor actors,
    IClock clock) : IConversationAdjudicationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string Policy = "conversation-adjudication-v1";
    private const int MaximumPreviewPageSize = 200;

    public async Task<ConversationAdjudicationPreviewResult> PreviewAsync(
        ConversationAdjudicationPreviewRequest request, CancellationToken cancellationToken)
    {
        var project = Authorize(request.ProjectId, write: false);
        if (request.ExpectedInventoryToken is { } expected &&
            (expected.Length != 64 || expected.Any(x => x is not (>= '0' and <= '9' or >= 'a' and <= 'f'))))
        {
            throw new ConversationAdjudicationInventoryException("ExpectedInventoryToken must be a lowercase SHA-256 digest.");
        }
        var receiptRequest = request.ReceiptRequest;
        if (receiptRequest is not null && !ProjectContext.Matches(receiptRequest.ProjectId, project))
        {
            throw new InvalidConversationAdjudicationRequestException("The receipt request must name the preview project.");
        }

        var receiptHash = receiptRequest is null ? null : ValidateAndHash(project, receiptRequest);

        await using var transaction = await db.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var query = Insights(project).AsNoTracking();
        var receipts = Receipts(project);
        var candidates = query.Where(x =>
            x.PromotionStatus == ConversationPromotionStatus.RequiresUserDecision ||
            x.PromotionStatus == ConversationPromotionStatus.Deferred ||
            x.PromotionStatus == ConversationPromotionStatus.HostBlocked ||
            x.PromotionStatus == ConversationPromotionStatus.Failed ||
            receipts.Any(r => r.InsightId == x.Id));
        var total = await candidates.CountAsync(cancellationToken);
        var unresolved = await candidates.CountAsync(x =>
            x.PromotionStatus == ConversationPromotionStatus.RequiresUserDecision, cancellationToken);
        var resolved = await candidates.CountAsync(x =>
            (x.PromotionStatus == ConversationPromotionStatus.Skipped || x.PromotionStatus == ConversationPromotionStatus.Promoted) &&
            receipts.Any(r => r.InsightId == x.Id), cancellationToken);
        var offset = Math.Max(0, request.Offset);
        var rows = await candidates.OrderBy(x => x.Id).Skip(offset).Take(Math.Clamp(request.Limit, 1, MaximumPreviewPageSize))
            .ToArrayAsync(cancellationToken);
        var context = await LoadEvidenceAsync(project, rows, cancellationToken);
        var results = rows.Select(row => Evaluate(row, context)).ToArray();
        var counterRun = string.IsNullOrWhiteSpace(request.GovernanceRunId) ? null : request.GovernanceRunId.Trim();
        var transitions = receipts.Where(x => counterRun == null || x.GovernanceRunId == counterRun);
        var newlyResolved = await transitions.CountAsync(x => x.Action != nameof(ConversationAdjudicationAction.Reopen), cancellationToken);
        var reopened = await transitions.CountAsync(x => x.Action == nameof(ConversationAdjudicationAction.Reopen), cancellationToken);
        var unchanged = await candidates.CountAsync(x => !transitions.Any(r => r.InsightId == x.Id), cancellationToken);
        var preview = new ConversationAdjudicationPreviewResult(
            project, total, unresolved, resolved, offset, offset + rows.Length < total, results)
        {
            CounterGovernanceRunId = counterRun,
            NewlyResolvedCount = newlyResolved,
            ReopenedCount = reopened,
            UnchangedCount = unchanged
        };

        if (request.IncludeInventoryToken || request.ExpectedInventoryToken is not null)
        {
            preview = preview with { InventoryToken = await BuildInventoryTokenAsync(project, candidates, preview, cancellationToken) };
        }
        if (receiptRequest is not null && receiptHash is not null)
        {
            preview = preview with
            {
                ReceiptLookup = await FindCommittedReceiptAsync(project, receiptRequest, receiptHash, cancellationToken)
            };
        }

        await transaction.CommitAsync(cancellationToken);
        if (request.ExpectedInventoryToken is not null &&
            !string.Equals(request.ExpectedInventoryToken, preview.InventoryToken, StringComparison.Ordinal))
        {
            throw new ConversationAdjudicationInventoryException("The adjudication inventory changed. Restart the preview from offset zero.");
        }

        return preview;
    }

    private async Task<string> BuildInventoryTokenAsync(
        string project,
        IQueryable<ConversationInsight> candidates,
        ConversationAdjudicationPreviewResult preview,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var actor = actors.Current;
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = "conversation-adjudication-inventory-v1",
            actor.TenantId,
            actor.UserId,
            Project = ProjectContext.IdentityKey(project),
            preview.CounterGovernanceRunId,
            preview.TotalCount,
            preview.UnresolvedHumanDecisionCount,
            preview.ResolvedHumanDecisionCount,
            preview.NewlyResolvedCount,
            preview.ReopenedCount,
            preview.UnchangedCount
        }, JsonOptions));

        // Keep materialized evidence bounded while hashing the entire ordered inventory,
        // including linked evidence behind candidates outside this response page.
        var ids = await candidates.OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync(cancellationToken);
        foreach (var batch in ids.Chunk(MaximumPreviewPageSize))
        {
            var rows = await candidates.Where(x => batch.Contains(x.Id)).OrderBy(x => x.Id)
                .ToArrayAsync(cancellationToken);
            var evidence = await LoadEvidenceAsync(project, rows, cancellationToken);
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = Evaluate(row, evidence);
                hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(new
                {
                    item.InsightId,
                    item.EvidenceToken,
                    item.Classification,
                    item.CanReconcile,
                    item.CanReopen
                }, JsonOptions));
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public async Task<ConversationAdjudicationReceipt> AdjudicateAsync(
        ConversationAdjudicationRequest request, CancellationToken cancellationToken)
    {
        var project = Authorize(request.ProjectId, write: true);
        var requestHash = ValidateAndHash(project, request);
        await using var transaction = await db.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var previousReceipt = await Receipts(project).SingleOrDefaultAsync(x => x.Id == request.OperationId, cancellationToken);
        if (previousReceipt is not null)
        {
            if (previousReceipt.RequestHash != requestHash)
                throw new InvalidOperationException("OperationId was already used with a different request.");
            await transaction.CommitAsync(cancellationToken);
            return Map(previousReceipt, replay: true);
        }

        var insight = await Insights(project).AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.InsightId, cancellationToken)
            ?? throw new KeyNotFoundException("The insight is outside the current owner/project scope or no longer exists.");
        var context = await LoadEvidenceAsync(project, [insight], cancellationToken);
        var candidate = Evaluate(insight, context);
        if (!string.Equals(request.ExpectedEvidenceToken, candidate.EvidenceToken, StringComparison.Ordinal))
            throw new InvalidOperationException("The insight or its evidence changed. Obtain a new preview before adjudicating.");
        if (candidate.Classification is "HostBlocked" or "ProtectedHold")
            throw new InvalidOperationException("Host-blocked or protected evidence cannot be adjudicated through this workflow.");

        var before = insight.PromotionStatus;
        var proposal = ChatGptProposalService.IsProposal(insight);
        string outcome;
        switch (request.Action)
        {
            case ConversationAdjudicationAction.Reopen:
                if (!candidate.CanReopen)
                    throw new InvalidOperationException("Reopen requires a prior terminal adjudication and changed semantic evidence.");
                insight.PromotionStatus = ConversationPromotionStatus.RequiresUserDecision;
                outcome = "Reopened";
                break;
            case ConversationAdjudicationAction.Reconcile:
                EnsureOpen(before);
                if (!candidate.CanReconcile)
                    throw new InvalidOperationException("No directly bound, terminal resource evidence permits reconciliation.");
                insight.PromotionStatus = ConversationPromotionStatus.Skipped;
                outcome = candidate.Classification;
                break;
            case ConversationAdjudicationAction.RejectProposal:
                EnsureOpen(before);
                if (!proposal) throw new InvalidOperationException("RejectProposal requires a proposal-backed insight.");
                insight.PromotionStatus = ConversationPromotionStatus.Skipped;
                outcome = "ResolvedRejected";
                break;
            case ConversationAdjudicationAction.ResolveSkipped:
                EnsureOpen(before);
                if (proposal) throw new InvalidOperationException("Proposal-backed insights require explicit rejection or evidence reconciliation.");
                insight.PromotionStatus = ConversationPromotionStatus.Skipped;
                outcome = "ResolvedSkipped";
                break;
            default:
                throw new InvalidOperationException("Unsupported adjudication action.");
        }

        var actor = actors.Current;
        var now = new DateTimeOffset(clock.UtcNow.UtcTicks / 10 * 10, TimeSpan.Zero);
        if (context.Receipts.TryGetValue(insight.Id, out var lastReceipt) && now <= lastReceipt.CreatedAt)
            now = lastReceipt.CreatedAt.AddTicks(10); // PostgreSQL timestamps have microsecond precision.
        var receipt = new ConversationAdjudication
        {
            Id = request.OperationId,
            TenantId = actor.TenantId!.Value,
            OwnerUserId = actor.UserId!.Value,
            ProjectId = insight.ProjectId,
            InsightId = insight.Id,
            RequestHash = requestHash,
            EvidenceFingerprint = candidate.EvidenceFingerprint,
            Action = request.Action.ToString(),
            Outcome = outcome,
            PreviousStatus = before.ToString(),
            ResultStatus = insight.PromotionStatus.ToString(),
            Reason = request.Reason.Trim(),
            GovernanceRunId = request.GovernanceRunId.Trim(),
            Actor = actor.Username,
            CreatedAt = now
        };
        var updated = await Insights(project).Where(x => x.Id == insight.Id && x.PromotionStatus == before && x.UpdatedAt == insight.UpdatedAt)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.PromotionStatus, insight.PromotionStatus)
                .SetProperty(x => x.GovernanceReason, receipt.Reason)
                .SetProperty(x => x.GovernanceRunId, receipt.GovernanceRunId)
                .SetProperty(x => x.GovernanceReasonClass, outcome)
                .SetProperty(x => x.GovernanceUpdatedAt, now)
                .SetProperty(x => x.GovernanceLastReevaluatedAt, now)
                .SetProperty(x => x.GovernanceEvidenceChangedSinceBlock, false)
                .SetProperty(x => x.GovernanceBlockedAt, (DateTimeOffset?)null)
                .SetProperty(x => x.GovernanceBlockingLayer, string.Empty)
                .SetProperty(x => x.Error, string.Empty)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken);
        if (updated != 1) throw new InvalidOperationException("The insight changed during adjudication. Obtain a fresh preview.");
        db.ConversationAdjudications.Add(receipt);
        db.SecurityAuditEvents.Add(new SecurityAuditEvent
        {
            TenantId = actor.TenantId,
            ActorUserId = actor.UserId,
            EventType = SecurityAuditEventType.ConversationInsightGovernanceUpdated,
            Outcome = outcome,
            DetailsJson = JsonSerializer.Serialize(new
            {
                action = "ManualAdjudication",
                operationId = receipt.Id,
                insightId = insight.Id,
                projectId = receipt.ProjectId,
                receipt.GovernanceRunId,
                receipt.EvidenceFingerprint,
                receipt.PreviousStatus,
                receipt.ResultStatus,
                receipt.Reason
            }, JsonOptions),
            CreatedAt = now
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var readBack = await Receipts(project).SingleAsync(x => x.Id == receipt.Id, cancellationToken);
        return Map(readBack, replay: false);
    }

    public Task<ConversationAdjudicationReceiptLookupResult> ReadReceiptAsync(
        ConversationAdjudicationRequest request, CancellationToken cancellationToken)
    {
        var project = Authorize(request.ProjectId, write: false);
        return FindCommittedReceiptAsync(project, request, ValidateAndHash(project, request), cancellationToken);
    }

    private async Task<ConversationAdjudicationReceiptLookupResult> FindCommittedReceiptAsync(
        string project, ConversationAdjudicationRequest request, string requestHash, CancellationToken cancellationToken)
    {
        var row = await Receipts(project).SingleOrDefaultAsync(x => x.Id == request.OperationId &&
            x.InsightId == request.InsightId && x.RequestHash == requestHash, cancellationToken);
        return new(request.OperationId, row is null ? null : Map(row, replay: false));
    }

    private static string ValidateAndHash(string project, ConversationAdjudicationRequest request)
    {
        if (request.OperationId == Guid.Empty || request.InsightId == Guid.Empty || !Enum.IsDefined(request.Action) ||
            string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 2000 ||
            string.IsNullOrWhiteSpace(request.GovernanceRunId) || request.GovernanceRunId.Length > 128 ||
            string.IsNullOrWhiteSpace(request.ExpectedEvidenceToken) || request.ExpectedEvidenceToken.Length != 64)
        {
            throw new InvalidConversationAdjudicationRequestException(
                "A valid operation, insight, preview token, reason and GovernanceRunId are required.");
        }

        return Hash(JsonSerializer.Serialize(request with { ProjectId = ProjectContext.IdentityKey(project) }, JsonOptions));
    }

    private string Authorize(string projectId, bool write)
    {
        var actor = actors.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, write ? SecurityScopes.MemoryWrite : SecurityScopes.MemoryRead);
        // An admin-like internal actor must not turn a scheduled capability into manual authority.
        if (actor.Role is not (TenantUserRole.Owner or TenantUserRole.Admin) ||
            actor.IsServiceActor || actor.HasScope(SecurityScopes.ScheduledGovernance))
            throw new UnauthorizedAccessException("An Owner/Admin on the general interactive surface is required.");
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidConversationAdjudicationRequestException("ProjectId is required.");
        var project = ProjectContext.Normalize(projectId);
        ActorAuthorization.EnsureProjectAllowed(actor, project, write);
        return project;
    }

    private IQueryable<ConversationInsight> Insights(string project)
        => db.ConversationInsights.Where(x => x.TenantId == actors.Current.TenantId &&
            x.OwnerUserId == actors.Current.UserId && ProjectContext.Matches(x.ProjectId, project));

    private IQueryable<ConversationAdjudication> Receipts(string project)
        => db.ConversationAdjudications.AsNoTracking().Where(x => x.TenantId == actors.Current.TenantId &&
            x.OwnerUserId == actors.Current.UserId && ProjectContext.Matches(x.ProjectId, project));

    private async Task<EvidenceContext> LoadEvidenceAsync(string project, ConversationInsight[] rows, CancellationToken cancellationToken)
    {
        var ids = rows.Select(x => x.Id).ToArray();
        var targetIds = rows.Select(TargetId).Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
        var proposalIds = rows.Select(LinkedProposalId).Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
        var memories = await db.MemoryItems.AsNoTracking().Where(x =>
            x.TenantId == actors.Current.TenantId && x.OwnerUserId == actors.Current.UserId &&
            ProjectContext.Matches(x.ProjectId, project) && targetIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var proposals = await Insights(project).AsNoTracking().Where(x => proposalIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var receipts = await Receipts(project).Where(x => ids.Contains(x.InsightId))
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).ToArrayAsync(cancellationToken);
        return new(memories, proposals, receipts.GroupBy(x => x.InsightId).ToDictionary(x => x.Key, x => x.First()));
    }

    private static ConversationAdjudicationCandidate Evaluate(ConversationInsight row, EvidenceContext context)
    {
        context.Memories.TryGetValue(TargetId(row) ?? Guid.Empty, out var target);
        context.Proposals.TryGetValue(LinkedProposalId(row) ?? Guid.Empty, out var linked);
        context.Receipts.TryGetValue(row.Id, out var receipt);
        var fingerprint = Hash(JsonSerializer.Serialize(new
        {
            Policy,
            row.Id,
            row.ProjectId,
            row.InsightType,
            row.Title,
            row.Content,
            row.Summary,
            row.SourceRef,
            row.SourceSystem,
            row.SourceKind,
            tags = row.Tags.Order().ToArray(),
            row.Importance,
            row.Confidence,
            row.MetadataJson,
            row.PromotedMemoryId,
            target = target is null ? null : GovernanceExceptionIdentity.MemoryEvidence(target),
            proposal = linked is null ? null : new
            {
                linked.Id,
                linked.PromotionStatus,
                linked.PromotedMemoryId,
                linked.Title,
                linked.Summary,
                linked.SourceSystem,
                linked.SourceRef,
                tags = linked.Tags.Order().ToArray(),
                linked.Content,
                linked.MetadataJson
            }
        }, JsonOptions));
        var token = Hash($"{fingerprint}|{row.PromotionStatus}|{row.UpdatedAt.UtcTicks}");
        var classification = "RequiresManualDecision";
        var canReconcile = false;
        var terminal = row.PromotionStatus is ConversationPromotionStatus.Promoted or ConversationPromotionStatus.Skipped;
        if (row.PromotionStatus == ConversationPromotionStatus.HostBlocked)
            classification = "HostBlocked";
        else if (HasHold(row) || (linked is not null && HasHold(linked)) ||
                 (target is not null && HasProtectedMetadata(target.Tags, target.MetadataJson)))
            classification = "ProtectedHold";
        else if (terminal)
            classification = receipt is null ? "AlreadyTerminal" : "ResolvedHumanDecision";
        else if (IsDeleteProposal(row) && target?.Status == MemoryStatus.Archived)
        {
            classification = "ResolvedWithoutDelete";
            canReconcile = true;
        }
        else if (linked is not null && ChatGptProposalService.IsProposal(linked) &&
                 linked.PromotionStatus == ConversationPromotionStatus.Skipped)
        {
            classification = "ResolvedRejectedProposal";
            canReconcile = true;
        }
        else if (linked is not null && ChatGptProposalService.IsProposal(linked) &&
                 linked.PromotionStatus == ConversationPromotionStatus.Promoted &&
                 target is not null && linked.PromotedMemoryId == target.Id)
        {
            classification = "ResolvedAppliedProposal";
            canReconcile = true;
        }
        return new(row.Id, row.ProjectId, row.Title, row.PromotionStatus, token, fingerprint, classification,
            canReconcile, terminal && receipt is not null && classification != "ProtectedHold" &&
            receipt.EvidenceFingerprint != fingerprint);
    }

    private static bool HasHold(ConversationInsight row)
        => HasProtectedMetadata(row.Tags, row.MetadataJson);

    private static bool HasProtectedMetadata(string[] tags, string json)
        => tags.Any(x => x.Equals("legal-hold", StringComparison.OrdinalIgnoreCase) ||
            x.Equals("security-hold", StringComparison.OrdinalIgnoreCase) ||
            x.Equals("immutable-quarantine", StringComparison.OrdinalIgnoreCase)) ||
            Flag(json, "legalHold") || Flag(json, "securityHold");

    private static bool IsDeleteProposal(ConversationInsight row)
        => ChatGptProposalService.IsProposal(row) && StringProperty(row.MetadataJson, "toolName") == "memory_delete";

    private static Guid? TargetId(ConversationInsight row)
    {
        if (!IsDeleteProposal(row)) return row.PromotedMemoryId;
        try
        {
            using var doc = JsonDocument.Parse(row.MetadataJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object &&
                payload.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.TryGetGuid(out var target) &&
                payload.TryGetProperty("projectId", out var project) && project.ValueKind == JsonValueKind.String &&
                ProjectContext.Matches(project.GetString(), row.ProjectId))
                return target;
        }
        catch (JsonException) { }
        return null;
    }

    // Legacy free text, titles, and fuzzy payload matches are never treated as evidence links.
    private static Guid? LinkedProposalId(ConversationInsight row)
    {
        var value = StringProperty(row.MetadataJson, "proposalId");
        return Guid.TryParse(value, out var id) && row.SourceRef == $"chatgpt-proposal:{id:D}" ? id : null;
    }

    private static string? StringProperty(string json, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static bool Flag(string json, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind != JsonValueKind.Object ||
                (doc.RootElement.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.False);
        }
        catch (JsonException) { return true; }
    }

    private static void EnsureOpen(ConversationPromotionStatus status)
    {
        if (status is not (ConversationPromotionStatus.RequiresUserDecision or ConversationPromotionStatus.Deferred or ConversationPromotionStatus.Failed))
            throw new InvalidOperationException("Manual adjudication requires a non-terminal governance exception.");
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static ConversationAdjudicationReceipt Map(ConversationAdjudication row, bool replay)
        => new(row.Id, row.InsightId, row.ProjectId, row.Action, row.Outcome, row.PreviousStatus, row.ResultStatus,
            row.Reason, row.GovernanceRunId, row.EvidenceFingerprint, row.CreatedAt, replay);

    private sealed record EvidenceContext(
        Dictionary<Guid, MemoryItem> Memories,
        Dictionary<Guid, ConversationInsight> Proposals,
        Dictionary<Guid, ConversationAdjudication> Receipts);
}
