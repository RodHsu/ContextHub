using System.Data;
using Memory.Application;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;
namespace Memory.UnitTests;

internal sealed class MemoryManifestTestDb : DbContext, IApplicationDbContext
{
    public MemoryManifestTestDb() : base(new DbContextOptionsBuilder<MemoryManifestTestDb>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options) { }
    DbSet<InstanceSetting> IApplicationDbContext.InstanceSettings => throw new NotSupportedException();
    DbSet<AgentConnectivityObservation> IApplicationDbContext.AgentConnectivityObservations => throw new NotSupportedException();
    DbSet<AgentConnectivitySummary> IApplicationDbContext.AgentConnectivitySummaries => throw new NotSupportedException();
    DbSet<Tenant> IApplicationDbContext.Tenants => throw new NotSupportedException();
    DbSet<TenantUser> IApplicationDbContext.TenantUsers => throw new NotSupportedException();
    public DbSet<TenantProjectGrant> TenantProjectGrants => Set<TenantProjectGrant>();
    DbSet<ApiToken> IApplicationDbContext.ApiTokens => throw new NotSupportedException();
    DbSet<SecurityAuditEvent> IApplicationDbContext.SecurityAuditEvents => throw new NotSupportedException();
    public DbSet<MemoryItem> MemoryItems => Set<MemoryItem>();
    public DbSet<MemoryItemRevision> MemoryItemRevisions => Set<MemoryItemRevision>();
    DbSet<MemoryItemChunk> IApplicationDbContext.MemoryItemChunks => throw new NotSupportedException();
    DbSet<MemoryChunkVector> IApplicationDbContext.MemoryChunkVectors => throw new NotSupportedException();
    DbSet<MemoryLink> IApplicationDbContext.MemoryLinks => throw new NotSupportedException();
    DbSet<MemoryJob> IApplicationDbContext.MemoryJobs => throw new NotSupportedException();
    DbSet<MaintenanceRun> IApplicationDbContext.MaintenanceRuns => throw new NotSupportedException();
    DbSet<RetrievalEvent> IApplicationDbContext.RetrievalEvents => throw new NotSupportedException();
    DbSet<RetrievalHit> IApplicationDbContext.RetrievalHits => throw new NotSupportedException();
    DbSet<RetrievalTelemetryDailySummary> IApplicationDbContext.RetrievalTelemetryDailySummaries => throw new NotSupportedException();
    DbSet<RetrievalTelemetryDailyHitSummary> IApplicationDbContext.RetrievalTelemetryDailyHitSummaries => throw new NotSupportedException();
    DbSet<EmbeddingUsageHourly> IApplicationDbContext.EmbeddingUsageHourly => throw new NotSupportedException();
    DbSet<RuntimeLogEntry> IApplicationDbContext.RuntimeLogEntries => throw new NotSupportedException();
    DbSet<LogIngestionCheckpoint> IApplicationDbContext.LogIngestionCheckpoints => throw new NotSupportedException();
    DbSet<SourceConnection> IApplicationDbContext.SourceConnections => throw new NotSupportedException();
    DbSet<SourceSyncRun> IApplicationDbContext.SourceSyncRuns => throw new NotSupportedException();
    DbSet<GovernanceFinding> IApplicationDbContext.GovernanceFindings => throw new NotSupportedException();
    DbSet<EvaluationSuite> IApplicationDbContext.EvaluationSuites => throw new NotSupportedException();
    DbSet<EvaluationCase> IApplicationDbContext.EvaluationCases => throw new NotSupportedException();
    DbSet<EvaluationRun> IApplicationDbContext.EvaluationRuns => throw new NotSupportedException();
    DbSet<EvaluationRunItem> IApplicationDbContext.EvaluationRunItems => throw new NotSupportedException();
    DbSet<SuggestedAction> IApplicationDbContext.SuggestedActions => throw new NotSupportedException();
    DbSet<ConversationSession> IApplicationDbContext.ConversationSessions => throw new NotSupportedException();
    DbSet<ConversationCheckpoint> IApplicationDbContext.ConversationCheckpoints => throw new NotSupportedException();
    DbSet<ConversationInsight> IApplicationDbContext.ConversationInsights => throw new NotSupportedException();
    public DbSet<KnowledgeGovernanceSnapshot> KnowledgeGovernanceSnapshots => Set<KnowledgeGovernanceSnapshot>();
    DbSet<GovernanceBatchRun> IApplicationDbContext.GovernanceBatchRuns => throw new NotSupportedException();
    DbSet<GovernanceBatchExecution> IApplicationDbContext.GovernanceBatchExecutions => throw new NotSupportedException();
    DbSet<MemoryRetentionState> IApplicationDbContext.MemoryRetentionStates => throw new NotSupportedException();
    DbSet<ResourceTombstone> IApplicationDbContext.ResourceTombstones => throw new NotSupportedException();
    DbSet<GovernanceRunReceipt> IApplicationDbContext.GovernanceRunReceipts => throw new NotSupportedException();
    DbSet<ScheduledGovernanceReliabilityRun> IApplicationDbContext.ScheduledGovernanceReliabilityRuns => throw new NotSupportedException();
    DbSet<ProjectHierarchy> IApplicationDbContext.ProjectHierarchies => throw new NotSupportedException();
    DbSet<DiscussionThread> IApplicationDbContext.DiscussionThreads => throw new NotSupportedException();
    DbSet<DiscussionParticipant> IApplicationDbContext.DiscussionParticipants => throw new NotSupportedException();
    DbSet<DiscussionMessage> IApplicationDbContext.DiscussionMessages => throw new NotSupportedException();
    DbSet<ProjectWorkItem> IApplicationDbContext.ProjectWorkItems => throw new NotSupportedException();
    DbSet<ProjectWorkItemChecklistItem> IApplicationDbContext.ProjectWorkItemChecklistItems => throw new NotSupportedException();
    DbSet<AgentExecution> IApplicationDbContext.AgentExecutions => throw new NotSupportedException();
    DbSet<AgentExecutionEvent> IApplicationDbContext.AgentExecutionEvents => throw new NotSupportedException();
    DbSet<AgentExecutionOperation> IApplicationDbContext.AgentExecutionOperations => throw new NotSupportedException();
    DbSet<AgentExecutionResolutionSnapshot> IApplicationDbContext.AgentExecutionResolutionSnapshots => throw new NotSupportedException();
    DbSet<AgentExecutionResolutionItem> IApplicationDbContext.AgentExecutionResolutionItems => throw new NotSupportedException();
    DbSet<AgentExecutionResourceApproval> IApplicationDbContext.AgentExecutionResourceApprovals => throw new NotSupportedException();
    DbSet<AuthorityOutboxEvent> IApplicationDbContext.AuthorityOutboxEvents => throw new NotSupportedException();
    DbSet<PlatformOutboxDelivery> IApplicationDbContext.PlatformOutboxDeliveries => throw new NotSupportedException();
    DbSet<MonitoringActivityProjection> IApplicationDbContext.MonitoringActivityProjections => throw new NotSupportedException();
    DbSet<MonitoringProjectionState> IApplicationDbContext.MonitoringProjectionStates => throw new NotSupportedException();
    DbSet<PlatformBackgroundRun> IApplicationDbContext.PlatformBackgroundRuns => throw new NotSupportedException();
    DbSet<PlatformBackgroundEvent> IApplicationDbContext.PlatformBackgroundEvents => throw new NotSupportedException();
    DbSet<Skill> IApplicationDbContext.Skills => throw new NotSupportedException();
    DbSet<SkillVersion> IApplicationDbContext.SkillVersions => throw new NotSupportedException();
    DbSet<SkillVersionDependency> IApplicationDbContext.SkillVersionDependencies => throw new NotSupportedException();
    DbSet<SkillBinding> IApplicationDbContext.SkillBindings => throw new NotSupportedException();
    DbSet<SkillSearchGeneration> IApplicationDbContext.SkillSearchGenerations => throw new NotSupportedException();
    DbSet<SkillSearchDocument> IApplicationDbContext.SkillSearchDocuments => throw new NotSupportedException();
    DbSet<SkillResolution> IApplicationDbContext.SkillResolutions => throw new NotSupportedException();
    DbSet<SkillResolutionCandidate> IApplicationDbContext.SkillResolutionCandidates => throw new NotSupportedException();
    DbSet<SkillResolutionPin> IApplicationDbContext.SkillResolutionPins => throw new NotSupportedException();
    DbSet<SkillTelemetryEvent> IApplicationDbContext.SkillTelemetryEvents => throw new NotSupportedException();
    DbSet<SkillMaterialization> IApplicationDbContext.SkillMaterializations => throw new NotSupportedException();
    DbSet<SkillMetadataProposal> IApplicationDbContext.SkillMetadataProposals => throw new NotSupportedException();
    DbSet<SkillSourceObservation> IApplicationDbContext.SkillSourceObservations => throw new NotSupportedException();
    DbSet<SkillTelemetryDailyAggregate> IApplicationDbContext.SkillTelemetryDailyAggregates => throw new NotSupportedException();
    DbSet<SkillTelemetryAggregationLedger> IApplicationDbContext.SkillTelemetryAggregationLedgers => throw new NotSupportedException();
    DbSet<SkillTelemetryReconciliationRun> IApplicationDbContext.SkillTelemetryReconciliationRuns => throw new NotSupportedException();
    public void ClearTrackedChanges() => ChangeTracker.Clear();
    public Task<IApplicationTransaction> BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task AcquireTransactionLockAsync(string lockKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<MemoryItem>().Ignore(x => x.Chunks).Ignore(x => x.OutgoingLinks).Ignore(x => x.IncomingLinks).Ignore(x => x.Supersedes).Ignore(x => x.SupersededBy).Ignore(x => x.SuccessorEvidence);
        model.Entity<MemoryItem>().HasMany(x => x.Revisions).WithOne(x => x.MemoryItem).HasForeignKey(x => x.MemoryItemId);
        model.Entity<TenantProjectGrant>().Ignore(x => x.Tenant);
        var supported = new[] { typeof(MemoryItem), typeof(MemoryItemRevision), typeof(TenantProjectGrant), typeof(KnowledgeGovernanceSnapshot) };
        foreach (var entity in model.Model.GetEntityTypes().ToArray())
            if (!supported.Contains(entity.ClrType)) model.Ignore(entity.ClrType);
    }
}
