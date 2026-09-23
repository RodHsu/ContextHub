using System.Data;
using System.Security.Cryptography;
using System.Text;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Memory.Application;

public sealed record SshCertificateIssueContext(
    Guid CaSecretId,
    string ProjectId,
    string ActorId,
    Guid? ExecutionId,
    string PublicKey,
    string TargetHost,
    int TargetPort,
    string TargetUser,
    long Serial,
    DateTimeOffset ValidAfter,
    DateTimeOffset ValidBefore,
    string Purpose);

public sealed record SshCertificateIssueArtifact(string Certificate, string CertificateFingerprint);

public interface ISshCertificateIssuer
{
    Task<SshCertificateIssueArtifact> IssueAsync(SshCertificateIssueContext context, CancellationToken cancellationToken);
}

public sealed class DisabledSshCertificateIssuer : ISshCertificateIssuer
{
    public Task<SshCertificateIssueArtifact> IssueAsync(SshCertificateIssueContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new CryptographicException("SSH certificate issuer is unavailable.");
    }
}

public sealed record IssueSshCertificateRequest(
    Guid CaSecretId,
    string PublicKey,
    string TargetHost,
    int TargetPort,
    string TargetUser,
    string Purpose,
    string RequestId,
    long ExpectedSecretRevision,
    Guid? ExecutionId,
    StepUpProof StepUp);

public sealed record RenewSshCertificateRequest(
    Guid CertificateLeaseId,
    string RenewalCapability,
    long ExpectedRevision,
    string RequestId);

public sealed record SshCertificateLeaseResult(
    Guid CertificateLeaseId,
    Guid SecretLeaseId,
    string RenewalCapability,
    string Certificate,
    string CertificateFingerprint,
    long Serial,
    int RenewalCount,
    int MaxRenewals,
    DateTimeOffset ValidAfter,
    DateTimeOffset ValidBefore,
    DateTimeOffset RenewalEligibleAt,
    DateTimeOffset MaxSessionExpiresAt,
    long Revision);

public interface ISshCertificateService
{
    Task<SshCertificateLeaseResult> IssueAsync(IssueSshCertificateRequest request, CancellationToken cancellationToken);
    Task<SshCertificateLeaseResult> RenewAsync(RenewSshCertificateRequest request, CancellationToken cancellationToken);
    Task<SecretMutationDecision> RevokeAsync(Guid certificateLeaseId, long expectedRevision, string approvalReference, string reason, CancellationToken cancellationToken);
}

