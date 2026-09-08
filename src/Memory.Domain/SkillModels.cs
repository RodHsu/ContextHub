namespace Memory.Domain;

public enum SkillLifecycleStatus
{
    Draft,
    Published,
    Deprecated,
    Revoked,
    Archived
}

public enum SkillSourceKind
{
    LocalUpload,
    Repository,
    ProviderAdapter,
    Generated
}

public enum SkillTrustLevel
{
    Unverified,
    SourceVerified,
    Signed,
    Attested,
    Trusted
}

public enum SkillRiskLevel
{
    Low,
    Medium,
    High,
    Critical
}

public enum SkillBindingScope
{
    Tenant,
    Project,
    Repository,
    AgentType,
    Execution
}

public enum SkillBindingMode
{
    Recommended,
    Auto,
    Required,
    AllowList,
    Disabled
}

public enum SkillDependencyKind
{
    Requires,
    Optional,
    ConflictsWith
}

public enum SkillSearchGenerationStatus
{
    Building,
    Validating,
    Active,
    Failed,
    RolledBack,
    Retired
}

public enum SkillResolutionStatus
{
    Searching,
    NoApplicableSkill,
    Selected,
    RequiresHumanDecision,
    Cancelled,
    Completed
}

public enum SkillRejectionStage
{
    SearchCandidateRejected,
    SelectionCancelledBeforePin,
    PinnedReleasedBeforeMaterialize,
    MaterializedRejectedBeforeInvoke,
    InvocationAborted,
    PostInvocationRejected,
    RevokedOrPolicyCancelled
}

public enum SkillRejectionReason
{
    NotApplicable,
    MissingCapability,
    ConflictWithTask,
    StaleAssumption,
    UnsupportedRuntime,
    RepositoryMismatch,
    InsufficientInformation,
    PolicyConflict,
    DuplicateCoverage,
    SupersededByBetterSkill,
    LowConfidenceMatch,
    IncorrectTagOrTrigger,
    IncorrectCompatibilityMetadata,
    InstructionMismatch,
    ToolUnavailable,
    NetworkUnavailable,
    SecretUnavailable,
    PermissionDenied,
    Revoked,
    VersionConflict,
    ExecutionContextChanged,
    ResultNotUseful,
    ResultInvalid,
    NeedAlternative,
    Other
}

public enum SkillTelemetryEventType
{
    SearchImpression,
    Selected,
    Rejected,
    SelectedThenReleased,
    Materialized,
    InvocationStarted,
    InvocationSucceeded,
    InvocationFailed,
    Cancellation
}

public enum SkillMaterializationStatus
{
    Active,
    Revoked,
    Cleaned,
    Failed
}

public enum SkillMetadataProposalStatus
{
    Pending,
    Applied,
    Rejected,
    Stale,
    Failed
}

public enum SkillSourceDriftStatus
{
    InSync,
    Changed,
    Deleted,
    TrustChanged,
    Compromised
}

public enum SkillAnalyticsDimension
{
    Skill,
    SkillVersion,
    Project,
    Repository,
    AgentType
}

public sealed class Skill
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public string StableKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string WhenToUse { get; set; } = string.Empty;
    public string[] Tags { get; set; } = [];
    public string[] Aliases { get; set; } = [];
    public string License { get; set; } = string.Empty;
    public string MaintainersJson { get; set; } = "[]";
    public SkillRiskLevel RiskLevel { get; set; }
    public long MetadataVersion { get; set; } = 1;
    public Guid? DefaultVersionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public ICollection<SkillVersion> Versions { get; set; } = [];
    public ICollection<SkillBinding> Bindings { get; set; } = [];
}

public sealed class SkillVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SkillId { get; set; }
    public string Version { get; set; } = string.Empty;
    public SkillLifecycleStatus Status { get; set; } = SkillLifecycleStatus.Draft;
    public string ContentHash { get; set; } = string.Empty;
    public string BundleJson { get; set; } = "{}";
    public string SearchText { get; set; } = string.Empty;
    public string CompatibilityJson { get; set; } = "{}";
    public string[] RequiredCapabilities { get; set; } = [];
    public string[] RequiredTools { get; set; } = [];
    public string[] AllowedActions { get; set; } = [];
    public bool RequiresNetwork { get; set; }
    public bool RequiresSecrets { get; set; }
    public SkillSourceKind SourceKind { get; set; }
    public string SourceRef { get; set; } = string.Empty;
    public string SourceRevision { get; set; } = string.Empty;
    public SkillTrustLevel TrustLevel { get; set; }
    public string SignatureAlgorithm { get; set; } = string.Empty;
    public string SignatureValue { get; set; } = string.Empty;
    public bool SignatureVerified { get; set; }
    public string PublishEvidenceJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset? DeprecatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public Skill? Skill { get; set; }
    public ICollection<SkillVersionDependency> Dependencies { get; set; } = [];
}

public sealed class SkillVersionDependency
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SkillVersionId { get; set; }
    public Guid TargetSkillId { get; set; }
    public SkillDependencyKind Kind { get; set; }
    public string VersionConstraint { get; set; } = string.Empty;
    public SkillVersion? SkillVersion { get; set; }
}

