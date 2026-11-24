using System.Data;
using System.Data.Common;
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

        await using var transaction = await BeginOwnedTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await EnsureDurableProjectGrantAsync(project, write: false, cancellationToken);
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
        var limit = Math.Clamp(request.Limit, 1, MaximumPreviewPageSize);
        var counterRun = string.IsNullOrWhiteSpace(request.GovernanceRunId) ? null : request.GovernanceRunId.Trim();
        var transitions = receipts.Where(x => counterRun == null || x.GovernanceRunId == counterRun);
        var newlyResolved = await transitions.CountAsync(x => x.Action != nameof(ConversationAdjudicationAction.Reopen), cancellationToken);
        var reopened = await transitions.CountAsync(x => x.Action == nameof(ConversationAdjudicationAction.Reopen), cancellationToken);
        var unchanged = await candidates.CountAsync(x => !transitions.Any(r => r.InsightId == x.Id), cancellationToken);
        var preview = new ConversationAdjudicationPreviewResult(
            project, total, unresolved, resolved, offset, false, [])
        {
            CounterGovernanceRunId = counterRun,
            NewlyResolvedCount = newlyResolved,
            ReopenedCount = reopened,
            UnchangedCount = unchanged
        };

        if (request.IncludeInventoryToken || request.ExpectedInventoryToken is not null)
        {
            preview = await ReadGuardedInventoryAsync(candidates, preview, limit, cancellationToken);
        }
        else
        {
            var rows = await candidates.OrderBy(x => x.Id).Skip(offset).Take(limit).ToArrayAsync(cancellationToken);
            var evidence = await LoadEvidenceAsync(project, rows, cancellationToken);
            preview = preview with { Items = rows.Select(row => Evaluate(row, evidence)).ToArray() };
        }

        preview = preview with { HasMore = offset + preview.Items.Count < total };
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

    private async Task<ConversationAdjudicationPreviewResult> ReadGuardedInventoryAsync(
        IQueryable<ConversationInsight> candidates,
        ConversationAdjudicationPreviewResult preview,
        int limit,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var actor = actors.Current;
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = "conversation-adjudication-inventory-v1",
            actor.TenantId,
            actor.UserId,
            Project = ProjectContext.IdentityKey(preview.ProjectId),
            preview.CounterGovernanceRunId,
            preview.TotalCount,
            preview.UnresolvedHumanDecisionCount,
            preview.ResolvedHumanDecisionCount,
            preview.NewlyResolvedCount,
            preview.ReopenedCount,
            preview.UnchangedCount
        }, JsonOptions));

        // Use the same snapshot and ordering for the response page and the full inventory digest.
        var page = new List<ConversationAdjudicationCandidate>(limit);
        Guid? lastId = null;
        var index = 0;
        while (index < preview.TotalCount)
        {
            var remaining = lastId is { } after ? candidates.Where(x => x.Id.CompareTo(after) > 0) : candidates;
            var rows = await remaining.OrderBy(x => x.Id).Take(MaximumPreviewPageSize).ToArrayAsync(cancellationToken);
            var evidence = await LoadEvidenceAsync(preview.ProjectId, rows, cancellationToken);
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
                if (index >= preview.Offset && page.Count < limit)
                    page.Add(item);
                index++;
            }
            lastId = rows[^1].Id;
        }

        return preview with
        {
            Items = page,
            InventoryToken = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()
        };
    }

    public async Task<ConversationAdjudicationReceipt> AdjudicateAsync(
        ConversationAdjudicationRequest request, CancellationToken cancellationToken)
    {
        var project = Authorize(request.ProjectId, write: true);
        var requestHash = ValidateAndHash(project, request);
        for (var attempt = 0; ; attempt++)
        {
            await using var transaction = await BeginOwnedTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            ConversationAdjudication receipt;
            bool replay;
            try
            {
                await EnsureDurableProjectGrantAsync(project, write: true, cancellationToken);
                (receipt, replay) = await ApplyAsync(project, request, requestHash, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (Exception error) when (attempt == 0 &&
                                         error.GetBaseException() is DbException { SqlState: "40001" })
            {
                // PostgreSQL aborted this transaction. Dispose it before retrying with a fresh snapshot.
                continue;
            }

            if (replay)
                return Map(receipt, replay: true);

            // A failed read after commit leaves the outcome unknown to the caller.
            var readBack = await Receipts(project).SingleAsync(x => x.Id == receipt.Id, cancellationToken);
            return Map(readBack, replay: false);
        }
    }

    private async Task<IApplicationTransaction> BeginOwnedTransactionAsync(
        IsolationLevel isolationLevel, CancellationToken cancellationToken)
    {
        if (System.Transactions.Transaction.Current is not null)
            throw new InvalidOperationException("Adjudication requires its own transaction.");
        if (db is not DbContext context || context.ChangeTracker.HasChanges())
            throw new InvalidOperationException("Adjudication requires a DbContext without pending changes.");

        var transaction = await db.BeginTransactionAsync(isolationLevel, cancellationToken);
        if (transaction.OwnsTransaction)
            return transaction;

        await transaction.DisposeAsync();
        throw new InvalidOperationException("Adjudication requires its own transaction.");
    }

    private async Task<(ConversationAdjudication Receipt, bool Replay)> ApplyAsync(
        string project, ConversationAdjudicationRequest request, string requestHash, CancellationToken cancellationToken)
    {
        var previousReceipt = await Receipts(project).SingleOrDefaultAsync(x => x.Id == request.OperationId, cancellationToken);
        if (previousReceipt is not null)
        {
            if (previousReceipt.RequestHash != requestHash)
                throw new InvalidOperationException("OperationId was already used with a different request.");
            return (previousReceipt, true);
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
        var tenantId = actor.TenantId!.Value;
        var ownerId = actor.UserId!.Value;
        if (EvidenceTargetId(insight, ReadMemoryDisposition(insight)) is { } memoryId &&
            context.Memories.ContainsKey(memoryId) &&
            !await db.LockMemoryEvidenceAsync(memoryId, tenantId, ownerId, project, cancellationToken))
        {
            throw new InvalidOperationException("Bound memory evidence changed. Obtain a new preview before adjudicating.");
        }
        if (LinkedProposalId(insight) is { } linkedId &&
            context.Proposals.ContainsKey(linkedId) &&
            !await db.LockAdjudicationInsightEvidenceAsync(insight.Id, linkedId, tenantId, ownerId, project, cancellationToken))
        {
            throw new InvalidOperationException("Bound insight evidence changed. Obtain a new preview before adjudicating.");
        }

        var now = new DateTimeOffset(clock.UtcNow.UtcTicks / 10 * 10, TimeSpan.Zero);
        if (context.Receipts.TryGetValue(insight.Id, out var lastReceipt) && now <= lastReceipt.CreatedAt)
            now = lastReceipt.CreatedAt.AddTicks(10); // PostgreSQL timestamps have microsecond precision.
        var receipt = new ConversationAdjudication
        {
            Id = request.OperationId,
            TenantId = tenantId,
            OwnerUserId = ownerId,
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
        return (receipt, false);
    }

    public async Task<ConversationAdjudicationReceiptLookupResult> ReadReceiptAsync(
        ConversationAdjudicationRequest request, CancellationToken cancellationToken)
    {
        var project = Authorize(request.ProjectId, write: false);
        var requestHash = ValidateAndHash(project, request);
        await using var transaction = await BeginOwnedTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await EnsureDurableProjectGrantAsync(project, write: false, cancellationToken);
        var result = await FindCommittedReceiptAsync(project, request, requestHash, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
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

    private Task EnsureDurableProjectGrantAsync(string project, bool write, CancellationToken cancellationToken)
        => GovernanceProjectScopeResolver.EnsureDurableGrantsAsync(
            db, actors.Current.TenantId!.Value, [project], write, cancellationToken);

    private IQueryable<ConversationInsight> Insights(string project)
        => db.ConversationInsights.Where(x => x.TenantId == actors.Current.TenantId &&
            x.OwnerUserId == actors.Current.UserId && ProjectContext.Matches(x.ProjectId, project));

    private IQueryable<ConversationAdjudication> Receipts(string project)
        => db.ConversationAdjudications.AsNoTracking().Where(x => x.TenantId == actors.Current.TenantId &&
            x.OwnerUserId == actors.Current.UserId && ProjectContext.Matches(x.ProjectId, project));

    private async Task<EvidenceContext> LoadEvidenceAsync(string project, ConversationInsight[] rows, CancellationToken cancellationToken)
    {
        var ids = rows.Select(x => x.Id).ToArray();
        var targetIds = rows.Select(row => EvidenceTargetId(row, ReadMemoryDisposition(row)))
            .Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
        var proposalIds = rows.Select(LinkedProposalId).Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
        var memories = await db.MemoryItems.AsNoTracking().Where(x =>
            x.TenantId == actors.Current.TenantId && x.OwnerUserId == actors.Current.UserId &&
            ProjectContext.Matches(x.ProjectId, project) && targetIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var proposals = await Insights(project).AsNoTracking().Where(x => proposalIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var receipts = await Receipts(project).Where(x => ids.Contains(x.InsightId))
            .GroupBy(x => x.InsightId)
            .Select(group => group.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).First())
            .ToDictionaryAsync(x => x.InsightId, cancellationToken);
        return new(memories, proposals, receipts);
    }

    private static ConversationAdjudicationCandidate Evaluate(ConversationInsight row, EvidenceContext context)
    {
        var disposition = ReadMemoryDisposition(row);
        context.Memories.TryGetValue(EvidenceTargetId(row, disposition) ?? Guid.Empty, out var target);
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
        var linkedProposal = linked is not null && ChatGptProposalService.IsProposal(linked) ? linked : null;
        if (row.PromotionStatus == ConversationPromotionStatus.HostBlocked ||
            linkedProposal?.PromotionStatus == ConversationPromotionStatus.HostBlocked)
            classification = "HostBlocked";
        else if (HasHold(row) || (linked is not null && HasHold(linked)) ||
                 (target is not null && HasProtectedMetadata(target.Tags, target.MetadataJson)))
            classification = "ProtectedHold";
        else if (terminal)
            classification = receipt is null ? "AlreadyTerminal" : "ResolvedHumanDecision";
        else if (disposition.TargetId.HasValue && target?.Status == MemoryStatus.Archived)
        {
            classification = disposition.Tool == "memory_delete" ? "ResolvedWithoutDelete" : "ResolvedAlreadyArchived";
            canReconcile = true;
        }
        else if (linkedProposal?.PromotionStatus == ConversationPromotionStatus.Skipped)
        {
            classification = "ResolvedRejectedProposal";
            canReconcile = true;
        }
        else if (linkedProposal?.PromotionStatus == ConversationPromotionStatus.Promoted &&
                 target is not null && linkedProposal.PromotedMemoryId == target.Id)
        {
            classification = "ResolvedAppliedProposal";
            canReconcile = true;
        }
        else if (linkedProposal?.PromotionStatus == ConversationPromotionStatus.Failed ||
                 (row.PromotionStatus == ConversationPromotionStatus.Failed && ChatGptProposalService.IsProposal(row)))
        {
            classification = "ProposalApplyFailed";
        }
        return new(row.Id, row.ProjectId, row.Title, row.PromotionStatus, token, fingerprint, classification,
            canReconcile, terminal && receipt is not null && classification is not ("ProtectedHold" or "HostBlocked") &&
            receipt.EvidenceFingerprint != fingerprint);
    }

    private static bool HasHold(ConversationInsight row)
        => HasProtectedMetadata(row.Tags, row.MetadataJson);

    private static bool HasProtectedMetadata(string[] tags, string json)
        => tags.Any(x => x.Equals("legal-hold", StringComparison.OrdinalIgnoreCase) ||
            x.Equals("security-hold", StringComparison.OrdinalIgnoreCase) ||
            x.Equals("immutable-quarantine", StringComparison.OrdinalIgnoreCase)) ||
            Flag(json, "legalHold") || Flag(json, "securityHold");

    private static Guid? EvidenceTargetId(ConversationInsight row, (string? Tool, Guid? TargetId) disposition)
        => disposition.Tool == "memory_delete" ? disposition.TargetId : disposition.TargetId ?? row.PromotedMemoryId;

    private static (string? Tool, Guid? TargetId) ReadMemoryDisposition(ConversationInsight row)
    {
        if (!ChatGptProposalService.IsProposal(row)) return default;
        try
        {
            using var doc = JsonDocument.Parse(row.MetadataJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("toolName", out var name) ||
                name.ValueKind != JsonValueKind.String || name.GetString() is not ("memory_archive" or "memory_delete"))
            {
                return default;
            }

            var tool = name.GetString();
            if (root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object &&
                (tool == "memory_delete" || !payload.TryGetProperty("archived", out var archived) || archived.ValueKind == JsonValueKind.True) &&
                payload.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.TryGetGuid(out var target) &&
                payload.TryGetProperty("projectId", out var project) && project.ValueKind == JsonValueKind.String &&
                ProjectContext.Matches(project.GetString(), row.ProjectId))
                return (tool, target);
            return (tool, null);
        }
        catch (JsonException) { }
        return default;
    }

    // Legacy free text, titles, and fuzzy payload matches are never treated as evidence links.
    private static Guid? LinkedProposalId(ConversationInsight row)
    {
        var value = StringProperty(row.MetadataJson, "proposalId");
        return Guid.TryParse(value, out var id) && id != row.Id && row.SourceRef == $"chatgpt-proposal:{id:D}" ? id : null;
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