public sealed class SshCertificateService(
    IApplicationDbContext dbContext,
    IRequestActorAccessor actorAccessor,
    IClock clock,
    IPlatformFoundationStore foundation,
    IStepUpAuthenticationService stepUp,
    IHighAssuranceApprovalVerifier externalApproval,
    ISshCertificateIssuer issuer,
    IOptions<SecretManagementOptions> options) : ISshCertificateService
{
    public async Task<SshCertificateLeaseResult> IssueAsync(IssueSshCertificateRequest request, CancellationToken cancellationToken)
    {
        var actor = RequireActor(SecurityScopes.SecretsUse);
        ValidateTarget(request.TargetHost, request.TargetPort, request.TargetUser);
        var purpose = RequireText(request.Purpose, nameof(request.Purpose), 200);
        var requestId = RequireText(request.RequestId, nameof(request.RequestId), 200);
        var publicKey = RequirePublicKey(request.PublicKey);
        var secret = await Scope(dbContext.Secrets.AsNoTracking(), actor).SingleOrDefaultAsync(x => x.Id == request.CaSecretId, cancellationToken) ?? throw Unavailable();
        if (secret.Kind != SecretKind.SshCertificateAuthorityReference || secret.State != SecretState.Active || secret.Revision != request.ExpectedSecretRevision) throw Unavailable();
        ActorAuthorization.EnsureProjectAllowed(actor, secret.ProjectId, write: false);
        await EnsureRightAsync(actor, secret.ProjectId, secret.Id, SecretRight.SshIssue, cancellationToken);
        await EnsureSecretRuleAsync(actor, secret, SecretRight.SshIssue, cancellationToken);
        var decision = await stepUp.AuthorizeAsync(StepUpOperationClass.SshCertificateIssue, "ssh:certificate:issue", "Secret", secret.Id.ToString("D"), request.StepUp, cancellationToken);
        if (decision.Outcome != StepUpRequirementOutcome.Allowed) throw new StepUpRequiredException(decision);

        await using var transaction = await dbContext.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await dbContext.AcquireTransactionLockAsync($"secret:{request.CaSecretId:D}", cancellationToken);
        secret = await Scope(dbContext.Secrets, actor).SingleOrDefaultAsync(x => x.Id == request.CaSecretId, cancellationToken) ?? throw Unavailable();
        if (secret.Kind != SecretKind.SshCertificateAuthorityReference || secret.State != SecretState.Active || secret.Revision != request.ExpectedSecretRevision) throw Unavailable();
        ActorAuthorization.EnsureProjectAllowed(actor, secret.ProjectId, write: false);
        await EnsureRightAsync(actor, secret.ProjectId, secret.Id, SecretRight.SshIssue, cancellationToken);
        await EnsureSecretRuleAsync(actor, secret, SecretRight.SshIssue, cancellationToken);
        var replay = await dbContext.SecretAccessEvents.AnyAsync(x => x.SecretId == secret.Id && x.RequestId == requestId, cancellationToken);
        if (replay) throw Unavailable();

        var now = clock.UtcNow;
        var validAfter = now - TimeSpan.FromSeconds(15);
        var validBefore = now + options.Value.NormalizedSshCertificateTtl;
        var serial = NewSerial();
        var context = new SshCertificateIssueContext(secret.Id, secret.ProjectId, ActorId(actor), request.ExecutionId, publicKey,
            request.TargetHost.Trim().ToLowerInvariant(), request.TargetPort, request.TargetUser.Trim(), serial, validAfter, validBefore, purpose);
        var artifact = await issuer.IssueAsync(context, cancellationToken);
        ValidateArtifact(artifact);
        var capabilityBytes = RandomNumberGenerator.GetBytes(32);
        var capability = Convert.ToBase64String(capabilityBytes);
        CryptographicOperations.ZeroMemory(capabilityBytes);
        var lease = new SecretLease
        {
            SecretId = secret.Id,
            SecretVersionId = secret.CurrentVersionId ?? Guid.Empty,
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = secret.ProjectId,
            ActorId = ActorId(actor),
            ExecutionId = request.ExecutionId,
            Kind = SecretLeaseKind.SshCertificate,
            Purpose = purpose,
            Target = SecretTargetBinding.Ssh(request.TargetHost, request.TargetPort, request.TargetUser),
            CapabilityHash = Hash(capability),
            AuthorityRevision = secret.Revision,
            MaxUses = options.Value.NormalizedSshMaxRenewals + 1,
            MaxConcurrency = 1,
            ExpiresAt = now + options.Value.NormalizedSshMaxSession,
            CreatedAt = now,
            UpdatedAt = now
        };
        var cert = CreateCertificateLease(secret, lease, context, artifact, 0, now, now + options.Value.NormalizedSshMaxSession);
        await dbContext.SecretLeases.AddAsync(lease, cancellationToken);
        await dbContext.SshCertificateLeases.AddAsync(cert, cancellationToken);
        await AddEventAsync(secret, lease, actor, SecretAccessOperation.IssueSshCertificate, purpose, lease.Target, requestId, "Issued", cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Map(cert, lease, capability);
    }

    public async Task<SshCertificateLeaseResult> RenewAsync(RenewSshCertificateRequest request, CancellationToken cancellationToken)
    {
        var actor = RequireActor(SecurityScopes.SecretsUse);
        var requestId = RequireText(request.RequestId, nameof(request.RequestId), 200);
        await using var transaction = await dbContext.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await dbContext.AcquireTransactionLockAsync($"ssh-certificate:{request.CertificateLeaseId:D}", cancellationToken);
        var current = await Scope(dbContext.SshCertificateLeases, actor).SingleOrDefaultAsync(x => x.Id == request.CertificateLeaseId, cancellationToken) ?? throw Unavailable();
        var lease = await Scope(dbContext.SecretLeases, actor).SingleOrDefaultAsync(x => x.Id == current.SecretLeaseId, cancellationToken) ?? throw Unavailable();
        await dbContext.AcquireTransactionLockAsync($"secret:{current.CaSecretId:D}", cancellationToken);
        var secret = await Scope(dbContext.Secrets.AsNoTracking(), actor).SingleOrDefaultAsync(x => x.Id == current.CaSecretId, cancellationToken) ?? throw Unavailable();
        var now = clock.UtcNow;
        if (current.Revision != request.ExpectedRevision || current.State != SshCertificateState.Active || now < current.RenewalEligibleAt ||
            current.ValidBefore <= now || current.RenewalCount >= current.MaxRenewals || current.MaxSessionExpiresAt <= now ||
            lease.State != SecretLeaseState.Active || lease.ExpiresAt <= now || secret.State != SecretState.Active ||
            lease.AuthorityRevision != secret.Revision ||
            !string.Equals(lease.ActorId, ActorId(actor), StringComparison.Ordinal) ||
            !string.Equals(current.ActorId, ActorId(actor), StringComparison.Ordinal) ||
            !FixedEquals(lease.CapabilityHash, Hash(request.RenewalCapability))) throw Unavailable();
        var replay = await dbContext.SecretAccessEvents.AnyAsync(x => x.LeaseId == lease.Id && x.RequestId == requestId, cancellationToken);
        if (replay) throw Unavailable();
        await EnsureRightAsync(actor, secret.ProjectId, secret.Id, SecretRight.SshIssue, cancellationToken);
        await EnsureSecretRuleAsync(actor, secret, SecretRight.SshIssue, cancellationToken);

        var validAfter = now - TimeSpan.FromSeconds(15);
        var validBefore = Min(now + options.Value.NormalizedSshCertificateTtl, current.MaxSessionExpiresAt);
        if (validBefore <= now) throw Unavailable();
        var context = new SshCertificateIssueContext(secret.Id, secret.ProjectId, ActorId(actor), lease.ExecutionId,
            current.PublicKey, current.TargetHost, current.TargetPort, current.TargetUser, NewSerial(), validAfter, validBefore, lease.Purpose);
        var artifact = await issuer.IssueAsync(context, cancellationToken);
        ValidateArtifact(artifact);
        current.State = SshCertificateState.Superseded;
        current.Revision++;
        current.UpdatedAt = now;
        lease.UsedCount++;
        lease.Revision++;
        lease.UpdatedAt = now;
        var renewed = CreateCertificateLease(secret, lease, context, artifact, current.RenewalCount + 1, current.SessionStartedAt, current.MaxSessionExpiresAt);
        await dbContext.SshCertificateLeases.AddAsync(renewed, cancellationToken);
        await AddEventAsync(secret, lease, actor, SecretAccessOperation.RenewSshCertificate, lease.Purpose, lease.Target, requestId, "Renewed", cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Map(renewed, lease, request.RenewalCapability);
    }

    public async Task<SecretMutationDecision> RevokeAsync(Guid certificateLeaseId, long expectedRevision, string approvalReference, string reason, CancellationToken cancellationToken)
    {
        var actor = RequireActor(SecurityScopes.SecretsManage);
        var approved = await externalApproval.VerifyAsync(ActorId(actor), approvalReference, "ssh:certificate:revoke", cancellationToken);
        if (!approved) return new(false, StepUpRequirementOutcome.RequiresExternalApproval, AssuranceLevel.Aal2, "Aal2Unavailable");
        await using var transaction = await dbContext.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await dbContext.AcquireTransactionLockAsync($"ssh-certificate:{certificateLeaseId:D}", cancellationToken);
        var cert = await Scope(dbContext.SshCertificateLeases, actor).SingleOrDefaultAsync(x => x.Id == certificateLeaseId, cancellationToken) ?? throw Unavailable();
        await dbContext.AcquireTransactionLockAsync($"secret:{cert.CaSecretId:D}", cancellationToken);
        var secret = await Scope(dbContext.Secrets.AsNoTracking(), actor).SingleOrDefaultAsync(x => x.Id == cert.CaSecretId, cancellationToken) ?? throw Unavailable();
        ActorAuthorization.EnsureProjectAllowed(actor, secret.ProjectId, write: true);
        await EnsureRightAsync(actor, secret.ProjectId, secret.Id, SecretRight.Revoke, cancellationToken);
        await EnsureSecretRuleAsync(actor, secret, SecretRight.Revoke, cancellationToken);
        if (cert.Revision != expectedRevision || cert.State is SshCertificateState.Revoked or SshCertificateState.Expired) throw Unavailable();
        cert.State = SshCertificateState.Revoked;
        cert.RevokedAt = clock.UtcNow;
        cert.UpdatedAt = clock.UtcNow;
        cert.Revision++;
        await dbContext.SshRevocationRecords.AddAsync(new SshRevocationRecord
        {
            SshCertificateLeaseId = cert.Id,
            ProjectId = cert.ProjectId,
            Serial = cert.Serial,
            CertificateFingerprint = cert.CertificateFingerprint,
            ReasonCode = RequireText(reason, nameof(reason), 200),
            KrlRequired = true,
            CreatedAt = clock.UtcNow
        }, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(true, StepUpRequirementOutcome.Allowed, AssuranceLevel.Aal2, "RevokedKrlPending", cert.Revision);
    }

    private SshCertificateLease CreateCertificateLease(Secret secret, SecretLease lease, SshCertificateIssueContext context,
        SshCertificateIssueArtifact artifact, int renewalCount, DateTimeOffset sessionStartedAt, DateTimeOffset maxSessionExpiresAt)
    {
        var jitterMaximum = Math.Max(1, (int)(options.Value.NormalizedSshRenewalWindow.TotalSeconds / 5));
        var jitter = TimeSpan.FromSeconds((int)(context.Serial % jitterMaximum));
        var renewalEligibleAt = context.ValidBefore - options.Value.NormalizedSshRenewalWindow + jitter;
        return new SshCertificateLease
        {
            SecretLeaseId = lease.Id,
            CaSecretId = secret.Id,
            TenantId = lease.TenantId,
            OwnerUserId = lease.OwnerUserId,
            ProjectId = secret.ProjectId,
            ActorId = lease.ActorId,
            ExecutionId = lease.ExecutionId,
            TargetHost = context.TargetHost,
            TargetPort = context.TargetPort,
            TargetUser = context.TargetUser,
            PublicKey = context.PublicKey,
            PublicKeyFingerprint = Fingerprint(context.PublicKey),
            Certificate = artifact.Certificate,
            CertificateFingerprint = artifact.CertificateFingerprint,
            Serial = context.Serial,
            RenewalCount = renewalCount,
            MaxRenewals = options.Value.NormalizedSshMaxRenewals,
            SessionStartedAt = sessionStartedAt,
            MaxSessionExpiresAt = maxSessionExpiresAt,
            ValidAfter = context.ValidAfter,
            ValidBefore = context.ValidBefore,
            RenewalEligibleAt = renewalEligibleAt,
            AuthorityRevision = secret.Revision,
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow
        };
    }

    private async Task EnsureRightAsync(ContextHubRequestActor actor, string projectId, Guid secretId, SecretRight right, CancellationToken cancellationToken)
    {
        var result = await foundation.EvaluateAsync(projectId, ActorId(actor), [$"secret.{right.ToString().ToLowerInvariant()}"], "Secret", secretId.ToString("D"), cancellationToken);
        if (!result.Decisions.Single().Allowed) throw Unavailable();
    }

    private async Task EnsureSecretRuleAsync(ContextHubRequestActor actor, Secret secret, SecretRight right, CancellationToken cancellationToken)
    {
        var principal = ActorId(actor);
        var now = clock.UtcNow;
        var grants = await Scope(dbContext.SecretGrants.AsNoTracking(), actor).Where(x => x.SecretId == secret.Id && (x.PrincipalId == principal || x.PrincipalId == "*") && x.Right == right && (x.ExpiresAt == null || x.ExpiresAt > now)).Select(x => x.Effect).ToArrayAsync(cancellationToken);
        var policies = await Scope(dbContext.SecretPolicies.AsNoTracking(), actor).Where(x => x.ProjectId == secret.ProjectId && (x.SecretId == secret.Id || x.SecretId == null) && (x.PrincipalId == principal || x.PrincipalId == "*") && x.Right == right).Select(x => new { x.SecretId, x.Effect }).ToArrayAsync(cancellationToken);
        var secretTier = policies.Where(x => x.SecretId == secret.Id).Select(x => x.Effect).ToArray();
        var tier = grants.Length > 0 ? grants : secretTier.Length > 0 ? secretTier : policies.Where(x => x.SecretId == null).Select(x => x.Effect).ToArray();
        if (tier.Length == 0 || tier.Contains(AuthorizationEffect.Deny) || !tier.Contains(AuthorizationEffect.Allow)) throw Unavailable();
    }

    private async Task AddEventAsync(Secret secret, SecretLease lease, ContextHubRequestActor actor, SecretAccessOperation operation,
        string purpose, string target, string requestId, string reasonCode, CancellationToken cancellationToken)
        => await dbContext.SecretAccessEvents.AddAsync(new SecretAccessEvent
        {
            SecretId = secret.Id,
            SecretVersionId = secret.CurrentVersionId,
            LeaseId = lease.Id,
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = secret.ProjectId,
            ActorId = ActorId(actor),
            Operation = operation,
            Purpose = purpose,
            TargetHash = Hash(target),
            RequestId = requestId,
            Allowed = true,
            ReasonCode = reasonCode,
            CreatedAt = clock.UtcNow
        }, cancellationToken);

    private static SshCertificateLeaseResult Map(SshCertificateLease cert, SecretLease lease, string capability)
        => new(cert.Id, lease.Id, capability, cert.Certificate, cert.CertificateFingerprint, cert.Serial, cert.RenewalCount, cert.MaxRenewals,
            cert.ValidAfter, cert.ValidBefore, cert.RenewalEligibleAt, cert.MaxSessionExpiresAt, cert.Revision);

    private ContextHubRequestActor RequireActor(string scope)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, scope);
        return actor;
    }

    private static IQueryable<T> Scope<T>(IQueryable<T> query, ContextHubRequestActor actor) where T : class
    {
        if (!actor.HasUser) return query;
        query = actor.IsServiceActor
            ? query.Where(x => EF.Property<Guid?>(x, "TenantId") == actor.TenantId)
            : query.Where(x => EF.Property<Guid?>(x, "TenantId") == actor.TenantId && EF.Property<Guid?>(x, "OwnerUserId") == actor.UserId);
        if (actor.AllowedProjectIds.Count == 0) return query;
        var allowed = actor.AllowedProjectIds.Select(x => ProjectContext.Normalize(x).ToLowerInvariant()).ToArray();
        return query.Where(x => allowed.Contains(EF.Property<string>(x, "ProjectId").ToLower()) ||
                                EF.Property<string>(x, "ProjectId").ToLower() == ProjectContext.SharedProjectId ||
                                EF.Property<string>(x, "ProjectId").ToLower() == ProjectContext.UserProjectId);
    }

    private static void ValidateTarget(string host, int port, string user)
    {
        _ = SecretTargetBinding.Ssh(host, port, user);
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
    }

    private static string RequirePublicKey(string value)
    {
        var normalized = RequireText(value, nameof(value), 16_384);
        if (!(normalized.StartsWith("ssh-ed25519 ", StringComparison.Ordinal) || normalized.StartsWith("ssh-rsa ", StringComparison.Ordinal) || normalized.StartsWith("ecdsa-sha2-", StringComparison.Ordinal)))
            throw new ArgumentException("Unsupported SSH public key format.", nameof(value));
        return normalized;
    }

    private static void ValidateArtifact(SshCertificateIssueArtifact artifact)
    {
        if (string.IsNullOrWhiteSpace(artifact.Certificate) || artifact.Certificate.Length > 32_768 || string.IsNullOrWhiteSpace(artifact.CertificateFingerprint))
            throw new CryptographicException("SSH certificate issuer returned an invalid artifact.");
    }

    private static long NewSerial() => BitConverter.ToInt64(RandomNumberGenerator.GetBytes(sizeof(long))) & long.MaxValue;
    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;
    private static string Fingerprint(string value) => "SHA256:" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(value))).TrimEnd('=');
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static bool FixedEquals(string left, string right) => CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));
    private static string ActorId(ContextHubRequestActor actor) => actor.UserId!.Value.ToString("D");
    private static UnauthorizedAccessException Unavailable() => new("Secret resource is unavailable.");
    private static string RequireText(string value, string name, int maxLength)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0 || normalized.Length > maxLength) throw new ArgumentException($"{name} is required and must not exceed {maxLength} characters.", name);
        return normalized;
    }
}

