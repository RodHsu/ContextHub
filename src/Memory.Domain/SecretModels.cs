namespace Memory.Domain;

public enum SecretKind
{
    Opaque,
    ApiToken,
    OAuthCredential,
    SshPrivateKeyPkcs8,
    SshCertificateAuthorityReference,
    TotpSeed
}

public enum SecretState
{
    Active,
    Revoked,
    Compromised
}

public enum SecretVersionState
{
    Active,
    Retired,
    Revoked,
    Compromised
}

public enum SecretRelationKind
{
    Project,
    WorkItem,
    ConnectionProfile,
    Rotates,
    Supersedes
}

public enum SecretRight
{
    Metadata,
    Use,
    Manage,
    Rotate,
    Revoke,
    Reveal,
    Export,
    SshIssue,
    SshSign
}

public enum SecretLeaseKind
{
    Use,
    SshSigner,
    SshCertificate
}

public enum SecretLeaseState
{
    Active,
    Exhausted,
    Revoked,
    Expired
}

public enum SecretAccessOperation
{
    Create,
    AddVersion,
    Rotate,
    Use,
    Reveal,
    Revoke,
    Grant,
    IssueSshCertificate,
    RenewSshCertificate,
    SignSshAuthentication
}

public enum AuthenticationMethod
{
    Password,
    Totp,
    RecoveryCode,
    WebAuthnPlatform,
    WebAuthnSecurityKey,
    Passkey
}

public enum AssuranceLevel
{
    Aal0 = 0,
    Aal1 = 1,
    Aal2 = 2,
    Aal3 = 3
}

public enum StepUpAssertionState
{
    Active,
    Exhausted,
    Revoked,
    Expired
}

public enum StepUpOperationClass
{
    SecretCreate,
    SecretVersionCreate,
    SecretUseLeaseCreate,
    SshCertificateIssue,
    CredentialRotate,
    CredentialRevoke,
    SecretReveal,
    RestrictedRelease,
    SensitiveAuthorizationChange,
    SecretExport,
    BreakGlassReveal,
    KekDestructiveOperation,
    SshCaDestructiveOperation,
    SecurityBoundaryDisable,
    MfaFactorEnroll,
    MfaFactorRemove,
    MfaRecovery
}

public enum StepUpRequirementOutcome
{
    Allowed,
    RequiresStepUp,
    RequiresExternalApproval,
    Disabled
}

public enum SshCertificateState
{
    Active,
    Superseded,
    Expired,
    Revoked
}

public sealed class Secret
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public SecretKind Kind { get; set; }
    public SecretState State { get; set; } = SecretState.Active;
    public Guid? CurrentVersionId { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? CompromisedAt { get; set; }
    public ICollection<SecretVersion> Versions { get; set; } = [];
    public ICollection<SecretRelation> Relations { get; set; } = [];
}

public sealed class SecretVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SecretId { get; set; }
    public int VersionNumber { get; set; }
    public SecretVersionState State { get; set; } = SecretVersionState.Active;
    public int EnvelopeSchemaVersion { get; set; } = 1;
    public string EncryptionAlgorithm { get; set; } = "AES-256-GCM";
    public string KeyId { get; set; } = string.Empty;
    public byte[] WrappedDek { get; set; } = [];
    public byte[] WrapNonce { get; set; } = [];
    public byte[] WrapTag { get; set; } = [];
    public byte[] Ciphertext { get; set; } = [];
    public byte[] CiphertextNonce { get; set; } = [];
    public byte[] CiphertextTag { get; set; } = [];
    public string CiphertextSha256 { get; set; } = string.Empty;
    public int PlaintextLength { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RetiredAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? CompromisedAt { get; set; }
    public Secret? Secret { get; set; }
}

