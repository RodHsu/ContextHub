using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Memory.Application;

public sealed class GovernanceBatchExecutor(
    IApplicationDbContext dbContext,
    IGovernanceProjectScopeResolver governanceScope,
    IDurableMemoryGovernanceService durableGovernance,
    IChatGptProposalService proposalService,
    IMemoryService memoryService,
    IGovernanceService governanceService,
    IConversationAutomationService conversationService,
    IFullGovernancePlanService fullGovernance,
    IAutonomousRetentionService autonomousRetention,
    IRequestActorAccessor actorAccessor,
    IClock clock,
    ICacheVersionStore cacheStore,
    IGovernanceRunReceiptService runReceipts) : IGovernanceBatchExecutor
{
    private const int MaximumMutations = 500;
    private const int MaximumDurationSeconds = 900;
    private const int MaximumPlanItems = 100_000;
    private const int CursorVersion = 2;
    private static readonly TimeSpan SnapshotLifetime = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<GovernanceBatchExecuteResult> ExecuteAsync(
        GovernanceBatchExecuteRequest request,
        CancellationToken cancellationToken)
    {
        var startedAt = clock.UtcNow;
        var receiptEligible = false;
        IAsyncDisposable? runLock = null;
        try
        {
            ValidateExecutionMode(request.ExecutionMode);
            EnsureExecutionActorAllowed(request, actorAccessor.Current);
            ValidatePublishedContract(request);
            if (request.ExecutionMode == GovernanceBatchExecutionMode.Scheduled)
            {
                runLock = await runReceipts.AcquireRunLockAsync(
                    request.GovernanceRunId,
                    cancellationToken);
                var identity = request.ReceiptContractIdentity!;
                var lineage = await runReceipts.GetScheduledLineageAsync(
                    request.GovernanceRunId,
                    identity,
                    cancellationToken);
                if (!lineage.IsValid)
                {
                    throw new GovernanceBatchException(
                        GovernanceBatchErrorCode.SchemaCapabilityMismatch,
                        $"Scheduled governance run lineage rejected at the mutation boundary: {lineage.Status}.");
                }
            }
            receiptEligible = true;
            if (request.AllowMaturedDelete ||
                request.AllowedActionTypes?.Contains(GovernanceBatchActionType.MaturedDelete) == true)
            {
                throw new GovernanceBatchException(
                    GovernanceBatchErrorCode.HostBlockedMaturedDelete,
                    "External MaturedDelete is fail-closed. Observe policy-bound internal retention through governance run receipts and tombstones.");
            }

            var terminalReplay = await runReceipts.GetTerminalPreExecutionReplayAsync(request, cancellationToken);
            if (terminalReplay is not null)
            {
                if (!string.IsNullOrWhiteSpace(request.SnapshotToken))
                {
                    _ = await durableGovernance.GetSnapshotAsync(
                        request.GovernanceRunId.Trim(), request.SnapshotToken.Trim(), request.IsReReview,
                        requireWriteAuthorization: true, cancellationToken);
                }
                await runReceipts.RecordExecutionAsync(request, terminalReplay, startedAt, CancellationToken.None);
                return terminalReplay;
            }

            await runReceipts.RecordExecutionStartedAsync(request, startedAt, CancellationToken.None);
            var result = await ExecuteCoreAsync(request, cancellationToken);
            await runReceipts.RecordExecutionAsync(request, result, startedAt, CancellationToken.None);
            return result;
        }
        catch (GovernanceBatchException ex)
        {
            var failure = GovernanceBatchExecuteResult.Failure(request, ex);
            if (receiptEligible)
            {
                await runReceipts.RecordExecutionAsync(request, failure, startedAt, CancellationToken.None);
            }
            return failure;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (receiptEligible)
            {
                await runReceipts.RecordExecutionStoppedAsync(
                    request, startedAt, "Stopped", "RequestCancelledOutcomeUnknown", "OutcomeUnknown", CancellationToken.None);
            }
            throw;
        }
        catch
        {
            if (receiptEligible)
            {
                await runReceipts.RecordExecutionStoppedAsync(
                    request, startedAt, "Failed", "UnhandledExecutionFailure", "PreExecutionUnhandled", CancellationToken.None);
            }
            throw;
        }
        finally
        {
            if (runLock is not null)
            {
                await runLock.DisposeAsync();
            }
        }
    }

    private async Task<GovernanceBatchExecuteResult> ExecuteCoreAsync(
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
        var actorHash = Hash($"{tenantId:N}\n{ownerUserId:N}");
        var cursorBefore = request.Cursor?.Trim() ?? string.Empty;
        var cursorPayload = string.IsNullOrEmpty(cursorBefore) ? null : ParseCursor(cursorBefore);
        if (cursorPayload is not null &&
            !string.Equals(cursorPayload.ActorHash, actorHash[..16], StringComparison.Ordinal))
        {
            throw new GovernanceBatchException(
                GovernanceBatchErrorCode.CursorActorMismatch,
                "Cursor belongs to a different tenant actor.");
        }
        DurableMemoryGovernanceSnapshotResult snapshot;
        IReadOnlyList<string> projectIds;
        if (!string.IsNullOrWhiteSpace(request.SnapshotToken))
        {
            snapshot = await durableGovernance.GetSnapshotAsync(
                request.GovernanceRunId.Trim(),
                request.SnapshotToken.Trim(),
                request.IsReReview,
                requireWriteAuthorization: true,
                cancellationToken);
            projectIds = DurableMemoryGovernancePolicy.ToExecutionProjectIds(snapshot.Coverage.GovernanceProjectIds);
            EnsureExplicitScopeMatchesSnapshot(request.ProjectIds, projectIds);
        }
        else
        {
            projectIds = await ResolveProjectIdsAsync(request.ProjectIds, actor, cancellationToken);
            snapshot = await durableGovernance.GetOrCreateSnapshotAsync(
                projectIds,
                request.GovernanceRunId.Trim(),
                request.IsReReview,
                cancellationToken);
        }
        var projectSetHash = Hash(string.Join('\n', projectIds.Append(ProjectContext.SharedProjectId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.ToLowerInvariant())));
        var policyHash = BuildPolicyHash(request, projectIds);
        if (cursorPayload is not null)
        {
            await ValidateIssuedCursorAsync(
                cursorBefore,
                cursorPayload,
                tenantId,
                ownerUserId,
                request.GovernanceRunId.Trim(),
                actorHash,
                projectSetHash,
                policyHash,
                cancellationToken);
        }
        var suppliedSnapshotToken = request.SnapshotToken?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(suppliedSnapshotToken))
        {
            var suppliedRequestJson = JsonSerializer.Serialize(
                Canonicalize(request, suppliedSnapshotToken, projectIds), JsonOptions);
            var suppliedRequestHash = Hash(suppliedRequestJson);
            var suppliedLogicalRunIds = await dbContext.GovernanceBatchRuns.AsNoTracking()
                .Where(x => x.TenantId == tenantId &&
                            x.OwnerUserId == ownerUserId &&
                            x.GovernanceRunId == request.GovernanceRunId.Trim() &&
                            x.ProjectSetHash == projectSetHash)
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);
            var suppliedPrior = await dbContext.GovernanceBatchExecutions.AsNoTracking()
                .FirstOrDefaultAsync(x => suppliedLogicalRunIds.Contains(x.GovernanceBatchRunId) &&
                                          x.RequestHash == suppliedRequestHash, cancellationToken);
            if (suppliedPrior is not null)
            {
                var replay = DeserializeResult(suppliedPrior.ResultJson);
                return string.Equals(suppliedPrior.Status, "Completed", StringComparison.Ordinal)
                    ? replay with { IsReplay = true }
                    : replay with { IsReplay = true, StoppedReason = "UnknownResult", RequiresReReview = true };
            }

            if (!string.IsNullOrEmpty(cursorBefore))
            {
                var suppliedConflicts = await dbContext.GovernanceBatchExecutions.AsNoTracking()
                    .Where(x => suppliedLogicalRunIds.Contains(x.GovernanceBatchRunId) &&
                                x.CursorBefore == cursorBefore &&
                                x.RequestHash != suppliedRequestHash)
                    .Select(x => x.RequestJson)
                    .ToListAsync(cancellationToken);
                if (suppliedConflicts.Any(x => !IsDryRunRequest(x)))
                {
                    throw new GovernanceBatchException(GovernanceBatchErrorCode.ReplayPayloadMismatch,
                        "Execution payload does not match the payload already recorded for this governance cursor.");
                }
            }
        }
        if (!string.IsNullOrWhiteSpace(request.SnapshotToken) &&
            !string.Equals(request.SnapshotToken.Trim(), snapshot.Coverage.SnapshotToken, StringComparison.Ordinal))
        {
            throw new GovernanceBatchException(
                cursorPayload is null ? GovernanceBatchErrorCode.CursorSnapshotMismatch : GovernanceBatchErrorCode.ReReviewRequired,
                "SnapshotToken does not match the requested governance review generation.");
        }

        var snapshotToken = snapshot.Coverage.SnapshotToken;
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
            throw new GovernanceBatchException(GovernanceBatchErrorCode.CursorScopeMismatch,
                "GovernanceRunId and SnapshotToken cannot be replayed with a different ProjectId scope.");
        }
        if (run.ExpiresAt <= clock.UtcNow)
        {
            throw new GovernanceBatchException(GovernanceBatchErrorCode.CursorExpired,
                "Governance batch snapshot has expired; perform a fresh review.");
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

        var logicalRunIds = await dbContext.GovernanceBatchRuns.AsNoTracking()
            .Where(x => x.TenantId == tenantId &&
                        x.OwnerUserId == ownerUserId &&
                        x.GovernanceRunId == request.GovernanceRunId.Trim() &&
                        x.ProjectSetHash == projectSetHash)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        IReadOnlyList<Guid> conflictRunIds = string.IsNullOrEmpty(cursorBefore) ? [run.Id] : logicalRunIds;
        var conflictingExecutions = await dbContext.GovernanceBatchExecutions.AsNoTracking()
            .Where(x => conflictRunIds.Contains(x.GovernanceBatchRunId) && x.CursorBefore == cursorBefore && x.RequestHash != requestHash)
            .Select(x => x.RequestJson)
            .ToListAsync(cancellationToken);
        if (conflictingExecutions.Any(x => !IsDryRunRequest(x)))
        {
            throw new GovernanceBatchException(GovernanceBatchErrorCode.ReplayPayloadMismatch,
                "Execution payload does not match the payload already recorded for this governance cursor.");
        }
        if (cursorPayload is not null && cursorPayload.RunId == run.Id &&
            !string.Equals(cursorBefore, run.LastCursor, StringComparison.Ordinal))
        {
            throw new GovernanceBatchException(GovernanceBatchErrorCode.InvalidCursor,
                "Cursor is stale or does not match the latest saved continuation for this snapshot.");
        }
        if (cursorPayload is null && !string.IsNullOrEmpty(run.LastCursor))
        {
            throw new GovernanceBatchException(GovernanceBatchErrorCode.InvalidCursor,
                "A continuation cursor is required for this snapshot generation.");
        }

        var planItems = JsonSerializer.Deserialize<List<BatchPlanItem>>(run.PlanJson, JsonOptions)
            ?? throw new InvalidOperationException("Persisted governance batch plan is invalid.");
        IReadOnlyDictionary<string, string>? scheduledPlanRejections = null;
        if (request.ExecutionMode == GovernanceBatchExecutionMode.Scheduled)
        {
            scheduledPlanRejections = await ValidateScheduledPlanAsync(
                planItems,
                snapshot,
                projectIds,
                request.GovernanceRunId.Trim(),
                actor,
                cancellationToken);
        }
        var terminalItemKeys = await GetTerminalItemKeysAsync(
            logicalRunIds,
            run.Id,
            planItems.Select(x => x.Key).ToHashSet(StringComparer.Ordinal),
            cancellationToken);
        var executablePlanItems = planItems.Where(x => !terminalItemKeys.Contains(x.Key)).ToList();
        var index = 0;
        var lastItemKey = cursorPayload?.ItemKey ?? string.Empty;
        var logicalPositionBase = terminalItemKeys.Count;
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
        var initial = EmptyResult(request.GovernanceRunId.Trim(), snapshotToken, cursorBefore, index < executablePlanItems.Count);
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

        while (index < executablePlanItems.Count)
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

            var item = executablePlanItems[index];
            accumulator.ScannedCount++;
            GovernanceBatchItemResult itemResult;
            if (scheduledPlanRejections is not null &&
                scheduledPlanRejections.TryGetValue(item.Key, out var planRejection))
            {
                itemResult = await AuditedResultAsync(
                    item,
                    ParseRecommendedAction(item.RecommendedAction),
                    GovernanceBatchItemDisposition.RequiresUserDecision,
                    $"Scheduled Governance persisted plan integrity validation failed ({planRejection}); no governed resource was mutated.",
                    [],
                    item.RelatedResourceIds,
                    actor,
                    cancellationToken);
            }
            else if (request.DryRun)
            {
                itemResult = Preview(item);
                accumulator.Add(itemResult);
                stoppedReason = "DryRunPreview";
                break;
            }
            else
            {
                try
                {
                    itemResult = await ProcessItemAsync(item, request, allowed, actor, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    run = await dbContext.GovernanceBatchRuns.SingleAsync(
                        x => x.Id == run.Id,
                        CancellationToken.None);
                    execution = await dbContext.GovernanceBatchExecutions.SingleAsync(
                        x => x.Id == execution.Id,
                        CancellationToken.None);
                    var failureAudit = await AddAuditAsync(actor, SecurityAuditEventType.GovernanceBatchItemProcessed,
                        "UnknownResult", new { item.Key, item.Kind, item.Id, error = "Execution cancelled; outcome unknown.", retryable = false }, CancellationToken.None);
                    itemResult = Failed(item, "The item outcome is unknown because execution was cancelled.", retryable: false, "NotAdvancedUnknown") with
                    {
                        Disposition = GovernanceBatchItemDisposition.UnknownResult,
                        AuditIds = [failureAudit]
                    };
                    accumulator.Add(itemResult);
                    stoppedReason = "UnknownResult";
                    await PersistProgressAsync(run, execution, accumulator, logicalPositionBase + index, lastItemKey, actorHash, projectSetHash, policyHash,
                        executablePlanItems.Count - index, stopwatch, stoppedReason, advance: false, cancellationToken: CancellationToken.None);
                    return accumulator.ToResult(execution.CursorAfter, index < executablePlanItems.Count, stopwatch.ElapsedMilliseconds, stoppedReason);
                }
                catch (Exception ex)
                {
                    run = await dbContext.GovernanceBatchRuns.SingleAsync(
                        x => x.Id == run.Id,
                        CancellationToken.None);
                    execution = await dbContext.GovernanceBatchExecutions.SingleAsync(
                        x => x.Id == execution.Id,
                        CancellationToken.None);
                    var failureAudit = await AddAuditAsync(actor, SecurityAuditEventType.GovernanceBatchItemProcessed,
                        "Failed", new { item.Key, item.Kind, item.Id, error = ex.Message, retryable = true }, CancellationToken.None);
                    itemResult = Failed(item, ex.Message, retryable: true, "NotAdvancedRetryable") with { AuditIds = [failureAudit] };
                    accumulator.Add(itemResult);
                    stoppedReason = "ItemFailed";
                    await PersistProgressAsync(run, execution, accumulator, logicalPositionBase + index, lastItemKey, actorHash, projectSetHash, policyHash,
                        executablePlanItems.Count - index, stopwatch, stoppedReason, advance: false, cancellationToken: CancellationToken.None);
                    return accumulator.ToResult(execution.CursorAfter, index < executablePlanItems.Count, stopwatch.ElapsedMilliseconds, stoppedReason);
                }
            }

            accumulator.Add(itemResult);
            if (itemResult.Disposition is GovernanceBatchItemDisposition.Failed or GovernanceBatchItemDisposition.UnknownResult)
            {
                stoppedReason = itemResult.Disposition == GovernanceBatchItemDisposition.UnknownResult ? "UnknownResult" : "ItemFailed";
                await PersistProgressAsync(run, execution, accumulator, logicalPositionBase + index, lastItemKey, actorHash, projectSetHash, policyHash,
                    executablePlanItems.Count - index, stopwatch, stoppedReason, advance: false, cancellationToken: CancellationToken.None);
                return accumulator.ToResult(execution.CursorAfter, index < executablePlanItems.Count, stopwatch.ElapsedMilliseconds, stoppedReason);
            }

            index++;
            lastItemKey = item.Key;
            await PersistProgressAsync(run, execution, accumulator, logicalPositionBase + index, lastItemKey, actorHash, projectSetHash, policyHash,
                executablePlanItems.Count - index, stopwatch, "Running", advance: true, cancellationToken);
        }

        var hasMore = index < executablePlanItems.Count;
        var nextCursor = request.DryRun
            ? (string.IsNullOrWhiteSpace(run.LastCursor) ? null : run.LastCursor)
            : hasMore ? BuildCursor(run, logicalPositionBase + index, lastItemKey, actorHash, projectSetHash, policyHash) : null;
        if (!request.DryRun)
        {
            run.LastCursor = nextCursor ?? string.Empty;
            run.UpdatedAt = clock.UtcNow;
        }
        execution.CursorAfter = run.LastCursor;
        execution.Status = "Completed";
        execution.CompletedAt = clock.UtcNow;
        execution.UpdatedAt = clock.UtcNow;
        var retentionReview = await autonomousRetention.ReviewAsync(projectIds, request.GovernanceRunId.Trim(), CancellationToken.None);
        accumulator.ApplyRetentionReview(retentionReview);
        var completionAudit = await AddAuditAsync(
            actor,
            SecurityAuditEventType.GovernanceBatchExecutionCompleted,
            stoppedReason,
            new { runId = run.Id, executionId = execution.Id, accumulator.ScannedCount, accumulator.AttemptedCount, accumulator.AppliedCount, hasMore, hardDeleteCount = accumulator.AutoDeletedCount },
            CancellationToken.None);
        accumulator.AuditIds.Add(completionAudit);
        var result = accumulator.ToResult(nextCursor, hasMore, stopwatch.ElapsedMilliseconds, stoppedReason);
        execution.ResultJson = JsonSerializer.Serialize(result, JsonOptions);
        await dbContext.SaveChangesAsync(CancellationToken.None);
        return result;
    }

    private static void ValidatePublishedContract(GovernanceBatchExecuteRequest request)
    {
        ValidateExecutionMode(request.ExecutionMode);

        if (request.ExecutionMode == GovernanceBatchExecutionMode.Scheduled)
        {
            ValidateOptionalScheduledContractValue(
                request.ToolContractVersion,
                ScheduledGovernanceContract.ToolContractVersion,
                GovernanceToolContract.ToolContractVersion,
                nameof(request.ToolContractVersion));
            ValidateOptionalScheduledContractValue(
                request.SchemaHash,
                ScheduledGovernanceContract.SchemaHash,
                GovernanceToolContract.SchemaHash,
                nameof(request.SchemaHash),
                ignoreCase: true);

            var identity = request.ReceiptContractIdentity;
            if (identity is null ||
                !string.Equals(identity.ToolContractVersion, ScheduledGovernanceContract.ToolContractVersion, StringComparison.Ordinal) ||
                !string.Equals(identity.SchemaHash, ScheduledGovernanceContract.SchemaHash, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(identity.PublishedCatalogVersion, ScheduledGovernanceContract.PublishedCatalogVersion, StringComparison.Ordinal))
            {
                throw new GovernanceBatchException(
                    GovernanceBatchErrorCode.SchemaCapabilityMismatch,
                    "Scheduled execution requires the current Scheduled Governance receipt contract identity.");
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(request.ToolContractVersion) &&
            !string.Equals(request.ToolContractVersion.Trim(), GovernanceToolContract.ToolContractVersion, StringComparison.Ordinal))
        {
            throw new GovernanceBatchException(GovernanceBatchErrorCode.SchemaCapabilityMismatch,
                $"ToolContractVersion must be '{GovernanceToolContract.ToolContractVersion}'.");
        }

        if (!string.IsNullOrWhiteSpace(request.SchemaHash) &&
            !string.Equals(request.SchemaHash.Trim(), GovernanceToolContract.SchemaHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new GovernanceBatchException(GovernanceBatchErrorCode.SchemaCapabilityMismatch,
                $"SchemaHash must be '{GovernanceToolContract.SchemaHash}'.");
        }
    }

    private static void ValidateOptionalScheduledContractValue(
        string? value,
        string scheduledValue,
        string generalValue,
        string fieldName,
        bool ignoreCase = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalized = value.Trim();
        if (!string.Equals(normalized, scheduledValue, comparison) &&
            !string.Equals(normalized, generalValue, comparison))
        {
            throw new GovernanceBatchException(
                GovernanceBatchErrorCode.SchemaCapabilityMismatch,
                $"{fieldName} does not match the current Scheduled Governance contract.");
        }
    }

    private static void EnsureExecutionActorAllowed(
        GovernanceBatchExecuteRequest request,
        ContextHubRequestActor actor)
    {
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryRead);
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryWrite);
        if (!actor.IsAdmin)
        {
            throw new UnauthorizedAccessException("Governance batch execution requires an administrator.");
        }

        var hasScheduledCapability = actor.HasScope(SecurityScopes.ScheduledGovernance);
        var isScheduledOnlyCapabilityProfile = hasScheduledCapability &&
                                               !actor.HasScope(SecurityScopes.GovernanceTrackerManage);
        if (isScheduledOnlyCapabilityProfile && request.ExecutionMode != GovernanceBatchExecutionMode.Scheduled)
        {
            throw new GovernanceBatchException(
                GovernanceBatchErrorCode.SchemaCapabilityMismatch,
                "The governance:scheduled capability is bound to Scheduled execution and cannot downgrade to the general executor.");
        }

        if (request.ExecutionMode == GovernanceBatchExecutionMode.Scheduled && !hasScheduledCapability)
        {
            throw new GovernanceBatchException(
                GovernanceBatchErrorCode.SchemaCapabilityMismatch,
                "Scheduled execution requires the governance:scheduled capability.");
        }
    }

    private async Task<GovernanceBatchItemResult> ProcessItemAsync(
        BatchPlanItem item,
        GovernanceBatchExecuteRequest request,
        IReadOnlySet<GovernanceBatchActionType> allowed,
        ContextHubRequestActor actor,
        CancellationToken cancellationToken)
    {
        if (request.ExecutionMode == GovernanceBatchExecutionMode.Scheduled)
        {
            var scheduledAction = ParseRecommendedAction(item.RecommendedAction);
            var eligibility = EvaluateScheduledEligibility(item);
            if (!eligibility.AutomationActionable ||
                !scheduledAction.HasValue ||
                !allowed.Contains(scheduledAction.Value))
            {
                return await AuditedResultAsync(
                    item,
                    scheduledAction,
                    GovernanceBatchItemDisposition.RequiresUserDecision,
                    $"Scheduled Governance eligibility denied mutation ({eligibility.ReasonClass}); no governed resource was mutated.",
                    [],
                    item.RelatedResourceIds,
                    actor,
                    cancellationToken);
            }
        }

        return item.Kind switch
        {
            "Finding" => await ProcessFindingAsync(item, request, allowed, actor, cancellationToken),
            "SuggestedAction" => await ProcessSuggestedActionAsync(item, request, allowed, actor, cancellationToken),
            "ConversationInsight" => await ProcessInsightAsync(item, request, allowed, actor, cancellationToken),
            "Proposal" => await ProcessPendingProposalAsync(item, request, actor, cancellationToken),
            _ when Enum.TryParse<GovernanceItemKind>(item.Kind, out _) =>
                await ProcessTypedSurfaceAsync(item, request, allowed, actor, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported governance plan item kind '{item.Kind}'.")
        };
    }

    private static ScheduledGovernanceEligibilityResult EvaluateScheduledEligibility(BatchPlanItem item)
    {
        if (item.CanonicalKind is not { } itemKind)
        {
            return new(
                ScheduledGovernanceEligibility.RequiresUserDecision,
                "missing-canonical-eligibility-projection");
        }

        return ScheduledGovernanceAutomationEligibility.Evaluate(new GovernanceReviewItem(
            item.Key,
            itemKind,
            item.ProjectId,
            item.Classification,
            item.RecommendedAction,
            item.RiskLevel,
            item.RequiresExplicitApproval,
            item.AuthorityResourceId,
            item.RelatedResourceIds,
            item.ReasonCodes ?? [],
            item.GovernanceRunId)
        {
            SemanticConfidence = item.SemanticConfidence,
            IsReversible = item.IsReversible,
            RetentionPolicyVersion = item.RetentionPolicyVersion,
            DeleteEligibleAt = item.DeleteEligibleAt
        });
    }

    private async Task<GovernanceBatchItemResult> ProcessTypedSurfaceAsync(
        BatchPlanItem item,
        GovernanceBatchExecuteRequest request,
        IReadOnlySet<GovernanceBatchActionType> allowed,
        ContextHubRequestActor actor,
        CancellationToken cancellationToken)
    {
        ActorAuthorization.EnsureProjectAllowed(actor, item.ProjectId, write: true);
        if (item.Kind == nameof(GovernanceItemKind.Retention))
        {
            return await ProcessRetentionAsync(item, request, allowed, actor, cancellationToken);
        }
        if (item.Kind == nameof(GovernanceItemKind.ProjectHierarchy) && item.RequiresExplicitApproval)
        {
            var rows = await dbContext.ProjectHierarchies.AsNoTracking()
                .Where(x => x.TenantId == actor.TenantId && x.OwnerUserId == actor.UserId && x.ParentProjectId == item.ProjectId)
                .OrderBy(x => x.ChildProjectId)
                .ThenBy(x => x.Id)
                .ToListAsync(cancellationToken);
            var proposedChildren = rows.Where(x => x.Id != item.Id)
                .Select(x => x.ChildProjectId)
                .Where(x => !string.Equals(x, item.ProjectId, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var proposal = await proposalService.CreateAsync(new ChatGptProposalCreateRequest(
                "project_hierarchy_set_children",
                item.ProjectId,
                JsonSerializer.Serialize(new ProjectHierarchySetChildrenRequest(item.ProjectId, proposedChildren), JsonOptions),
                "Review hierarchy reconciliation",
                $"{item.Classification} requires explicit approval; Scheduled mode did not mutate the project tree.",
                actor.UserId?.ToString("D") ?? actor.Username,
                string.Empty,
                actor.Username,
                request.GovernanceRunId), cancellationToken);
            return await AuditedResultAsync(item, GovernanceBatchActionType.HierarchyReconcile,
                GovernanceBatchItemDisposition.RequiresUserDecision,
                "A schema-validated hierarchy proposal was created; no hierarchy mutation occurred.", [proposal.Id],
                item.RelatedResourceIds, actor, cancellationToken);
        }
        var semanticAutoResolutionAllowed = item.IsReversible &&
            item.SemanticConfidence >= request.SemanticAutoResolutionConfidenceThreshold &&
            item.RiskLevel < GovernanceBatchRiskLevel.Critical;
        if ((item.RequiresExplicitApproval || item.RiskLevel > request.MaxRiskLevel) && !semanticAutoResolutionAllowed)
        {
            return await AuditedResultAsync(item, ParseRecommendedAction(item.RecommendedAction),
                GovernanceBatchItemDisposition.RequiresUserDecision,
                $"{item.Classification} requires explicit authority or exceeds this batch risk policy.", [],
                item.RelatedResourceIds, actor, cancellationToken);
        }

        if (item.Kind == nameof(GovernanceItemKind.Artifact) &&
            item.Classification == "DuplicateArtifact" &&
            allowed.Contains(GovernanceBatchActionType.ArtifactReconcile))
        {
            var duplicate = await dbContext.MemoryItems.AsNoTracking().SingleOrDefaultAsync(x => x.Id == item.Id, cancellationToken);
            var authorityId = item.RelatedResourceIds.FirstOrDefault();
            var authority = authorityId == Guid.Empty
                ? null
                : await dbContext.MemoryItems.AsNoTracking().SingleOrDefaultAsync(x => x.Id == authorityId, cancellationToken);
            if (duplicate is null || authority is null || duplicate.Status == MemoryStatus.Archived)
            {
                return await AuditedResultAsync(item, GovernanceBatchActionType.ArtifactReconcile,
                    GovernanceBatchItemDisposition.NoOp, "Artifact duplicate is already reconciled or no longer exists.", [],
                    authority is null ? [] : [authority.Id], actor, cancellationToken);
            }
            if (!string.Equals(duplicate.ProjectId, authority.ProjectId, StringComparison.OrdinalIgnoreCase) ||
                duplicate.MemoryType != MemoryType.Artifact || authority.MemoryType != MemoryType.Artifact ||
                !string.Equals(Normalize(duplicate.Title), Normalize(authority.Title), StringComparison.Ordinal) ||
                !string.Equals(Normalize(duplicate.Summary), Normalize(authority.Summary), StringComparison.Ordinal) ||
                !string.Equals(Normalize(duplicate.SourceType), Normalize(authority.SourceType), StringComparison.Ordinal))
            {
                return await AuditedResultAsync(item, GovernanceBatchActionType.ArtifactReconcile,
                    GovernanceBatchItemDisposition.RequiresUserDecision,
                    "Artifact semantic evidence no longer establishes a deterministic authority winner.", [], [duplicate.Id], actor, cancellationToken);
            }
            var proposal = await CreateAndApplyProposalAsync("memory_archive", duplicate.ProjectId,
                "Archive deterministic duplicate artifact",
                $"Archive duplicate artifact {duplicate.Id:D}; authority {authority.Id:D} remains active.",
                new MemoryArchiveRequest(duplicate.Id, duplicate.ProjectId, true, "Autonomous semantic duplicate resolution."),
                request.GovernanceRunId, cancellationToken);
            var readBack = await memoryService.GetAsync(duplicate.Id, cancellationToken);
            if (readBack?.Status != MemoryStatus.Archived)
                throw new InvalidOperationException("Semantic artifact resolution failed resource read-back.");
            var result = await AuditedResultAsync(item, GovernanceBatchActionType.ArtifactReconcile,
                GovernanceBatchItemDisposition.Applied,
                "Deterministic duplicate artifact was autonomously archived with authority and resource read-back.",
                [proposal.Id], [duplicate.Id, authority.Id], actor, cancellationToken);
            return result with { SemanticAutoResolved = true };
        }

        if (item.Kind == nameof(GovernanceItemKind.Discussion) &&
            item.Classification == "CompletedDiscussion" &&
            allowed.Contains(GovernanceBatchActionType.DiscussionReconcile))
        {
            var proposal = await CreateAndApplyProposalAsync("discussion_thread_archive", item.ProjectId,
                "Archive completed discussion", "Archive a closed discussion while retaining its complete history and audit chain.",
                new DiscussionThreadArchiveRequest(item.Id), request.GovernanceRunId, cancellationToken);
            var readBack = await dbContext.DiscussionThreads.AsNoTracking().SingleOrDefaultAsync(x => x.Id == item.Id, cancellationToken);
            if (readBack?.ArchivedAt is null) throw new InvalidOperationException("Discussion archive proposal applied without resource read-back.");
            return await AuditedResultAsync(item, GovernanceBatchActionType.DiscussionReconcile, GovernanceBatchItemDisposition.Applied,
                "Closed discussion was proposal-applied, archived, and read back; history was retained.", [proposal.Id], [item.Id], actor, cancellationToken);
        }

        if (item.Kind == nameof(GovernanceItemKind.WorkItem) &&
            item.Classification == "CompletedHistoricalWorkItem" &&
            allowed.Contains(GovernanceBatchActionType.WorkItemReconcile))
        {
            var proposal = await CreateAndApplyProposalAsync("project_work_item_archive", item.ProjectId,
                "Archive terminal historical work item", "Archive a completed or cancelled historical work item without changing business status.",
                new ProjectWorkItemArchiveRequest(item.Id), request.GovernanceRunId, cancellationToken);
            var readBack = await dbContext.ProjectWorkItems.AsNoTracking().SingleOrDefaultAsync(x => x.Id == item.Id, cancellationToken);
            if (readBack?.ArchivedAt is null) throw new InvalidOperationException("Work item archive proposal applied without resource read-back.");
            return await AuditedResultAsync(item, GovernanceBatchActionType.WorkItemReconcile, GovernanceBatchItemDisposition.Applied,
                "Terminal work item was proposal-applied, archived, and read back; business status was not changed.", [proposal.Id], [item.Id], actor, cancellationToken);
        }

        var disposition = item.Kind is nameof(GovernanceItemKind.LogPartition) or nameof(GovernanceItemKind.LogCandidate)
            ? GovernanceBatchItemDisposition.Deferred
            : GovernanceBatchItemDisposition.RequiresUserDecision;
        return await AuditedResultAsync(item, ParseRecommendedAction(item.RecommendedAction), disposition,
            item.Kind.StartsWith("Log", StringComparison.Ordinal)
                ? "Log candidate was classified server-side; Scheduled mode performed no purge or unredacted promotion."
                : "Typed governance candidate requires a proposal or deterministic authority evidence before mutation.",
            [], item.RelatedResourceIds, actor, cancellationToken);
    }

    private async Task<GovernanceBatchItemResult> ProcessFindingAsync(
        BatchPlanItem item,
        GovernanceBatchExecuteRequest request,
        IReadOnlySet<GovernanceBatchActionType> allowed,
        ContextHubRequestActor actor,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var result = await ProcessFindingCoreAsync(item, request, allowed, actor, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<GovernanceBatchItemResult> ProcessFindingCoreAsync(
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

        if ((finding.Type is GovernanceFindingType.StaleMemoryCandidate or
             GovernanceFindingType.LowSignalEpisodeCandidate or
             GovernanceFindingType.ObsoleteMemoryCandidate or
             GovernanceFindingType.LowValueMemoryCandidate or
             GovernanceFindingType.ArchiveMemoryCandidate) &&
            finding.PrimaryMemoryId.HasValue &&
            allowed.Contains(GovernanceBatchActionType.Archive) &&
            await HasStrongSuccessorEvidenceAsync(finding.PrimaryMemoryId.Value, actor, cancellationToken))
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

    private async Task<GovernanceBatchItemResult> ProcessRetentionAsync(
        BatchPlanItem item,
        GovernanceBatchExecuteRequest request,
        IReadOnlySet<GovernanceBatchActionType> allowed,
        ContextHubRequestActor actor,
        CancellationToken cancellationToken)
    {
        if (item.RecommendedAction == "Quarantine")
        {
            if (!allowed.Contains(GovernanceBatchActionType.Quarantine))
            {
                return await AuditedResultAsync(item, GovernanceBatchActionType.Quarantine,
                    GovernanceBatchItemDisposition.Deferred, "Quarantine is not allowed by this execution payload.", [],
                    [item.Id], actor, cancellationToken);
            }
            var candidate = await autonomousRetention.QuarantineAsync(item.Id, item.ProjectId, request.GovernanceRunId, cancellationToken);
            return await AuditedResultAsync(item, GovernanceBatchActionType.Quarantine,
                GovernanceBatchItemDisposition.Applied,
                $"Resource was archived and quarantined under typed policy {candidate.PolicyKind}; deleteEligibleAt={candidate.DeleteEligibleAt:O}.",
                [], [item.Id], actor, cancellationToken);
        }

        if (item.RecommendedAction != "MaturedDelete")
        {
            return await AuditedResultAsync(item, null, GovernanceBatchItemDisposition.Deferred,
                "Retention item does not identify an executable lifecycle transition.", [], [item.Id], actor, cancellationToken);
        }
        if (!request.AllowMaturedDelete || !allowed.Contains(GovernanceBatchActionType.MaturedDelete))
        {
            return await AuditedResultAsync(item, GovernanceBatchActionType.MaturedDelete,
                GovernanceBatchItemDisposition.Deferred,
                "Matured delete requires the explicit scheduled matured-delete capability; direct hard-delete remains prohibited.",
                [], [item.Id], actor, cancellationToken);
        }
        var deleted = await autonomousRetention.DeleteMaturedAsync(item.Id, item.ProjectId, request.GovernanceRunId, cancellationToken);
        var result = await AuditedResultAsync(item, GovernanceBatchActionType.MaturedDelete,
            deleted.IsReplay ? GovernanceBatchItemDisposition.NoOp : GovernanceBatchItemDisposition.Applied,
            deleted.IsReplay
                ? "Matured delete replay returned the original immutable tombstone and audit references."
                : "Matured resource passed immediate eligibility revalidation, was hard-deleted, tombstoned, and read back.",
            [], [deleted.TombstoneId], actor, cancellationToken);
        return result with
        {
            IsReplay = deleted.IsReplay,
            TombstoneId = deleted.TombstoneId,
            AuditIds = result.AuditIds.Append(deleted.AuditId).Distinct().ToArray()
        };
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
        var typedAuthorityConflict = await GetTypedReplacementConflictAsync(
            primary.Id, secondary.Id, primary.ProjectId, actor, cancellationToken);
        if (!string.IsNullOrEmpty(typedAuthorityConflict))
        {
            return await SetFindingDispositionAsync(
                item,
                finding,
                GovernanceFindingDisposition.RequiresUserDecision,
                typedAuthorityConflict,
                actor,
                cancellationToken);
        }

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

        await ApplyTypedReplacementChainAsync(
            primary.Id,
            secondary.Id,
            primary.ProjectId,
            finding.Id,
            request.GovernanceRunId,
            actor,
            cancellationToken);

        var primaryBeforeArchive = await memoryService.GetAsync(primary.Id, cancellationToken);
        var secondaryBeforeArchive = await memoryService.GetAsync(secondary.Id, cancellationToken);
        if (primaryBeforeArchive is null || secondaryBeforeArchive is null ||
            !MetadataContainsId(primaryBeforeArchive.MetadataJson, "mergedFromMemoryIds", secondary.Id) ||
            !MetadataContainsId(secondaryBeforeArchive.MetadataJson, "supersededByMemoryId", primary.Id) ||
            !await HasTypedReplacementChainAsync(primary.Id, secondary.Id, primary.ProjectId, actor, cancellationToken))
        {
            throw new InvalidOperationException("Merge authority read-back failed before replacement-link creation and archival.");
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
            secondaryReadBack.Status != MemoryStatus.Archived || !linked ||
            !await HasTypedReplacementChainAsync(primary.Id, secondary.Id, primary.ProjectId, actor, cancellationToken))
        {
            throw new InvalidOperationException("Merge resource read-back did not verify the complete replacement chain.");
        }
        await governanceService.AcceptAsync(finding.Id, cancellationToken);
        return await AuditedResultAsync(item, GovernanceBatchActionType.Merge, GovernanceBatchItemDisposition.Applied,
            $"Merged {secondary.Id:D} into {primary.Id:D}; replacement chain read-back passed before Suggested Action convergence.", proposals, [primary.Id, secondary.Id], actor, cancellationToken);
    }

    private async Task<string?> GetTypedReplacementConflictAsync(
        Guid primaryId,
        Guid secondaryId,
        string projectId,
        ContextHubRequestActor actor,
        CancellationToken cancellationToken)
    {
        if (!actor.TenantId.HasValue || !actor.UserId.HasValue)
        {
            return "Typed replacement requires an authenticated tenant owner.";
        }

        var pair = await dbContext.MemoryItems.AsNoTracking()
            .Where(x => (x.Id == primaryId || x.Id == secondaryId) &&
                        x.TenantId == actor.TenantId && x.OwnerUserId == actor.UserId &&
                        x.ProjectId == projectId)
            .ToArrayAsync(cancellationToken);
        if (pair.Length != 2)
        {
            return "Typed replacement candidates are missing or outside the authenticated authority scope.";
        }

        var primary = pair.Single(x => x.Id == primaryId);
        var secondary = pair.Single(x => x.Id == secondaryId);
        if (primary.Scope != secondary.Scope)
        {
            return "Typed replacement cannot cross memory scope.";
        }

        if (IsExactTypedReplacement(primary, secondary))
        {
            return null;
        }

        var hasExistingAuthorityRelationship =
            primary.AuthorityState != MemoryAuthorityState.Current ||
            secondary.AuthorityState != MemoryAuthorityState.Current ||
            primary.SupersedesId.HasValue || primary.SupersededById.HasValue ||
            secondary.SupersedesId.HasValue || secondary.SupersededById.HasValue ||
            primary.SuccessorEvidenceId.HasValue || secondary.SuccessorEvidenceId.HasValue ||
            !string.IsNullOrWhiteSpace(primary.SuccessorEvidenceRef) ||
            !string.IsNullOrWhiteSpace(secondary.SuccessorEvidenceRef);
        return hasExistingAuthorityRelationship
            ? "Merge candidates already participate in a typed authority relationship; explicit authority review is required."
            : null;
    }

    private async Task ApplyTypedReplacementChainAsync(
        Guid primaryId,
        Guid secondaryId,
        string projectId,
        Guid findingId,
        string governanceRunId,
        ContextHubRequestActor actor,
        CancellationToken cancellationToken)
    {
        var conflict = await GetTypedReplacementConflictAsync(
            primaryId, secondaryId, projectId, actor, cancellationToken);
        if (!string.IsNullOrEmpty(conflict))
        {
            throw new InvalidOperationException($"Typed replacement revalidation failed: {conflict}");
        }

        var pair = await dbContext.MemoryItems
            .Where(x => (x.Id == primaryId || x.Id == secondaryId) &&
                        x.TenantId == actor.TenantId && x.OwnerUserId == actor.UserId &&
                        x.ProjectId == projectId)
            .ToArrayAsync(cancellationToken);
        var primary = pair.Single(x => x.Id == primaryId);
        var secondary = pair.Single(x => x.Id == secondaryId);
        if (IsExactTypedReplacement(primary, secondary))
        {
            return;
        }

        var now = clock.UtcNow;
        primary.AuthorityState = MemoryAuthorityState.Current;
        primary.SupersedesId = secondary.Id;
        primary.ValidFrom ??= primary.CreatedAt;
        primary.SuccessorEvidenceRef = $"governance-finding:{findingId:D}:run:{governanceRunId.Trim()}";
        primary.Version += 1;
        primary.UpdatedAt = now;

        secondary.AuthorityState = MemoryAuthorityState.Superseded;
        secondary.SupersededById = primary.Id;
        secondary.ValidFrom ??= secondary.CreatedAt;
        secondary.ValidUntil = now;
        secondary.Version += 1;
        secondary.UpdatedAt = now;

        await AddAuthorityRevisionAsync(primary, now, cancellationToken);
        await AddAuthorityRevisionAsync(secondary, now, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await cacheStore.IncrementProjectAsync(projectId, cancellationToken);
    }

    private async Task<bool> HasTypedReplacementChainAsync(
        Guid primaryId,
        Guid secondaryId,
        string projectId,
        ContextHubRequestActor actor,
        CancellationToken cancellationToken)
    {
        var pair = await dbContext.MemoryItems.AsNoTracking()
            .Where(x => (x.Id == primaryId || x.Id == secondaryId) &&
                        x.TenantId == actor.TenantId && x.OwnerUserId == actor.UserId &&
                        x.ProjectId == projectId)
            .ToArrayAsync(cancellationToken);
        return pair.Length == 2 && IsExactTypedReplacement(
            pair.Single(x => x.Id == primaryId),
            pair.Single(x => x.Id == secondaryId));
    }

    private static bool IsExactTypedReplacement(MemoryItem primary, MemoryItem secondary)
        => primary.Scope == secondary.Scope &&
           primary.AuthorityState == MemoryAuthorityState.Current &&
           primary.SupersedesId == secondary.Id &&
           secondary.AuthorityState == MemoryAuthorityState.Superseded &&
           secondary.SupersededById == primary.Id &&
           !string.IsNullOrWhiteSpace(primary.SuccessorEvidenceRef);

    private async ValueTask AddAuthorityRevisionAsync(
        MemoryItem memory,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await dbContext.MemoryItemRevisions.AddAsync(new MemoryItemRevision
        {
            MemoryItemId = memory.Id,
            Version = memory.Version,
            Title = memory.Title,
            Content = memory.Content,
            Summary = memory.Summary,
            MetadataJson = memory.MetadataJson,
            ChangedBy = "governance-authority-replacement",
            CreatedAt = now
        }, cancellationToken);
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

        var authorityContext = await LoadSuccessorEvidenceContextAsync(
            finding.PrimaryMemoryId.Value, actor, cancellationToken);
        var secondary = await memoryService.GetAsync(finding.PrimaryMemoryId.Value, cancellationToken);
        if (secondary is null || authorityContext is null)
        {
            return await AuditedResultAsync(item, GovernanceBatchActionType.Archive, GovernanceBatchItemDisposition.NoOp, "Secondary no longer exists.", [], [], actor, cancellationToken);
        }
        var secondaryEntity = authorityContext.Predecessor;
        if (secondary.Status == MemoryStatus.Archived || secondaryEntity.Status == MemoryStatus.Archived)
        {
            await governanceService.AcceptAsync(finding.Id, cancellationToken);
            return await AuditedResultAsync(item, GovernanceBatchActionType.Archive, GovernanceBatchItemDisposition.NoOp, "Secondary is already archived.", [], [secondary.Id], actor, cancellationToken);
        }

        var successorEvidence = authorityContext.Index.Evaluate(secondaryEntity, clock.UtcNow);
        var hasTypedSuccessorSignal = successorEvidence.HasTypedSignal ||
                                      authorityContext.RelatedItems.Any(x =>
                                          x.SupersedesId == secondaryEntity.Id ||
                                          x.SupersededById == secondaryEntity.Id);
        if (hasTypedSuccessorSignal)
        {
            if (!successorEvidence.IsStrong)
            {
                return await SetFindingDispositionAsync(
                    item,
                    finding,
                    GovernanceFindingDisposition.RequiresUserDecision,
                    $"Typed successor evidence is not strong or same-scope ({successorEvidence.ReasonCode}); no archive mutation was attempted.",
                    actor,
                    cancellationToken);
            }

            if (SuccessorEvidencePolicy.RequiresHumanDecision(secondaryEntity))
            {
                return await SetFindingDispositionAsync(
                    item,
                    finding,
                    GovernanceFindingDisposition.RequiresUserDecision,
                    "Successor chain is valid but the predecessor is critical, protected, legal/privacy, business-sensitive, read-only, high-risk, or irreversible; explicit human approval is required.",
                    actor,
                    cancellationToken);
            }

            // Re-read the typed chain immediately before creating the proposal.
            // A concurrent successor or evidence change must stop the item,
            // never fall back to legacy metadata or revive the stale blocker.
            authorityContext = await LoadSuccessorEvidenceContextAsync(
                secondaryEntity.Id, actor, cancellationToken);
            if (authorityContext is null)
            {
                return await SetFindingDispositionAsync(item, finding, GovernanceFindingDisposition.RequiresUserDecision,
                    "Successor chain disappeared during revalidation; a fresh review is required.", actor, cancellationToken);
            }

            secondaryEntity = authorityContext.Predecessor;
            successorEvidence = authorityContext.Index.Evaluate(secondaryEntity, clock.UtcNow);
            if (!successorEvidence.IsStrong || SuccessorEvidencePolicy.RequiresHumanDecision(secondaryEntity))
            {
                return await SetFindingDispositionAsync(item, finding, GovernanceFindingDisposition.RequiresUserDecision,
                    $"Successor chain changed during revalidation ({successorEvidence.ReasonCode}); no archive mutation was attempted.", actor, cancellationToken);
            }

            var typedSuccessor = successorEvidence.Successor!;
            var typedEvidence = successorEvidence.Evidence!;
            var typedProposal = await CreateAndApplyProposalAsync("memory_archive", secondaryEntity.ProjectId,
                "Archive verified typed successor predecessor",
                $"Archive {secondaryEntity.Id:D} only after explicit same-scope successor {typedSuccessor.Id:D} and evidence {typedEvidence.Id:D} read-back.",
                new MemoryArchiveRequest(secondaryEntity.Id, secondaryEntity.ProjectId, true, "Verified typed successor evidence."),
                request.GovernanceRunId, cancellationToken);
            var typedReadBack = await memoryService.GetAsync(secondaryEntity.Id, cancellationToken);
            if (typedReadBack?.Status != MemoryStatus.Archived)
            {
                throw new InvalidOperationException("Typed successor archive proposal applied but resource read-back is not Archived.");
            }

            var typedAuthorityReadBack = await LoadSuccessorEvidenceContextAsync(
                secondaryEntity.Id, actor, cancellationToken);
            var typedAuthority = typedAuthorityReadBack is null
                ? null
                : typedAuthorityReadBack.Index.Evaluate(typedAuthorityReadBack.Predecessor, clock.UtcNow);
            if (typedAuthority is null || !typedAuthority.IsStrong ||
                typedAuthority.Successor?.Id != typedSuccessor.Id ||
                typedAuthority.Evidence?.Id != typedEvidence.Id)
            {
                throw new InvalidOperationException("Typed successor archive read-back no longer proves the same authority chain; review is required.");
            }

            await governanceService.AcceptAsync(finding.Id, cancellationToken);
            return await AuditedResultAsync(item, GovernanceBatchActionType.Archive, GovernanceBatchItemDisposition.Applied,
                "Typed successor predecessor was reversibly archived after same-scope evidence and resource read-back.",
                [typedProposal.Id], [secondaryEntity.Id, typedSuccessor.Id, typedEvidence.Id], actor, cancellationToken);
        }

        return await SetFindingDispositionAsync(
            item,
            finding,
            GovernanceFindingDisposition.RequiresUserDecision,
            "Legacy metadata-only replacement evidence cannot resolve a stale blocker; persist the typed AuthorityState, SupersededById, SupersedesId, and SuccessorEvidence chain first.",
            actor,
            cancellationToken);
    }

    private async Task<bool> HasStrongSuccessorEvidenceAsync(
        Guid predecessorId,
        ContextHubRequestActor actor,
        CancellationToken cancellationToken)
    {
        var context = await LoadSuccessorEvidenceContextAsync(predecessorId, actor, cancellationToken);
        return context is not null && context.Index.Evaluate(context.Predecessor, clock.UtcNow).IsStrong;
    }

    private async Task<SuccessorEvidenceContext?> LoadSuccessorEvidenceContextAsync(
        Guid predecessorId,
        ContextHubRequestActor actor,
        CancellationToken cancellationToken)
    {
        if (!actor.TenantId.HasValue || !actor.UserId.HasValue)
        {
            return null;
        }

        var predecessor = await dbContext.MemoryItems.AsNoTracking().FirstOrDefaultAsync(x =>
            x.Id == predecessorId &&
            x.TenantId == actor.TenantId &&
            x.OwnerUserId == actor.UserId, cancellationToken);
        if (predecessor is null)
        {
            return null;
        }

        // The authority graph is deliberately bounded to the target, its
        // directly competing links, and referenced evidence. A large project
        // fixture must not make an unrelated stale blocker appear resolved.
        var anchorIds = new[]
            {
                predecessor.Id,
                predecessor.SupersededById,
                predecessor.SuccessorEvidenceId
            }
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .Distinct()
            .ToArray();
        var related = await dbContext.MemoryItems.AsNoTracking()
            .Where(x => x.TenantId == actor.TenantId &&
                        x.OwnerUserId == actor.UserId &&
                        (anchorIds.Contains(x.Id) ||
                         x.SupersedesId == predecessor.Id ||
                         x.SupersededById == predecessor.Id))
            .ToListAsync(cancellationToken);

        var evidenceIds = related
            .Select(x => x.SuccessorEvidenceId)
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .Except(related.Select(x => x.Id))
            .Distinct()
            .ToArray();
        if (evidenceIds.Length > 0)
        {
            related.AddRange(await dbContext.MemoryItems.AsNoTracking()
                .Where(x => x.TenantId == actor.TenantId &&
                            x.OwnerUserId == actor.UserId &&
                            evidenceIds.Contains(x.Id))
                .ToListAsync(cancellationToken));
        }

        return new SuccessorEvidenceContext(
            predecessor,
            related
                .Append(predecessor)
                .DistinctBy(x => x.Id)
                .ToArray(),
            SuccessorEvidencePolicy.CreateIndex(related.Append(predecessor)));
    }

    private sealed record SuccessorEvidenceContext(
        MemoryItem Predecessor,
        IReadOnlyList<MemoryItem> RelatedItems,
        SuccessorEvidencePolicy.SuccessorEvidenceIndex Index);

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
        var protectedInsight = insight.InsightType is ConversationInsightType.Decision or ConversationInsightType.Fact || insight.Importance >= 0.8m;
        if (!protectedInsight && insight.Confidence >= request.SemanticAutoResolutionConfidenceThreshold)
        {
            var title = GovernanceEvidenceFingerprint.NormalizeExactText(insight.Title);
            var summary = GovernanceEvidenceFingerprint.NormalizeExactText(insight.Summary);
            var now = clock.UtcNow;
            var durableEquivalents = await dbContext.MemoryItems.AsNoTracking().Where(x =>
                x.TenantId == actor.TenantId && x.OwnerUserId == actor.UserId && x.ProjectId == insight.ProjectId &&
                x.Status == MemoryStatus.Active &&
                x.AuthorityState == MemoryAuthorityState.Current &&
                x.ValidFrom <= now &&
                (!x.ValidUntil.HasValue || x.ValidUntil > now) &&
                x.Title.Trim().ToLower() == title && x.Summary.Trim().ToLower() == summary)
                .Take(2)
                .ToArrayAsync(cancellationToken);
            if (durableEquivalents.Length == 1)
            {
                var durableEquivalent = durableEquivalents[0];
                await conversationService.SkipInsightAsync(new ConversationInsightGovernanceRequest(
                    insight.Id, request.GovernanceRunId,
                    $"Autonomously resolved against durable evidence {durableEquivalent.Id:D}."), cancellationToken);
                var semantic = await AuditedResultAsync(item, GovernanceBatchActionType.SemanticReevaluate,
                    GovernanceBatchItemDisposition.Applied,
                    "Reopened or pending insight was autonomously resolved against an exact durable evidence match.",
                    [], [insight.Id, durableEquivalent.Id], actor, cancellationToken);
                return semantic with { SemanticAutoResolved = true };
            }
        }
        var disposition = protectedInsight ? ConversationInsightDisposition.RequiresUserDecision : ConversationInsightDisposition.Deferred;
        var updated = await conversationService.SetInsightDispositionAsync(new ConversationInsightDispositionRequest(
            insight.Id,
            disposition,
            protectedInsight
                ? "Semantic or high-signal insight requires explicit user authority."
                : "Scheduled batch deferred a non-terminal insight without inventing durable knowledge.",
            request.GovernanceRunId,
            ExpectedPromotionStatus: insight.PromotionStatus), cancellationToken);
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
        var reason = "Pending proposal requires explicit approval through the canonical proposal lifecycle; governance did not modify its status or payload.";
        return await AuditedResultAsync(item, GovernanceBatchActionType.ProposalApply, GovernanceBatchItemDisposition.RequiresUserDecision,
            reason, [proposal.Id], [], actor, cancellationToken);
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
        var fullPlan = await fullGovernance.BuildAsync(projectIds, governanceRunId, snapshot, cancellationToken);
        return fullPlan.Items.Select(x => new BatchPlanItem(
                x.ItemKey,
                DispatchKindFor(x.ItemKind),
                ParseItemId(x.ItemKey, x.AuthorityResourceId),
                x.ProjectId,
                snapshot.Coverage.SnapshotToken,
                governanceRunId,
                x.Classification,
                x.RecommendedAction,
                x.RiskLevel,
                x.RequiresExplicitApproval,
                x.RelatedResourceIds,
                x.SemanticConfidence,
                x.IsReversible,
                x.RetentionPolicyVersion,
                x.DeleteEligibleAt,
                x.ItemKind,
                x.AuthorityResourceId,
                x.ReasonCodes))
             .OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.ProjectId, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Key, StringComparer.Ordinal).ToList();
    }

    private async Task<IReadOnlyDictionary<string, string>> ValidateScheduledPlanAsync(
        IReadOnlyList<BatchPlanItem> persistedPlan,
        DurableMemoryGovernanceSnapshotResult snapshot,
        IReadOnlyList<string> projectIds,
        string governanceRunId,
        ContextHubRequestActor actor,
        CancellationToken cancellationToken)
    {
        if (persistedPlan.Count > MaximumPlanItems)
        {
            throw new GovernanceBatchException(
                GovernanceBatchErrorCode.ReReviewRequired,
                "Persisted governance plan exceeds the safety limit; perform a fresh review.");
        }

        var duplicateKeys = persistedPlan
            .GroupBy(x => x.Key, StringComparer.Ordinal)
            .Where(x => string.IsNullOrWhiteSpace(x.Key) || x.Count() > 1)
            .Select(x => x.Key)
            .ToArray();
        if (duplicateKeys.Length > 0)
        {
            throw new GovernanceBatchException(
                GovernanceBatchErrorCode.ReReviewRequired,
                "Persisted governance plan contains duplicate or empty item keys; perform a fresh review.");
        }

        // PlanJson is a durable execution input, not an authority source. Rebuild
        // the canonical projection from the immutable snapshot and current server
        // state before a Scheduled mutation is allowed to cross this boundary.
        var rebuiltPlan = await BuildPlanAsync(
            snapshot,
            projectIds,
            governanceRunId,
            actor,
            cancellationToken);
        var rebuiltByKey = rebuiltPlan.ToDictionary(x => x.Key, StringComparer.Ordinal);
        if (rebuiltByKey.Count != persistedPlan.Count ||
            persistedPlan.Any(item => !rebuiltByKey.ContainsKey(item.Key)))
        {
            throw new GovernanceBatchException(
                GovernanceBatchErrorCode.ReReviewRequired,
                "Persisted governance plan no longer matches the server-rebuilt authority projection; perform a fresh review.");
        }

        var rejected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in persistedPlan)
        {
            var localReason = ValidateScheduledPlanItem(item);
            if (localReason is not null)
            {
                rejected[item.Key] = localReason;
                continue;
            }

            if (!CanonicalPlanEquivalent(item, rebuiltByKey[item.Key]))
            {
                rejected[item.Key] = "server-rebuilt-canonical-projection-mismatch";
            }
        }

        return rejected;
    }

    private static string? ValidateScheduledPlanItem(BatchPlanItem item)
    {
        if (item.CanonicalKind is not { } canonicalKind || !Enum.IsDefined(canonicalKind))
        {
            return "missing-or-invalid-canonical-kind";
        }

        if (!string.Equals(item.Kind, DispatchKindFor(canonicalKind), StringComparison.Ordinal))
        {
            return "canonical-dispatch-kind-mismatch";
        }

        if (item.ReasonCodes is not { Count: > 0 } ||
            item.ReasonCodes.Any(string.IsNullOrWhiteSpace))
        {
            return "missing-or-invalid-reason-codes";
        }

        return null;
    }

    private static bool CanonicalPlanEquivalent(BatchPlanItem persisted, BatchPlanItem rebuilt)
        => string.Equals(persisted.Key, rebuilt.Key, StringComparison.Ordinal) &&
           string.Equals(persisted.Kind, rebuilt.Kind, StringComparison.Ordinal) &&
           persisted.Id == rebuilt.Id &&
           string.Equals(persisted.ProjectId, rebuilt.ProjectId, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(persisted.SnapshotToken, rebuilt.SnapshotToken, StringComparison.Ordinal) &&
           string.Equals(persisted.GovernanceRunId, rebuilt.GovernanceRunId, StringComparison.Ordinal) &&
           string.Equals(persisted.Classification, rebuilt.Classification, StringComparison.Ordinal) &&
           string.Equals(persisted.RecommendedAction, rebuilt.RecommendedAction, StringComparison.Ordinal) &&
           persisted.RiskLevel == rebuilt.RiskLevel &&
           persisted.RequiresExplicitApproval == rebuilt.RequiresExplicitApproval &&
           persisted.SemanticConfidence == rebuilt.SemanticConfidence &&
           persisted.IsReversible == rebuilt.IsReversible &&
           string.Equals(persisted.RetentionPolicyVersion, rebuilt.RetentionPolicyVersion, StringComparison.Ordinal) &&
           persisted.DeleteEligibleAt == rebuilt.DeleteEligibleAt &&
           persisted.CanonicalKind == rebuilt.CanonicalKind &&
           persisted.AuthorityResourceId == rebuilt.AuthorityResourceId &&
           persisted.RelatedResourceIds.OrderBy(x => x).SequenceEqual(rebuilt.RelatedResourceIds.OrderBy(x => x)) &&
           persisted.ReasonCodes!.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(
               rebuilt.ReasonCodes!.OrderBy(x => x, StringComparer.Ordinal), StringComparer.Ordinal);

    private static string DispatchKindFor(GovernanceItemKind kind)
        => kind switch
        {
            GovernanceItemKind.Memory => "Finding",
            GovernanceItemKind.SuggestedAction => "SuggestedAction",
            GovernanceItemKind.ConversationInsight => "ConversationInsight",
            GovernanceItemKind.Proposal => "Proposal",
            _ => kind.ToString()
        };

    private async Task<IReadOnlyList<string>> ResolveProjectIdsAsync(
        IReadOnlyList<string>? requested,
        ContextHubRequestActor actor,
        CancellationToken cancellationToken)
    {
        var available = (await governanceScope.ResolveAsync(requested, cancellationToken))
            .Where(x => x.CanRead && x.CanWrite)
            .ToArray();
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

    private static void EnsureExplicitScopeMatchesSnapshot(
        IReadOnlyList<string>? requested,
        IReadOnlyList<string> snapshotExecutionProjectIds)
    {
        if (requested is not { Count: > 0 })
        {
            return;
        }

        var requestedExecutionProjectIds = DurableMemoryGovernancePolicy.ToExecutionProjectIds(requested);
        if (!requestedExecutionProjectIds.SequenceEqual(snapshotExecutionProjectIds, StringComparer.OrdinalIgnoreCase))
        {
            throw new GovernanceBatchException(
                GovernanceBatchErrorCode.CursorScopeMismatch,
                "Explicit ProjectIds do not match the immutable governance review snapshot scope.");
        }
    }

    private async Task PersistProgressAsync(
        GovernanceBatchRun run,
        GovernanceBatchExecution execution,
        BatchAccumulator accumulator,
        int logicalPosition,
        string lastItemKey,
        string actorHash,
        string projectSetHash,
        string policyHash,
        int remainingCount,
        Stopwatch stopwatch,
        string stoppedReason,
        bool advance,
        CancellationToken cancellationToken)
    {
        var cursor = remainingCount > 0
            ? BuildCursor(run, logicalPosition, lastItemKey, actorHash, projectSetHash, policyHash)
            : string.Empty;
        if (advance)
        {
            run.LastCursor = cursor;
            run.UpdatedAt = clock.UtcNow;
            execution.CursorAfter = cursor;
        }
        execution.UpdatedAt = clock.UtcNow;
        execution.ResultJson = JsonSerializer.Serialize(accumulator.ToResult(
            string.IsNullOrEmpty(execution.CursorAfter) ? null : execution.CursorAfter,
            remainingCount > 0,
            stopwatch.ElapsedMilliseconds,
            stoppedReason), JsonOptions);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    internal static void ValidateRequest(GovernanceBatchExecuteRequest request)
    {
        ValidateExecutionMode(request.ExecutionMode);
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
        if (request.SemanticAutoResolutionConfidenceThreshold is < 0m or > 1m)
            throw new InvalidOperationException("SemanticAutoResolutionConfidenceThreshold must be between 0 and 1.");
    }

    private static void ValidateExecutionMode(GovernanceBatchExecutionMode executionMode)
    {
        if (!Enum.IsDefined(executionMode))
        {
            throw new GovernanceBatchException(
                GovernanceBatchErrorCode.SchemaCapabilityMismatch,
                "ExecutionMode is invalid.");
        }
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
            request.AllowMaturedDelete,
            request.SemanticAutoResolutionConfidenceThreshold,
            toolContractVersion = request.ToolContractVersion?.Trim() ?? GovernanceToolContract.ToolContractVersion,
            schemaHash = request.SchemaHash?.Trim().ToLowerInvariant() ?? GovernanceToolContract.SchemaHash,
            request.IsReReview,
            request.ExecutionMode
        };

    private async Task ValidateIssuedCursorAsync(
        string cursor,
        CursorPayload payload,
        Guid tenantId,
        Guid ownerUserId,
        string governanceRunId,
        string actorHash,
        string projectSetHash,
        string policyHash,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(payload.ActorHash, actorHash[..16], StringComparison.Ordinal))
            throw new GovernanceBatchException(GovernanceBatchErrorCode.CursorActorMismatch, "Cursor belongs to a different tenant actor.");
        if (!string.Equals(payload.ScopeHash, projectSetHash[..16], StringComparison.Ordinal))
            throw new GovernanceBatchException(GovernanceBatchErrorCode.CursorScopeMismatch, "Cursor belongs to a different authorized ProjectId scope.");
        if (!string.Equals(payload.PolicyHash, policyHash[..16], StringComparison.Ordinal))
            throw new GovernanceBatchException(GovernanceBatchErrorCode.CursorPolicyMismatch, "Cursor belongs to a different governance execution policy.");
        if (payload.ExpiresAtUnixSeconds <= clock.UtcNow.ToUnixTimeSeconds())
            throw new GovernanceBatchException(GovernanceBatchErrorCode.CursorExpired, "Cursor has expired; perform a fresh review.");

        var cursorRun = await dbContext.GovernanceBatchRuns.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == payload.RunId, cancellationToken);
        if (cursorRun is null || !string.Equals(cursorRun.GovernanceRunId, governanceRunId, StringComparison.Ordinal))
            throw new GovernanceBatchException(GovernanceBatchErrorCode.InvalidCursor, "Cursor does not identify this governance run.");
        if (cursorRun.TenantId != tenantId || cursorRun.OwnerUserId != ownerUserId)
            throw new GovernanceBatchException(GovernanceBatchErrorCode.CursorActorMismatch, "Cursor belongs to a different tenant actor.");
        if (!string.Equals(cursorRun.ProjectSetHash, projectSetHash, StringComparison.Ordinal))
            throw new GovernanceBatchException(GovernanceBatchErrorCode.CursorScopeMismatch, "Cursor belongs to a different authorized ProjectId scope.");
        if (cursorRun.ExpiresAt <= clock.UtcNow)
            throw new GovernanceBatchException(GovernanceBatchErrorCode.CursorExpired, "Cursor has expired; perform a fresh review.");

        var issued = await dbContext.GovernanceBatchExecutions.AsNoTracking()
            .AnyAsync(x => x.GovernanceBatchRunId == cursorRun.Id &&
                           x.CursorAfter == cursor &&
                           x.Status == "Completed", cancellationToken);
        if (!issued)
            throw new GovernanceBatchException(GovernanceBatchErrorCode.InvalidCursor, "Cursor was not issued by a completed governance batch.");
    }

    private async Task<HashSet<string>> GetTerminalItemKeysAsync(
        IReadOnlyCollection<Guid> logicalRunIds,
        Guid currentRunId,
        IReadOnlySet<string> currentPlanItemKeys,
        CancellationToken cancellationToken)
    {
        var executions = await dbContext.GovernanceBatchExecutions.AsNoTracking()
            .Where(x => logicalRunIds.Contains(x.GovernanceBatchRunId) && x.Status == "Completed")
            .Select(x => new { x.GovernanceBatchRunId, x.RequestJson, x.ResultJson })
            .ToListAsync(cancellationToken);
        var terminal = new HashSet<string>(StringComparer.Ordinal);
        foreach (var execution in executions.Where(x => !IsDryRunRequest(x.RequestJson)))
        {
            GovernanceBatchExecuteResult result;
            try { result = DeserializeResult(execution.ResultJson); }
            catch (InvalidOperationException) { continue; }
            foreach (var item in result.Items)
            {
                var isTerminal = item.Disposition is
                    GovernanceBatchItemDisposition.Applied or
                    GovernanceBatchItemDisposition.Deferred or
                    GovernanceBatchItemDisposition.RequiresUserDecision;
                var isCurrentGenerationNoOp = item.Disposition == GovernanceBatchItemDisposition.NoOp &&
                                              execution.GovernanceBatchRunId == currentRunId;
                var isReconciledPriorGenerationNoOp = item.Disposition == GovernanceBatchItemDisposition.NoOp &&
                                                       !currentPlanItemKeys.Contains(item.ItemKey);
                if (isTerminal || isCurrentGenerationNoOp || isReconciledPriorGenerationNoOp)
                {
                    terminal.Add(item.ItemKey);
                }
            }
        }
        return terminal;
    }

    private static string BuildPolicyHash(GovernanceBatchExecuteRequest request, IReadOnlyList<string> projectIds)
        => Hash(JsonSerializer.Serialize(new
        {
            projectIds = projectIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase),
            request.MaxMutations,
            request.MaxDurationSeconds,
            allowedActionTypes = (request.AllowedActionTypes ?? []).Distinct().OrderBy(x => x).ToArray(),
            request.MaxRiskLevel,
            allowHardDelete = request.ExecutionMode == GovernanceBatchExecutionMode.Scheduled ? false : request.AllowHardDelete,
            request.AllowMaturedDelete,
            request.SemanticAutoResolutionConfidenceThreshold,
            toolContractVersion = request.ToolContractVersion?.Trim() ?? GovernanceToolContract.ToolContractVersion,
            schemaHash = request.SchemaHash?.Trim().ToLowerInvariant() ?? GovernanceToolContract.SchemaHash,
            request.ExecutionMode
        }, JsonOptions));

    private static CursorPayload ParseCursor(string cursor)
    {
        try
        {
            if (!cursor.StartsWith("gb2.", StringComparison.Ordinal)) throw new FormatException();
            var encoded = cursor[4..].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
            var payload = JsonSerializer.Deserialize<CursorPayload>(Convert.FromBase64String(encoded), JsonOptions);
            if (payload is null || payload.Version != CursorVersion || payload.RunId == Guid.Empty || payload.Position < 0 ||
                string.IsNullOrWhiteSpace(payload.ActorHash) || string.IsNullOrWhiteSpace(payload.ScopeHash) ||
                string.IsNullOrWhiteSpace(payload.PolicyHash)) throw new FormatException();
            return payload;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new GovernanceBatchException(GovernanceBatchErrorCode.InvalidCursor, "Cursor format or version is invalid.");
        }
    }

    private static string BuildCursor(
        GovernanceBatchRun run,
        int logicalPosition,
        string lastItemKey,
        string actorHash,
        string projectSetHash,
        string policyHash)
    {
        var payload = new CursorPayload(
            CursorVersion,
            run.Id,
            logicalPosition,
            lastItemKey,
            actorHash[..16],
            projectSetHash[..16],
            policyHash[..16],
            run.ExpiresAt.ToUnixTimeSeconds());
        return "gb2." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

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

    private static Guid ParseItemId(string itemKey, Guid? fallback)
    {
        var separator = itemKey.IndexOf(':');
        if (separator >= 0)
        {
            var suffix = itemKey[(separator + 1)..];
            var nextSeparator = suffix.IndexOf(':');
            if (nextSeparator >= 0) suffix = suffix[..nextSeparator];
            if (Guid.TryParse(suffix, out var parsed)) return parsed;
        }
        return fallback ?? DeterministicItemId(itemKey);
    }

    private static GovernanceBatchActionType? ParseRecommendedAction(string value)
        => Enum.TryParse<GovernanceBatchActionType>(value, ignoreCase: true, out var action) ? action : null;

    private static Guid DeterministicItemId(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record CursorPayload(
        int Version,
        Guid RunId,
        int Position,
        string ItemKey,
        string ActorHash,
        string ScopeHash,
        string PolicyHash,
        long ExpiresAtUnixSeconds);

    private sealed record BatchPlanItem(
        string Key,
        string Kind,
        Guid Id,
        string ProjectId,
        string SnapshotToken,
        string GovernanceRunId,
        string Classification = "",
        string RecommendedAction = "",
        GovernanceBatchRiskLevel RiskLevel = GovernanceBatchRiskLevel.Low,
        bool RequiresExplicitApproval = false,
        IReadOnlyList<Guid>? RelatedIds = null,
        decimal SemanticConfidence = 0m,
        bool IsReversible = false,
        string RetentionPolicyVersion = "",
        DateTimeOffset? DeleteEligibleAt = null,
        GovernanceItemKind? CanonicalKind = null,
        Guid? AuthorityResourceId = null,
        IReadOnlyList<string>? ReasonCodes = null)
    {
        public IReadOnlyList<Guid> RelatedResourceIds => RelatedIds ?? [];
    }

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
        public int QuarantinedCount { get; private set; }
        public int DeleteEligibleCount { get; private set; }
        public int DeleteMaturedCount { get; private set; }
        public int AutoDeletedCount { get; private set; }
        public int DeleteCancelledCount { get; private set; }
        public int TombstonedCount { get; private set; }
        public int SemanticAutoResolvedCount { get; private set; }
        public int ProtectedRetentionCount { get; private set; }
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
            if (item.Disposition == GovernanceBatchItemDisposition.Applied && item.ActionType == GovernanceBatchActionType.Quarantine) QuarantinedCount++;
            if (item.Disposition == GovernanceBatchItemDisposition.Applied && item.ActionType == GovernanceBatchActionType.MaturedDelete)
            {
                DeleteMaturedCount++;
                AutoDeletedCount++;
                TombstonedCount++;
            }
            if (item.SemanticAutoResolved && item.Disposition == GovernanceBatchItemDisposition.Applied) SemanticAutoResolvedCount++;
        }

        public void ApplyRetentionReview(AutonomousRetentionReviewResult review)
        {
            QuarantinedCount += review.QuarantinedCount;
            DeleteEligibleCount = review.DeleteEligibleCount;
            DeleteMaturedCount += review.DeleteMaturedCount;
            DeleteCancelledCount = review.DeleteCancelledCount;
            ProtectedRetentionCount = review.ProtectedRetentionCount;
        }

        public GovernanceBatchExecuteResult ToResult(string? nextCursor, bool hasMore, long elapsedMilliseconds, string stoppedReason)
            => new(ScannedCount, AttemptedCount, AppliedCount, NoOpCount, FailedCount, DeferredCount, RequiresUserDecisionCount,
                MergedCount, UpdatedCount, MovedCount, ArchivedCount, ReindexedCount, DeleteProposalCount,
                nextCursor, hasMore, Items.Count > 0, Items.ToArray(), AuditIds.Distinct().ToArray(), snapshotToken, stoppedReason)
            {
                GovernanceRunId = governanceRunId,
                ElapsedMilliseconds = elapsedMilliseconds,
                QuarantinedCount = QuarantinedCount,
                DeleteEligibleCount = DeleteEligibleCount,
                DeleteMaturedCount = DeleteMaturedCount,
                AutoDeletedCount = AutoDeletedCount,
                DeleteCancelledCount = DeleteCancelledCount,
                TombstonedCount = TombstonedCount,
                SemanticAutoResolvedCount = SemanticAutoResolvedCount,
                RemainingHumanDecisionCount = RequiresUserDecisionCount,
                ProtectedRetentionCount = ProtectedRetentionCount
            };
    }
}
