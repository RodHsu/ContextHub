using Memory.Domain;

namespace Memory.Application;

public sealed record PortableSkillFile(
    string Path,
    string ContentBase64,
    bool Executable = false,
    bool IsSymbolicLink = false);

public sealed record PortableSkillBundle(
    IReadOnlyList<PortableSkillFile> Files,
    string FormatVersion = "1.0");

public sealed record SkillDependencyInput(
    Guid TargetSkillId,
    SkillDependencyKind Kind,
    string VersionConstraint);

public sealed record SkillImportPreviewRequest(
    string StableKey,
    string Name,
    string Description,
    string WhenToUse,
    string Version,
    PortableSkillBundle Bundle,
    SkillSourceKind SourceKind,
    string SourceRef,
    string SourceRevision,
    string License,
    IReadOnlyList<string>? Tags = null,
    IReadOnlyList<string>? Aliases = null,
    IReadOnlyList<string>? Maintainers = null,
    IReadOnlyList<string>? RequiredCapabilities = null,
    IReadOnlyList<string>? RequiredTools = null,
    IReadOnlyList<string>? AllowedActions = null,
    IReadOnlyList<SkillDependencyInput>? Dependencies = null,
    SkillRiskLevel RiskLevel = SkillRiskLevel.Low,
    SkillTrustLevel TrustLevel = SkillTrustLevel.Unverified,
    bool RequiresNetwork = false,
    bool RequiresSecrets = false,
    string? SignatureAlgorithm = null,
    string? SignatureValue = null);

public sealed record SkillValidationIssue(
    string Code,
    string Message,
    string Severity,
    string? Path = null);

public sealed record SkillPublishEvidenceResult(
    string ValidatorVersion,
    string ContentHash,
    bool StaticValidationPassed,
    bool SelfTestExecuted,
    bool SelfTestPassed,
    bool SignatureVerified,
    IReadOnlyList<string> Checks,
    IReadOnlyList<SkillValidationIssue> Issues,
    string SelfTestMode = "NotExecuted",
    bool SandboxSelfTestExecuted = false,
    bool SandboxSelfTestPassed = false,
    bool PublishApprovalGranted = false,
    string ApprovalActor = "",
    string ApprovalReference = "",
    string ApprovalReason = "",
    bool SelfTestWaiverGranted = false,
    string CanaryReference = "",
    string SandboxReceiptId = "",
    string SandboxFailureCode = "",
    string SandboxSummary = "",
    long SandboxDurationMilliseconds = 0,
    string SandboxContractVersion = "");

public static class SkillSandboxContract
{
    public const string Version = "1.0";
}

public sealed record SkillSandboxSelfTestDefinition(
    string Entrypoint,
    IReadOnlyList<string> Arguments,
    int TimeoutSeconds);

public sealed record SkillSandboxSelfTestRequest(
    string ContentHash,
    PortableSkillBundle Bundle,
    SkillSandboxSelfTestDefinition Definition);

public sealed record SkillSandboxSelfTestResult(
    bool Executed,
    bool Passed,
    string ReceiptId,
    string FailureCode,
    string Summary,
    long DurationMilliseconds,
    string ContractVersion = SkillSandboxContract.Version);

public sealed record SkillImportPreviewResult(
    string StableKey,
    string Version,
    string ContentHash,
    long BundleSizeBytes,
    string SkillMarkdown,
    IReadOnlyList<string> Paths,
    IReadOnlyList<string> SearchTerms,
    SkillPublishEvidenceResult Validation,
    bool CanImport,
    bool RequiresPublishApproval);

public sealed record SkillImportRequest(
    SkillImportPreviewRequest Skill,
    string IdempotencyKey);

public sealed record SkillVersionSummaryResult(
    Guid Id,
    Guid SkillId,
    string Version,
    SkillLifecycleStatus Status,
    string ContentHash,
    IReadOnlyList<string> RequiredCapabilities,
    IReadOnlyList<string> RequiredTools,
    IReadOnlyList<string> AllowedActions,
    SkillSourceKind SourceKind,
    string SourceRef,
    string SourceRevision,
    SkillTrustLevel TrustLevel,
    bool SignatureVerified,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PublishedAt,
    DateTimeOffset? DeprecatedAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset? ArchivedAt,
    IReadOnlyList<SkillDependencyInput>? Dependencies = null,
    SkillPublishEvidenceResult? PublishEvidence = null);

