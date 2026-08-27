using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Memory.Application;

public sealed class GovernanceBatchExecutor(
    IApplicationDbContext dbContext,
    IAccessibleProjectService accessibleProjects,
    IDurableMemoryGovernanceService durableGovernance,
    IChatGptProposalService proposalService,
    IMemoryService memoryService,
    IGovernanceService governanceService,
    IConversationAutomationService conversationService,
    IRequestActorAccessor actorAccessor,
    IClock clock) : IGovernanceBatchExecutor
{
    private const int MaximumMutations = 500;
    private const int MaximumDurationSeconds = 900;
    private const int MaximumPlanItems = 100_000;
    private static readonly TimeSpan SnapshotLifetime = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<GovernanceBatchExecuteResult> ExecuteAsync(
        GovernanceBatchExecuteRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryRead);
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryWrite);
        if (!actor.IsAdmin)
        {
            throw new UnauthorizedAccessException("Governance batch execution requires an administrator.");
        }
        var tenantId = actor.TenantId ?? throw new UnauthorizedAccessException("Governance batch execution requires a tenant actor.");
        var ownerUserId = actor.UserId ?? throw new UnauthorizedAccessException("Governance batch execution requires a tenant user.");
        var projectIds = await ResolveProjectIdsAsync(request.ProjectIds, actor, cancellationToken);
        var snapshot = await durableGovernance.GetOrCreateSnapshotAsync(
            projectIds,
            request.GovernanceRunId.Trim(),
            request.IsReReview,
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(request.SnapshotToken) &&
            !string.Equals(request.SnapshotToken.Trim(), snapshot.Coverage.SnapshotToken, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("SnapshotToken does not match the governance run snapshot.");
        }

        var snapshotToken = snapshot.Coverage.SnapshotToken;
        var projectSetHash = Hash(string.Join('\n', projectIds.Append(ProjectContext.SharedProjectId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.ToLowerInvariant())));
        var run = await dbContext.GovernanceBatchRuns
            .FirstOrDefaultAsync(x =>
                x.TenantId == tenantId &&
                x.OwnerUserId == ownerUserId &&
                x.GovernanceRunId == request.GovernanceRunId.Trim() &&
                x.SnapshotToken == snapshotToken,
                cancellationToken);

        if (run is null)
        {
            var plan = await BuildPlanAsync(snapshot, projectIds, request.GovernanceRunId.Trim(), actor, cancellationToken);
            if (plan.Count > MaximumPlanItems)
            {
                throw new InvalidOperationException($"Governance batch plan exceeds the {MaximumPlanItems} item safety limit.");
            }

            var now = clock.UtcNow;
            run = new GovernanceBatchRun
            {
                TenantId = tenantId,
                OwnerUserId = ownerUserId,
                GovernanceRunId = request.GovernanceRunId.Trim(),
                SnapshotToken = snapshotToken,
                ProjectSetHash = projectSetHash,
                ProjectIdsJson = JsonSerializer.Serialize(projectIds, JsonOptions),
                PlanJson = JsonSerializer.Serialize(plan, JsonOptions),
                CreatedAt = now,
                ExpiresAt = now.Add(SnapshotLifetime),
                UpdatedAt = now
            };
            await dbContext.GovernanceBatchRuns.AddAsync(run, cancellationToken);
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                dbContext.ClearTrackedChanges();
                run = await dbContext.GovernanceBatchRuns.FirstOrDefaultAsync(x =>
                    x.TenantId == tenantId &&
                    x.OwnerUserId == ownerUserId &&
                    x.GovernanceRunId == request.GovernanceRunId.Trim() &&
                    x.SnapshotToken == snapshotToken,
                    cancellationToken) ?? throw new InvalidOperationException("Concurrent governance batch run could not be read back.");
            }
        }

        if (!string.Equals(run.ProjectSetHash, projectSetHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("GovernanceRunId and SnapshotToken cannot be replayed with a different ProjectId scope.");
        }
        if (run.ExpiresAt <= clock.UtcNow)
        {
            throw new InvalidOperationException("Governance batch snapshot has expired; perform a fresh review.");
        }

        var canonicalRequest = Canonicalize(request, snapshotToken, projectIds);
        var requestJson = JsonSerializer.Serialize(canonicalRequest, JsonOptions);
        var requestHash = Hash(requestJson);
        var prior = await dbContext.GovernanceBatchExecutions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.GovernanceBatchRunId == run.Id && x.RequestHash == requestHash, cancellationToken);
        if (prior is not null)
        {
            var replay = DeserializeResult(prior.ResultJson);
            if (string.Equals(prior.Status, "Completed", StringComparison.Ordinal))
            {
                return replay with { IsReplay = true };
            }

            return replay with
            {
                IsReplay = true,
                StoppedReason = "UnknownResult",
                RequiresReReview = true
            };
        }

        var cursorBefore = request.Cursor?.Trim() ?? string.Empty;
        var conflictingExecutions = await dbContext.GovernanceBatchExecutions.AsNoTracking()
            .Where(x => x.GovernanceBatchRunId == run.Id && x.CursorBefore == cursorBefore && x.RequestHash != requestHash)
            .Select(x => x.RequestJson)
            .ToListAsync(cancellationToken);
        if (conflictingExecutions.Any(x => !IsDryRunRequest(x)))
        {
            throw new InvalidOperationException("Execution payload does not match the payload already recorded for this governance cursor.");
        }
        if (!string.Equals(cursorBefore, run.LastCursor, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Cursor is invalid, stale, or does not match the saved continuation.");
        }

        var planItems = JsonSerializer.Deserialize<List<BatchPlanItem>>(run.PlanJson, JsonOptions)
            ?? throw new InvalidOperationException("Persisted governance batch plan is invalid.");
        var index = ParseCursor(cursorBefore, run, planItems.Count);
        var execution = new GovernanceBatchExecution
        {
            GovernanceBatchRunId = run.Id,
            RequestHash = requestHash,
            RequestJson = requestJson,
            CursorBefore = cursorBefore,
            CursorAfter = cursorBefore,
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow
        };
        var initial = EmptyResult(request.GovernanceRunId.Trim(), snapshotToken, cursorBefore, index < planItems.Count);
        execution.ResultJson = JsonSerializer.Serialize(initial, JsonOptions);
        await dbContext.GovernanceBatchExecutions.AddAsync(execution, cancellationToken);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            dbContext.ClearTrackedChanges();
            var concurrent = await dbContext.GovernanceBatchExecutions.AsNoTracking()
                .FirstOrDefaultAsync(x => x.GovernanceBatchRunId == run.Id && x.RequestHash == requestHash, cancellationToken);
            if (concurrent is null)
            {
                throw;
            }
            var concurrentResult = DeserializeResult(concurrent.ResultJson);
            return string.Equals(concurrent.Status, "Completed", StringComparison.Ordinal)
                ? concurrentResult with { IsReplay = true }
                : concurrentResult with { IsReplay = true, StoppedReason = "UnknownResult", RequiresReReview = true };
        }

        var stopwatch = Stopwatch.StartNew();
        var accumulator = new BatchAccumulator(request.GovernanceRunId.Trim(), snapshotToken);
        var allowed = (request.AllowedActionTypes is { Count: > 0 }
            ? request.AllowedActionTypes
            : Enum.GetValues<GovernanceBatchActionType>()).ToHashSet();
        var stoppedReason = "Completed";

        while (index < planItems.Count)
        {
            if (accumulator.AttemptedCount >= request.MaxMutations)
            {
                stoppedReason = "MutationLimit";
                break;
            }
            if (stopwatch.Elapsed >= TimeSpan.FromSeconds(request.MaxDurationSeconds))
            {
                stoppedReason = "DurationLimit";
                break;
            }
            if (accumulator.ScannedCount >= request.MaxMutations * 4)
            {
                stoppedReason = "ScanLimit";
                break;
            }
            if (cancellationToken.IsCancellationRequested)
            {
                stoppedReason = "Cancelled";
                break;
            }

            var item = planItems[index];
            accumulator.ScannedCount++;
            GovernanceBatchItemResult itemResult;
            if (request.DryRun)
            {
                itemResult = Preview(item);
                accumulator.Add(itemResult);
                stoppedReason = "DryRunPreview";
                break;
            }

            try
            {
                itemResult = await ProcessItemAsync(item, request, allowed, actor, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                var failureAudit = await AddAuditAsync(actor, SecurityAuditEventType.GovernanceBatchItemProcessed,
                    "UnknownResult", new { item.Key, item.Kind, item.Id, error = "Execution cancelled; outcome unknown.", retryable = false }, CancellationToken.None);
                itemResult = Failed(item, "The item outcome is unknown because execution was cancelled.", retryable: false, "NotAdvancedUnknown") with
                {
                    Disposition = GovernanceBatchItemDisposition.UnknownResult,
                    AuditIds = [failureAudit]
                };
                accumulator.Add(itemResult);
                stoppedReason = "UnknownResult";
                await PersistProgressAsync(run, execution, accumulator, index, planItems.Count, stopwatch, stoppedReason, advance: false, cancellationToken: CancellationToken.None);
                return accumulator.ToResult(run.LastCursor, index < planItems.Count, stopwatch.ElapsedMilliseconds, stoppedReason);
            }
            catch (Exception ex)
            {
                var failureAudit = await AddAuditAsync(actor, SecurityAuditEventType.GovernanceBatchItemProcessed,
                    "Failed", new { item.Key, item.Kind, item.Id, error = ex.Message, retryable = true }, CancellationToken.None);
                itemResult = Failed(item, ex.Message, retryable: true, "NotAdvancedRetryable") with { AuditIds = [failureAudit] };
                accumulator.Add(itemResult);
                stoppedReason = "ItemFailed";
                await PersistProgressAsync(run, execution, accumulator, index, planItems.Count, stopwatch, stoppedReason, advance: false, cancellationToken: CancellationToken.None);
                return accumulator.ToResult(run.LastCursor, index < planItems.Count, stopwatch.ElapsedMilliseconds, stoppedReason);
            }

            accumulator.Add(itemResult);
            if (itemResult.Disposition is GovernanceBatchItemDisposition.Failed or GovernanceBatchItemDisposition.UnknownResult)
            {
                stoppedReason = itemResult.Disposition == GovernanceBatchItemDisposition.UnknownResult ? "UnknownResult" : "ItemFailed";
                await PersistProgressAsync(run, execution, accumulator, index, planItems.Count, stopwatch, stoppedReason, advance: false, cancellationToken: CancellationToken.None);
                return accumulator.ToResult(run.LastCursor, index < planItems.Count, stopwatch.ElapsedMilliseconds, stoppedReason);
            }

            index++;
            await PersistProgressAsync(run, execution, accumulator, index, planItems.Count, stopwatch, "Running", advance: true, cancellationToken);
        }

        var hasMore = index < planItems.Count;
        var nextCursor = request.DryRun
            ? (string.IsNullOrWhiteSpace(run.LastCursor) ? null : run.LastCursor)
            : hasMore ? BuildCursor(run, index) : null;
        if (!request.DryRun)
        {
            run.LastCursor = nextCursor ?? string.Empty;
            run.UpdatedAt = clock.UtcNow;
        }
        execution.CursorAfter = run.LastCursor;
        execution.Status = "Completed";
        execution.CompletedAt = clock.UtcNow;
        execution.UpdatedAt = clock.UtcNow;
        var completionAudit = await AddAuditAsync(
            actor,
            SecurityAuditEventType.GovernanceBatchExecutionCompleted,
            stoppedReason,
            new { runId = run.Id, executionId = execution.Id, accumulator.ScannedCount, accumulator.AttemptedCount, accumulator.AppliedCount, hasMore, hardDeleteCount = 0 },
            CancellationToken.None);
        accumulator.AuditIds.Add(completionAudit);
        var result = accumulator.ToResult(nextCursor, hasMore, stopwatch.ElapsedMilliseconds, stoppedReason);
        execution.ResultJson = JsonSerializer.Serialize(result, JsonOptions);
        await dbContext.SaveChangesAsync(CancellationToken.None);
        return result;
    }

    private async Task<GovernanceBatchItemResult> ProcessItemAsync(
        BatchPlanItem item,
        GovernanceBatchExecuteRequest request,
        IReadOnlySet<GovernanceBatchActionType> allowed,
        ContextHubRequestActor actor,
        CancellationToken cancellationToken)
    {
        return item.Kind switch
        {
            "Finding" => await ProcessFindingAsync(item, request, allowed, actor, cancellationToken),
            "SuggestedAction" => await ProcessSuggestedActionAsync(item, request, allowed, actor, cancellationToken),
            "ConversationInsight" => await ProcessInsightAsync(item, request, allowed, actor, cancellationToken),
            "Proposal" => await ProcessPendingProposalAsync(item, request, actor, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported governance plan item kind '{item.Kind}'.")
        };
    }

    private async Task<GovernanceBatchItemResult> ProcessFindingAsync(
        BatchPlanItem item,
        GovernanceBatchExecuteRequest request,
        IReadOnlySet<GovernanceBatchActionType> allowed,
        ContextHubRequestActor actor,
        CancellationToken cancellationToken)
    {
        var finding = await dbContext.GovernanceFindings.FirstOrDefaultAsync(x => x.Id == item.Id, cancellationToken);
        if (finding is null || finding.Status != GovernanceFindingStatus.Open)
        {
            return await AuditedResultAsync(item, null, GovernanceBatchItemDisposition.NoOp, "Finding is already terminal or no longer exists.", [], [], actor, cancellationToken);
        }
        ActorAuthorization.EnsureProjectAllowed(actor, finding.ProjectId, write: true);

        if (finding.Type is GovernanceFindingType.ConflictCandidate or
            GovernanceFindingType.MoveMemoryCandidate or
            GovernanceFindingType.MisplacedProjectCandidate or
            GovernanceFindingType.SharedKnowledgePromotionCandidate or
            GovernanceFindingType.SharedKnowledgeDemotionCandidate or
            GovernanceFindingType.InvalidMemoryCandidate)
        {
            return await SetFindingDispositionAsync(item, finding, GovernanceFindingDisposition.RequiresUserDecision,
                "Scheduled governance cannot resolve semantic conflicts, uncertain authority, cross-project moves, shared-layer changes, invalid-memory audit value, or protected data.", actor, cancellationToken);
        }

        if (finding.Type == GovernanceFindingType.ReindexRequired && allowed.Contains(GovernanceBatchActionType.Reindex))
        {
            var payload = new EnqueueReindexRequest(MemoryItemId: finding.PrimaryMemoryId, ProjectId: finding.ProjectId);
            var proposal = await CreateAndApplyProposalAsync("enqueue_reindex", finding.ProjectId, "Reindex governed memory", finding.Summary, payload, request.GovernanceRunId, cancellationToken);
            if (proposal.AppliedResourceId is null)
            {
                throw new InvalidOperationException("Reindex proposal applied without a job read-back reference.");
            }
            var jobReadBack = await dbContext.MemoryJobs.AsNoTracking().AnyAsync(x =>
                x.Id == proposal.AppliedResourceId.Value && x.ProjectId == finding.ProjectId, cancellationToken);
            if (!jobReadBack)
            {
                throw new InvalidOperationException("Reindex job reference failed server-side read-back.");
            }
            await governanceService.AcceptAsync(finding.Id, cancellationToken);
            return await AuditedResultAsync(item, GovernanceBatchActionType.Reindex, GovernanceBatchItemDisposition.Applied,
                "Reindex job was proposal-applied and returned a durable job reference.", [proposal.Id], [proposal.AppliedResourceId.Value], actor, cancellationToken);
        }

        if ((finding.Type is GovernanceFindingType.DuplicateCandidate or GovernanceFindingType.DuplicateMemoryCandidate or GovernanceFindingType.MergeMemoryCandidate) &&
            allowed.Contains(GovernanceBatchActionType.Merge))
        {
            return await MergeExactDuplicateAsync(item, finding, request, actor, cancellationToken);
        }

        if ((finding.Type is GovernanceFindingType.SupersededMemoryCandidate or GovernanceFindingType.ReplacementChainCandidate or GovernanceFindingType.AuthoritativeSourceCandidate) &&
            allowed.Contains(GovernanceBatchActionType.Archive))
        {
            return await ArchiveVerifiedSecondaryAsync(item, finding, request, actor, cancellationToken);
        }

        if ((finding.Type is GovernanceFindingType.ObsoleteMemoryCandidate or GovernanceFindingType.LowValueMemoryCandidate) &&
            allowed.Contains(GovernanceBatchActionType.DeleteProposal))
        {
            if (!finding.PrimaryMemoryId.HasValue)
            {
                return await SetFindingDispositionAsync(item, finding, GovernanceFindingDisposition.Deferred, "Delete proposal requires a concrete MemoryId.", actor, cancellationToken);
            }
            var proposal = await proposalService.CreateAsync(new ChatGptProposalCreateRequest(
                "memory_delete",
                finding.ProjectId,
                JsonSerializer.Serialize(new MemoryDeleteRequest(finding.PrimaryMemoryId.Value, finding.ProjectId, "Governance delete proposal; explicit irreversible approval is still required."), JsonOptions),
                "Review permanent deletion",
                "Scheduled governance created a proposal only; no hard-delete was executed.",
                actor.UserId?.ToString("D") ?? actor.Username,
                string.Empty,
                actor.Username,
                request.GovernanceRunId), cancellationToken);
            await governanceService.SetDispositionAsync(new GovernanceFindingDispositionRequest(
                finding.Id,
                GovernanceFindingDisposition.RequiresUserDecision,
                "Delete proposal created; permanent deletion requires explicit per-item authorization and risk review.",
                request.GovernanceRunId), cancellationToken);
            return await AuditedResultAsync(item, GovernanceBatchActionType.DeleteProposal, GovernanceBatchItemDisposition.RequiresUserDecision,
                "Delete proposal created without hard-delete.", [proposal.Id], [finding.PrimaryMemoryId.Value], actor, cancellationToken);
        }

        return await SetFindingDispositionAsync(item, finding, GovernanceFindingDisposition.Deferred,
            "No scheduled low-risk mechanical action is authorized for this finding classification.", actor, cancellationToken);
    }

    private async Task<GovernanceBatchItemResult> MergeExactDuplicateAsync(
        BatchPlanItem item,
        GovernanceFinding finding,
        GovernanceBatchExecuteRequest request,
        ContextHubRequestActor actor,
        CancellationToken cancellationToken)
    {
        if (!finding.PrimaryMemoryId.HasValue || !finding.SecondaryMemoryId.HasValue)
        {
            return await SetFindingDispositionAsync(item, finding, GovernanceFindingDisposition.Deferred, "Merge requires two concrete MemoryIds.", actor, cancellationToken);
        }
        var left = await memoryService.GetAsync(finding.PrimaryMemoryId.Value, cancellationToken);
        var right = await memoryService.GetAsync(finding.SecondaryMemoryId.Value, cancellationToken);
        if (left is null || right is null || !string.Equals(left.ProjectId, right.ProjectId, StringComparison.OrdinalIgnoreCase))
        {
            return await SetFindingDispositionAsync(item, finding, GovernanceFindingDisposition.RequiresUserDecision, "Merge candidates are missing or cross ProjectId.", actor, cancellationToken);
        }
        if (IsProtected(left) || IsProtected(right))
        {
            return await SetFindingDispositionAsync(item, finding, GovernanceFindingDisposition.RequiresUserDecision, "High-value or protected memory requires explicit authority review.", actor, cancellationToken);
        }
        if (!IsExactOrSameKeyDuplicate(left, right))
        {
            return await SetFindingDispositionAsync(item, finding, GovernanceFindingDisposition.RequiresUserDecision, "The pair is not an exact or same-key mechanical duplicate.", actor, cancellationToken);
        }

        var primary = AuthorityScore(left) >= AuthorityScore(right) ? left : right;
        var secondary = primary.Id == left.Id ? right : left;
        var primaryMetadata = MergePrimaryMetadata(primary.MetadataJson, secondary.MetadataJson, secondary.Id);
        var secondaryMetadata = MergeSecondaryMetadata(secondary.MetadataJson, primary.Id);
        var mergedContent = MergeText(primary.Content, secondary.Content);
        var mergedSummary = MergeText(primary.Summary, secondary.Summary);
        var tags = primary.Tags.Concat(secondary.Tags).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var proposals = new List<Guid>();

        var primaryProposal = await CreateAndApplyProposalAsync("memory_update", primary.ProjectId, "Merge authoritative memory",
            $"Merge exact duplicate {secondary.Id:D} into {primary.Id:D}.",
            new MemoryUpdateRequest(primary.Id, Content: mergedContent, Summary: mergedSummary, Tags: tags, MetadataJson: primaryMetadata, ProjectId: primary.ProjectId),
            request.GovernanceRunId, cancellationToken);
        proposals.Add(primaryProposal.Id);
        var secondaryProposal = await CreateAndApplyProposalAsync("memory_update", secondary.ProjectId, "Link superseded memory",
            $"Record supersededByMemoryId={primary.Id:D} before archival.",
            new MemoryUpdateRequest(secondary.Id, MetadataJson: secondaryMetadata, ProjectId: secondary.ProjectId),
            request.GovernanceRunId, cancellationToken);
        proposals.Add(secondaryProposal.Id);

        var primaryBeforeArchive = await memoryService.GetAsync(primary.Id, cancellationToken);
        var secondaryBeforeArchive = await memoryService.GetAsync(secondary.Id, cancellationToken);
        if (primaryBeforeArchive is null || secondaryBeforeArchive is null ||
            !MetadataContainsId(primaryBeforeArchive.MetadataJson, "mergedFromMemoryIds", secondary.Id) ||
            !MetadataContainsId(secondaryBeforeArchive.MetadataJson, "supersededByMemoryId", primary.Id))
        {
            throw new InvalidOperationException("Merge metadata read-back failed before replacement-link creation and archival.");
        }
        var linked = await dbContext.MemoryLinks.AnyAsync(x =>
            x.LinkType == "replaced_by" && x.FromId == secondary.Id && x.ToId == primary.Id, cancellationToken);
        if (!linked)
        {
            await dbContext.MemoryLinks.AddAsync(new MemoryLink
            {
                Id = DeterministicReplacementLinkId(secondary.Id, primary.Id),
                FromId = secondary.Id,
                ToId = primary.Id,
                LinkType = "replaced_by",
                CreatedAt = clock.UtcNow
            }, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        linked = await dbContext.MemoryLinks.AsNoTracking().AnyAsync(x =>
            x.LinkType == "replaced_by" && x.FromId == secondary.Id && x.ToId == primary.Id, cancellationToken);
        if (!linked)
        {
            throw new InvalidOperationException("Merge replacement-link read-back failed before archival.");
        }

        var archiveProposal = await CreateAndApplyProposalAsync("memory_archive", secondary.ProjectId, "Archive merged secondary",
            $"Archive secondary {secondary.Id:D} only after replacement metadata is persisted.",
            new MemoryArchiveRequest(secondary.Id, secondary.ProjectId, Archived: true, "Exact duplicate merged into authoritative memory."),
            request.GovernanceRunId, cancellationToken);
        proposals.Add(archiveProposal.Id);

        var primaryReadBack = await memoryService.GetAsync(primary.Id, cancellationToken);
        var secondaryReadBack = await memoryService.GetAsync(secondary.Id, cancellationToken);
        if (primaryReadBack is null || secondaryReadBack is null ||
            !MetadataContainsId(primaryReadBack.MetadataJson, "mergedFromMemoryIds", secondary.Id) ||
            !MetadataContainsId(secondaryReadBack.MetadataJson, "supersededByMemoryId", primary.Id) ||
            secondaryReadBack.Status != MemoryStatus.Archived || !linked)
        {
            throw new InvalidOperationException("Merge resource read-back did not verify the complete replacement chain.");
        }
        await governanceService.AcceptAsync(finding.Id, cancellationToken);
        return await AuditedResultAsync(item, GovernanceBatchActionType.Merge, GovernanceBatchItemDisposition.Applied,
            $"Merged {secondary.Id:D} into {primary.Id:D}; replacement chain read-back passed before Suggested Action convergence.", proposals, [primary.Id, secondary.Id], actor, cancellationToken);
    }

    private async Task<GovernanceBatchItemResult> ArchiveVerifiedSecondaryAsync(
        BatchPlanItem item,
        GovernanceFinding finding,
        GovernanceBatchExecuteRequest request,
        ContextHubRequestActor actor,
        CancellationToken cancellationToken)
    {
        if (!finding.PrimaryMemoryId.HasValue)
        {
            return await SetFindingDispositionAsync(item, finding, GovernanceFindingDisposition.Deferred, "Archive requires a concrete MemoryId.", actor, cancellationToken);
        }
        var secondary = await memoryService.GetAsync(finding.PrimaryMemoryId.Value, cancellationToken);
        if (secondary is null)
        {
            return await AuditedResultAsync(item, GovernanceBatchActionType.Archive, GovernanceBatchItemDisposition.NoOp, "Secondary no longer exists.", [], [], actor, cancellationToken);
        }
        if (secondary.Status == MemoryStatus.Archived)
        {
            await governanceService.AcceptAsync(finding.Id, cancellationToken);
            return await AuditedResultAsync(item, GovernanceBatchActionType.Archive, GovernanceBatchItemDisposition.NoOp, "Secondary is already archived.", [], [secondary.Id], actor, cancellationToken);
        }
        var replacementId = ReadGuid(secondary.MetadataJson, "supersededByMemoryId") ?? finding.SecondaryMemoryId;
        if (!replacementId.HasValue)
        {
            return await SetFindingDispositionAsync(item, finding, GovernanceFindingDisposition.RequiresUserDecision, "Replacement chain has no authoritative primary.", actor, cancellationToken);
        }
        var primary = await memoryService.GetAsync(replacementId.Value, cancellationToken);
        if (primary is null || primary.Status != MemoryStatus.Active || !string.Equals(primary.ProjectId, secondary.ProjectId, StringComparison.OrdinalIgnoreCase))
        {
            return await SetFindingDispositionAsync(item, finding, GovernanceFindingDisposition.RequiresUserDecision, "Replacement primary is missing, inactive, or cross ProjectId.", actor, cancellationToken);
        }
        var linked = await dbContext.MemoryLinks.AsNoTracking().AnyAsync(x =>
            x.LinkType == "replaced_by" && x.FromId == secondary.Id && x.ToId == primary.Id, cancellationToken);
        if (!linked)
        {
            return await SetFindingDispositionAsync(item, finding, GovernanceFindingDisposition.Deferred, "Replacement link is not yet materialized.", actor, cancellationToken);
        }
        var proposal = await CreateAndApplyProposalAsync("memory_archive", secondary.ProjectId, "Archive verified replacement secondary",
            $"Archive {secondary.Id:D} after replacement-chain read-back.",
            new MemoryArchiveRequest(secondary.Id, secondary.ProjectId, true, "Verified replacement chain."), request.GovernanceRunId, cancellationToken);
        var readBack = await memoryService.GetAsync(secondary.Id, cancellationToken);
        if (readBack?.Status != MemoryStatus.Archived)
        {
            throw new InvalidOperationException("Archive proposal applied but resource read-back is not Archived.");
        }
        await governanceService.AcceptAsync(finding.Id, cancellationToken);
        return await AuditedResultAsync(item, GovernanceBatchActionType.Archive, GovernanceBatchItemDisposition.Applied,
            "Verified replacement secondary was archived and read back.", [proposal.Id], [secondary.Id, primary.Id], actor, cancellationToken);
    }

    private async Task<GovernanceBatchItemResult> ProcessSuggestedActionAsync(
        BatchPlanItem item,
        GovernanceBatchExecuteRequest request,
        IReadOnlySet<GovernanceBatchActionType> allowed,
        ContextHubRequestActor actor,
        CancellationToken cancellationToken)
    {
        var action = await dbContext.SuggestedActions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == item.Id, cancellationToken);
        if (action is null || action.Status != SuggestedActionStatus.Pending)
        {
            return await AuditedResultAsync(item, GovernanceBatchActionType.SuggestedActionReconcile, GovernanceBatchItemDisposition.NoOp, "Suggested Action is already terminal.", [], [], actor, cancellationToken);
        }
        ActorAuthorization.EnsureProjectAllowed(actor, action.ProjectId, write: true);
        if (!allowed.Contains(GovernanceBatchActionType.SuggestedActionReconcile))
        {
            return await AuditedResultAsync(item, null, GovernanceBatchItemDisposition.Deferred, "Suggested Action reconciliation is not allowed by this execution payload.", [], [], actor, cancellationToken);
        }
        if (action.Type != SuggestedActionType.ReindexProject)
        {
            return await AuditedResultAsync(item, GovernanceBatchActionType.SuggestedActionReconcile, GovernanceBatchItemDisposition.Deferred,
                "Suggested Action requires underlying resource proof or explicit agent approval before execution.", [], [], actor, cancellationToken);
        }
        var proposal = await CreateAndApplyProposalAsync("suggested_action_accept", action.ProjectId, "Execute low-risk reindex action",
            action.Summary, new HubActionRequest(action.Id), request.GovernanceRunId, cancellationToken);
        var readBack = await dbContext.SuggestedActions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == action.Id, cancellationToken);
        if (readBack?.Status != SuggestedActionStatus.Executed)
        {
            throw new InvalidOperationException("Suggested Action proposal applied without terminal resource read-back.");
        }
        return await AuditedResultAsync(item, GovernanceBatchActionType.SuggestedActionReconcile, GovernanceBatchItemDisposition.Applied,
            "Low-risk reindex Suggested Action executed and read back.", [proposal.Id], [action.Id], actor, cancellationToken);
    }

    private async Task<GovernanceBatchItemResult> ProcessInsightAsync(
        BatchPlanItem item,
        GovernanceBatchExecuteRequest request,
        IReadOnlySet<GovernanceBatchActionType> allowed,
        ContextHubRequestActor actor,
        CancellationToken cancellationToken)
    {
        var insight = await dbContext.ConversationInsights.AsNoTracking().FirstOrDefaultAsync(x => x.Id == item.Id, cancellationToken);
        if (insight is null || insight.PromotionStatus is ConversationPromotionStatus.Promoted or ConversationPromotionStatus.Skipped or ConversationPromotionStatus.Deferred or ConversationPromotionStatus.RequiresUserDecision or ConversationPromotionStatus.HostBlocked)
        {
            return await AuditedResultAsync(item, GovernanceBatchActionType.ConversationInsightDisposition, GovernanceBatchItemDisposition.NoOp, "Conversation Insight is already terminal.", [], [], actor, cancellationToken);
        }
        if (!allowed.Contains(GovernanceBatchActionType.ConversationInsightDisposition))
        {
            return await AuditedResultAsync(item, null, GovernanceBatchItemDisposition.Deferred, "Conversation Insight disposition is not allowed by this execution payload.", [], [], actor, cancellationToken);
        }
        var protectedInsight = insight.InsightType is ConversationInsightType.Decision or ConversationInsightType.Fact || insight.Importance >= 0.8m || insight.Confidence >= 0.9m;
        var disposition = protectedInsight ? ConversationInsightDisposition.RequiresUserDecision : ConversationInsightDisposition.Deferred;
        var updated = await conversationService.SetInsightDispositionAsync(new ConversationInsightDispositionRequest(
            insight.Id,
            disposition,
            protectedInsight
                ? "Semantic or high-signal insight requires explicit user authority."
                : "Scheduled batch deferred a non-terminal insight without inventing durable knowledge.",
            request.GovernanceRunId), cancellationToken);
        var resultDisposition = updated.PromotionStatus == ConversationPromotionStatus.RequiresUserDecision
            ? GovernanceBatchItemDisposition.RequiresUserDecision
            : GovernanceBatchItemDisposition.Deferred;
        return await AuditedResultAsync(item, GovernanceBatchActionType.ConversationInsightDisposition, resultDisposition,
            updated.GovernanceReason, [], [insight.Id], actor, cancellationToken);
    }

    private async Task<GovernanceBatchItemResult> ProcessPendingProposalAsync(
        BatchPlanItem item,
        GovernanceBatchExecuteRequest request,
        ContextHubRequestActor actor,
        CancellationToken cancellationToken)
    {
        var proposal = await dbContext.ConversationInsights.FirstOrDefaultAsync(x => x.Id == item.Id, cancellationToken);
        if (proposal is null || proposal.PromotionStatus != ConversationPromotionStatus.Pending)
        {
            return await AuditedResultAsync(item, GovernanceBatchActionType.ProposalApply, GovernanceBatchItemDisposition.NoOp, "Proposal is already terminal.", [], [], actor, cancellationToken);
        }
        proposal.PromotionStatus = ConversationPromotionStatus.RequiresUserDecision;
        proposal.GovernanceReason = "Pre-existing proposal was not auto-approved; target payload and irreversible effects require explicit review.";
        proposal.GovernanceRunId = request.GovernanceRunId;
        proposal.GovernanceUpdatedAt = clock.UtcNow;
        proposal.UpdatedAt = clock.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return await AuditedResultAsync(item, GovernanceBatchActionType.ProposalApply, GovernanceBatchItemDisposition.RequiresUserDecision,
            proposal.GovernanceReason, [proposal.Id], [], actor, cancellationToken);
    }

    private async Task<GovernanceBatchItemResult> SetFindingDispositionAsync(
        BatchPlanItem item,
        GovernanceFinding finding,
        GovernanceFindingDisposition disposition,
        string reason,
        ContextHubRequestActor actor,
        CancellationToken cancellationToken)
    {
        await governanceService.SetDispositionAsync(new GovernanceFindingDispositionRequest(
            finding.Id, disposition, reason, item.GovernanceRunId), cancellationToken);
        return await AuditedResultAsync(item, null,
            disposition == GovernanceFindingDisposition.RequiresUserDecision
                ? GovernanceBatchItemDisposition.RequiresUserDecision
                : GovernanceBatchItemDisposition.Deferred,
            reason, [], finding.PrimaryMemoryId.HasValue ? [finding.PrimaryMemoryId.Value] : [], actor, cancellationToken);
    }

    private async Task<ChatGptProposalResult> CreateAndApplyProposalAsync<T>(
        string toolName,
        string projectId,
        string title,
        string summary,
        T payload,
        string governanceRunId,
        CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        var proposal = await proposalService.CreateAsync(new ChatGptProposalCreateRequest(
            toolName,
            projectId,
            JsonSerializer.Serialize(payload, JsonOptions),
            title,
            summary,
            actor.UserId?.ToString("D") ?? actor.Username,
            string.Empty,
            actor.Username,
            governanceRunId), cancellationToken);
        var applied = await proposalService.ApproveAsync(new ChatGptProposalDecisionRequest(proposal.Id, "Approved by bounded low-risk governance batch executor."), cancellationToken);
        if (applied.Status != ChatGptProposalStatus.Applied)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(applied.Error)
                ? $"Proposal {applied.Id:D} did not apply."
                : applied.Error);
        }
        return applied;
    }

    private async Task<GovernanceBatchItemResult> AuditedResultAsync(
        BatchPlanItem item,
        GovernanceBatchActionType? actionType,
        GovernanceBatchItemDisposition disposition,
        string summary,
        IReadOnlyList<Guid> proposalIds,
        IReadOnlyList<Guid> resourceIds,
        ContextHubRequestActor actor,
        CancellationToken cancellationToken)
    {
        var auditId = await AddAuditAsync(actor, SecurityAuditEventType.GovernanceBatchItemProcessed, disposition.ToString(), new
        {
            item.Key,
            item.Kind,
            item.Id,
            item.ProjectId,
            actionType,
            disposition,
            summary,
            proposalIds,
            resourceIds,
            hardDelete = false
        }, cancellationToken);
        return new GovernanceBatchItemResult(item.Key, item.Kind, item.Id, item.ProjectId, actionType, disposition, summary, string.Empty,
            Retryable: false, "Advanced", [auditId], proposalIds, resourceIds);
    }

    private async Task<Guid> AddAuditAsync(ContextHubRequestActor actor, SecurityAuditEventType eventType, string outcome, object details, CancellationToken cancellationToken)
    {
        var audit = new SecurityAuditEvent
        {
            TenantId = actor.TenantId,
            ActorUserId = actor.UserId,
            EventType = eventType,
            Outcome = outcome,
            DetailsJson = JsonSerializer.Serialize(details, JsonOptions),
            CreatedAt = clock.UtcNow
        };
        await dbContext.SecurityAuditEvents.AddAsync(audit, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return audit.Id;
    }

    private async Task<List<BatchPlanItem>> BuildPlanAsync(
        DurableMemoryGovernanceSnapshotResult snapshot,
        IReadOnlyList<string> projectIds,
        string governanceRunId,
        ContextHubRequestActor actor,
        CancellationToken cancellationToken)
    {
        var plan = snapshot.ProjectCandidates.Concat(snapshot.SharedCandidates)
            .Select(x => new BatchPlanItem($"finding:{x.FindingId:N}", "Finding", x.FindingId, x.ProjectId, snapshot.Coverage.SnapshotToken, governanceRunId))
            .ToList();
        var scopedProjects = projectIds.Append(ProjectContext.SharedProjectId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var actions = await dbContext.SuggestedActions.AsNoTracking()
            .Where(x => scopedProjects.Contains(x.ProjectId) && x.Status == SuggestedActionStatus.Pending)
            .Select(x => new { x.Id, x.ProjectId })
            .ToListAsync(cancellationToken);
        plan.AddRange(actions.Select(x => new BatchPlanItem($"action:{x.Id:N}", "SuggestedAction", x.Id, x.ProjectId, snapshot.Coverage.SnapshotToken, governanceRunId)));
        var insights = await dbContext.ConversationInsights.AsNoTracking()
            .Where(x => x.TenantId == actor.TenantId && x.OwnerUserId == actor.UserId)
            .Where(x => projectIds.Contains(x.ProjectId))
            .Where(x => x.PromotionStatus == ConversationPromotionStatus.Pending || x.PromotionStatus == ConversationPromotionStatus.Failed)
            .ToListAsync(cancellationToken);
        foreach (var insight in insights)
        {
            var proposal = insight.SourceSystem == ChatGptProposalService.SourceSystem && insight.Tags.Contains("chatgpt-proposal");
            plan.Add(new BatchPlanItem(
                $"{(proposal ? "proposal" : "insight")}:{insight.Id:N}",
                proposal ? "Proposal" : "ConversationInsight",
                insight.Id,
                insight.ProjectId,
                snapshot.Coverage.SnapshotToken,
                governanceRunId));
        }
        return plan.OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.ProjectId, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Id).ToList();
    }

    private async Task<IReadOnlyList<string>> ResolveProjectIdsAsync(
        IReadOnlyList<string>? requested,
        ContextHubRequestActor actor,
        CancellationToken cancellationToken)
    {
        var available = (await accessibleProjects.ListAsync(0, cancellationToken)).Where(x => x.CanRead && x.CanWrite).ToArray();
        var normalized = requested?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => ProjectContext.Normalize(x))
            .Where(x => !ProjectContext.IsShared(x) && !ProjectContext.IsUser(x))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var result = normalized is { Length: > 0 }
            ? normalized
            : available.Select(x => x.ProjectId).Where(x => !ProjectContext.IsShared(x) && !ProjectContext.IsUser(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (result.Length == 0) throw new InvalidOperationException("At least one authorized ProjectId is required.");
        ActorAuthorization.EnsureProjectsAllowed(actor, result, write: true);
        return result.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task PersistProgressAsync(
        GovernanceBatchRun run,
        GovernanceBatchExecution execution,
        BatchAccumulator accumulator,
        int index,
        int total,
        Stopwatch stopwatch,
        string stoppedReason,
        bool advance,
        CancellationToken cancellationToken)
    {
        var cursor = index < total ? BuildCursor(run, index) : string.Empty;
        if (advance)
        {
            run.LastCursor = cursor;
            run.UpdatedAt = clock.UtcNow;
        }
        execution.CursorAfter = run.LastCursor;
        execution.UpdatedAt = clock.UtcNow;
        execution.ResultJson = JsonSerializer.Serialize(accumulator.ToResult(
            string.IsNullOrEmpty(run.LastCursor) ? null : run.LastCursor,
            index < total,
            stopwatch.ElapsedMilliseconds,
            stoppedReason), JsonOptions);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    internal static void ValidateRequest(GovernanceBatchExecuteRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.GovernanceRunId) || request.GovernanceRunId.Trim().Length > 128)
            throw new InvalidOperationException("GovernanceRunId is required and must not exceed 128 characters.");
        if (request.MaxMutations is < 1 or > MaximumMutations)
            throw new InvalidOperationException($"MaxMutations must be between 1 and {MaximumMutations}.");
        if (request.MaxDurationSeconds is < 1 or > MaximumDurationSeconds)
            throw new InvalidOperationException($"MaxDurationSeconds must be between 1 and {MaximumDurationSeconds}.");
        if (request.ExecutionMode == GovernanceBatchExecutionMode.Scheduled && request.AllowHardDelete)
            throw new InvalidOperationException("Scheduled governance always requires AllowHardDelete=false.");
        if (request.ExecutionMode == GovernanceBatchExecutionMode.Scheduled && string.IsNullOrWhiteSpace(request.SnapshotToken))
            throw new InvalidOperationException("Scheduled governance requires the snapshotToken returned by a full knowledge review.");
        if (!Enum.IsDefined(request.MaxRiskLevel))
            throw new InvalidOperationException("MaxRiskLevel is invalid.");
    }

    private static object Canonicalize(GovernanceBatchExecuteRequest request, string snapshotToken, IReadOnlyList<string> projectIds)
        => new
        {
            governanceRunId = request.GovernanceRunId.Trim(),
            projectIds = projectIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase),
            snapshotToken,
            cursor = request.Cursor?.Trim() ?? string.Empty,
            request.MaxMutations,
            request.MaxDurationSeconds,
            allowedActionTypes = (request.AllowedActionTypes ?? []).Distinct().OrderBy(x => x).ToArray(),
            request.MaxRiskLevel,
            request.DryRun,
            allowHardDelete = request.ExecutionMode == GovernanceBatchExecutionMode.Scheduled ? false : request.AllowHardDelete,
            request.IsReReview,
            request.ExecutionMode
        };

    private static int ParseCursor(string cursor, GovernanceBatchRun run, int planCount)
    {
        if (string.IsNullOrEmpty(cursor)) return 0;
        var parts = cursor.Split(':');
        if (parts.Length != 4 || parts[0] != "gb" || !Guid.TryParseExact(parts[1], "N", out var runId) || runId != run.Id ||
            !int.TryParse(parts[2], out var index) || index < 0 || index > planCount ||
            !string.Equals(parts[3], Hash(run.PlanJson)[..16], StringComparison.Ordinal))
            throw new InvalidOperationException("Cursor is invalid for this governance batch plan.");
        return index;
    }

    private static string BuildCursor(GovernanceBatchRun run, int index)
        => $"gb:{run.Id:N}:{index}:{Hash(run.PlanJson)[..16]}";

    private static Guid DeterministicReplacementLinkId(Guid replacedId, Guid authoritativeId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"replaced_by:{replacedId:N}:{authoritativeId:N}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static GovernanceBatchExecuteResult EmptyResult(string governanceRunId, string snapshotToken, string? cursor, bool hasMore)
        => new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, cursor, hasMore, false, [], [], snapshotToken, "Running")
        { GovernanceRunId = governanceRunId };

    private static GovernanceBatchExecuteResult DeserializeResult(string json)
        => JsonSerializer.Deserialize<GovernanceBatchExecuteResult>(json, JsonOptions)
           ?? throw new InvalidOperationException("Persisted governance batch result is invalid.");

    private static bool IsDryRunRequest(string requestJson)
    {
        try
        {
            using var document = JsonDocument.Parse(requestJson);
            return document.RootElement.TryGetProperty("dryRun", out var dryRun) && dryRun.GetBoolean();
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static GovernanceBatchItemResult Preview(BatchPlanItem item)
        => new(item.Key, item.Kind, item.Id, item.ProjectId, null, GovernanceBatchItemDisposition.NoOp,
            "Dry-run preview; cursor and durable state were not changed.", string.Empty, false, "NotAdvancedDryRun", [], [], []);

    private static GovernanceBatchItemResult Failed(BatchPlanItem item, string error, bool retryable, string cursorDisposition)
        => new(item.Key, item.Kind, item.Id, item.ProjectId, null, GovernanceBatchItemDisposition.Failed,
            "Item failed in isolation.", error, retryable, cursorDisposition, [], [], []);

    private static bool IsProtected(MemoryDocument memory)
        => memory.Importance >= 0.85m || memory.Confidence >= 0.95m ||
           memory.Tags.Any(x => string.Equals(x, "protected", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(x, "legal", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(x, "security-sensitive", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(x, "secret", StringComparison.OrdinalIgnoreCase));

    private static bool IsExactOrSameKeyDuplicate(MemoryDocument left, MemoryDocument right)
    {
        if (left.MemoryType != right.MemoryType || left.Status != MemoryStatus.Active || right.Status != MemoryStatus.Active) return false;
        var exact = Normalize(left.Title) == Normalize(right.Title) && Normalize(left.Content) == Normalize(right.Content) && Normalize(left.Summary) == Normalize(right.Summary);
        var sameKey = !string.IsNullOrWhiteSpace(left.ExternalKey) && string.Equals(left.ExternalKey, right.ExternalKey, StringComparison.OrdinalIgnoreCase);
        var contained = Normalize(left.Content).Contains(Normalize(right.Content), StringComparison.Ordinal) || Normalize(right.Content).Contains(Normalize(left.Content), StringComparison.Ordinal);
        return exact || (sameKey && contained);
    }

    private static decimal AuthorityScore(MemoryDocument memory)
    {
        var score = memory.Confidence * 3m + memory.Importance * 2m + Math.Min(memory.Version, 20) / 20m;
        if (memory.Tags.Any(x => string.Equals(x, "authoritative", StringComparison.OrdinalIgnoreCase) || string.Equals(x, "source-of-truth", StringComparison.OrdinalIgnoreCase))) score += 10m;
        return score;
    }

    private static string MergeText(string primary, string secondary)
        => Normalize(primary).Contains(Normalize(secondary), StringComparison.Ordinal) ? primary
            : Normalize(secondary).Contains(Normalize(primary), StringComparison.Ordinal) ? secondary
            : $"{primary.Trim()}\n\n{secondary.Trim()}".Trim();

    private static string MergePrimaryMetadata(string primaryJson, string secondaryJson, Guid secondaryId)
    {
        var metadata = ReadObject(primaryJson);
        var merged = ReadIds(metadata, "mergedFromMemoryIds");
        merged.Add(secondaryId);
        metadata["mergedFromMemoryIds"] = merged.OrderBy(x => x).Select(x => x.ToString("D")).ToArray();
        metadata["mergedSourceMetadata"] = new[] { ParseElement(primaryJson), ParseElement(secondaryJson) };
        return JsonSerializer.Serialize(metadata, JsonOptions);
    }

    private static string MergeSecondaryMetadata(string json, Guid primaryId)
    {
        var metadata = ReadObject(json);
        metadata["supersededByMemoryId"] = primaryId.ToString("D");
        return JsonSerializer.Serialize(metadata, JsonOptions);
    }

    private static Dictionary<string, object?> ReadObject(string json)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, object?>>(json, JsonOptions) ?? []; }
        catch (JsonException) { return []; }
    }

    private static HashSet<Guid> ReadIds(IReadOnlyDictionary<string, object?> metadata, string property)
    {
        var result = new HashSet<Guid>();
        if (metadata.TryGetValue(property, out var value) && value is JsonElement element && element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) if (item.ValueKind == JsonValueKind.String && Guid.TryParse(item.GetString(), out var id)) result.Add(id);
        return result;
    }

    private static JsonElement ParseElement(string json)
    {
        try { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }
        catch (JsonException) { using var document = JsonDocument.Parse("{}"); return document.RootElement.Clone(); }
    }

    private static Guid? ReadGuid(string json, string propertyName)
    {
        try { using var document = JsonDocument.Parse(json); return document.RootElement.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id) ? id : null; }
        catch (JsonException) { return null; }
    }

    private static bool MetadataContainsId(string json, string propertyName, Guid id)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty(propertyName, out var value)) return false;
            if (value.ValueKind == JsonValueKind.String) return Guid.TryParse(value.GetString(), out var parsed) && parsed == id;
            return value.ValueKind == JsonValueKind.Array && value.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && Guid.TryParse(x.GetString(), out var parsed) && parsed == id);
        }
        catch (JsonException) { return false; }
    }

    private static string Normalize(string value)
        => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record BatchPlanItem(string Key, string Kind, Guid Id, string ProjectId, string SnapshotToken, string GovernanceRunId);

    private sealed class BatchAccumulator(string governanceRunId, string snapshotToken)
    {
        public int ScannedCount { get; set; }
        public int AttemptedCount { get; private set; }
        public int AppliedCount { get; private set; }
        public int NoOpCount { get; private set; }
        public int FailedCount { get; private set; }
        public int DeferredCount { get; private set; }
        public int RequiresUserDecisionCount { get; private set; }
        public int MergedCount { get; private set; }
        public int UpdatedCount { get; private set; }
        public int MovedCount { get; private set; }
        public int ArchivedCount { get; private set; }
        public int ReindexedCount { get; private set; }
        public int DeleteProposalCount { get; private set; }
        public List<GovernanceBatchItemResult> Items { get; } = [];
        public List<Guid> AuditIds { get; } = [];

        public void Add(GovernanceBatchItemResult item)
        {
            Items.Add(item);
            AuditIds.AddRange(item.AuditIds);
            if (item.Disposition != GovernanceBatchItemDisposition.NoOp) AttemptedCount++;
            if (item.Disposition == GovernanceBatchItemDisposition.Applied) AppliedCount++;
            if (item.Disposition == GovernanceBatchItemDisposition.NoOp) NoOpCount++;
            if (item.Disposition is GovernanceBatchItemDisposition.Failed or GovernanceBatchItemDisposition.UnknownResult) FailedCount++;
            if (item.Disposition == GovernanceBatchItemDisposition.Deferred) DeferredCount++;
            if (item.Disposition == GovernanceBatchItemDisposition.RequiresUserDecision) RequiresUserDecisionCount++;
            if (item.Disposition == GovernanceBatchItemDisposition.Applied && item.ActionType == GovernanceBatchActionType.Merge) MergedCount++;
            if (item.Disposition == GovernanceBatchItemDisposition.Applied && item.ActionType == GovernanceBatchActionType.Merge) UpdatedCount += 2;
            if (item.Disposition == GovernanceBatchItemDisposition.Applied && item.ActionType == GovernanceBatchActionType.Merge) ArchivedCount++;
            if (item.Disposition == GovernanceBatchItemDisposition.Applied && item.ActionType == GovernanceBatchActionType.Update) UpdatedCount++;
            if (item.Disposition == GovernanceBatchItemDisposition.Applied && item.ActionType == GovernanceBatchActionType.Move) MovedCount++;
            if (item.Disposition == GovernanceBatchItemDisposition.Applied && item.ActionType == GovernanceBatchActionType.Archive) ArchivedCount++;
            if (item.Disposition == GovernanceBatchItemDisposition.Applied && item.ActionType == GovernanceBatchActionType.Reindex) ReindexedCount++;
            if (item.ActionType == GovernanceBatchActionType.DeleteProposal) DeleteProposalCount++;
        }

        public GovernanceBatchExecuteResult ToResult(string? nextCursor, bool hasMore, long elapsedMilliseconds, string stoppedReason)
            => new(ScannedCount, AttemptedCount, AppliedCount, NoOpCount, FailedCount, DeferredCount, RequiresUserDecisionCount,
                MergedCount, UpdatedCount, MovedCount, ArchivedCount, ReindexedCount, DeleteProposalCount,
                nextCursor, hasMore, Items.Count > 0, Items.ToArray(), AuditIds.Distinct().ToArray(), snapshotToken, stoppedReason)
            { GovernanceRunId = governanceRunId, ElapsedMilliseconds = elapsedMilliseconds };
    }
}