public sealed class SecretRelation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SecretId { get; set; }
    public SecretRelationKind Kind { get; set; }
    public string TargetProjectId { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public bool IsStale { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastValidatedAt { get; set; }
    public Secret? Secret { get; set; }
}

public sealed class SecretGrant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public Guid SecretId { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public string PrincipalId { get; set; } = string.Empty;
    public SecretRight Right { get; set; }
    public AuthorizationEffect Effect { get; set; }
    public string EvidenceRef { get; set; } = string.Empty;
    public long Revision { get; set; } = 1;
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class SecretPolicy
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public Guid? SecretId { get; set; }
    public string PrincipalId { get; set; } = string.Empty;
    public SecretRight Right { get; set; }
    public AuthorizationEffect Effect { get; set; }
    public string EvidenceRef { get; set; } = string.Empty;
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class SecretLease
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SecretId { get; set; }
    public Guid SecretVersionId { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public string ActorId { get; set; } = string.Empty;
    public Guid? ExecutionId { get; set; }
    public SecretLeaseKind Kind { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public Guid CapabilityId { get; set; } = Guid.NewGuid();
    public string CapabilityHash { get; set; } = string.Empty;
    public long AuthorityRevision { get; set; }
    public long Revision { get; set; } = 1;
    public int MaxUses { get; set; } = 1;
    public int UsedCount { get; set; }
    public int MaxConcurrency { get; set; } = 1;
    public int ActiveUses { get; set; }
    public SecretLeaseState State { get; set; } = SecretLeaseState.Active;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class SecretAccessEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SecretId { get; set; }
    public Guid? SecretVersionId { get; set; }
    public Guid? LeaseId { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public string ActorId { get; set; } = string.Empty;
    public SecretAccessOperation Operation { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public string TargetHash { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
    public bool Allowed { get; set; }
    public string ReasonCode { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class StepUpAssertion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ActorUserId { get; set; }
    public string SessionHash { get; set; } = string.Empty;
    public AuthenticationMethod AuthenticationMethod { get; set; } = AuthenticationMethod.Password;
    public AssuranceLevel AssuranceLevel { get; set; } = AssuranceLevel.Aal1;
    public string Purpose { get; set; } = string.Empty;
    public string? ResourceType { get; set; }
    public string? ResourceId { get; set; }
    public string NonceHash { get; set; } = string.Empty;
    public long MfaAuthorityRevision { get; set; } = 1;
    public string MfaPolicyRevision { get; set; } = string.Empty;
    public long Revision { get; set; } = 1;
    public int MaxUses { get; set; } = 1;
    public int UsedCount { get; set; }
    public StepUpAssertionState State { get; set; } = StepUpAssertionState.Active;
    public DateTimeOffset AuthTime { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class StepUpAuthenticationAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ActorUserId { get; set; }
    public string SessionHash { get; set; } = string.Empty;
    public bool Succeeded { get; set; }
    public string ReasonCode { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class MfaAuthorityState
{
    public Guid TenantId { get; set; }
    public Guid ActorUserId { get; set; }
    public long Revision { get; set; } = 1;
    public string PolicyRevision { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum MfaFactorState
{
    Pending,
    Active,
    Removed,
    Revoked
}

public enum WebAuthnCredentialKind
{
    Platform,
    RoamingSecurityKey,
    Passkey
}

public enum WebAuthnCeremonyKind
{
    Registration,
    Authentication
}

public enum WebAuthnCeremonyState
{
    Pending,
    Used,
    Expired,
    Failed
}

public enum MfaSecurityAction
{
    TotpEnrollmentStarted,
    TotpEnrolled,
    TotpVerified,
    TotpReplayRejected,
    RecoveryCodeUsed,
    RecoveryCodeReplayRejected,
    WebAuthnRegistrationStarted,
    WebAuthnRegistered,
    WebAuthnAuthenticationStarted,
    WebAuthnAuthenticated,
    WebAuthnReplayRejected,
    FactorRemoved,
    FactorReset,
    RecoveryApproved,
    AssuranceIssued
}

public sealed class TotpFactor
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ActorUserId { get; set; }
    public Guid SeedSecretId { get; set; }
    public Guid SeedSecretVersionId { get; set; }
    public string Issuer { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public long AuthorityRevisionAtStart { get; set; } = 1;
    public string PolicyRevisionAtStart { get; set; } = string.Empty;
    public AssuranceLevel RequiredAssuranceAtStart { get; set; } = AssuranceLevel.Aal1;
    public Guid AuthorizationAssertionId { get; set; }
    public long AuthorizationAssertionRevision { get; set; }
    public MfaFactorState State { get; set; } = MfaFactorState.Pending;
    public long? LastAcceptedCounter { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public DateTimeOffset? RemovedAt { get; set; }
}

public sealed class MfaRecoveryCode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TotpFactorId { get; set; }
    public byte[] Salt { get; set; } = [];
    public byte[] CodeHash { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}

public sealed class WebAuthnCredential
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ActorUserId { get; set; }
    public byte[] CredentialId { get; set; } = [];
    public byte[] PublicKey { get; set; } = [];
    public byte[] UserHandle { get; set; } = [];
    public uint SignCount { get; set; }
    public string Transports { get; set; } = string.Empty;
    public WebAuthnCredentialKind Kind { get; set; }
    public bool UserVerificationRequired { get; set; } = true;
    public bool IsBackupEligible { get; set; }
    public bool IsBackedUp { get; set; }
    public Guid AaGuid { get; set; }
    public MfaFactorState State { get; set; } = MfaFactorState.Active;
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastUsedAt { get; set; }
    public DateTimeOffset? RemovedAt { get; set; }
}

public sealed class WebAuthnCeremony
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ActorUserId { get; set; }
    public string SessionHash { get; set; } = string.Empty;
    public WebAuthnCeremonyKind Kind { get; set; }
    public WebAuthnCeremonyState State { get; set; } = WebAuthnCeremonyState.Pending;
    public string Purpose { get; set; } = string.Empty;
    public string? ResourceType { get; set; }
    public string? ResourceId { get; set; }
    public string OptionsJson { get; set; } = string.Empty;
    public string ChallengeHash { get; set; } = string.Empty;
    public bool UserVerificationRequired { get; set; } = true;
    public long AuthorityRevisionAtStart { get; set; } = 1;
    public string PolicyRevisionAtStart { get; set; } = string.Empty;
    public AssuranceLevel RequiredAssuranceAtStart { get; set; } = AssuranceLevel.Aal3;
    public Guid? AuthorizationAssertionId { get; set; }
    public long? AuthorizationAssertionRevision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}

public sealed class MfaSecurityEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ActorUserId { get; set; }
    public Guid? FactorId { get; set; }
    public MfaSecurityAction Action { get; set; }
    public AuthenticationMethod? AuthenticationMethod { get; set; }
    public AssuranceLevel? AssuranceLevel { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public string ResourceHash { get; set; } = string.Empty;
    public string ReasonCode { get; set; } = string.Empty;
    public bool Succeeded { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class SshCertificateLease
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SecretLeaseId { get; set; }
    public Guid CaSecretId { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public string ActorId { get; set; } = string.Empty;
    public Guid? ExecutionId { get; set; }
    public string TargetHost { get; set; } = string.Empty;
    public int TargetPort { get; set; } = 22;
    public string TargetUser { get; set; } = string.Empty;
    public string PublicKey { get; set; } = string.Empty;
    public string PublicKeyFingerprint { get; set; } = string.Empty;
    public string Certificate { get; set; } = string.Empty;
    public string CertificateFingerprint { get; set; } = string.Empty;
    public long Serial { get; set; }
    public int RenewalCount { get; set; }
    public int MaxRenewals { get; set; }
    public DateTimeOffset SessionStartedAt { get; set; }
    public DateTimeOffset MaxSessionExpiresAt { get; set; }
    public DateTimeOffset ValidAfter { get; set; }
    public DateTimeOffset ValidBefore { get; set; }
    public DateTimeOffset RenewalEligibleAt { get; set; }
    public long AuthorityRevision { get; set; }
    public long Revision { get; set; } = 1;
    public SshCertificateState State { get; set; } = SshCertificateState.Active;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class SshRevocationRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SshCertificateLeaseId { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public long Serial { get; set; }
    public string CertificateFingerprint { get; set; } = string.Empty;
    public string ReasonCode { get; set; } = string.Empty;
    public bool KrlRequired { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReconciledAt { get; set; }
}