public sealed record SkillVersionDiffResult(
    Guid SkillId,
    SkillVersionSummaryResult Left,
    SkillVersionSummaryResult Right,
    IReadOnlyList<string> AddedPaths,
    IReadOnlyList<string> RemovedPaths,
    IReadOnlyList<string> ChangedPaths,
    bool DiscoveryContractChanged,
    bool ProvenanceChanged);

public sealed record SkillSummaryResult(
    Guid Id,
    string StableKey,
    string Name,
    string Description,
    string WhenToUse,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Aliases,
    string License,
    IReadOnlyList<string> Maintainers,
    SkillRiskLevel RiskLevel,
    long MetadataVersion,
    string MetadataHash,
    Guid? DefaultVersionId,
    IReadOnlyList<SkillBindingResult> Bindings,
    IReadOnlyList<SkillVersionSummaryResult> Versions,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt,
    Guid? OwnerUserId = null);

public sealed record SkillImportResult(
    SkillSummaryResult Skill,
    SkillVersionSummaryResult Version,
    bool Created,
    bool Replayed,
    SkillPublishEvidenceResult Validation);

public sealed record SkillPublishApprovalEvidence(
    string ApprovalReference,
    string Reason,
    bool SelfTestWaiverGranted = false,
    string? CanaryReference = null);

public sealed record SkillPublishRequest(
    Guid SkillVersionId,
    string ExpectedContentHash,
    bool ApprovalGranted,
    string IdempotencyKey,
    SkillPublishApprovalEvidence? ApprovalEvidence = null);

public sealed record SkillLifecycleRequest(
    Guid SkillVersionId,
    SkillLifecycleStatus TargetStatus,
    string Reason,
    string IdempotencyKey);

public sealed record SkillDefaultVersionRequest(
    Guid SkillId,
    Guid SkillVersionId,
    long ExpectedMetadataVersion,
    string IdempotencyKey);

public sealed record SkillBindingUpsertRequest(
    Guid SkillId,
    SkillBindingScope Scope,
    string ScopeValue,
    SkillBindingMode Mode,
    string VersionConstraint,
    long? ExpectedRevision,
    string IdempotencyKey);

public sealed record SkillBindingResult(
    Guid Id,
    Guid SkillId,
    SkillBindingScope Scope,
    string ScopeValue,
    SkillBindingMode Mode,
    string VersionConstraint,
    long Revision,
    DateTimeOffset UpdatedAt);

public sealed record SkillSourceObservationRequest(
    Guid SkillId,
    string SourceRef,
    string ObservedRevision,
    string ObservedContentHash,
    bool SourceAvailable,
    bool SignatureVerified,
    bool CompromiseReported,
    string? EvidenceRef,
    string IdempotencyKey);

public sealed record SkillSourceObservationResult(
    Guid ObservationId,
    Guid SkillId,
    SkillSourceDriftStatus Status,
    string SourceRef,
    string ObservedRevision,
    string ObservedContentHash,
    bool SourceAvailable,
    bool SignatureVerified,
    bool RequiresNewDraft,
    bool EmergencyRevocationRecommended,
    string EvidenceJson,
    DateTimeOffset ObservedAt,
    bool Replayed);

public sealed record SkillSearchPolicy(
    bool Enabled = true,
    bool Required = false,
    int TopN = 5,
    decimal? Threshold = null,
    int MaxSearchRounds = 3,
    int MaxSelectedSkills = 5,
    bool AllowDeprecated = true,
    bool AllowKeywordOnlyFallback = true,
    SkillRiskLevel MaximumRisk = SkillRiskLevel.Medium);

public sealed record SkillSearchForExecutionRequest(
    Guid ExecutionId,
    Guid? WorkItemId,
    string ProjectId,
    string RepositoryId,
    string AgentType,
    string Objective,
    IReadOnlyList<string>? Keywords,
    IReadOnlyList<string>? Tags,
    IReadOnlyList<string>? AvailableCapabilities,
    IReadOnlyList<string>? AvailableTools,
    IReadOnlyList<string>? AllowedActions,
    IReadOnlyList<Guid>? ExcludedSkillVersionIds,
    SkillSearchPolicy Policy,
    string IdempotencyKey,
    Guid? ExplicitSkillId = null,
    Guid? ExplicitSkillVersionId = null,
    int? Round = null);

