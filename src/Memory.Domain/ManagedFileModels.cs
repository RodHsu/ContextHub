namespace Memory.Domain;

public enum FileAssetState
{
    Active,
    Unassigned,
    LogicalDeleted
}

public enum FileVersionLifecycle
{
    PendingUpload,
    Uploaded,
    IntegrityVerified,
    SecurityScanning,
    Classified,
    Profiling,
    Ready,
    NeedsRescan,
    Quarantined,
    LogicalDeleted
}

public enum FileClassification
{
    Normal,
    Sensitive,
    Restricted,
    Quarantined
}

public enum FileRelationKind
{
    Project,
    Discussion,
    WorkItem,
    LegalHold,
    SecurityHold,
    ParentFile,
    Alias
}

public enum FileRepresentationKind
{
    ExtractedText,
    Ocr,
    Preview,
    Thumbnail,
    SearchProjection
}

public enum FileOperation
{
    Metadata,
    Read,
    Download,
    Extract,
    Ocr,
    Embed,
    SearchIndex,
    AutoShare,
    CrossProjectShare,
    SecurityRescan,
    SecurityRelease,
    Delete
}

public enum SecurityFindingCategory
{
    Malware,
    Executable,
    Macro,
    ArchiveBomb,
    EmbeddedPayload,
    CredentialSecret,
    PrivateKey,
    Token,
    Pii,
    FinancialSensitive,
    SourceCodeSensitive,
    PolicyViolation,
    ScanFailure,
    ContentTypeMismatch
}

public enum SecurityFindingSeverity
{
    Informational,
    Low,
    Medium,
    High,
    Critical
}

public enum SecurityFindingDisposition
{
    Open,
    Confirmed,
    FalsePositive,
    AcceptedRisk,
    Resolved
}

public enum FileDeletionState
{
    DeleteRequested,
    LogicalDeleted,
    PhysicalDeletePending,
    PrimaryDeleted,
    BackupRetentionPending,
    FullyExpired,
    Verified
}

public sealed class FileAsset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public string? ProjectId { get; set; }
    public string LogicalFileName { get; set; } = string.Empty;
    public string NormalizedFileName { get; set; } = string.Empty;
    public FileAssetState State { get; set; }
    public string CreatedByActorId { get; set; } = string.Empty;
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? GovernanceReminderAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public ICollection<FileVersion> Versions { get; set; } = [];
    public ICollection<FileRelation> Relations { get; set; } = [];
}

public sealed class FileVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FileAssetId { get; set; }
    public Guid ManagedObjectId { get; set; }
    public int VersionNumber { get; set; }
    public string ContentSha256 { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public string DeduplicationScopeKey { get; set; } = string.Empty;
    public FileVersionLifecycle Lifecycle { get; set; } = FileVersionLifecycle.IntegrityVerified;
    public FileClassification Classification { get; set; } = FileClassification.Restricted;
    public long ClassificationRevision { get; set; } = 1;
    public bool SearchProjectionAllowed { get; set; }
    public bool EmbeddingAllowed { get; set; }
    public bool NeedsRescan { get; set; }
    public DateTimeOffset? LastScanAt { get; set; }
    public string ScannerSetVersion { get; set; } = string.Empty;
    public string SecurityPolicyVersion { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public FileAsset? FileAsset { get; set; }
    public ManagedObject? ManagedObject { get; set; }
    public ICollection<FileSecurityFinding> Findings { get; set; } = [];
    public ICollection<FileRepresentation> Representations { get; set; } = [];
}

public sealed class FileRelation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FileAssetId { get; set; }
    public FileRelationKind Kind { get; set; }
    public string TargetProjectId { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public FileAsset? FileAsset { get; set; }
}

public sealed class FileProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FileVersionId { get; set; }
    public string DetectedContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public int? PageCount { get; set; }
    public string MetadataJson { get; set; } = "{}";
    public long ClassificationRevision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class FileRepresentation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FileVersionId { get; set; }
    public Guid? ManagedObjectId { get; set; }
    public FileRepresentationKind Kind { get; set; }
    public FileClassification Classification { get; set; }
    public long SourceClassificationRevision { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? InvalidatedAt { get; set; }
    public FileVersion? FileVersion { get; set; }
}

public sealed class FileAccessEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FileAssetId { get; set; }
    public Guid? FileVersionId { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public string ActorId { get; set; } = string.Empty;
    public FileOperation Operation { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public bool Allowed { get; set; }
    public string ReasonCode { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class FileSecurityFinding
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FileVersionId { get; set; }
    public string ScannerRuleId { get; set; } = string.Empty;
    public SecurityFindingCategory Category { get; set; }
    public SecurityFindingSeverity Severity { get; set; }
    public decimal Confidence { get; set; }
    public string ScannerVersion { get; set; } = string.Empty;
    public DateTimeOffset DetectedAt { get; set; }
    public SecurityFindingDisposition Disposition { get; set; }
    public string EvidenceHash { get; set; } = string.Empty;
    public string RedactedEvidence { get; set; } = string.Empty;
    public FileVersion? FileVersion { get; set; }
}

public sealed class FileSearchProjection
{
    public Guid FileVersionId { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public FileClassification Classification { get; set; }
    public long ClassificationRevision { get; set; }
    public bool ContentSearchEnabled { get; set; }
    public bool EmbeddingEnabled { get; set; }
    public string RedactedText { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? InvalidatedAt { get; set; }
}

public sealed class FileDeletionRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FileAssetId { get; set; }
    public Guid ManagedObjectId { get; set; }
    public FileDeletionState State { get; set; }
    public bool LegalHold { get; set; }
    public bool ReferenceBlocked { get; set; }
    public DateTimeOffset? RetainUntil { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
    public int AttemptCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? PrimaryDeletedAt { get; set; }
    public DateTimeOffset? FullyExpiredAt { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
}

public enum CanonicalTagStatus
{
    Active,
    Deprecated,
    Superseded
}

public enum CanonicalTagTelemetryKind
{
    SearchImpression,
    FilterUse,
    Selection,
    Rejection,
    Mismatch
}

public enum CanonicalTagGovernanceProposalKind
{
    NormalizeAlias,
    CleanupStaleSuggestion,
    Merge,
    Split,
    Rename,
    Deprecate
}

public enum CanonicalTagGovernanceProposalStatus
{
    Pending,
    Applied,
    Dismissed
}

public sealed class CanonicalTagTelemetryEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public Guid DefinitionId { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public CanonicalTagTelemetryKind Kind { get; set; }
    public string ReasonCode { get; set; } = string.Empty;
    public string QueryHash { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public string ActorType { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class CanonicalTagDailyAggregate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public Guid DefinitionId { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public DateOnly Day { get; set; }
    public long SearchImpressions { get; set; }
    public long FilterUses { get; set; }
    public long Selections { get; set; }
    public long Rejections { get; set; }
    public long Mismatches { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class CanonicalTagGovernanceProposal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public CanonicalTagGovernanceProposalKind Kind { get; set; }
    public CanonicalTagGovernanceProposalStatus Status { get; set; }
    public Guid SourceDefinitionId { get; set; }
    public Guid? TargetDefinitionId { get; set; }
    public string ProposedValue { get; set; } = string.Empty;
    public string ReasonCode { get; set; } = string.Empty;
    public string CandidateResourceIdsJson { get; set; } = "[]";
    public int AffectedBindingCount { get; set; }
    public decimal Confidence { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? AppliedAt { get; set; }
}
