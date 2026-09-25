using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Memory.Application;

public sealed class AgentExecutionService(
    IApplicationDbContext dbContext,
    IRequestActorAccessor actorAccessor,
    ISkillService skillService,
    IAgentExecutionResourceResolver resourceResolver,
    ISecretProtector secretProtector,
    IClock clock) : IAgentExecutionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly AgentExecutionStatus[] ActiveStatuses = [AgentExecutionStatus.Claimed, AgentExecutionStatus.Running];

    public async Task<AgentExecutionResult> PrepareAsync(AgentExecutionPrepareRequest request, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureAdminOrScopeAllowed(actor, SecurityScopes.AgentExecutionsManage);
        var projectId = NormalizeRequired(request.ProjectId, 200, "ProjectId");
        ActorAuthorization.EnsureProjectAllowed(actor, projectId, write: true);
        ValidateIdempotencyKey(request.IdempotencyKey);
        var requestHash = HashJson(request);
        if (await ReplayAsync<AgentExecutionResult>(actor, "prepare", "manager", request.IdempotencyKey, requestHash, cancellationToken) is { } replay)
        {
            return replay;
        }

        return await dbContext.ExecuteInTransactionAsync(async transactionCancellationToken =>
        {
            await dbContext.AcquireTransactionLockAsync(
                $"agent-execution-prepare:{actor.TenantId}:{request.WorkItemId}",
                transactionCancellationToken);
            if (await ReplayAsync<AgentExecutionResult>(actor, "prepare", "manager", request.IdempotencyKey, requestHash, transactionCancellationToken) is { } lockedReplay)
            {
                return lockedReplay;
            }

            return await PrepareCoreAsync(request, actor, projectId, requestHash, transactionCancellationToken);
        }, cancellationToken);
    }

    private async Task<AgentExecutionResult> PrepareCoreAsync(
        AgentExecutionPrepareRequest request,
        ContextHubRequestActor actor,
        string projectId,
        string requestHash,
        CancellationToken cancellationToken)
    {
        var workItem = await Scope(dbContext.ProjectWorkItems.Include(x => x.ChecklistItems), actor)
            .SingleOrDefaultAsync(x => x.Id == request.WorkItemId, cancellationToken)
            ?? throw new InvalidOperationException($"Project work item '{request.WorkItemId}' was not found.");
        if (!string.Equals(workItem.ProjectId, projectId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Work item ProjectId does not match the execution package.");
        }
        if (workItem.ArchivedAt.HasValue || workItem.Status is ProjectWorkItemStatus.Completed or ProjectWorkItemStatus.Cancelled)
        {
            throw new InvalidOperationException("Only active, non-archived work items may be prepared for execution.");
        }

        var existing = await Scope(dbContext.AgentExecutions, actor)
            .AnyAsync(x => x.WorkItemId == request.WorkItemId &&
                (x.Status == AgentExecutionStatus.Ready ||
                 x.Status == AgentExecutionStatus.Claimed ||
                 x.Status == AgentExecutionStatus.Running ||
                 x.Status == AgentExecutionStatus.Blocked ||
                 x.Status == AgentExecutionStatus.FailedRetryable), cancellationToken);
        if (existing)
        {
            throw new InvalidOperationException("This work item already has an open execution. Cancel or terminally resolve it before preparing another package.");
        }

        var repositoryId = NormalizeRequired(request.RepositoryId, 300, "RepositoryId");
        var agentType = NormalizeRequired(request.AgentType, 100, "AgentType");
        var objective = NormalizeRequired(request.Objective, 12000, "Objective");
        var acceptanceCriteria = NormalizeValues(request.AcceptanceCriteria, 100, 1000);
        var authorityRefs = NormalizeValues(request.AuthorityRefs, 100, 500);
        var constraints = NormalizeValues(request.Constraints, 100, 1000);
        var allowedActions = NormalizeValues(request.AllowedActions, 100, 120);
        var requiredValidation = NormalizeValues(request.RequiredValidation, 100, 1000);
        var requiredCapabilities = NormalizeValues(request.RequiredCapabilities, 64, 100);
        if (acceptanceCriteria.Length == 0 || authorityRefs.Length == 0 || requiredValidation.Length == 0)
            throw new InvalidOperationException("Execution package requires acceptance criteria, authority references, and required validation.");

        var now = clock.UtcNow;
        var execution = new AgentExecution
        {
            Id = request.ExecutionId ?? Guid.NewGuid(),
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            WorkItemId = workItem.Id,
            ProjectId = projectId,
            RepositoryId = repositoryId,
            AgentType = agentType,
            RequiredCapabilitiesJson = JsonSerializer.Serialize(requiredCapabilities, JsonOptions),
            AllowedActionsJson = JsonSerializer.Serialize(allowedActions, JsonOptions),
            PackageContextVersion = AgentExecutionContract.Version,
            Priority = Math.Clamp(request.Priority, 0, 100),
            MaxAttempts = Math.Clamp(request.MaxAttempts, 1, 10),
            EligibleAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };

        SkillExecutionSnapshotResult? skillSnapshot = null;
        if (request.SkillResolutionId.HasValue)
        {
            skillSnapshot = await skillService.CreateExecutionSnapshotAsync(
                new SkillExecutionSnapshotCreateRequest(execution.Id, request.SkillResolutionId.Value, AgentExecutionContract.Version),
                cancellationToken);
            if (!string.Equals(skillSnapshot.ProjectId, projectId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(skillSnapshot.RepositoryId, execution.RepositoryId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(skillSnapshot.AgentType, execution.AgentType, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Skill resolution scope does not match the execution package.");
            }
            if (!requiredCapabilities.All(value => skillSnapshot.AvailableCapabilities.Contains(value, StringComparer.OrdinalIgnoreCase)) ||
                !allowedActions.ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .SetEquals(skillSnapshot.AllowedActions))
            {
                throw new InvalidOperationException("Skill resolution policy does not match the execution package capabilities or allowed actions.");
            }
            execution.SkillSnapshotJson = JsonSerializer.Serialize(skillSnapshot, JsonOptions);
        }

        resourceResolver.ValidateRequirements(request.ResourceRequirements, skillSnapshot);

        var package = new AgentExecutionPackage(
            AgentExecutionContract.Version,
            execution.Id,
            workItem.Id,
            projectId,
            execution.RepositoryId,
            objective,
            acceptanceCriteria,
            authorityRefs,
            constraints,
            allowedActions,
            requiredValidation,
            execution.AgentType,
            requiredCapabilities,
            AgentExecutionContract.Version,
            skillSnapshot,
            request.ResourceRequirements,
            request.ResourceRetryMode);
        execution.PackageJson = JsonSerializer.Serialize(package, JsonOptions);
        execution.PackageHash = Hash(execution.PackageJson);
        await dbContext.AgentExecutions.AddAsync(execution, cancellationToken);
        await AppendEventAsync(execution, AgentExecutionEventType.Prepared, actor.Username, new { request.IdempotencyKey }, cancellationToken);
        var result = Map(execution);
        await StoreOperationAsync(actor, execution.Id, "prepare", "manager", request.IdempotencyKey, requestHash, result, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return result;
    }

    public async Task<AgentExecutionClaimResult> ClaimNextAsync(AgentExecutionClaimRequest request, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.AgentExecutionsClaim);
        var projectId = NormalizeRequired(request.ProjectId, 200, "ProjectId");
        ActorAuthorization.EnsureProjectAllowed(actor, projectId, write: true);
        ValidateIdempotencyKey(request.IdempotencyKey);
        var agentId = NormalizeRequired(request.AgentId, 200, "AgentId");
        var requestHash = HashJson(request);
        if (await ReplayAsync<AgentExecutionClaimResult>(actor, "claim-next", agentId, request.IdempotencyKey, requestHash, cancellationToken) is { } replay)
        {
            return replay with { Replayed = true };
        }

        return await dbContext.ExecuteInTransactionAsync(async ct =>
        {
            await dbContext.AcquireTransactionLockAsync($"agent-execution-claim:{actor.TenantId}:{projectId}:{request.RepositoryId}", ct);
            if (await ReplayAsync<AgentExecutionClaimResult>(actor, "claim-next", agentId, request.IdempotencyKey, requestHash, ct) is { } lockedReplay)
            {
                return lockedReplay with { Replayed = true };
            }
            var now = clock.UtcNow;
            var scoped = Scope(dbContext.AgentExecutions, actor);
            var expired = await scoped.Where(x => ActiveStatuses.Contains(x.Status) && x.LeaseExpiresAt < now).ToListAsync(ct);
            foreach (var item in expired)
            {
                item.Status = AgentExecutionStatus.Expired;
                item.UpdatedAt = now;
                item.CompletedAt = now;
                await AppendEventAsync(item, AgentExecutionEventType.Expired, item.ClaimedByAgentId, new { item.LeaseVersion }, ct);
                ClearLease(item);
                if (item.Attempt < item.MaxAttempts)
                {
                    item.Attempt++;
                    item.Status = AgentExecutionStatus.Ready;
                    item.EligibleAt = now;
                    item.CompletedAt = null;
                }
            }

            var retryable = await scoped.Where(x => x.Status == AgentExecutionStatus.FailedRetryable && x.EligibleAt <= now && x.Attempt < x.MaxAttempts).ToListAsync(ct);
            foreach (var item in retryable)
            {
                item.Attempt++;
                item.Status = AgentExecutionStatus.Ready;
                item.CompletedAt = null;
                item.UpdatedAt = now;
                ClearLease(item);
            }
            await dbContext.SaveChangesAsync(ct);

            var capabilities = NormalizeValues(request.Capabilities, 128, 100).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var availableTools = NormalizeValues(request.AvailableTools, 128, 200);
            var candidates = await scoped
                .Where(x => x.ProjectId == projectId && x.RepositoryId == request.RepositoryId && x.Status == AgentExecutionStatus.Ready && x.EligibleAt <= now)
                .OrderBy(x => x.EligibleAt)
                .ThenBy(x => x.CreatedAt)
                .Take(5000)
                .ToListAsync(ct);
            var orderedCandidates = candidates
                .Where(x => string.Equals(x.AgentType, request.AgentType, StringComparison.OrdinalIgnoreCase) &&
                    DeserializeValues(x.RequiredCapabilitiesJson).All(capabilities.Contains))
                .OrderByDescending(x => x.Priority + Math.Min(100, (int)Math.Floor((now - x.EligibleAt).TotalHours)))
                .ThenBy(x => x.EligibleAt)
                .ThenBy(x => x.CreatedAt)
                .ToArray();
            AgentExecution? candidate = null;
            foreach (var item in orderedCandidates)
            {
                var skill = await RevalidateSkillsAsync(item, capabilities.ToArray(), availableTools, ct);
                if (skill is null || skill.Decision == SkillExecutionSnapshotDecision.Continue)
                {
                    candidate = item;
                    break;
                }

                if (skill.Decision is SkillExecutionSnapshotDecision.StopRevoked or SkillExecutionSnapshotDecision.RequiresHumanDecision)
                {
                    item.Status = AgentExecutionStatus.Blocked;
                    item.FailureClass = skill.Decision.ToString();
                    item.StructuredReasonJson = JsonSerializer.Serialize(skill.Issues, JsonOptions);
                    item.UpdatedAt = now;
                    item.CompletedAt = now;
                    await AppendEventAsync(item, AgentExecutionEventType.SkillSnapshotRevalidated, agentId, skill, ct);
                }
            }

            AgentExecutionClaimResult result;
            if (candidate is null)
            {
                result = new AgentExecutionClaimResult(false, "NoEligibleWork", null, null, false);
            }
            else
            {
                var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
                candidate.Status = AgentExecutionStatus.Claimed;
                candidate.ClaimedByAgentId = agentId;
                candidate.LeaseTokenHash = Hash(token);
                candidate.LeaseVersion++;
                candidate.LeaseExpiresAt = now.AddSeconds(NormalizeLeaseSeconds(request.LeaseSeconds));
                candidate.UpdatedAt = now;
                var package = DeserializePackage(candidate);
                var resolution = await resourceResolver.ResolveAsync(candidate, package, ct);
                if (resolution.Snapshot.Outcome != AgentExecutionResolutionOutcome.Resolved)
                {
                    candidate.Status = AgentExecutionStatus.Blocked;
                    candidate.FailureClass = $"ResourceResolution:{resolution.Snapshot.Outcome}";
                    candidate.StructuredReasonJson = JsonSerializer.Serialize(new
                    {
                        resolution.Snapshot.Id,
                        resolution.Snapshot.Outcome,
                        resolution.Snapshot.SnapshotHash
                    }, JsonOptions);
                    candidate.CompletedAt = now;
                    ClearLease(candidate);
                    await AppendEventAsync(candidate, AgentExecutionEventType.ResourceResolutionBlocked, agentId,
                        new { resolution.Snapshot.Id, resolution.Snapshot.Outcome, resolution.Snapshot.SnapshotHash }, ct);
                    result = new AgentExecutionClaimResult(false, resolution.Snapshot.Outcome.ToString(), Map(candidate, resolution.Snapshot), null, false, resolution.Snapshot, []);
                }
                else
                {
                    await AppendEventAsync(candidate, AgentExecutionEventType.ResourcesResolved, agentId,
                        new { resolution.Snapshot.Id, resolution.Snapshot.SnapshotHash, resolution.Snapshot.RetryMode }, ct);
                    await AppendEventAsync(candidate, AgentExecutionEventType.Claimed, agentId, new { candidate.LeaseVersion, candidate.LeaseExpiresAt }, ct);
                    result = new AgentExecutionClaimResult(true, "Claimed", Map(candidate, resolution.Snapshot), token, false,
                        resolution.Snapshot, resolution.CredentialCapabilities);
                }
            }
            await StoreOperationAsync(actor, candidate?.Id, "claim-next", agentId, request.IdempotencyKey, requestHash, result, ct);
            await dbContext.SaveChangesAsync(ct);
            return result;
        }, cancellationToken);
    }

    public async Task<AgentExecutionResult> ApproveResourceAsync(AgentExecutionResourceApprovalRequest request, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureAdminOrScopeAllowed(actor, SecurityScopes.AgentExecutionsManage);
        ValidateIdempotencyKey(request.IdempotencyKey);
        var requestHash = HashJson(request);
        if (await ReplayAsync<AgentExecutionResult>(actor, "approve-resource", "manager", request.IdempotencyKey, requestHash, cancellationToken) is { } replay)
            return replay;

        return await dbContext.ExecuteInTransactionAsync(async ct =>
        {
            await dbContext.AcquireTransactionLockAsync($"agent-execution-mutation:{actor.TenantId}:{request.ExecutionId}", ct);
            if (await ReplayAsync<AgentExecutionResult>(actor, "approve-resource", "manager", request.IdempotencyKey, requestHash, ct) is { } lockedReplay)
                return lockedReplay;

            var execution = await GetForMutationAsync(actor, request.ExecutionId, ct);
            if (execution.Status != AgentExecutionStatus.Blocked)
                throw new InvalidOperationException("Resource approval requires a blocked execution.");
            var package = DeserializePackage(execution);
            await resourceResolver.ApproveAsync(execution, package, request, ct);
            execution.Status = AgentExecutionStatus.Ready;
            execution.FailureClass = string.Empty;
            execution.StructuredReasonJson = "{}";
            execution.CompletedAt = null;
            execution.EligibleAt = clock.UtcNow;
            execution.UpdatedAt = clock.UtcNow;
            await AppendEventAsync(execution, AgentExecutionEventType.ResourceApprovalGranted, actor.Username,
                new { request.RequirementId }, ct);
            var result = Map(execution);
            await StoreOperationAsync(actor, execution.Id, "approve-resource", "manager", request.IdempotencyKey, requestHash, result, ct);
            await dbContext.SaveChangesAsync(ct);
            return result;
        }, cancellationToken);
    }

    public async Task<AgentExecutionResult?> GetAsync(Guid executionId, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.AgentExecutionsRead);
        var entity = await Scope(dbContext.AgentExecutions.AsNoTracking().Include(x => x.Events).Include(x => x.ResolutionSnapshots).ThenInclude(x => x.Items), actor).SingleOrDefaultAsync(x => x.Id == executionId, cancellationToken);
        if (entity is null) return null;
        ActorAuthorization.EnsureProjectAllowed(actor, entity.ProjectId, write: false);
        return Map(entity);
    }

    public async Task<IReadOnlyList<AgentExecutionResult>> ListAsync(AgentExecutionListRequest request, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.AgentExecutionsRead);
        var projectId = NormalizeRequired(request.ProjectId, 200, "ProjectId");
        ActorAuthorization.EnsureProjectAllowed(actor, projectId, write: false);
        var query = Scope(dbContext.AgentExecutions.AsNoTracking().Include(x => x.ResolutionSnapshots).ThenInclude(x => x.Items), actor).Where(x => x.ProjectId == projectId);
        if (request.Status.HasValue) query = query.Where(x => x.Status == request.Status.Value);
        return (await query.OrderByDescending(x => x.UpdatedAt).Skip(Math.Max(0, request.Offset)).Take(Math.Clamp(request.Limit, 1, 200)).ToListAsync(cancellationToken)).Select(x => Map(x)).ToArray();
    }

    public async Task<AgentExecutionDashboardResult> GetDashboardAsync(string projectId, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.AgentExecutionsRead);
        projectId = NormalizeRequired(projectId, 200, "ProjectId");
        ActorAuthorization.EnsureProjectAllowed(actor, projectId, write: false);
        var query = Scope(dbContext.AgentExecutions.AsNoTracking(), actor).Where(item => item.ProjectId == projectId);
        var now = clock.UtcNow;
        var groupedCounts = await query.GroupBy(item => item.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        var counts = Enum.GetValues<AgentExecutionStatus>().ToDictionary(status => status, _ => 0);
        foreach (var item in groupedCounts) counts[item.Status] = item.Count;
        var activeLeases = await query.CountAsync(item => ActiveStatuses.Contains(item.Status) && item.LeaseExpiresAt >= now, cancellationToken);
        var expiredLeases = await query.CountAsync(item => ActiveStatuses.Contains(item.Status) && item.LeaseExpiresAt < now, cancellationToken);
        var retryableFailures = await query.CountAsync(item => item.Status == AgentExecutionStatus.FailedRetryable, cancellationToken);
        var recent = (await Scope(dbContext.AgentExecutions.AsNoTracking().Include(x => x.ResolutionSnapshots).ThenInclude(x => x.Items), actor)
            .Where(item => item.ProjectId == projectId).OrderByDescending(item => item.UpdatedAt).Take(25).ToListAsync(cancellationToken)).Select(x => Map(x)).ToArray();
        return new AgentExecutionDashboardResult(
            ProjectContext.Normalize(projectId),
            counts,
            activeLeases,
            expiredLeases,
            retryableFailures,
            recent);
    }

    public Task<AgentExecutionMutationResult> HeartbeatAsync(AgentExecutionLeaseRequest request, CancellationToken cancellationToken)
        => MutateLeaseAsync(request.ExecutionId, request.AgentId, request.LeaseToken, request.LeaseVersion, request.LeaseSeconds,
            request.IdempotencyKey, "heartbeat", AgentExecutionEventType.Heartbeat, null,
            request.CurrentCapabilities, request.CurrentTools, cancellationToken);

    public Task<AgentExecutionMutationResult> CheckpointAsync(AgentExecutionCheckpointRequest request, CancellationToken cancellationToken)
        => MutateLeaseAsync(request.ExecutionId, request.AgentId, request.LeaseToken, request.LeaseVersion, request.LeaseSeconds,
            request.IdempotencyKey, "checkpoint", AgentExecutionEventType.Checkpoint,
            new { stage = NormalizeRequired(request.Stage, 100, "Stage"), summary = NormalizeRequired(request.Summary, 12000, "Summary"), evidenceRefs = NormalizeValues(request.EvidenceRefs, 100, 500) },
            request.CurrentCapabilities, request.CurrentTools, cancellationToken);

    public Task<AgentExecutionMutationResult> BlockAsync(AgentExecutionTerminalRequest request, CancellationToken cancellationToken)
        => MutateTerminalAsync(request, AgentExecutionStatus.Blocked, AgentExecutionEventType.Blocked, "block", cancellationToken);

    public Task<AgentExecutionMutationResult> CompleteAsync(AgentExecutionTerminalRequest request, CancellationToken cancellationToken)
        => MutateTerminalAsync(request, AgentExecutionStatus.Completed, AgentExecutionEventType.Completed, "complete", cancellationToken);

    public Task<AgentExecutionMutationResult> FailAsync(AgentExecutionTerminalRequest request, CancellationToken cancellationToken)
        => MutateTerminalAsync(request, request.Retryable ? AgentExecutionStatus.FailedRetryable : AgentExecutionStatus.FailedTerminal, AgentExecutionEventType.Failed, "fail", cancellationToken);

    public Task<AgentExecutionMutationResult> AbandonAsync(AgentExecutionTerminalRequest request, CancellationToken cancellationToken)
        => MutateTerminalAsync(request,
            request.Retryable ? AgentExecutionStatus.FailedRetryable : AgentExecutionStatus.Abandoned,
            AgentExecutionEventType.Abandoned,
            "abandon",
            cancellationToken);

    public async Task<AgentExecutionMutationResult> CancelAsync(AgentExecutionCancelRequest request, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureAdminOrScopeAllowed(actor, SecurityScopes.AgentExecutionsManage);
        ValidateIdempotencyKey(request.IdempotencyKey);
        var requestHash = HashJson(request);
        if (await ReplayAsync<AgentExecutionMutationResult>(actor, "cancel", "manager", request.IdempotencyKey, requestHash, cancellationToken) is { } replay)
            return replay with { Replayed = true };

        return await dbContext.ExecuteInTransactionAsync(async transactionCancellationToken =>
        {
            await dbContext.AcquireTransactionLockAsync($"agent-execution-mutation:{actor.TenantId}:{request.ExecutionId}", transactionCancellationToken);
            if (await ReplayAsync<AgentExecutionMutationResult>(actor, "cancel", "manager", request.IdempotencyKey, requestHash, transactionCancellationToken) is { } lockedReplay)
                return lockedReplay with { Replayed = true };

            var execution = await GetForMutationAsync(actor, request.ExecutionId, transactionCancellationToken);
            if (execution.Status is AgentExecutionStatus.Completed or AgentExecutionStatus.FailedTerminal or AgentExecutionStatus.Abandoned or AgentExecutionStatus.Expired or AgentExecutionStatus.Cancelled)
                throw new InvalidOperationException("Only a non-terminal execution may be cancelled.");

            var now = clock.UtcNow;
            execution.Status = AgentExecutionStatus.Cancelled;
            execution.FailureClass = NormalizeOptional(request.ReasonClass, 200);
            execution.StructuredReasonJson = JsonSerializer.Serialize(new
            {
                reasonClass = execution.FailureClass,
                reason = NormalizeOptional(request.Reason, 12000),
                evidenceRefs = NormalizeValues(request.EvidenceRefs, 100, 500)
            }, JsonOptions);
            execution.UpdatedAt = now;
            execution.CompletedAt = now;
            ClearLease(execution);
            await AppendEventAsync(execution, AgentExecutionEventType.Cancelled, actor.Username,
                JsonSerializer.Deserialize<JsonElement>(execution.StructuredReasonJson), transactionCancellationToken);
            var result = new AgentExecutionMutationResult(Map(execution), AgentExecutionStatus.Cancelled.ToString(), null, [], false);
            await StoreOperationAsync(actor, execution.Id, "cancel", "manager", request.IdempotencyKey, requestHash, result, transactionCancellationToken);
            await dbContext.SaveChangesAsync(transactionCancellationToken);
            return result;
        }, cancellationToken);
    }

    private async Task<AgentExecutionMutationResult> MutateLeaseAsync(
        Guid executionId, string agentId, string leaseToken, long leaseVersion, int leaseSeconds, string idempotencyKey,
        string operation, AgentExecutionEventType eventType, object? payload,
        IReadOnlyList<string>? currentCapabilities, IReadOnlyList<string>? currentTools,
        CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.AgentExecutionsWrite);
        ValidateIdempotencyKey(idempotencyKey);
        var normalizedCapabilities = NormalizeValues(currentCapabilities, 128, 100);
        var normalizedTools = NormalizeValues(currentTools, 128, 200);
        var requestShape = new
        {
            executionId,
            agentId,
            leaseTokenHash = Hash(leaseToken),
            leaseVersion,
            leaseSeconds,
            payload,
            currentCapabilities = normalizedCapabilities,
            currentTools = normalizedTools
        };
        var requestHash = HashJson(requestShape);
        if (await ReplayAsync<AgentExecutionMutationResult>(actor, operation, agentId, idempotencyKey, requestHash, cancellationToken) is { } replay)
            return replay with { Replayed = true };

        return await dbContext.ExecuteInTransactionAsync(async transactionCancellationToken =>
        {
            await dbContext.AcquireTransactionLockAsync($"agent-execution-mutation:{actor.TenantId}:{executionId}", transactionCancellationToken);
            if (await ReplayAsync<AgentExecutionMutationResult>(actor, operation, agentId, idempotencyKey, requestHash, transactionCancellationToken) is { } lockedReplay)
                return lockedReplay with { Replayed = true };

            var execution = await GetForMutationAsync(actor, executionId, transactionCancellationToken);
            ValidateLease(execution, agentId, leaseToken, leaseVersion);
            var package = DeserializePackage(execution);
            var resourceOutcome = await resourceResolver.RevalidateAsync(execution, package, transactionCancellationToken);
            var skill = resourceOutcome == AgentExecutionResolutionOutcome.Resolved
                ? await RevalidateSkillsAsync(
                execution,
                normalizedCapabilities,
                normalizedTools,
                transactionCancellationToken)
                : null;
            if (resourceOutcome != AgentExecutionResolutionOutcome.Resolved)
            {
                execution.Status = AgentExecutionStatus.Blocked;
                execution.FailureClass = $"ResourceRevalidation:{resourceOutcome}";
                execution.StructuredReasonJson = JsonSerializer.Serialize(new { outcome = resourceOutcome }, JsonOptions);
                execution.UpdatedAt = clock.UtcNow;
                execution.CompletedAt = clock.UtcNow;
                ClearLease(execution);
                await AppendEventAsync(execution, AgentExecutionEventType.ResourceResolutionBlocked, agentId,
                    new { outcome = resourceOutcome, phase = operation }, transactionCancellationToken);
            }
            else if (skill is { Decision: not SkillExecutionSnapshotDecision.Continue })
            {
                execution.Status = AgentExecutionStatus.Blocked;
                execution.FailureClass = skill.Decision.ToString();
                execution.StructuredReasonJson = JsonSerializer.Serialize(skill.Issues, JsonOptions);
                execution.UpdatedAt = clock.UtcNow;
                execution.CompletedAt = clock.UtcNow;
                ClearLease(execution);
                await AppendEventAsync(execution, AgentExecutionEventType.SkillSnapshotRevalidated, agentId, skill, transactionCancellationToken);
            }
            else
            {
                var wasClaimed = execution.Status == AgentExecutionStatus.Claimed;
                execution.Status = AgentExecutionStatus.Running;
                execution.StartedAt ??= clock.UtcNow;
                execution.LeaseExpiresAt = clock.UtcNow.AddSeconds(NormalizeLeaseSeconds(leaseSeconds));
                execution.UpdatedAt = clock.UtcNow;
                if (wasClaimed)
                    await AppendEventAsync(execution, AgentExecutionEventType.Started, agentId, new { execution.LeaseVersion }, transactionCancellationToken);
                await AppendEventAsync(execution, eventType, agentId, payload ?? new { execution.LeaseVersion }, transactionCancellationToken);
            }
            var blockedOutcome = resourceOutcome != AgentExecutionResolutionOutcome.Resolved ? "BlockedByResourceAuthority" : "BlockedBySkillPolicy";
            var result = new AgentExecutionMutationResult(Map(execution), execution.Status == AgentExecutionStatus.Blocked ? blockedOutcome : operation, skill?.Decision, skill?.Issues ?? [], false);
            await StoreOperationAsync(actor, execution.Id, operation, agentId, idempotencyKey, requestHash, result, transactionCancellationToken);
            await dbContext.SaveChangesAsync(transactionCancellationToken);
            return result;
        }, cancellationToken);
    }

    private async Task<AgentExecutionMutationResult> MutateTerminalAsync(
        AgentExecutionTerminalRequest request, AgentExecutionStatus status, AgentExecutionEventType eventType, string operation, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.AgentExecutionsWrite);
        ValidateTerminalRequest(request, operation);
        ValidateIdempotencyKey(request.IdempotencyKey);
        var requestHash = HashJson(request with { LeaseToken = Hash(request.LeaseToken) });
        if (await ReplayAsync<AgentExecutionMutationResult>(actor, operation, request.AgentId, request.IdempotencyKey, requestHash, cancellationToken) is { } replay)
            return replay with { Replayed = true };

        return await dbContext.ExecuteInTransactionAsync(async transactionCancellationToken =>
        {
            await dbContext.AcquireTransactionLockAsync($"agent-execution-mutation:{actor.TenantId}:{request.ExecutionId}", transactionCancellationToken);
            if (await ReplayAsync<AgentExecutionMutationResult>(actor, operation, request.AgentId, request.IdempotencyKey, requestHash, transactionCancellationToken) is { } lockedReplay)
                return lockedReplay with { Replayed = true };

            var execution = await GetForMutationAsync(actor, request.ExecutionId, transactionCancellationToken);
            ValidateLease(execution, request.AgentId, request.LeaseToken, request.LeaseVersion);
            var package = DeserializePackage(execution);
            var resourceOutcome = operation == "complete"
                ? await resourceResolver.RevalidateAsync(execution, package, transactionCancellationToken)
                : AgentExecutionResolutionOutcome.Resolved;
            if (resourceOutcome != AgentExecutionResolutionOutcome.Resolved)
            {
                execution.Status = AgentExecutionStatus.Blocked;
                execution.FailureClass = $"ResourceRevalidation:{resourceOutcome}";
                execution.StructuredReasonJson = JsonSerializer.Serialize(new { outcome = resourceOutcome }, JsonOptions);
                execution.UpdatedAt = clock.UtcNow;
                execution.CompletedAt = clock.UtcNow;
                ClearLease(execution);
                await AppendEventAsync(execution, AgentExecutionEventType.ResourceResolutionBlocked, request.AgentId,
                    new { outcome = resourceOutcome, phase = operation }, transactionCancellationToken);
                var blocked = new AgentExecutionMutationResult(Map(execution), "BlockedByResourceAuthority", null, [], false);
                await StoreOperationAsync(actor, execution.Id, operation, request.AgentId, request.IdempotencyKey, requestHash, blocked, transactionCancellationToken);
                await dbContext.SaveChangesAsync(transactionCancellationToken);
                return blocked;
            }
            var skill = operation == "complete"
                ? await RevalidateSkillsAsync(
                    execution,
                    NormalizeValues(request.CurrentCapabilities, 128, 100),
                    NormalizeValues(request.CurrentTools, 128, 200),
                    transactionCancellationToken)
                : null;
            if (skill is { Decision: not SkillExecutionSnapshotDecision.Continue })
            {
                execution.Status = AgentExecutionStatus.Blocked;
                execution.FailureClass = skill.Decision.ToString();
                execution.StructuredReasonJson = JsonSerializer.Serialize(skill.Issues, JsonOptions);
                execution.UpdatedAt = clock.UtcNow;
                execution.CompletedAt = clock.UtcNow;
                ClearLease(execution);
                await AppendEventAsync(execution, AgentExecutionEventType.SkillSnapshotRevalidated, request.AgentId, skill, transactionCancellationToken);
                var blocked = new AgentExecutionMutationResult(Map(execution), "BlockedBySkillPolicy", skill.Decision, skill.Issues, false);
                await StoreOperationAsync(actor, execution.Id, operation, request.AgentId, request.IdempotencyKey, requestHash, blocked, transactionCancellationToken);
                await dbContext.SaveChangesAsync(transactionCancellationToken);
                return blocked;
            }

            var now = clock.UtcNow;
            execution.Status = status;
            execution.FailureClass = NormalizeOptional(request.ReasonClass, 200);
            execution.StructuredReasonJson = JsonSerializer.Serialize(new
            {
                reasonClass = execution.FailureClass,
                reason = NormalizeOptional(request.Reason, 12000),
                evidenceRefs = NormalizeValues(request.EvidenceRefs, 100, 500),
                retryable = request.Retryable
            }, JsonOptions);
            execution.UpdatedAt = now;
            execution.CompletedAt = now;
            if (status == AgentExecutionStatus.FailedRetryable && execution.Attempt < execution.MaxAttempts)
                execution.EligibleAt = now.AddSeconds(Math.Min(300, 10 * execution.Attempt));
            else if (status == AgentExecutionStatus.FailedRetryable)
                execution.Status = AgentExecutionStatus.FailedTerminal;
            ClearLease(execution);
            await AppendEventAsync(execution, eventType, request.AgentId, JsonSerializer.Deserialize<JsonElement>(execution.StructuredReasonJson), transactionCancellationToken);
            var result = new AgentExecutionMutationResult(Map(execution), execution.Status.ToString(), null, [], false);
            await StoreOperationAsync(actor, execution.Id, operation, request.AgentId, request.IdempotencyKey, requestHash, result, transactionCancellationToken);
            await dbContext.SaveChangesAsync(transactionCancellationToken);
            return result;
        }, cancellationToken);
    }

    private async Task<SkillExecutionSnapshotRevalidationResult?> RevalidateSkillsAsync(
        AgentExecution execution,
        IReadOnlyList<string> currentCapabilities,
        IReadOnlyList<string> currentTools,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(execution.SkillSnapshotJson)) return null;
        var snapshot = JsonSerializer.Deserialize<SkillExecutionSnapshotResult>(execution.SkillSnapshotJson, JsonOptions)
            ?? throw new InvalidOperationException("Persisted Skill execution snapshot is invalid.");
        return await skillService.RevalidateExecutionSnapshotAsync(new SkillExecutionSnapshotRevalidateRequest(
            snapshot,
            execution.PackageContextVersion,
            currentCapabilities,
            currentTools,
            DeserializeValues(execution.AllowedActionsJson),
            snapshot.MaximumRisk), cancellationToken);
    }

    private async Task<AgentExecution> GetForMutationAsync(ContextHubRequestActor actor, Guid executionId, CancellationToken cancellationToken)
    {
        var execution = await Scope(dbContext.AgentExecutions.Include(x => x.ResolutionSnapshots).ThenInclude(x => x.Items), actor).SingleOrDefaultAsync(x => x.Id == executionId, cancellationToken)
            ?? throw new InvalidOperationException($"Agent execution '{executionId}' was not found.");
        ActorAuthorization.EnsureProjectAllowed(actor, execution.ProjectId, write: true);
        return execution;
    }

    private void ValidateLease(AgentExecution execution, string agentId, string leaseToken, long leaseVersion)
    {
        if (!ActiveStatuses.Contains(execution.Status)) throw new InvalidOperationException("Execution does not have an active lease.");
        if (!string.Equals(execution.ClaimedByAgentId, agentId, StringComparison.Ordinal)) throw new UnauthorizedAccessException("Execution lease belongs to another agent.");
        if (execution.LeaseVersion != leaseVersion) throw new UnauthorizedAccessException("Stale execution lease version.");
        if (execution.LeaseExpiresAt < clock.UtcNow) throw new UnauthorizedAccessException("Execution lease has expired.");
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(execution.LeaseTokenHash), Convert.FromHexString(Hash(leaseToken))))
            throw new UnauthorizedAccessException("Invalid execution lease token.");
    }

    private async Task AppendEventAsync(AgentExecution execution, AgentExecutionEventType eventType, string agentId, object payload, CancellationToken cancellationToken)
    {
        var trackedSequence = execution.Events.Count == 0 ? 0 : execution.Events.Max(x => x.Sequence);
        var persistedSequence = await dbContext.AgentExecutionEvents.Where(x => x.ExecutionId == execution.Id).Select(x => (long?)x.Sequence).MaxAsync(cancellationToken) ?? 0;
        var entry = new AgentExecutionEvent
        {
            ExecutionId = execution.Id,
            EventType = eventType,
            Sequence = Math.Max(trackedSequence, persistedSequence) + 1,
            AgentId = NormalizeOptional(agentId, 200),
            PayloadJson = JsonSerializer.Serialize(payload, JsonOptions),
            CreatedAt = clock.UtcNow
        };
        execution.Events.Add(entry);
        await dbContext.AgentExecutionEvents.AddAsync(entry, cancellationToken);
    }

    private async Task<T?> ReplayAsync<T>(ContextHubRequestActor actor, string operation, string agentId, string key, string requestHash, CancellationToken cancellationToken)
    {
        var existing = await dbContext.AgentExecutionOperations.AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == actor.TenantId && x.AgentId == agentId && x.Operation == operation && x.IdempotencyKey == key, cancellationToken);
        if (existing is null) return default;
        if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
            throw new InvalidOperationException("Idempotency key was already used with a different request.");
        return JsonSerializer.Deserialize<T>(secretProtector.Unprotect(existing.ProtectedResultJson), JsonOptions)
            ?? throw new InvalidOperationException("Persisted idempotent execution result is invalid.");
    }

    private async Task StoreOperationAsync<T>(ContextHubRequestActor actor, Guid? executionId, string operation, string agentId, string key, string requestHash, T result, CancellationToken cancellationToken)
        => await dbContext.AgentExecutionOperations.AddAsync(new AgentExecutionOperation
        {
            TenantId = actor.TenantId,
            ExecutionId = executionId,
            AgentId = agentId,
            Operation = operation,
            IdempotencyKey = key,
            RequestHash = requestHash,
            ProtectedResultJson = secretProtector.Protect(JsonSerializer.Serialize(result, JsonOptions)),
            CreatedAt = clock.UtcNow
        }, cancellationToken);

    private static IQueryable<T> Scope<T>(IQueryable<T> query, ContextHubRequestActor actor) where T : class
        => typeof(T) == typeof(AgentExecution)
            ? (IQueryable<T>)(object)(actor.HasUser
                ? ((IQueryable<AgentExecution>)(object)query).Where(x => x.TenantId == actor.TenantId && (actor.IsServiceActor || x.OwnerUserId == actor.UserId))
                : (IQueryable<AgentExecution>)(object)query)
            : typeof(T) == typeof(ProjectWorkItem)
                ? (IQueryable<T>)(object)(actor.HasUser
                    ? ((IQueryable<ProjectWorkItem>)(object)query).Where(x => x.TenantId == actor.TenantId && (actor.IsServiceActor || x.OwnerUserId == actor.UserId))
                    : (IQueryable<ProjectWorkItem>)(object)query)
                : query;

    private static AgentExecutionResult Map(AgentExecution entity, AgentExecutionResolutionSnapshotResult? resolutionSnapshot = null)
        => new(entity.Id, entity.WorkItemId, entity.ParentExecutionId, entity.ProjectId, entity.RepositoryId, entity.AgentType,
            entity.Status, entity.Priority, entity.Attempt, entity.MaxAttempts, entity.ClaimedByAgentId, entity.LeaseVersion,
            entity.LeaseExpiresAt, entity.FailureClass, entity.StructuredReasonJson, entity.PackageHash,
            JsonSerializer.Deserialize<AgentExecutionPackage>(entity.PackageJson, JsonOptions)
                ?? throw new InvalidOperationException("Persisted execution package is invalid."),
            entity.CreatedAt, entity.UpdatedAt, entity.StartedAt, entity.CompletedAt,
            entity.Events.OrderBy(x => x.Sequence).Select(x => new AgentExecutionEventResult(x.Id, x.EventType, x.Sequence, x.AgentId, x.PayloadJson, x.CreatedAt)).ToArray(),
            resolutionSnapshot ?? entity.ResolutionSnapshots.OrderByDescending(x => x.Attempt).ThenByDescending(x => x.ResolutionSequence)
                .Select(AgentExecutionResourceResolver.MapSnapshot).FirstOrDefault());

    private static AgentExecutionPackage DeserializePackage(AgentExecution execution)
        => JsonSerializer.Deserialize<AgentExecutionPackage>(execution.PackageJson, JsonOptions)
            ?? throw new InvalidOperationException("Persisted execution package is invalid.");

    private static void ClearLease(AgentExecution execution)
    {
        execution.ClaimedByAgentId = string.Empty;
        execution.LeaseTokenHash = string.Empty;
        execution.LeaseExpiresAt = null;
    }

    private static int NormalizeLeaseSeconds(int value)
        => Math.Clamp(value <= 0 ? AgentExecutionContract.DefaultLeaseSeconds : value, 30, AgentExecutionContract.MaximumLeaseSeconds);

    private static string[] NormalizeValues(IReadOnlyList<string>? values, int maxCount, int maxLength)
    {
        var normalized = (values ?? []).Select(Normalize).Where(x => x.Length > 0).ToArray();
        if (normalized.Length > maxCount || normalized.Any(value => value.Length > maxLength))
            throw new InvalidOperationException($"List values must contain at most {maxCount} entries of at most {maxLength} characters each.");
        return normalized.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string[] DeserializeValues(string json)
        => JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? [];

    private static void ValidateTerminalRequest(AgentExecutionTerminalRequest request, string operation)
    {
        NormalizeRequired(request.AgentId, 200, "AgentId");
        NormalizeRequired(request.LeaseToken, 500, "LeaseToken");
        NormalizeRequired(request.ReasonClass, 200, "ReasonClass");
        NormalizeRequired(request.Reason, 12000, "Reason");
        if (operation == "complete" && (request.EvidenceRefs is null || NormalizeValues(request.EvidenceRefs, 100, 500).Length == 0))
            throw new InvalidOperationException("Completion requires at least one bounded evidence reference.");
        if (operation == "complete" && request.Retryable)
            throw new InvalidOperationException("A completed execution cannot be marked retryable.");
    }

    private static string NormalizeRequired(string? value, int maxLength, string name)
    {
        var normalized = Normalize(value);
        if (normalized.Length == 0 || normalized.Length > maxLength) throw new InvalidOperationException($"{name} is required and must not exceed {maxLength} characters.");
        return normalized;
    }

    private static string NormalizeOptional(string? value, int maxLength) => Normalize(value).Length <= maxLength ? Normalize(value) : Normalize(value)[..maxLength];
    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;

    private static void ValidateIdempotencyKey(string value) => NormalizeRequired(value, 200, "IdempotencyKey");
    private static string HashJson<T>(T value) => Hash(JsonSerializer.Serialize(value, JsonOptions));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