public sealed record SkillSearchCandidateResult(
    Guid SkillId,
    Guid SkillVersionId,
    string Version,
    string Name,
    string Description,
    string WhenToUse,
    decimal Score,
    int Rank,
    decimal Threshold,
    IReadOnlyList<string> MatchReasons,
    SkillRiskLevel RiskLevel,
    IReadOnlyList<string> RequiredCapabilities,
    IReadOnlyList<string> RequiredTools,
    string ContentHash,
    SkillBindingMode EffectiveBindingMode,
    bool ExplicitCandidate);

public sealed record SkillSearchForExecutionResult(
    Guid ResolutionId,
    Guid ExecutionId,
    int Round,
    int MaxSearchRounds,
    SkillResolutionStatus Status,
    IReadOnlyList<SkillSearchCandidateResult> Candidates,
    string QueryHash,
    Guid SearchGenerationId,
    string SearchProfileVersion,
    string EmbeddingModelId,
    string EmbeddingModelVersion,
    decimal Threshold,
    bool DegradedKeywordOnly,
    bool Replayed,
    string OutcomeReason);

public sealed record SkillResolutionCandidateAuditResult(
    Guid SkillVersionId,
    string Version,
    string SkillName,
    int Rank,
    decimal Score,
    decimal Threshold,
    IReadOnlyList<string> MatchReasons,
    bool Pinned,
    bool Released,
    string ContentHash);

public sealed record SkillResolutionEventAuditResult(
    Guid EventId,
    Guid SkillVersionId,
    SkillTelemetryEventType EventType,
    SkillRejectionStage? RejectionStage,
    SkillRejectionReason? ReasonClass,
    string ReasonText,
    string EvidenceJson,
    DateTimeOffset OccurredAt);

