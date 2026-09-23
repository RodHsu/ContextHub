using System.Buffers;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Memory.Application;

public sealed class SecretManagementOptions
{
    public const string SectionName = "SecretManagement";
    public int MaxSecretBytes { get; set; } = 1024 * 1024;
    public int DefaultLeaseMinutes { get; set; } = 10;
    public int MaximumLeaseMinutes { get; set; } = 30;
    public int ReconciliationIntervalMinutes { get; set; } = 15;
    public int SshCertificateMinutes { get; set; } = 10;
    public int SshRenewalWindowSeconds { get; set; } = 120;
    public int SshMaxSessionMinutes { get; set; } = 60;
    public int SshMaxRenewals { get; set; } = 5;

    public int NormalizedMaxSecretBytes => Math.Clamp(MaxSecretBytes, 32, 16 * 1024 * 1024);
    public TimeSpan NormalizedDefaultLeaseTtl => TimeSpan.FromMinutes(Math.Clamp(DefaultLeaseMinutes, 1, Math.Clamp(MaximumLeaseMinutes, 1, 120)));
    public TimeSpan NormalizedMaximumLeaseTtl => TimeSpan.FromMinutes(Math.Clamp(MaximumLeaseMinutes, 1, 120));
    public TimeSpan NormalizedSshCertificateTtl => TimeSpan.FromMinutes(Math.Clamp(SshCertificateMinutes, 1, 30));
    public TimeSpan NormalizedSshRenewalWindow => TimeSpan.FromSeconds(Math.Clamp(SshRenewalWindowSeconds, 30, 300));
    public TimeSpan NormalizedSshMaxSession => TimeSpan.FromMinutes(Math.Clamp(SshMaxSessionMinutes, 5, 480));
    public int NormalizedSshMaxRenewals => Math.Clamp(SshMaxRenewals, 0, 50);
}

public sealed record WrappedSecretKey(string KeyId, byte[] Ciphertext, byte[] Nonce, byte[] Tag);

public interface ISecretEnvelopeKeyAuthority
{
    WrappedSecretKey Wrap(Guid secretId, Guid versionId, ReadOnlySpan<byte> plaintextKey);
    byte[] Unwrap(Guid secretId, Guid versionId, WrappedSecretKey wrappedKey);
    WrappedSecretKey Rewrap(Guid secretId, Guid versionId, WrappedSecretKey wrappedKey);
}

public interface ISshBoundSigner
{
    byte[] SignAuthenticationPayload(ReadOnlySpan<byte> privateKeyPkcs8, ReadOnlySpan<byte> canonicalPayload);
}

public sealed record CreateSecretRequest(string ProjectId, string Name, SecretKind Kind, StepUpProof StepUp);
public sealed record AddSecretVersionRequest(Guid SecretId, ReadOnlyMemory<byte> Material, long ExpectedSecretRevision, string RequestId, StepUpProof? StepUp = null, string? ExternalApprovalReference = null, DateTimeOffset? ExpiresAt = null);
public sealed record SecretSummary(Guid Id, string ProjectId, string Name, SecretKind Kind, SecretState State, int? CurrentVersion, long Revision, DateTimeOffset UpdatedAt);
public sealed record SecretVersionResult(Guid SecretId, Guid VersionId, int VersionNumber, SecretVersionState State, DateTimeOffset? ExpiresAt, long SecretRevision);
public sealed record CreateSecretLeaseRequest(Guid SecretId, SecretLeaseKind Kind, string Purpose, string Target, string RequestId, long ExpectedSecretRevision, int MaxUses, int MaxConcurrency, TimeSpan? Ttl, Guid? ExecutionId, StepUpProof StepUp);
public sealed record SecretLeaseResult(Guid LeaseId, Guid CapabilityId, string Capability, Guid SecretId, Guid SecretVersionId, SecretLeaseKind Kind, string Purpose, string Target, long Revision, int MaxUses, int MaxConcurrency, DateTimeOffset ExpiresAt);
public sealed record SecretUseContext(Guid SecretId, Guid SecretVersionId, Guid LeaseId, string ProjectId, string Purpose, string Target, Guid? ExecutionId);
public sealed record SecretUseRequest(Guid LeaseId, string Capability, long ExpectedRevision, string RequestId, string Purpose, string Target, SecretLeaseKind? RequiredKind = null);
public sealed record SecretUseResult(long Revision, int UsedCount, int RemainingUses, SecretLeaseState State);
public sealed record SshSignerRequest(Guid LeaseId, string Capability, long ExpectedRevision, string RequestId, string SessionId, string Host, int Port, string User, byte[] Challenge);
public sealed record SshSignerResult(byte[] Signature, string Algorithm, SecretUseResult Lease);
public sealed record SecretMutationDecision(bool Applied, StepUpRequirementOutcome Outcome, AssuranceLevel RequiredAssurance, string ReasonCode, long? Revision = null);

