using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Memory.Application;

public sealed record AgentExecutionResourceResolution(
    AgentExecutionResolutionSnapshotResult Snapshot,
    IReadOnlyList<AgentExecutionCredentialCapability> CredentialCapabilities);

public interface IAgentExecutionResourceResolver
{
    void ValidateRequirements(IReadOnlyList<AgentExecutionResourceRequirement>? requirements, SkillExecutionSnapshotResult? skillSnapshot);
    Task<AgentExecutionResourceResolution> ResolveAsync(AgentExecution execution, AgentExecutionPackage package, CancellationToken cancellationToken);
    Task<AgentExecutionResolutionOutcome> RevalidateAsync(AgentExecution execution, AgentExecutionPackage package, CancellationToken cancellationToken);
    Task ApproveAsync(AgentExecution execution, AgentExecutionPackage package, AgentExecutionResourceApprovalRequest request, CancellationToken cancellationToken);
}

public sealed class AgentExecutionResourceResolver(
    IApplicationDbContext dbContext,
    IRequestActorAccessor actorAccessor,
    IClock clock,
    IManagedFileService managedFiles,
    IPlatformFoundationStore foundation,
    IStepUpAuthenticationService stepUp,
    IOptions<SecretManagementOptions> secretOptions) : IAgentExecutionResourceResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public void ValidateRequirements(IReadOnlyList<AgentExecutionResourceRequirement>? requirements, SkillExecutionSnapshotResult? skillSnapshot)
    {
        var values = requirements ?? [];
        if (values.Count > 100) throw new InvalidOperationException("Execution package supports at most 100 logical resource requirements.");
        if (values.Select(x => x.RequirementId).Distinct().Count() != values.Count || values.Any(x => x.RequirementId == Guid.Empty || x.LogicalResourceId == Guid.Empty))
            throw new InvalidOperationException("Resource requirement identities must be non-empty and unique.");
        foreach (var requirement in values)
        {
            if (requirement.AuthorityRevision < 0) throw new InvalidOperationException("Resource authority revision cannot be negative.");
            if (!string.IsNullOrWhiteSpace(requirement.ExpectedIntegrityIdentity) && !IsSha256(requirement.ExpectedIntegrityIdentity))
                throw new InvalidOperationException("Resource integrity identity must be a SHA-256 value when supplied.");
            ValidateLogicalLabel(requirement.Purpose, "Resource purpose", 200);
            ValidateLogicalLabel(requirement.PolicyRevision, "Resource policy revision", 200);
            if (requirement.Kind == AgentExecutionResourceKind.File && requirement.ResolutionMode == AgentExecutionResourceResolutionMode.Exact &&
                (!requirement.RequestedVersionId.HasValue || !IsSha256(requirement.ExpectedIntegrityIdentity)))
                throw new InvalidOperationException("Exact file requirements require FileVersionId and SHA-256 integrity identity.");
            if (requirement.Kind == AgentExecutionResourceKind.Credential && string.IsNullOrWhiteSpace(requirement.Purpose))
                throw new InvalidOperationException("Credential requirements require a bounded logical Purpose value.");
            if (requirement.Kind == AgentExecutionResourceKind.Skill)
            {
                var pin = skillSnapshot?.PinnedVersions.SingleOrDefault(x => x.SkillId == requirement.LogicalResourceId);
                if (pin is null || (requirement.RequestedVersionId.HasValue && pin.SkillVersionId != requirement.RequestedVersionId.Value) ||
                    (!string.IsNullOrWhiteSpace(requirement.ExpectedIntegrityIdentity) && !FixedEquals(pin.ContentHash, NormalizeHash(requirement.ExpectedIntegrityIdentity))))
                    throw new InvalidOperationException("Skill requirements must reuse an exact version/contentHash from the existing Skill snapshot.");
            }
        }
    }

    public async Task<AgentExecutionResourceResolution> ResolveAsync(AgentExecution execution, AgentExecutionPackage package, CancellationToken cancellationToken)
    {
        var requirements = package.ResourceRequirements ?? [];
        ValidateRequirements(requirements, package.SkillSnapshot);
        var previous = package.ResourceRetryMode == AgentExecutionResourceRetryMode.ReuseSnapshot && execution.Attempt > 1
            ? await dbContext.AgentExecutionResolutionSnapshots.AsNoTracking().Include(x => x.Items)
                .Where(x => x.ExecutionId == execution.Id && x.Attempt < execution.Attempt && x.Outcome == AgentExecutionResolutionOutcome.Resolved)
                .OrderByDescending(x => x.Attempt).ThenByDescending(x => x.ResolutionSequence).FirstOrDefaultAsync(cancellationToken)
            : null;
        var sequence = (await dbContext.AgentExecutionResolutionSnapshots
            .Where(x => x.ExecutionId == execution.Id && x.Attempt == execution.Attempt)
            .Select(x => (int?)x.ResolutionSequence).MaxAsync(cancellationToken) ?? 0) + 1;
        var now = clock.UtcNow;
        var items = new List<AgentExecutionResolutionItem>();
        var capabilities = new List<AgentExecutionCredentialCapability>();
        foreach (var requirement in requirements)
        {
            var prior = previous?.Items.SingleOrDefault(x => x.RequirementId == requirement.RequirementId);
            var item = await ResolveOneAsync(execution, package, requirement, prior, capabilities, cancellationToken);
            items.Add(item);
        }
        var outcome = items.Where(x => x.Outcome != AgentExecutionResolutionOutcome.Resolved)
            .Select(x => x.Outcome).DefaultIfEmpty(AgentExecutionResolutionOutcome.Resolved).Max();
        var authorityContextHash = Hash(JsonSerializer.Serialize(items.Select(x => new
        {
            x.RequirementId,
            x.Kind,
            x.LogicalResourceId,
            x.ResolvedVersionId,
            x.IntegrityIdentity,
            x.AuthorityRevision,
            x.PolicyRevision,
            x.Outcome
        }).OrderBy(x => x.RequirementId), JsonOptions));
        var snapshot = new AgentExecutionResolutionSnapshot
        {
            ExecutionId = execution.Id,
            Attempt = execution.Attempt,
            ResolutionSequence = sequence,
            RetryMode = package.ResourceRetryMode,
            Outcome = outcome,
            AuthorityContextHash = authorityContextHash,
            EvidenceRefsJson = JsonSerializer.Serialize(items.SelectMany(x => Deserialize(x.EvidenceRefsJson)).Distinct().ToArray(), JsonOptions),
            ResolvedAt = now,
            Items = items
        };
        foreach (var item in items) item.SnapshotId = snapshot.Id;
        snapshot.SnapshotHash = Hash(JsonSerializer.Serialize(new
        {
            snapshot.ExecutionId,
            snapshot.Attempt,
            snapshot.ResolutionSequence,
            snapshot.RetryMode,
            snapshot.Outcome,
            snapshot.AuthorityContextHash,
            items = items.OrderBy(x => x.RequirementId).Select(MapStable)
        }, JsonOptions));
        await dbContext.AgentExecutionResolutionSnapshots.AddAsync(snapshot, cancellationToken);
        return new(Map(snapshot), capabilities);
    }

    public async Task<AgentExecutionResolutionOutcome> RevalidateAsync(AgentExecution execution, AgentExecutionPackage package, CancellationToken cancellationToken)
    {
        var snapshot = await dbContext.AgentExecutionResolutionSnapshots.AsNoTracking().Include(x => x.Items)
            .Where(x => x.ExecutionId == execution.Id && x.Attempt == execution.Attempt && x.Outcome == AgentExecutionResolutionOutcome.Resolved)
            .OrderByDescending(x => x.ResolutionSequence).FirstOrDefaultAsync(cancellationToken);
        if ((package.ResourceRequirements?.Count ?? 0) == 0 && package.SkillSnapshot is null) return AgentExecutionResolutionOutcome.Resolved;
        if (snapshot is null) return AgentExecutionResolutionOutcome.Denied;
        foreach (var item in snapshot.Items)
        {
            if (!await IsCurrentAsync(execution, item, cancellationToken)) return AgentExecutionResolutionOutcome.Denied;
        }
        return AgentExecutionResolutionOutcome.Resolved;
    }

    public async Task ApproveAsync(AgentExecution execution, AgentExecutionPackage package, AgentExecutionResourceApprovalRequest request, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureAdminOrScopeAllowed(actor, SecurityScopes.AgentExecutionsManage);
        if (!actor.IsInteractiveUser || !actor.UserId.HasValue) throw new UnauthorizedAccessException("Resource approval requires an interactive human session.");
        var requirement = (package.ResourceRequirements ?? []).SingleOrDefault(x => x.RequirementId == request.RequirementId && x.Kind == AgentExecutionResourceKind.Credential)
            ?? throw new InvalidOperationException("Credential resource requirement is unavailable.");
        var secret = await VisibleSecrets(actor).AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == requirement.LogicalResourceId && x.ProjectId == execution.ProjectId && x.State == SecretState.Active, cancellationToken)
            ?? throw new UnauthorizedAccessException("Resource is unavailable.");
        var decision = await stepUp.AuthorizeAsync(StepUpOperationClass.SecretUseLeaseCreate, "secret:lease:create", "Secret", secret.Id.ToString("D"), request.StepUp, cancellationToken);
        if (decision.Outcome != StepUpRequirementOutcome.Allowed) throw new StepUpRequiredException(decision);
        var now = clock.UtcNow;
        var active = await dbContext.AgentExecutionResourceApprovals.SingleOrDefaultAsync(
            x => x.ExecutionId == execution.Id && x.RequirementId == requirement.RequirementId && x.Attempt == execution.Attempt && x.Status == AgentExecutionResourceApprovalStatus.Active, cancellationToken);
        if (active is null)
        {
            active = new AgentExecutionResourceApproval
            {
                ExecutionId = execution.Id,
                RequirementId = requirement.RequirementId,
                Attempt = execution.Attempt,
                ApprovedByUserId = actor.UserId.Value,
                AssertionId = request.StepUp.AssertionId,
                AuthorityRevision = secret.Revision,
                PolicyRevision = requirement.PolicyRevision?.Trim() ?? "secret-policy-v1",
                ExpiresAt = decision.AssertionExpiresAt ?? now.AddMinutes(5),
                CreatedAt = now,
                UpdatedAt = now
            };
            await dbContext.AgentExecutionResourceApprovals.AddAsync(active, cancellationToken);
        }
        else
        {
            active.ApprovedByUserId = actor.UserId.Value;
            active.AssertionId = request.StepUp.AssertionId;
            active.AuthorityRevision = secret.Revision;
            active.ExpiresAt = decision.AssertionExpiresAt ?? now.AddMinutes(5);
            active.UpdatedAt = now;
        }
    }

    private async Task<AgentExecutionResolutionItem> ResolveOneAsync(
        AgentExecution execution,
        AgentExecutionPackage package,
        AgentExecutionResourceRequirement requirement,
        AgentExecutionResolutionItem? prior,
        List<AgentExecutionCredentialCapability> capabilities,
        CancellationToken cancellationToken)
        => requirement.Kind switch
        {
            AgentExecutionResourceKind.File => await ResolveFileAsync(execution, requirement, prior, cancellationToken),
            AgentExecutionResourceKind.Credential => await ResolveCredentialAsync(execution, requirement, prior, capabilities, cancellationToken),
            AgentExecutionResourceKind.ConnectionProfile => await ResolveConnectionAsync(execution, requirement, prior, cancellationToken),
            AgentExecutionResourceKind.Skill => ResolveSkill(package, requirement),
            _ => Denied(requirement, "ResourceUnavailable")
        };

    private async Task<AgentExecutionResolutionItem> ResolveFileAsync(AgentExecution execution, AgentExecutionResourceRequirement requirement, AgentExecutionResolutionItem? prior, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        var query = dbContext.FileVersions.AsNoTracking().Include(x => x.FileAsset)
            .Where(x => x.FileAssetId == requirement.LogicalResourceId && x.FileAsset!.ProjectId == execution.ProjectId &&
                        x.FileAsset.State == FileAssetState.Active && x.Lifecycle == FileVersionLifecycle.Ready);
        if (actor.HasUser)
            query = actor.IsServiceActor ? query.Where(x => x.FileAsset!.TenantId == actor.TenantId) : query.Where(x => x.FileAsset!.TenantId == actor.TenantId && x.FileAsset.OwnerUserId == actor.UserId);
        Guid? requested = prior?.ResolvedVersionId ?? (requirement.ResolutionMode == AgentExecutionResourceResolutionMode.Exact ? requirement.RequestedVersionId : null);
        if (requested.HasValue) query = query.Where(x => x.Id == requested.Value);
        var version = requested.HasValue
            ? await query.SingleOrDefaultAsync(cancellationToken)
            : await query.OrderByDescending(x => x.VersionNumber).FirstOrDefaultAsync(cancellationToken);
        if (version is null) return Denied(requirement, "ResourceUnavailable");
        var expected = prior?.IntegrityIdentity ?? requirement.ExpectedIntegrityIdentity;
        if (!string.IsNullOrWhiteSpace(expected) && !FixedEquals(version.ContentSha256, NormalizeHash(expected))) return Denied(requirement, "ResourceUnavailable");
        var decision = await managedFiles.AuthorizeOperationAsync(version.Id, FileOperation.Read, requirement.Purpose ?? "agent-execution", cancellationToken);
        if (!decision.Allowed || version.Classification == FileClassification.Quarantined) return Denied(requirement, "ResourceUnavailable");
        return Item(requirement, version.Id, version.ContentSha256, Math.Max(version.FileAsset!.Revision, version.ClassificationRevision), version.SecurityPolicyVersion, "Resolved", $"file:{version.Id:D}");
    }

    private async Task<AgentExecutionResolutionItem> ResolveCredentialAsync(
        AgentExecution execution,
        AgentExecutionResourceRequirement requirement,
        AgentExecutionResolutionItem? prior,
        List<AgentExecutionCredentialCapability> capabilities,
        CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        var secret = await VisibleSecrets(actor).Include(x => x.Versions).SingleOrDefaultAsync(
            x => x.Id == requirement.LogicalResourceId && x.ProjectId == execution.ProjectId && x.State == SecretState.Active, cancellationToken);
        if (secret?.CurrentVersionId is null) return Denied(requirement, "ResourceUnavailable");
        var version = secret.Versions.SingleOrDefault(x => x.Id == (prior?.ResolvedVersionId ?? secret.CurrentVersionId.Value));
        if (version is null || version.State != SecretVersionState.Active || version.ExpiresAt <= clock.UtcNow ||
            (prior is not null && secret.CurrentVersionId != prior.ResolvedVersionId)) return Denied(requirement, "ResourceUnavailable");
        if (!await SecretUseAllowedAsync(actor, secret, cancellationToken)) return Denied(requirement, "ResourceUnavailable");
        var approval = await dbContext.AgentExecutionResourceApprovals.SingleOrDefaultAsync(x =>
            x.ExecutionId == execution.Id && x.RequirementId == requirement.RequirementId && x.Attempt == execution.Attempt && x.Status == AgentExecutionResourceApprovalStatus.Active, cancellationToken);
        if (approval is null || approval.ExpiresAt <= clock.UtcNow)
            return Gated(requirement, AgentExecutionResolutionOutcome.RequiresStepUp, "HumanStepUpRequired", secret.Revision, requirement.PolicyRevision, $"secret:{secret.Id:D}");
        if (approval.AuthorityRevision != secret.Revision)
            return Gated(requirement, AgentExecutionResolutionOutcome.HumanDecision, "AuthorityChanged", secret.Revision, requirement.PolicyRevision, $"secret:{secret.Id:D}");
        var capabilityBytes = RandomNumberGenerator.GetBytes(32);
        var capability = Convert.ToBase64String(capabilityBytes);
        CryptographicOperations.ZeroMemory(capabilityBytes);
        var now = clock.UtcNow;
        var lease = new SecretLease
        {
            SecretId = secret.Id,
            SecretVersionId = version.Id,
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = secret.ProjectId,
            ActorId = actor.UserId?.ToString("D") ?? actor.Username,
            ExecutionId = execution.Id,
            Kind = SecretLeaseKind.Use,
            Purpose = Bound(requirement.Purpose, 200),
            Target = ExecutionTarget(execution.Id, requirement.RequirementId),
            CapabilityHash = Hash(capability),
            AuthorityRevision = secret.Revision,
            MaxUses = 10,
            MaxConcurrency = 1,
            ExpiresAt = now + secretOptions.Value.NormalizedDefaultLeaseTtl,
            CreatedAt = now,
            UpdatedAt = now
        };
        await dbContext.SecretLeases.AddAsync(lease, cancellationToken);
        await dbContext.SecretAccessEvents.AddAsync(new SecretAccessEvent
        {
            SecretId = secret.Id,
            SecretVersionId = version.Id,
            LeaseId = lease.Id,
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = secret.ProjectId,
            ActorId = lease.ActorId,
            Operation = SecretAccessOperation.Use,
            Purpose = lease.Purpose,
            TargetHash = Hash(lease.Target),
            RequestId = $"execution-resolution:{execution.Id:D}:{execution.Attempt}:{requirement.RequirementId:D}",
            Allowed = true,
            ReasonCode = "ExecutionLeaseCreated",
            CreatedAt = now
        }, cancellationToken);
        capabilities.Add(new(requirement.RequirementId, lease.Id, capability, lease.Revision, lease.ExpiresAt));
        return Item(requirement, version.Id, string.Empty, secret.Revision, requirement.PolicyRevision ?? "secret-policy-v1", "Resolved", $"secret:{secret.Id:D}", lease.Id, lease.ExpiresAt);
    }

    private async Task<AgentExecutionResolutionItem> ResolveConnectionAsync(AgentExecution execution, AgentExecutionResourceRequirement requirement, AgentExecutionResolutionItem? prior, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        var query = dbContext.SourceConnections.AsNoTracking().Where(x => x.Id == requirement.LogicalResourceId && x.ProjectId == execution.ProjectId && x.Enabled);
        if (actor.HasUser) query = actor.IsServiceActor ? query.Where(x => x.TenantId == actor.TenantId) : query.Where(x => x.TenantId == actor.TenantId && x.OwnerUserId == actor.UserId);
        var profile = await query.SingleOrDefaultAsync(cancellationToken);
        if (profile is null) return Denied(requirement, "ResourceUnavailable");
        var fingerprint = Hash($"{profile.SourceKind}|{profile.Revision}|{profile.ConfigJson}");
        var expected = prior?.IntegrityIdentity ?? requirement.ExpectedIntegrityIdentity;
        if (prior is not null && (prior.AuthorityRevision != profile.Revision || !FixedEquals(prior.IntegrityIdentity, fingerprint))) return Denied(requirement, "ResourceUnavailable");
        if (!string.IsNullOrWhiteSpace(expected) && !FixedEquals(NormalizeHash(expected), fingerprint)) return Denied(requirement, "ResourceUnavailable");
        return Item(requirement, profile.Id, fingerprint, profile.Revision, requirement.PolicyRevision ?? "connection-profile-v1", "Resolved", $"connection-profile:{profile.Id:D}");
    }

    private static AgentExecutionResolutionItem ResolveSkill(AgentExecutionPackage package, AgentExecutionResourceRequirement requirement)
    {
        var pin = package.SkillSnapshot?.PinnedVersions.SingleOrDefault(x => x.SkillId == requirement.LogicalResourceId);
        return pin is null
            ? Denied(requirement, "ResourceUnavailable")
            : Item(requirement, pin.SkillVersionId, pin.ContentHash, 1, pin.ScopePolicyHash, "Resolved", $"skill-version:{pin.SkillVersionId:D}");
    }

    private async Task<bool> IsCurrentAsync(AgentExecution execution, AgentExecutionResolutionItem item, CancellationToken cancellationToken)
    {
        return item.Kind switch
        {
            AgentExecutionResourceKind.File => await dbContext.FileVersions.AsNoTracking().Include(x => x.FileAsset).AnyAsync(x =>
                x.Id == item.ResolvedVersionId && x.FileAssetId == item.LogicalResourceId && x.FileAsset!.ProjectId == execution.ProjectId &&
                x.FileAsset.State == FileAssetState.Active && x.Lifecycle == FileVersionLifecycle.Ready && x.Classification != FileClassification.Quarantined &&
                x.ContentSha256 == item.IntegrityIdentity && Math.Max(x.FileAsset.Revision, x.ClassificationRevision) == item.AuthorityRevision, cancellationToken),
            AgentExecutionResourceKind.Credential => await dbContext.Secrets.AsNoTracking().AnyAsync(x =>
                x.Id == item.LogicalResourceId && x.ProjectId == execution.ProjectId && x.State == SecretState.Active &&
                x.CurrentVersionId == item.ResolvedVersionId && x.Revision == item.AuthorityRevision, cancellationToken) &&
                await dbContext.SecretLeases.AsNoTracking().AnyAsync(x => x.Id == item.CapabilityLeaseId && x.ExecutionId == execution.Id &&
                    x.SecretVersionId == item.ResolvedVersionId && x.State == SecretLeaseState.Active && x.ExpiresAt > clock.UtcNow && x.AuthorityRevision == item.AuthorityRevision, cancellationToken),
            AgentExecutionResourceKind.ConnectionProfile => await IsConnectionCurrentAsync(execution, item, cancellationToken),
            // Skill lifecycle and policy remain authoritative in the existing SkillService revalidation path.
            AgentExecutionResourceKind.Skill => true,
            _ => false
        };
    }

    private async Task<bool> IsConnectionCurrentAsync(AgentExecution execution, AgentExecutionResolutionItem item, CancellationToken cancellationToken)
    {
        var profile = await dbContext.SourceConnections.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == item.LogicalResourceId && x.ProjectId == execution.ProjectId && x.Enabled && x.Revision == item.AuthorityRevision,
            cancellationToken);
        return profile is not null && FixedEquals(Hash($"{profile.SourceKind}|{profile.Revision}|{profile.ConfigJson}"), item.IntegrityIdentity);
    }

    private async Task<bool> SecretUseAllowedAsync(ContextHubRequestActor actor, Secret secret, CancellationToken cancellationToken)
    {
        var principal = actor.UserId?.ToString("D") ?? actor.Username;
        var effective = await foundation.EvaluateAsync(secret.ProjectId, principal, ["secret.use"], "Secret", secret.Id.ToString("D"), cancellationToken);
        if (!effective.Decisions.Single().Allowed) return false;
        var now = clock.UtcNow;
        var grants = await dbContext.SecretGrants.AsNoTracking().Where(x => x.SecretId == secret.Id && (x.PrincipalId == principal || x.PrincipalId == "*") && x.Right == SecretRight.Use && (x.ExpiresAt == null || x.ExpiresAt > now)).Select(x => x.Effect).ToArrayAsync(cancellationToken);
        var secretPolicies = await dbContext.SecretPolicies.AsNoTracking().Where(x => x.ProjectId == secret.ProjectId && x.SecretId == secret.Id && (x.PrincipalId == principal || x.PrincipalId == "*") && x.Right == SecretRight.Use).Select(x => x.Effect).ToArrayAsync(cancellationToken);
        var projectPolicies = await dbContext.SecretPolicies.AsNoTracking().Where(x => x.ProjectId == secret.ProjectId && x.SecretId == null && (x.PrincipalId == principal || x.PrincipalId == "*") && x.Right == SecretRight.Use).Select(x => x.Effect).ToArrayAsync(cancellationToken);
        var tier = grants.Length > 0 ? grants : secretPolicies.Length > 0 ? secretPolicies : projectPolicies;
        return tier.Length > 0 && !tier.Contains(AuthorizationEffect.Deny) && tier.Contains(AuthorizationEffect.Allow);
    }

    private IQueryable<Secret> VisibleSecrets(ContextHubRequestActor actor)
    {
        var query = dbContext.Secrets.AsQueryable();
        if (!actor.HasUser) return query;
        return actor.IsServiceActor ? query.Where(x => x.TenantId == actor.TenantId) : query.Where(x => x.TenantId == actor.TenantId && x.OwnerUserId == actor.UserId);
    }

    private static AgentExecutionResolutionItem Item(AgentExecutionResourceRequirement requirement, Guid? versionId, string integrity, long revision, string policyRevision, string reason, string evidence, Guid? leaseId = null, DateTimeOffset? leaseExpiresAt = null)
        => new()
        {
            RequirementId = requirement.RequirementId,
            Kind = requirement.Kind,
            Outcome = AgentExecutionResolutionOutcome.Resolved,
            LogicalResourceId = requirement.LogicalResourceId,
            ResolvedVersionId = versionId,
            IntegrityIdentity = integrity,
            AuthorityRevision = revision,
            PolicyRevision = policyRevision,
            CapabilityLeaseId = leaseId,
            CapabilityExpiresAt = leaseExpiresAt,
            ReasonCode = reason,
            EvidenceRefsJson = JsonSerializer.Serialize(new[] { evidence }, JsonOptions),
            CreatedAt = DateTimeOffset.UtcNow
        };

    private static AgentExecutionResolutionItem Gated(AgentExecutionResourceRequirement requirement, AgentExecutionResolutionOutcome outcome, string reason, long revision, string? policyRevision, string evidence)
        => new()
        {
            RequirementId = requirement.RequirementId,
            Kind = requirement.Kind,
            Outcome = outcome,
            LogicalResourceId = requirement.LogicalResourceId,
            AuthorityRevision = revision,
            PolicyRevision = policyRevision?.Trim() ?? string.Empty,
            ReasonCode = reason,
            EvidenceRefsJson = JsonSerializer.Serialize(new[] { evidence }, JsonOptions),
            CreatedAt = DateTimeOffset.UtcNow
        };

    private static AgentExecutionResolutionItem Denied(AgentExecutionResourceRequirement requirement, string reason)
        => Gated(requirement, AgentExecutionResolutionOutcome.Denied, reason, 0, requirement.PolicyRevision, $"requirement:{requirement.RequirementId:D}");

    private static string ExecutionTarget(Guid executionId, Guid requirementId)
        => $"agent-execution:{executionId:D}:requirement:{requirementId:D}";

    private static void ValidateLogicalLabel(string? value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var normalized = value.Trim();
        if (normalized.Length > maxLength || normalized.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':' or '/')))
            throw new InvalidOperationException($"{name} must be a bounded logical identifier.");
    }

    private static object MapStable(AgentExecutionResolutionItem value) => new
    {
        value.RequirementId,
        value.Kind,
        value.Outcome,
        value.LogicalResourceId,
        value.ResolvedVersionId,
        value.IntegrityIdentity,
        value.AuthorityRevision,
        value.PolicyRevision,
        value.CapabilityLeaseId,
        value.CapabilityExpiresAt,
        value.ReasonCode
    };

    private static AgentExecutionResolutionSnapshotResult Map(AgentExecutionResolutionSnapshot value)
        => new(value.Id, value.ExecutionId, value.Attempt, value.ResolutionSequence, value.RetryMode, value.Outcome,
            value.AuthorityContextHash, value.SnapshotHash, Deserialize(value.EvidenceRefsJson), value.ResolvedAt,
            value.Items.OrderBy(x => x.RequirementId).Select(x => new AgentExecutionResolutionItemResult(x.RequirementId, x.Kind, x.Outcome,
                x.LogicalResourceId, x.ResolvedVersionId, x.IntegrityIdentity, x.AuthorityRevision, x.PolicyRevision,
                x.CapabilityLeaseId, x.CapabilityExpiresAt, x.ReasonCode, Deserialize(x.EvidenceRefsJson))).ToArray());

    internal static AgentExecutionResolutionSnapshotResult MapSnapshot(AgentExecutionResolutionSnapshot value) => Map(value);
    private static string[] Deserialize(string json) => JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? [];
    private static string Bound(string? value, int max) => string.IsNullOrWhiteSpace(value) || value.Trim().Length > max ? throw new InvalidOperationException("Bound resource value is invalid.") : value.Trim();
    private static string NormalizeHash(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;
    private static bool IsSha256(string? value) => NormalizeHash(value) is { Length: 64 } hash && hash.All(Uri.IsHexDigit);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static bool FixedEquals(string left, string right) => IsSha256(left) && IsSha256(right) && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));
}