public sealed record SkillResolutionDetailResult(
    Guid ResolutionId,
    Guid ExecutionId,
    Guid? WorkItemId,
    string ProjectId,
    string RepositoryId,
    string AgentType,
    int Round,
    int MaxSearchRounds,
    SkillResolutionStatus Status,
    string QueryHash,
    Guid SearchGenerationId,
    IReadOnlyList<SkillResolutionCandidateAuditResult> Candidates,
    IReadOnlyList<SkillResolutionEventAuditResult> Events,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SkillResolutionFeedbackRequest(
    Guid ResolutionId,
    Guid SkillVersionId,
    SkillRejectionStage Stage,
    SkillRejectionReason ReasonClass,
    string? ReasonText,
    IReadOnlyList<string>? EvidenceRefs,
    IReadOnlyList<string>? MissingCapabilities,
    IReadOnlyList<string>? RequestedAlternativeTags,
    IReadOnlyList<string>? RequestedAlternativeKeywords,
    string IdempotencyKey);

public sealed record SkillResolutionFeedbackResult(
    Guid EventId,
    Guid ResolutionId,
    Guid SkillVersionId,
    SkillRejectionStage Stage,
    SkillRejectionReason ReasonClass,
    bool Replayed,
    bool ReSearchAllowed,
    int NextRound,
    SkillResolutionStatus ResolutionStatus);

public sealed record SkillSelectForExecutionRequest(
    Guid ResolutionId,
    IReadOnlyList<Guid> SkillVersionIds,
    string? SelectionReason,
    string IdempotencyKey);

public sealed record SkillPinnedVersionResult(
    Guid SkillId,
    Guid SkillVersionId,
    string Version,
    string ContentHash,
    bool IsDependency);

public sealed record SkillSelectForExecutionResult(
    Guid ResolutionId,
    Guid ExecutionId,
    IReadOnlyList<SkillPinnedVersionResult> PinnedVersions,
    SkillResolutionStatus Status,
    bool Replayed);

public enum SkillExecutionSnapshotDecision
{
    Continue,
    ReResolve,
    StopRevoked,
    RequiresHumanDecision
}

public static class SkillExecutionSnapshotContract
{
    public const string Version = "1.0";
}

public sealed record SkillExecutionSnapshotCreateRequest(
    Guid ExecutionId,
    Guid ResolutionId,
    string ExecutionPackageContextVersion);

public sealed record SkillExecutionPinnedVersionSnapshot(
    Guid SkillId,
    Guid SkillVersionId,
    string Version,
    string ContentHash,
    bool IsDependency,
    SkillLifecycleStatus LifecycleStatus,
    SkillRiskLevel RiskLevel,
    IReadOnlyList<string> RequiredCapabilities,
    IReadOnlyList<string> RequiredTools,
    IReadOnlyList<string> AllowedActions,
    IReadOnlyList<SkillDependencyInput> Dependencies,
    string ScopePolicyHash,
    bool RequiresNetwork,
    bool RequiresSecrets);

public sealed record SkillExecutionSnapshotResult(
    string ContractVersion,
    Guid ExecutionId,
    Guid? WorkItemId,
    Guid ResolutionId,
    int ResolutionRound,
    string ProjectId,
    string RepositoryId,
    string AgentType,
    string ExecutionPackageContextVersion,
    string QueryHash,
    Guid SearchGenerationId,
    string SearchProfileVersion,
    string EmbeddingModelId,
    string EmbeddingModelVersion,
    IReadOnlyList<string> AvailableCapabilities,
    IReadOnlyList<string> AvailableTools,
    IReadOnlyList<string> AllowedActions,
    SkillRiskLevel MaximumRisk,
    IReadOnlyList<SkillExecutionPinnedVersionSnapshot> PinnedVersions,
    string SnapshotHash);

public sealed record SkillExecutionSnapshotRevalidateRequest(
    SkillExecutionSnapshotResult Snapshot,
    string CurrentExecutionPackageContextVersion,
    IReadOnlyList<string>? CurrentAvailableCapabilities,
    IReadOnlyList<string>? CurrentAvailableTools,
    IReadOnlyList<string>? CurrentAllowedActions,
    SkillRiskLevel CurrentMaximumRisk);

public sealed record SkillExecutionSnapshotIssue(
    string Code,
    Guid? SkillVersionId,
    SkillRejectionReason Reason,
    string Message);

public sealed record SkillExecutionSnapshotRevalidationResult(
    SkillExecutionSnapshotDecision Decision,
    bool SnapshotHashValid,
    bool ExactPinsValid,
    bool PolicyValid,
    IReadOnlyList<SkillExecutionSnapshotIssue> Issues,
    SkillExecutionSnapshotResult CurrentSnapshot);

public sealed record SkillVersionGetRequest(
    Guid ExecutionId,
    Guid ResolutionId,
    Guid SkillVersionId,
    string ExpectedContentHash);

public sealed record SkillVersionBundleResult(
    Guid SkillId,
    Guid SkillVersionId,
    string Version,
    string ContentHash,
    PortableSkillBundle Bundle,
    SkillLifecycleStatus Status,
    bool MidExecutionRevoked);

public sealed record SkillMaterializeRequest(
    Guid ExecutionId,
    Guid ResolutionId,
    Guid SkillVersionId,
    string ExpectedContentHash,
    string IdempotencyKey);

public sealed record SkillMaterializationResult(
    Guid MaterializationId,
    Guid SkillVersionId,
    string ContentHash,
    string RelativePath,
    SkillMaterializationStatus Status,
    bool Replayed);

public sealed record SkillMaterializationCleanupRequest(
    Guid ExecutionId,
    string Reason,
    string IdempotencyKey);

public sealed record SkillMaterializationCleanupResult(
    Guid ExecutionId,
    int CleanedCount,
    IReadOnlyList<Guid> MaterializationIds,
    bool Replayed);

public sealed record SkillInvocationRecordRequest(
    Guid ExecutionId,
    Guid? WorkItemId,
    Guid ResolutionId,
    Guid SkillVersionId,
    string ContentHash,
    bool Started,
    bool Succeeded,
    string? FailureClass,
    long LatencyMilliseconds,
    IReadOnlyList<string>? ToolUsage,
    IReadOnlyList<string>? EvidenceRefs,
    string IdempotencyKey);

public sealed record SkillTelemetryRecordResult(
    Guid EventId,
    SkillTelemetryEventType EventType,
    bool Replayed);

public sealed record SkillReindexRequest(
    string SearchProfileVersion,
    string EmbeddingModelId,
    string EmbeddingModelVersion,
    decimal Threshold,
    bool FullRebuild,
    bool Activate,
    string IdempotencyKey);

public sealed record SkillReindexResult(
    Guid GenerationId,
    SkillSearchGenerationStatus Status,
    int IndexedVersionCount,
    string BenchmarkJson,
    bool Activated,
    bool Replayed);

public sealed record SkillSearchGenerationResult(
    Guid GenerationId,
    string SearchProfileVersion,
    string EmbeddingModelId,
    string EmbeddingModelVersion,
    decimal Threshold,
    SkillSearchGenerationStatus Status,
    int IndexedVersionCount,
    string BenchmarkJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ActivatedAt);

public sealed record SkillSearchGenerationActivateRequest(
    Guid GenerationId,
    string Reason,
    string IdempotencyKey);

public sealed record SkillTelemetryAggregateResult(
    Guid SkillId,
    Guid? SkillVersionId,
    int SearchImpressionCount,
    int SelectedCount,
    int RejectedCount,
    int InvocationCount,
    int SuccessCount,
    int FailureCount,
    decimal SelectionRate,
    decimal RejectionRate,
    decimal InvocationRate,
    decimal SuccessRate,
    IReadOnlyDictionary<string, int> StageReasonCounts,
    DateTimeOffset? LastSearchedAt,
    DateTimeOffset? LastSelectedAt,
    DateTimeOffset? LastRejectedAt,
    DateTimeOffset? LastInvokedAt,
    string ProjectId,
    string RepositoryId,
    string AgentType,
    int WindowDays);

public sealed record SkillAnalyticsRequest(
    string? ProjectId = null,
    Guid? SkillId = null,
    Guid? SkillVersionId = null,
    int WindowDays = 30,
    string? RepositoryId = null,
    string? AgentType = null,
    SkillAnalyticsDimension Dimension = SkillAnalyticsDimension.Skill);

public sealed record SkillTelemetryTrendPointResult(
    DateOnly Date,
    int SearchImpressionCount,
    int SelectedCount,
    int RejectedCount,
    int InvocationCount,
    int SuccessCount,
    int FailureCount);

public sealed record SkillTelemetryReconciliationRequest(
    int RawEventRetentionDays = 90,
    int AggregateRetentionDays = 1095,
    string IdempotencyKey = "");

public sealed record SkillTelemetryReconciliationResult(
    Guid RunId,
    int AggregatedEventCount,
    int AggregateRowCount,
    int DeletedRawEventCount,
    int DeletedAggregateRowCount,
    int ProtectedRawEventCount,
    int RawEventRetentionDays,
    int AggregateRetentionDays,
    DateTimeOffset CompletedAt,
    bool Replayed);

public sealed record SkillMetadataGovernancePolicy(
    int MinimumSampleSize = 20,
    int WindowDays = 30,
    decimal MinimumRejectionRate = 0.35m,
    decimal MinimumSignalConfidence = 0.75m);

public sealed record SkillMetadataGovernanceFindingResult(
    string FindingKey,
    Guid SkillId,
    Guid? SkillVersionId,
    string SignalType,
    int SampleSize,
    decimal Rate,
    decimal Confidence,
    string CurrentMetadataHash,
    IReadOnlyDictionary<string, int> StageReasonCounts,
    string RecommendedAction,
    bool RequiresNewVersion,
    IReadOnlyList<string> EvidenceRefs);

public sealed record SkillMetadataGovernanceReviewResult(
    int TotalSkillCount,
    int ScannedSkillCount,
    int CandidateCount,
    int ActionableCount,
    int ExceptionCount,
    bool CoverageComplete,
    bool HasMore,
    IReadOnlyList<SkillMetadataGovernanceFindingResult> Findings);

public sealed record SkillMetadataProposalResult(
    Guid Id,
    Guid SkillId,
    long ExpectedMetadataVersion,
    string ExpectedMetadataHash,
    string ProposedPatchJson,
    string EvidenceJson,
    decimal Confidence,
    SkillMetadataProposalStatus Status,
    string GovernanceRunId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SkillMetadataProposalDecisionRequest(
    Guid ProposalId,
    bool Approve,
    long ExpectedMetadataVersion,
    string ExpectedMetadataHash,
    string? Name = null,
    string? Description = null,
    string? WhenToUse = null,
    IReadOnlyList<string>? Tags = null,
    IReadOnlyList<string>? Aliases = null,
    SkillRiskLevel? RiskLevel = null,
    string? Note = null);

public interface ISkillService
{
    Task<SkillImportPreviewResult> PreviewImportAsync(SkillImportPreviewRequest request, CancellationToken cancellationToken);
    Task<SkillImportResult> ImportAsync(SkillImportRequest request, CancellationToken cancellationToken);
    Task<SkillVersionSummaryResult> PublishAsync(SkillPublishRequest request, CancellationToken cancellationToken);
    Task<SkillVersionSummaryResult> ChangeLifecycleAsync(SkillLifecycleRequest request, CancellationToken cancellationToken);
    Task<SkillSummaryResult> SetDefaultVersionAsync(SkillDefaultVersionRequest request, CancellationToken cancellationToken);
    Task<SkillBindingResult> UpsertBindingAsync(SkillBindingUpsertRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<SkillSummaryResult>> ListAsync(string? projectId, bool includeArchived, CancellationToken cancellationToken);
    Task<SkillSummaryResult?> GetAsync(Guid skillId, CancellationToken cancellationToken);
    Task<PortableSkillBundle> ExportAsync(Guid skillVersionId, CancellationToken cancellationToken);
    Task<SkillVersionDiffResult> DiffVersionsAsync(Guid skillId, Guid leftVersionId, Guid rightVersionId, CancellationToken cancellationToken);
    Task<SkillSourceObservationResult> RecordSourceObservationAsync(SkillSourceObservationRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<SkillSourceObservationResult>> ListSourceObservationsAsync(Guid skillId, CancellationToken cancellationToken);
    Task<SkillSearchForExecutionResult> SearchForExecutionAsync(SkillSearchForExecutionRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<SkillResolutionDetailResult>> ListResolutionsAsync(string? projectId, int limit, CancellationToken cancellationToken);
    Task<SkillResolutionFeedbackResult> RecordFeedbackAsync(SkillResolutionFeedbackRequest request, CancellationToken cancellationToken);
    Task<SkillSelectForExecutionResult> SelectAsync(SkillSelectForExecutionRequest request, CancellationToken cancellationToken);
    Task<SkillExecutionSnapshotResult> CreateExecutionSnapshotAsync(SkillExecutionSnapshotCreateRequest request, CancellationToken cancellationToken);
    Task<SkillExecutionSnapshotRevalidationResult> RevalidateExecutionSnapshotAsync(SkillExecutionSnapshotRevalidateRequest request, CancellationToken cancellationToken);
    Task<SkillVersionBundleResult> GetPinnedVersionAsync(SkillVersionGetRequest request, CancellationToken cancellationToken);
    Task<SkillMaterializationResult> MaterializeAsync(SkillMaterializeRequest request, CancellationToken cancellationToken);
    Task<SkillMaterializationCleanupResult> CleanupMaterializationsAsync(SkillMaterializationCleanupRequest request, CancellationToken cancellationToken);
    Task<SkillTelemetryRecordResult> RecordInvocationAsync(SkillInvocationRecordRequest request, CancellationToken cancellationToken);
    Task<SkillReindexResult> ReindexAsync(SkillReindexRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<SkillSearchGenerationResult>> ListSearchGenerationsAsync(CancellationToken cancellationToken);
    Task<SkillReindexResult> ActivateSearchGenerationAsync(SkillSearchGenerationActivateRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<SkillTelemetryAggregateResult>> GetAnalyticsAsync(SkillAnalyticsRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<SkillTelemetryTrendPointResult>> GetAnalyticsTrendAsync(SkillAnalyticsRequest request, CancellationToken cancellationToken);
    Task<SkillTelemetryReconciliationResult> ReconcileTelemetryAsync(SkillTelemetryReconciliationRequest request, CancellationToken cancellationToken);
    Task<SkillMetadataGovernanceReviewResult> ReviewMetadataGovernanceAsync(SkillMetadataGovernancePolicy policy, CancellationToken cancellationToken);
    Task<IReadOnlyList<SkillMetadataProposalResult>> ListMetadataProposalsAsync(SkillMetadataProposalStatus? status, CancellationToken cancellationToken);
    Task<SkillMetadataProposalResult> DecideMetadataProposalAsync(SkillMetadataProposalDecisionRequest request, CancellationToken cancellationToken);
}

public interface ISkillMaterializationStore
{
    Task<string> MaterializeAsync(Guid executionId, string contentHash, PortableSkillBundle bundle, CancellationToken cancellationToken);
    Task CleanupExecutionAsync(Guid executionId, CancellationToken cancellationToken);
}

public interface ISkillSandboxSelfTestRunner
{
    Task<SkillSandboxSelfTestResult> RunAsync(SkillSandboxSelfTestRequest request, CancellationToken cancellationToken);
}