public interface ISecretMaterialConsumer
{
    Task ExecuteAsync(SecretUseContext context, ReadOnlyMemory<byte> material, CancellationToken cancellationToken);
}

public interface ISecretManagementService
{
    Task<SecretSummary> CreateAsync(CreateSecretRequest request, CancellationToken cancellationToken);
    Task<SecretVersionResult> AddVersionAsync(AddSecretVersionRequest request, CancellationToken cancellationToken);
    Task<SecretLeaseResult> CreateLeaseAsync(CreateSecretLeaseRequest request, CancellationToken cancellationToken);
    Task<SecretUseResult> UseAsync(SecretUseRequest request, ISecretMaterialConsumer consumer, CancellationToken cancellationToken);
    Task<SshSignerResult> SignSshAuthenticationAsync(SshSignerRequest request, CancellationToken cancellationToken);
    Task<SecretMutationDecision> RevokeAsync(Guid secretId, long expectedRevision, string approvalReference, string requestId, CancellationToken cancellationToken);
    Task<SecretMutationDecision> RewrapCurrentVersionAsync(Guid secretId, long expectedRevision, string approvalReference, string requestId, CancellationToken cancellationToken);
    Task<IReadOnlyList<SecretSummary>> ListAsync(string projectId, CancellationToken cancellationToken);
}