public interface ISecretReconciliationService
{
    Task<SecretReconciliationResult> RunAsync(CancellationToken cancellationToken);
}

public sealed record SecretReconciliationResult(int ExpiredLeases, int ExpiredCertificates, int StaleRelations, int PendingKrl);

public sealed class SecretReconciliationService(IApplicationDbContext dbContext, IClock clock) : ISecretReconciliationService
{
    public async Task<SecretReconciliationResult> RunAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var expiredLeases = await dbContext.SecretLeases.Where(x => x.State == SecretLeaseState.Active && x.ExpiresAt <= now)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.State, SecretLeaseState.Expired).SetProperty(x => x.ActiveUses, 0).SetProperty(x => x.UpdatedAt, now), cancellationToken);
        var expiredCertificates = await dbContext.SshCertificateLeases.Where(x => x.State == SshCertificateState.Active && x.ValidBefore <= now)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.State, SshCertificateState.Expired).SetProperty(x => x.UpdatedAt, now), cancellationToken);
        var staleRelations = await dbContext.SecretRelations.CountAsync(x => x.IsStale, cancellationToken);
        var pendingKrl = await dbContext.SshRevocationRecords.CountAsync(x => x.KrlRequired && x.ReconciledAt == null, cancellationToken);
        return new(expiredLeases, expiredCertificates, staleRelations, pendingKrl);
    }
}