public sealed class SkillBinding
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SkillId { get; set; }
    public SkillBindingScope Scope { get; set; }
    public string ScopeValue { get; set; } = string.Empty;
    public SkillBindingMode Mode { get; set; }
    public string VersionConstraint { get; set; } = string.Empty;
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Skill? Skill { get; set; }
}

public sealed class SkillSearchGeneration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public string SearchProfileVersion { get; set; } = string.Empty;
    public string EmbeddingModelId { get; set; } = string.Empty;
    public string EmbeddingModelVersion { get; set; } = string.Empty;
    public decimal Threshold { get; set; }
    public SkillSearchGenerationStatus Status { get; set; }
    public string BenchmarkJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ActivatedAt { get; set; }
}

public sealed class SkillSearchDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GenerationId { get; set; }
    public Guid SkillVersionId { get; set; }
    public string SearchText { get; set; } = string.Empty;
    public string TermsJson { get; set; } = "[]";
    public string EmbeddingJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class SkillResolution
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public Guid ExecutionId { get; set; }
    public Guid? WorkItemId { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public string RepositoryId { get; set; } = string.Empty;
    public string AgentType { get; set; } = string.Empty;
    public int Round { get; set; }
    public int MaxSearchRounds { get; set; }
    public int MaxSelectedSkills { get; set; }
    public string QueryHash { get; set; } = string.Empty;
    public string QueryTermsJson { get; set; } = "[]";
    public Guid SearchGenerationId { get; set; }
    public SkillResolutionStatus Status { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ICollection<SkillResolutionCandidate> Candidates { get; set; } = [];
    public ICollection<SkillResolutionPin> Pins { get; set; } = [];
}

public sealed class SkillResolutionCandidate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ResolutionId { get; set; }
    public Guid SkillVersionId { get; set; }
    public int Rank { get; set; }
    public decimal Score { get; set; }
    public decimal Threshold { get; set; }
    public string MatchReasonsJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
    public SkillResolution? Resolution { get; set; }
}

public sealed class SkillResolutionPin
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ResolutionId { get; set; }
    public Guid SkillVersionId { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public bool IsDependency { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public SkillResolution? Resolution { get; set; }
}

public sealed class SkillTelemetryEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public Guid SkillId { get; set; }
    public Guid SkillVersionId { get; set; }
    public Guid ExecutionId { get; set; }
    public Guid? WorkItemId { get; set; }
    public Guid? ResolutionId { get; set; }
    public int ResolutionRound { get; set; }
    public SkillTelemetryEventType EventType { get; set; }
    public SkillRejectionStage? RejectionStage { get; set; }
    public SkillRejectionReason? ReasonClass { get; set; }
    public string ReasonText { get; set; } = string.Empty;
    public string EvidenceJson { get; set; } = "[]";
    public string QueryHash { get; set; } = string.Empty;
    public int? CandidateRank { get; set; }
    public decimal? CandidateScore { get; set; }
    public decimal? Threshold { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public string RepositoryId { get; set; } = string.Empty;
    public string AgentType { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class SkillMaterialization
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public Guid ExecutionId { get; set; }
    public Guid ResolutionId { get; set; }
    public Guid SkillVersionId { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public SkillMaterializationStatus Status { get; set; }
    public string FailureReason { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CleanedAt { get; set; }
}

public sealed class SkillMetadataProposal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public Guid SkillId { get; set; }
    public long ExpectedMetadataVersion { get; set; }
    public string ExpectedMetadataHash { get; set; } = string.Empty;
    public string ProposedPatchJson { get; set; } = "{}";
    public string EvidenceJson { get; set; } = "{}";
    public decimal Confidence { get; set; }
    public SkillMetadataProposalStatus Status { get; set; }
    public string GovernanceRunId { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class SkillSourceObservation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public Guid SkillId { get; set; }
    public string SourceRef { get; set; } = string.Empty;
    public string ObservedRevision { get; set; } = string.Empty;
    public string ObservedContentHash { get; set; } = string.Empty;
    public SkillSourceDriftStatus Status { get; set; }
    public bool SourceAvailable { get; set; }
    public bool SignatureVerified { get; set; }
    public string EvidenceJson { get; set; } = "{}";
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTimeOffset ObservedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class SkillTelemetryDailyAggregate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public DateOnly AggregateDate { get; set; }
    public Guid SkillId { get; set; }
    public Guid SkillVersionId { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public string RepositoryId { get; set; } = string.Empty;
    public string AgentType { get; set; } = string.Empty;
    public SkillTelemetryEventType EventType { get; set; }
    public SkillRejectionStage? RejectionStage { get; set; }
    public SkillRejectionReason? ReasonClass { get; set; }
    public int EventCount { get; set; }
    public DateTimeOffset LastOccurredAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class SkillTelemetryAggregationLedger
{
    public Guid EventId { get; set; }
    public DateTimeOffset AggregatedAt { get; set; }
}

public sealed class SkillTelemetryReconciliationRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public int AggregatedEventCount { get; set; }
    public int AggregateRowCount { get; set; }
    public int DeletedRawEventCount { get; set; }
    public int DeletedAggregateRowCount { get; set; }
    public int ProtectedRawEventCount { get; set; }
    public int RawRetentionDays { get; set; }
    public int AggregateRetentionDays { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