public sealed class SecretManagementService(
    IApplicationDbContext dbContext,
    IRequestActorAccessor actorAccessor,
    IClock clock,
    IPlatformFoundationStore foundation,
    IStepUpAuthenticationService stepUp,
    IHighAssuranceApprovalVerifier externalApproval,
    ISecretEnvelopeKeyAuthority keyAuthority,
    ISshBoundSigner sshSigner,
    IOptions<SecretManagementOptions> options) : ISecretManagementService
{
    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    public async Task<SecretSummary> CreateAsync(CreateSecretRequest request, CancellationToken cancellationToken)
    {
        var actor = RequireActor(SecurityScopes.SecretsManage);
        var projectId = ProjectContext.Normalize(request.ProjectId);
        ActorAuthorization.EnsureProjectAllowed(actor, projectId, write: true);
        await EnsureFoundationRightAsync(actor, projectId, SecretRight.Manage, null, cancellationToken);
        var stepUpDecision = await stepUp.AuthorizeAsync(StepUpOperationClass.SecretCreate, "secret:create", "Project", projectId, request.StepUp, cancellationToken);
        EnsureAllowed(stepUpDecision);
        var name = RequireText(request.Name, nameof(request.Name), 300);
        var normalizedName = NormalizeName(name);
        var now = clock.UtcNow;
        var secret = new Secret
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = projectId,
            Name = name,
            NormalizedName = normalizedName,
            Kind = request.Kind,
            CreatedAt = now,
            UpdatedAt = now
        };

        await using var transaction = await dbContext.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await dbContext.AcquireTransactionLockAsync($"secret-name:{actor.TenantId:D}:{projectId}:{normalizedName}", cancellationToken);
        var exists = await Scope(dbContext.Secrets, actor).AnyAsync(x => x.ProjectId == projectId && x.NormalizedName == normalizedName && x.State == SecretState.Active, cancellationToken);
        if (exists) throw new InvalidOperationException("An active secret with the same name already exists.");
        await dbContext.Secrets.AddAsync(secret, cancellationToken);
        await AddAccessEventAsync(secret, null, null, actor, SecretAccessOperation.Create, "secret:create", projectId, Guid.NewGuid().ToString("N"), true, "Created", cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Map(secret, null);
    }

    public async Task<SecretVersionResult> AddVersionAsync(AddSecretVersionRequest request, CancellationToken cancellationToken)
    {
        var actor = RequireActor(SecurityScopes.SecretsManage);
        if (request.Material.Length is <= 0 || request.Material.Length > options.Value.NormalizedMaxSecretBytes) throw new ArgumentOutOfRangeException(nameof(request.Material));
        var requestId = RequireText(request.RequestId, nameof(request.RequestId), 200);
        await using var transaction = await dbContext.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await dbContext.AcquireTransactionLockAsync($"secret:{request.SecretId:D}", cancellationToken);
        var secret = await FindVisibleSecretAsync(request.SecretId, actor, tracking: true, cancellationToken);
        if (secret.Revision != request.ExpectedSecretRevision) throw new DbUpdateConcurrencyException("Secret revision conflict; reload before retrying.");
        await EnsureFoundationRightAsync(actor, secret.ProjectId, secret.CurrentVersionId.HasValue ? SecretRight.Rotate : SecretRight.Manage, secret.Id, cancellationToken);
        if (secret.State != SecretState.Active) throw Unavailable();

        if (secret.CurrentVersionId.HasValue)
        {
            var approved = !string.IsNullOrWhiteSpace(request.ExternalApprovalReference) &&
                await externalApproval.VerifyAsync(ActorId(actor), request.ExternalApprovalReference, "secret:rotate", cancellationToken);
            if (!approved) throw new StepUpRequiredException(new StepUpAuthorizationResult(StepUpRequirementOutcome.RequiresExternalApproval, AssuranceLevel.Aal2, "Aal2Unavailable"));
        }
        else
        {
            var decision = await stepUp.AuthorizeAsync(StepUpOperationClass.SecretVersionCreate, "secret:version:create", "Secret", secret.Id.ToString("D"), request.StepUp, cancellationToken);
            EnsureAllowed(decision);
        }

        var replayed = await dbContext.SecretAccessEvents.AnyAsync(x => x.SecretId == secret.Id && x.RequestId == requestId, cancellationToken);
        if (replayed) throw new InvalidOperationException("Secret mutation request was already used.");
        var version = EncryptVersion(secret, request.Material.Span, request.ExpiresAt);
        var previous = secret.CurrentVersionId.HasValue
            ? await dbContext.SecretVersions.SingleAsync(x => x.Id == secret.CurrentVersionId.Value, cancellationToken)
            : null;
        if (previous is not null)
        {
            previous.State = SecretVersionState.Retired;
            previous.RetiredAt = clock.UtcNow;
        }
        await dbContext.SecretVersions.AddAsync(version, cancellationToken);
        secret.CurrentVersionId = version.Id;
        secret.Revision++;
        secret.UpdatedAt = clock.UtcNow;
        await AddAccessEventAsync(secret, version, null, actor, previous is null ? SecretAccessOperation.AddVersion : SecretAccessOperation.Rotate,
            previous is null ? "secret:version:create" : "secret:rotate", secret.ProjectId, requestId, true, previous is null ? "VersionCreated" : "Rotated", cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new SecretVersionResult(secret.Id, version.Id, version.VersionNumber, version.State, version.ExpiresAt, secret.Revision);
    }

    public async Task<SecretLeaseResult> CreateLeaseAsync(CreateSecretLeaseRequest request, CancellationToken cancellationToken)
    {
        var actor = RequireActor(SecurityScopes.SecretsUse);
        var purpose = RequireText(request.Purpose, nameof(request.Purpose), 200);
        var target = RequireText(request.Target, nameof(request.Target), 1000);
        _ = RequireText(request.RequestId, nameof(request.RequestId), 200);
        if (request.MaxUses is < 1 or > 100 || request.MaxConcurrency is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(request));
        if (request.Kind is not (SecretLeaseKind.Use or SecretLeaseKind.SshSigner)) throw new ArgumentOutOfRangeException(nameof(request.Kind));
        var secret = await FindVisibleSecretAsync(request.SecretId, actor, tracking: false, cancellationToken);
        if (secret.Revision != request.ExpectedSecretRevision || secret.State != SecretState.Active || !secret.CurrentVersionId.HasValue) throw Unavailable();
        var right = request.Kind == SecretLeaseKind.SshSigner ? SecretRight.SshSign : SecretRight.Use;
        await EnsureFoundationRightAsync(actor, secret.ProjectId, right, secret.Id, cancellationToken);
        await EnsureSecretRuleAsync(actor, secret, right, cancellationToken);
        var decision = await stepUp.AuthorizeAsync(StepUpOperationClass.SecretUseLeaseCreate, "secret:lease:create", "Secret", secret.Id.ToString("D"), request.StepUp, cancellationToken);
        EnsureAllowed(decision);
        await using var transaction = await dbContext.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await dbContext.AcquireTransactionLockAsync($"secret:{request.SecretId:D}", cancellationToken);
        secret = await FindVisibleSecretAsync(request.SecretId, actor, tracking: true, cancellationToken);
        if (secret.Revision != request.ExpectedSecretRevision || secret.State != SecretState.Active || !secret.CurrentVersionId.HasValue) throw Unavailable();
        await EnsureFoundationRightAsync(actor, secret.ProjectId, right, secret.Id, cancellationToken);
        await EnsureSecretRuleAsync(actor, secret, right, cancellationToken);
        var version = await dbContext.SecretVersions.AsNoTracking().SingleAsync(x => x.Id == secret.CurrentVersionId.Value, cancellationToken);
        if (version.State != SecretVersionState.Active || version.ExpiresAt <= clock.UtcNow) throw Unavailable();
        if (request.Kind == SecretLeaseKind.SshSigner && secret.Kind != SecretKind.SshPrivateKeyPkcs8) throw new InvalidOperationException("The requested secret is not an SSH signer credential.");
        if (request.Kind == SecretLeaseKind.Use && secret.Kind == SecretKind.SshPrivateKeyPkcs8) throw Unavailable();
        var ttl = request.Ttl ?? options.Value.NormalizedDefaultLeaseTtl;
        if (ttl <= TimeSpan.Zero || ttl > options.Value.NormalizedMaximumLeaseTtl) throw new ArgumentOutOfRangeException(nameof(request.Ttl));
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
            ActorId = ActorId(actor),
            ExecutionId = request.ExecutionId,
            Kind = request.Kind,
            Purpose = purpose,
            Target = target,
            CapabilityHash = Hash(capability),
            AuthorityRevision = secret.Revision,
            MaxUses = request.MaxUses,
            MaxConcurrency = request.MaxConcurrency,
            ExpiresAt = now + ttl,
            CreatedAt = now,
            UpdatedAt = now
        };
        await dbContext.SecretLeases.AddAsync(lease, cancellationToken);
        await AddAccessEventAsync(secret, version, lease, actor, SecretAccessOperation.Use, purpose, target, request.RequestId, true, "LeaseCreated", cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new SecretLeaseResult(lease.Id, lease.CapabilityId, capability, secret.Id, version.Id, lease.Kind, lease.Purpose, lease.Target, lease.Revision, lease.MaxUses, lease.MaxConcurrency, lease.ExpiresAt);
    }

    public async Task<SecretUseResult> UseAsync(SecretUseRequest request, ISecretMaterialConsumer consumer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        var actor = RequireActor(SecurityScopes.SecretsUse);
        SecretLease lease;
        Secret secret;
        SecretVersion version;
        await using (var transaction = await dbContext.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken))
        {
            await dbContext.AcquireTransactionLockAsync($"secret-lease:{request.LeaseId:D}", cancellationToken);
            lease = await Scope(dbContext.SecretLeases, actor).SingleOrDefaultAsync(x => x.Id == request.LeaseId, cancellationToken) ?? throw Unavailable();
            secret = await FindVisibleSecretAsync(lease.SecretId, actor, tracking: true, cancellationToken);
            version = await dbContext.SecretVersions.SingleAsync(x => x.Id == lease.SecretVersionId, cancellationToken);
            await ValidateLeaseAsync(actor, secret, version, lease, request.Capability, request.ExpectedRevision, request.Purpose, request.Target,
                request.RequestId, request.RequiredKind, cancellationToken);
            lease.ActiveUses++;
            lease.UsedCount++;
            lease.Revision++;
            lease.UpdatedAt = clock.UtcNow;
            if (lease.UsedCount >= lease.MaxUses) lease.State = SecretLeaseState.Exhausted;
            await AddAccessEventAsync(secret, version, lease, actor, SecretAccessOperation.Use, lease.Purpose, lease.Target, request.RequestId, true, "UseStarted", cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        var plaintext = Decrypt(version, secret.Id);
        try
        {
            var context = new SecretUseContext(secret.Id, version.Id, lease.Id, secret.ProjectId, lease.Purpose, lease.Target, lease.ExecutionId);
            await consumer.ExecuteAsync(context, plaintext, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            await ReleaseActiveUseAsync(lease.Id, actor, cancellationToken);
        }
        return new SecretUseResult(lease.Revision, lease.UsedCount, Math.Max(0, lease.MaxUses - lease.UsedCount), lease.State);
    }

    public async Task<SshSignerResult> SignSshAuthenticationAsync(SshSignerRequest request, CancellationToken cancellationToken)
    {
        if (request.Challenge.Length != 32) throw new ArgumentException("SSH authentication challenge must be exactly 32 bytes.", nameof(request));
        if (request.Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(request.Port));
        var target = SecretTargetBinding.Ssh(request.Host, request.Port, request.User);
        var payload = SecretTargetBinding.BuildSshAuthenticationPayload(request.SessionId, request.Host, request.Port, request.User, request.Challenge);
        byte[]? signature = null;
        var consumer = new DelegateSecretMaterialConsumer((context, material, _) =>
        {
            if (!string.Equals(context.Target, target, StringComparison.Ordinal)) throw new UnauthorizedAccessException("Secret capability target binding is invalid.");
            signature = sshSigner.SignAuthenticationPayload(material.Span, payload);
            return Task.CompletedTask;
        });
        var result = await UseAsync(new SecretUseRequest(request.LeaseId, request.Capability, request.ExpectedRevision, request.RequestId,
            "ssh:authenticate", target, SecretLeaseKind.SshSigner), consumer, cancellationToken);
        CryptographicOperations.ZeroMemory(payload);
        return new SshSignerResult(signature ?? throw new CryptographicException("SSH signer failed closed."), "rsa-sha2-512", result);
    }

    public async Task<SecretMutationDecision> RevokeAsync(Guid secretId, long expectedRevision, string approvalReference, string requestId, CancellationToken cancellationToken)
    {
        var actor = RequireActor(SecurityScopes.SecretsManage);
        var approved = await externalApproval.VerifyAsync(ActorId(actor), approvalReference, "secret:revoke", cancellationToken);
        if (!approved) return new(false, StepUpRequirementOutcome.RequiresExternalApproval, AssuranceLevel.Aal2, "Aal2Unavailable");
        await using var transaction = await dbContext.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await dbContext.AcquireTransactionLockAsync($"secret:{secretId:D}", cancellationToken);
        var secret = await FindVisibleSecretAsync(secretId, actor, tracking: true, cancellationToken);
        if (secret.Revision != expectedRevision) throw new DbUpdateConcurrencyException("Secret revision conflict; reload before retrying.");
        await EnsureFoundationRightAsync(actor, secret.ProjectId, SecretRight.Revoke, secret.Id, cancellationToken);
        await EnsureSecretRuleAsync(actor, secret, SecretRight.Revoke, cancellationToken);
        secret.State = SecretState.Revoked;
        secret.RevokedAt = clock.UtcNow;
        secret.UpdatedAt = clock.UtcNow;
        secret.Revision++;
        var issuedCertificates = await dbContext.SshCertificateLeases
            .Where(x => x.CaSecretId == secret.Id &&
                        (x.State == SshCertificateState.Active || x.State == SshCertificateState.Superseded) &&
                        x.ValidBefore > clock.UtcNow)
            .ToArrayAsync(cancellationToken);
        var existingRevocations = await dbContext.SshRevocationRecords
            .Where(x => x.ProjectId == secret.ProjectId && issuedCertificates.Select(c => c.Id).Contains(x.SshCertificateLeaseId))
            .Select(x => x.SshCertificateLeaseId)
            .ToArrayAsync(cancellationToken);
        var revokedCertificateIds = existingRevocations.ToHashSet();
        foreach (var certificate in issuedCertificates)
        {
            certificate.State = SshCertificateState.Revoked;
            certificate.RevokedAt = clock.UtcNow;
            certificate.UpdatedAt = clock.UtcNow;
            certificate.Revision++;
            if (revokedCertificateIds.Add(certificate.Id))
            {
                await dbContext.SshRevocationRecords.AddAsync(new SshRevocationRecord
                {
                    SshCertificateLeaseId = certificate.Id,
                    ProjectId = certificate.ProjectId,
                    Serial = certificate.Serial,
                    CertificateFingerprint = certificate.CertificateFingerprint,
                    ReasonCode = "CaSecretRevoked",
                    KrlRequired = true,
                    CreatedAt = clock.UtcNow
                }, cancellationToken);
            }
        }
        await dbContext.SecretLeases.Where(x => x.SecretId == secret.Id && x.State == SecretLeaseState.Active)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.State, SecretLeaseState.Revoked).SetProperty(x => x.RevokedAt, clock.UtcNow), cancellationToken);
        await AddAccessEventAsync(secret, null, null, actor, SecretAccessOperation.Revoke, "secret:revoke", secret.ProjectId, requestId, true, "Revoked", cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(true, StepUpRequirementOutcome.Allowed, AssuranceLevel.Aal2, "ExternalApprovalSatisfied", secret.Revision);
    }

    public async Task<SecretMutationDecision> RewrapCurrentVersionAsync(Guid secretId, long expectedRevision, string approvalReference, string requestId, CancellationToken cancellationToken)
    {
        var actor = RequireActor(SecurityScopes.SecretsManage);
        var approved = await externalApproval.VerifyAsync(ActorId(actor), approvalReference, "secret:kek:rewrap", cancellationToken);
        if (!approved) return new(false, StepUpRequirementOutcome.RequiresExternalApproval, AssuranceLevel.Aal2, "Aal2Unavailable");
        await using var transaction = await dbContext.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await dbContext.AcquireTransactionLockAsync($"secret:{secretId:D}", cancellationToken);
        var secret = await FindVisibleSecretAsync(secretId, actor, tracking: true, cancellationToken);
        if (secret.Revision != expectedRevision || !secret.CurrentVersionId.HasValue) throw new DbUpdateConcurrencyException("Secret revision conflict; reload before retrying.");
        await EnsureFoundationRightAsync(actor, secret.ProjectId, SecretRight.Rotate, secret.Id, cancellationToken);
        await EnsureSecretRuleAsync(actor, secret, SecretRight.Rotate, cancellationToken);
        var version = await dbContext.SecretVersions.SingleAsync(x => x.Id == secret.CurrentVersionId.Value, cancellationToken);
        var rewrapped = keyAuthority.Rewrap(secret.Id, version.Id, Wrapped(version));
        version.KeyId = rewrapped.KeyId;
        version.WrappedDek = rewrapped.Ciphertext;
        version.WrapNonce = rewrapped.Nonce;
        version.WrapTag = rewrapped.Tag;
        secret.Revision++;
        secret.UpdatedAt = clock.UtcNow;
        await AddAccessEventAsync(secret, version, null, actor, SecretAccessOperation.Rotate, "secret:kek:rewrap", secret.ProjectId, requestId, true, "KekRewrapped", cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(true, StepUpRequirementOutcome.Allowed, AssuranceLevel.Aal2, "ExternalApprovalSatisfied", secret.Revision);
    }

    public async Task<IReadOnlyList<SecretSummary>> ListAsync(string projectId, CancellationToken cancellationToken)
    {
        var actor = RequireActor(SecurityScopes.SecretsRead);
        var project = ProjectContext.Normalize(projectId);
        ActorAuthorization.EnsureProjectAllowed(actor, project, write: false);
        await EnsureFoundationRightAsync(actor, project, SecretRight.Metadata, null, cancellationToken);
        return await Scope(dbContext.Secrets.AsNoTracking(), actor).Where(x => x.ProjectId == project)
            .OrderBy(x => x.NormalizedName)
            .Select(x => new SecretSummary(x.Id, x.ProjectId, x.Name, x.Kind, x.State,
                x.CurrentVersionId == null ? null : x.Versions.Where(v => v.Id == x.CurrentVersionId).Select(v => (int?)v.VersionNumber).FirstOrDefault(),
                x.Revision, x.UpdatedAt))
            .ToArrayAsync(cancellationToken);
    }

    private SecretVersion EncryptVersion(Secret secret, ReadOnlySpan<byte> material, DateTimeOffset? expiresAt)
    {
        var versionId = Guid.NewGuid();
        var dek = RandomNumberGenerator.GetBytes(KeyBytes);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[material.Length];
        var tag = new byte[TagBytes];
        try
        {
            using var aes = new AesGcm(dek, TagBytes);
            aes.Encrypt(nonce, material, ciphertext, tag, BuildAad(secret.Id, versionId));
            var wrapped = keyAuthority.Wrap(secret.Id, versionId, dek);
            return new SecretVersion
            {
                Id = versionId,
                SecretId = secret.Id,
                VersionNumber = secret.Versions.Count == 0 ? 1 : secret.Versions.Max(x => x.VersionNumber) + 1,
                KeyId = wrapped.KeyId,
                WrappedDek = wrapped.Ciphertext,
                WrapNonce = wrapped.Nonce,
                WrapTag = wrapped.Tag,
                Ciphertext = ciphertext,
                CiphertextNonce = nonce,
                CiphertextTag = tag,
                CiphertextSha256 = Convert.ToHexString(SHA256.HashData(ciphertext)).ToLowerInvariant(),
                PlaintextLength = material.Length,
                CreatedAt = clock.UtcNow,
                ExpiresAt = expiresAt
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    private byte[] Decrypt(SecretVersion version, Guid secretId)
    {
        var dek = keyAuthority.Unwrap(secretId, version.Id, Wrapped(version));
        var plaintext = ArrayPool<byte>.Shared.Rent(version.PlaintextLength);
        try
        {
            using var aes = new AesGcm(dek, TagBytes);
            aes.Decrypt(version.CiphertextNonce, version.Ciphertext, version.CiphertextTag, plaintext.AsSpan(0, version.PlaintextLength), BuildAad(secretId, version.Id));
            var bounded = plaintext.AsSpan(0, version.PlaintextLength).ToArray();
            CryptographicOperations.ZeroMemory(plaintext);
            ArrayPool<byte>.Shared.Return(plaintext);
            return bounded;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            ArrayPool<byte>.Shared.Return(plaintext);
            throw new CryptographicException("Secret material could not be authenticated.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    private async Task ValidateLeaseAsync(ContextHubRequestActor actor, Secret secret, SecretVersion version, SecretLease lease,
        string capability, long expectedRevision, string purpose, string target, string requestId, SecretLeaseKind? requiredKind,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        if (lease.Revision != expectedRevision || lease.State != SecretLeaseState.Active || lease.ExpiresAt <= now ||
            lease.AuthorityRevision != secret.Revision || secret.State != SecretState.Active || version.State != SecretVersionState.Active ||
            version.ExpiresAt <= now || lease.UsedCount >= lease.MaxUses || lease.ActiveUses >= lease.MaxConcurrency ||
            (requiredKind.HasValue && lease.Kind != requiredKind.Value) ||
            !string.Equals(lease.ActorId, ActorId(actor), StringComparison.Ordinal) ||
            !string.Equals(lease.Purpose, RequireText(purpose, nameof(purpose), 200), StringComparison.Ordinal) ||
            !string.Equals(lease.Target, RequireText(target, nameof(target), 1000), StringComparison.Ordinal) ||
            !FixedEquals(lease.CapabilityHash, Hash(capability))) throw Unavailable();
        var replayed = await dbContext.SecretAccessEvents.AnyAsync(x => x.LeaseId == lease.Id && x.RequestId == requestId, cancellationToken);
        if (replayed) throw Unavailable();
        var right = lease.Kind == SecretLeaseKind.SshSigner ? SecretRight.SshSign : SecretRight.Use;
        await EnsureFoundationRightAsync(actor, secret.ProjectId, right, secret.Id, cancellationToken);
        await EnsureSecretRuleAsync(actor, secret, right, cancellationToken);
    }

    private async Task ReleaseActiveUseAsync(Guid leaseId, ContextHubRequestActor actor, CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await dbContext.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            await dbContext.AcquireTransactionLockAsync($"secret-lease:{leaseId:D}", cancellationToken);
            var lease = await Scope(dbContext.SecretLeases, actor).SingleAsync(x => x.Id == leaseId, cancellationToken);
            lease.ActiveUses = Math.Max(0, lease.ActiveUses - 1);
            lease.UpdatedAt = clock.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch when (cancellationToken.IsCancellationRequested)
        {
            // Scheduled reconciliation clears expired in-flight counters after an interrupted consumer.
        }
    }

    private async Task EnsureFoundationRightAsync(ContextHubRequestActor actor, string projectId, SecretRight right, Guid? secretId, CancellationToken cancellationToken)
    {
        var result = await foundation.EvaluateAsync(projectId, ActorId(actor), [$"secret.{right.ToString().ToLowerInvariant()}"],
            secretId.HasValue ? "Secret" : null, secretId?.ToString("D"), cancellationToken);
        if (!result.Decisions.Single().Allowed) throw Unavailable();
    }

    private async Task EnsureSecretRuleAsync(ContextHubRequestActor actor, Secret secret, SecretRight right, CancellationToken cancellationToken)
    {
        var principal = ActorId(actor);
        var now = clock.UtcNow;
        var grants = await Scope(dbContext.SecretGrants.AsNoTracking(), actor)
            .Where(x => x.SecretId == secret.Id && (x.PrincipalId == principal || x.PrincipalId == "*") && x.Right == right && (x.ExpiresAt == null || x.ExpiresAt > now))
            .Select(x => x.Effect).ToArrayAsync(cancellationToken);
        var secretPolicies = await Scope(dbContext.SecretPolicies.AsNoTracking(), actor)
            .Where(x => x.ProjectId == secret.ProjectId && x.SecretId == secret.Id && (x.PrincipalId == principal || x.PrincipalId == "*") && x.Right == right)
            .Select(x => x.Effect).ToArrayAsync(cancellationToken);
        var projectPolicies = await Scope(dbContext.SecretPolicies.AsNoTracking(), actor)
            .Where(x => x.ProjectId == secret.ProjectId && x.SecretId == null && (x.PrincipalId == principal || x.PrincipalId == "*") && x.Right == right)
            .Select(x => x.Effect).ToArrayAsync(cancellationToken);
        var tier = grants.Length > 0 ? grants : secretPolicies.Length > 0 ? secretPolicies : projectPolicies;
        if (tier.Length == 0 || tier.Contains(AuthorizationEffect.Deny) || !tier.Contains(AuthorizationEffect.Allow)) throw Unavailable();
    }

    private async Task<Secret> FindVisibleSecretAsync(Guid id, ContextHubRequestActor actor, bool tracking, CancellationToken cancellationToken)
    {
        var query = Scope(dbContext.Secrets, actor).Include(x => x.Versions).AsQueryable();
        if (!tracking) query = query.AsNoTracking();
        return await query.SingleOrDefaultAsync(x => x.Id == id, cancellationToken) ?? throw Unavailable();
    }

    private async Task AddAccessEventAsync(Secret secret, SecretVersion? version, SecretLease? lease, ContextHubRequestActor actor,
        SecretAccessOperation operation, string purpose, string target, string requestId, bool allowed, string reasonCode, CancellationToken cancellationToken)
        => await dbContext.SecretAccessEvents.AddAsync(new SecretAccessEvent
        {
            SecretId = secret.Id,
            SecretVersionId = version?.Id,
            LeaseId = lease?.Id,
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = secret.ProjectId,
            ActorId = ActorId(actor),
            Operation = operation,
            Purpose = purpose,
            TargetHash = Hash(target),
            RequestId = requestId,
            Allowed = allowed,
            ReasonCode = reasonCode,
            CreatedAt = clock.UtcNow
        }, cancellationToken);

    private static SecretSummary Map(Secret value, int? currentVersion)
        => new(value.Id, value.ProjectId, value.Name, value.Kind, value.State, currentVersion, value.Revision, value.UpdatedAt);

    private static WrappedSecretKey Wrapped(SecretVersion value) => new(value.KeyId, value.WrappedDek, value.WrapNonce, value.WrapTag);
    private static byte[] BuildAad(Guid secretId, Guid versionId) => Encoding.UTF8.GetBytes($"ContextHub.Secret.Material|1|{secretId:D}|{versionId:D}");
    private static string ActorId(ContextHubRequestActor actor) => actor.UserId!.Value.ToString("D");
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static bool FixedEquals(string left, string right) => CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));
    private static UnauthorizedAccessException Unavailable() => new("Secret resource is unavailable.");
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

    private ContextHubRequestActor RequireActor(string scope)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, scope);
        return actor;
    }

    private static void EnsureAllowed(StepUpAuthorizationResult result)
    {
        if (result.Outcome != StepUpRequirementOutcome.Allowed) throw new StepUpRequiredException(result);
    }

    private static string RequireText(string value, string name, int maximumLength)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0 || normalized.Length > maximumLength) throw new ArgumentException($"{name} is required and must not exceed {maximumLength} characters.", name);
        return normalized;
    }

    private static string NormalizeName(string value) => string.Join('-', value.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private sealed class DelegateSecretMaterialConsumer(Func<SecretUseContext, ReadOnlyMemory<byte>, CancellationToken, Task> action) : ISecretMaterialConsumer
    {
        public Task ExecuteAsync(SecretUseContext context, ReadOnlyMemory<byte> material, CancellationToken cancellationToken) => action(context, material, cancellationToken);
    }
}

public sealed class StepUpRequiredException(StepUpAuthorizationResult decision) : InvalidOperationException(decision.ReasonCode)
{
    public StepUpAuthorizationResult Decision { get; } = decision;
}

public static class SecretTargetBinding
{
    public static string Ssh(string host, int port, string user)
        => $"ssh://{NormalizeHost(host)}:{port}/{RequireSegment(user, nameof(user))}";

    public static byte[] BuildSshAuthenticationPayload(string sessionId, string host, int port, string user, ReadOnlySpan<byte> challenge)
    {
        var prefix = Encoding.UTF8.GetBytes(string.Join('|', "ContextHub.SshAuthentication", 1, RequireSegment(sessionId, nameof(sessionId)), NormalizeHost(host), port, RequireSegment(user, nameof(user))));
        var payload = new byte[prefix.Length + 1 + challenge.Length];
        prefix.CopyTo(payload, 0);
        payload[prefix.Length] = 0;
        challenge.CopyTo(payload.AsSpan(prefix.Length + 1));
        return payload;
    }

    private static string NormalizeHost(string host)
    {
        var normalized = RequireSegment(host, nameof(host)).ToLowerInvariant();
        if (normalized.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '.' or '-' or ':' or '[' or ']'))) throw new ArgumentException("SSH host contains unsupported characters.", nameof(host));
        return normalized;
    }

    private static string RequireSegment(string value, string name)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is 0 or > 512 || normalized.Contains('|') || normalized.Contains('\0')) throw new ArgumentException($"{name} is invalid.", name);
        return normalized;
    }
}
