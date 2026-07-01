using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Memory.IntegrationTests;

public sealed class MemoryWorkflowTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Upsert_ProcessJob_And_Search_Should_Return_Result()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(scope.ServiceProvider);
        var memoryService = scope.ServiceProvider.GetRequiredService<IMemoryService>();
        var processor = scope.ServiceProvider.GetRequiredService<IBackgroundJobProcessor>();
        var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

        var created = await memoryService.UpsertAsync(
            new MemoryUpsertRequest(
                ExternalKey: "repo:bugfix:1",
                Scope: MemoryScope.Project,
                MemoryType: MemoryType.Decision,
                Title: "Fix null worker bug",
                Content: "Resolved a NullReferenceException in the worker by guarding empty payloads.",
                Summary: "Guard empty payloads in worker.",
                SourceType: "document",
                SourceRef: "ADR-001",
                Tags: ["bugfix", "worker"],
                Importance: 0.9m,
                Confidence: 0.8m),
            CancellationToken.None);

        created.Version.Should().Be(1);

        var processed = await processor.ProcessNextAsync(CancellationToken.None);
        processed.Should().NotBeNull();

        var results = await memoryService.SearchAsync(new MemorySearchRequest("NullReferenceException worker", 5), CancellationToken.None);

        results.Should().ContainSingle(x => x.MemoryId == created.Id);
        dbContext.MemoryChunkVectors.Should().NotBeNull();
    }

    [DockerRequiredFact]
    public async Task ProcessJob_WithOwnerActor_Should_Refresh_MemoryGraphIndex_Without_Scope_Warning()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(scope.ServiceProvider);
        var memoryService = scope.ServiceProvider.GetRequiredService<IMemoryService>();
        var processor = scope.ServiceProvider.GetRequiredService<IBackgroundJobProcessor>();
        var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var actorAccessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        var snapshotStore = scope.ServiceProvider.GetRequiredService<IDashboardSnapshotStore>();
        var tenantId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var projectId = $"GraphOwner_{suffix}";
        var title = $"Owner graph refresh fixture {suffix}";
        var startedAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        dbContext.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Slug = $"graph-owner-{suffix}"[..20],
            DisplayName = "Graph Owner Tenant",
            Status = TenantStatus.Active,
            CreatedAt = startedAt,
            UpdatedAt = startedAt
        });
        dbContext.TenantUsers.Add(new TenantUser
        {
            Id = ownerUserId,
            TenantId = tenantId,
            Username = $"graph-owner-{suffix}"[..20],
            DisplayName = "Graph Owner",
            Role = TenantUserRole.Member,
            Status = TenantUserStatus.Active,
            CreatedAt = startedAt,
            UpdatedAt = startedAt
        });
        await dbContext.SaveChangesAsync(CancellationToken.None);

        actorAccessor.Current = new ContextHubRequestActor(
            tenantId,
            ownerUserId,
            "owner",
            TenantUserRole.Member,
            [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite],
            [projectId],
            true);

        var created = await memoryService.UpsertAsync(
            new MemoryUpsertRequest(
                ExternalKey: $"repo:graph-owner:{suffix}",
                Scope: MemoryScope.Project,
                MemoryType: MemoryType.Fact,
                Title: title,
                Content: $"This fixture validates owner-scoped graph refresh {suffix}.",
                Summary: title,
                SourceType: "test",
                SourceRef: "integration",
                Tags: ["graph", "owner"],
                Importance: 0.8m,
                Confidence: 0.9m,
                ProjectId: projectId),
            CancellationToken.None);
        actorAccessor.Current = ContextHubRequestActor.Unrestricted;

        var processed = await processor.ProcessNextAsync(CancellationToken.None);

        processed.Should().NotBeNull();
        processed!.Status.Should().Be(MemoryJobStatus.Completed);
        processed.Error.Should().BeEmpty();

        var snapshot = await snapshotStore.GetAsync<DashboardMemoryGraphIndexSnapshotPayload>(
            DashboardSnapshotKeys.MemoryGraphIndex,
            CancellationToken.None);
        snapshot.Should().NotBeNull();
        snapshot!.Payload.Graph.Nodes.Should().Contain(node => node.Id == created.Id);

        dbContext.RuntimeLogEntries.Should().NotContain(entry =>
            entry.CreatedAt >= startedAt &&
            entry.Message.Contains("Memory graph index event refresh failed", StringComparison.Ordinal));
    }

    [DockerRequiredFact]
    public async Task ServiceActor_WithoutTenantUser_Should_Search_For_DashboardSnapshotCollector()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(scope.ServiceProvider);
        var memoryService = scope.ServiceProvider.GetRequiredService<IMemoryService>();
        var processor = scope.ServiceProvider.GetRequiredService<IBackgroundJobProcessor>();
        var actorAccessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var projectId = $"ServiceGraph_{suffix}";
        var title = $"Service actor graph fixture {suffix}";

        await memoryService.UpsertAsync(
            new MemoryUpsertRequest(
                ExternalKey: $"repo:service-graph:{suffix}",
                Scope: MemoryScope.Project,
                MemoryType: MemoryType.Fact,
                Title: title,
                Content: $"Dashboard snapshot collector service actor search fixture {suffix}.",
                Summary: title,
                SourceType: "test",
                SourceRef: "integration",
                Tags: ["graph", "service-actor"],
                Importance: 0.8m,
                Confidence: 0.9m,
                ProjectId: projectId),
            CancellationToken.None);

        var processed = await processor.ProcessNextAsync(CancellationToken.None);
        processed.Should().NotBeNull();
        processed!.Status.Should().Be(MemoryJobStatus.Completed);

        actorAccessor.Current = new ContextHubRequestActor(
            null,
            null,
            "dashboard-snapshot-collector",
            null,
            [SecurityScopes.MemoryRead],
            [],
            IsAuthenticated: true,
            IsServiceActor: true);

        var act = async () => await memoryService.SearchAsync(
            new MemorySearchRequest(
                $"service actor graph fixture {suffix}",
                Limit: 5,
                IncludeArchived: false,
                ProjectId: projectId),
            CancellationToken.None);

        await act.Should().NotThrowAsync<UnauthorizedAccessException>();
    }

    [DockerRequiredFact]
    public async Task Upserting_Shared_Summary_Source_Type_Should_Enqueue_Summary_Refresh_Job()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(scope.ServiceProvider);
        var memoryService = scope.ServiceProvider.GetRequiredService<IMemoryService>();
        var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

        await memoryService.UpsertAsync(
            new MemoryUpsertRequest(
                ExternalKey: "repo:summary:auto:1",
                Scope: MemoryScope.Project,
                MemoryType: MemoryType.Fact,
                Title: "Shared summary auto refresh fixture",
                Content: "Writing a shared-summary source memory should enqueue a summary refresh job.",
                Summary: "Shared summary auto refresh fixture",
                SourceType: "document",
                SourceRef: "tests",
                Tags: ["summary", "auto-refresh"],
                Importance: 0.8m,
                Confidence: 0.9m),
            CancellationToken.None);

        var jobs = await dbContext.MemoryJobs
            .OrderByDescending(x => x.CreatedAt)
            .Take(4)
            .ToListAsync(CancellationToken.None);

        jobs.Should().Contain(x => x.JobType == MemoryJobType.Reindex && x.ProjectId == ProjectContext.DefaultProjectId);
        jobs.Should().Contain(x => x.JobType == MemoryJobType.RefreshSummary && x.ProjectId == ProjectContext.DefaultProjectId);
    }

    [DockerRequiredFact]
    public async Task Refresh_Summary_Rebuild_All_Should_Complete_Without_Query_Translation_Errors()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(scope.ServiceProvider);
        var memoryService = scope.ServiceProvider.GetRequiredService<IMemoryService>();
        var processor = scope.ServiceProvider.GetRequiredService<IBackgroundJobProcessor>();
        var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var projectA = $"Alpha_{suffix}";
        var projectB = $"Beta_{suffix}";
        var now = DateTimeOffset.UtcNow;

        dbContext.MemoryItems.AddRange(
            CreateMemoryItem(projectA, $"repo:{projectA}:1", "Alpha rebuild fixture", now),
            CreateMemoryItem(projectB, $"repo:{projectB}:1", "Beta rebuild fixture", now.AddSeconds(-1)),
            CreateMemoryItem(ProjectContext.SharedProjectId, $"repo:shared:{suffix}", "Shared layer should be skipped", now.AddSeconds(-2)),
            CreateMemoryItem(ProjectContext.UserProjectId, $"repo:user:{suffix}", "User layer should be skipped", now.AddSeconds(-3)));
        await dbContext.SaveChangesAsync(CancellationToken.None);

        var enqueue = await memoryService.EnqueueSummaryRefreshAsync(
            new EnqueueSummaryRefreshRequest(ProjectId: null, IncludedProjectIds: null),
            CancellationToken.None);

        var processed = await processor.ProcessNextAsync(CancellationToken.None);
        processed.Should().NotBeNull();
        processed!.Id.Should().Be(enqueue.JobId);
        processed.Status.Should().Be(MemoryJobStatus.Completed);
        processed.Error.Should().BeEmpty();

        var sharedSummaries = await dbContext.MemoryItems
            .Where(x => x.ProjectId == ProjectContext.SharedProjectId && x.MemoryType == MemoryType.Summary)
            .ToListAsync(CancellationToken.None);

        sharedSummaries.Should().Contain(x => x.SourceRef == projectA && x.Content.Contains("Alpha rebuild fixture", StringComparison.Ordinal));
        sharedSummaries.Should().Contain(x => x.SourceRef == projectB && x.Content.Contains("Beta rebuild fixture", StringComparison.Ordinal));
        sharedSummaries.Should().NotContain(x => x.SourceRef == ProjectContext.SharedProjectId || x.SourceRef == ProjectContext.UserProjectId);
    }

    [DockerRequiredFact]
    public async Task Error_Logs_Should_Be_Flushed_To_Runtime_Log_Table()
    {
        using (var scope = environment.GetFactory().Services.CreateScope())
        {
            var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger("Memory.IntegrationTests.RuntimeLogs");
            logger.LogError("Database log sink integration test {Marker}", "runtime-log-marker");
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var verificationScope = environment.GetFactory().Services.CreateScope();
            var dbContext = verificationScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var exists = await dbContext.RuntimeLogEntries.AnyAsync(
                x => x.Message.Contains("runtime-log-marker"),
                CancellationToken.None);

            if (exists)
            {
                return;
            }

            await Task.Delay(250);
        }

        false.Should().BeTrue("the database log writer should flush application errors into runtime_log_entries");
    }

    [DockerRequiredFact]
    public async Task Build_Working_Context_Should_Include_User_Preferences()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(scope.ServiceProvider);
        var memoryService = scope.ServiceProvider.GetRequiredService<IMemoryService>();

        var preference = await memoryService.UpsertUserPreferenceAsync(
            new UserPreferenceUpsertRequest(
                Key: "compose-first",
                Kind: UserPreferenceKind.ToolingPreference,
                Title: "偏好 Docker Compose",
                Content: "部署與本機開發都優先使用 Docker Compose。",
                Rationale: "跨專案一致"),
            CancellationToken.None);

        var context = await memoryService.BuildWorkingContextAsync(
            new WorkingContextRequest("請根據我的工具習慣建立上下文", 3, 3),
            CancellationToken.None);

        context.UserPreferences.Should().ContainSingle(x => x.Id == preference.Id);
        context.Citations.Should().Contain(x => x.MemoryId == preference.Id);
    }

    [DockerRequiredFact]
    public async Task Search_Should_Write_Retrieval_Telemetry_Event_And_Hits()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(scope.ServiceProvider);
        var memoryService = scope.ServiceProvider.GetRequiredService<IMemoryService>();
        var processor = scope.ServiceProvider.GetRequiredService<IBackgroundJobProcessor>();
        var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var query = $"retrieval-telemetry-search-{Guid.NewGuid():N}";
        var content = $"This fixture should be found for query {query}.";

        var created = await memoryService.UpsertAsync(
            new MemoryUpsertRequest(
                ExternalKey: $"repo:telemetry:{Guid.NewGuid():N}",
                Scope: MemoryScope.Project,
                MemoryType: MemoryType.Decision,
                Title: "Retrieval telemetry fixture",
                Content: content,
                Summary: "Retrieval telemetry fixture",
                SourceType: "document",
                SourceRef: "tests",
                Tags: ["telemetry", "search"],
                Importance: 0.8m,
                Confidence: 0.9m),
            CancellationToken.None);

        await processor.ProcessNextAsync(CancellationToken.None);

        var hits = await memoryService.SearchAsync(
            new MemorySearchRequest(
                query,
                5,
                false,
                ProjectContext.DefaultProjectId,
                null,
                MemoryQueryMode.CurrentOnly,
                false,
                new RetrievalTelemetryContext("tests.search", "test", "integration telemetry search")),
            CancellationToken.None);

        hits.Should().Contain(x => x.MemoryId == created.Id);
        hits.Single(x => x.MemoryId == created.Id).SourceTokenEstimate.Should().Be(ChunkingService.ApproximateTokenCount(content));

        var telemetryEvent = await dbContext.RetrievalEvents
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(x => x.EntryPoint == "tests.search" && x.QueryText == query, CancellationToken.None);

        telemetryEvent.Should().NotBeNull();
        telemetryEvent!.Channel.Should().Be("test");
        telemetryEvent.Purpose.Should().Be("integration telemetry search");
        telemetryEvent.ResultCount.Should().BeGreaterThan(0);
        telemetryEvent.Success.Should().BeTrue();

        var telemetryHits = await dbContext.RetrievalHits
            .Where(x => x.RetrievalEventId == telemetryEvent.Id)
            .OrderBy(x => x.Rank)
            .ToListAsync(CancellationToken.None);

        telemetryHits.Should().NotBeEmpty();
        telemetryHits.Should().Contain(x => x.MemoryId == created.Id);
    }

    [DockerRequiredFact]
    public async Task Build_Working_Context_Should_Write_Single_Composite_Telemetry_Event()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(scope.ServiceProvider);
        var memoryService = scope.ServiceProvider.GetRequiredService<IMemoryService>();
        var processor = scope.ServiceProvider.GetRequiredService<IBackgroundJobProcessor>();
        var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var query = $"retrieval-telemetry-context-{Guid.NewGuid():N}";

        await memoryService.UpsertAsync(
            new MemoryUpsertRequest(
                ExternalKey: $"repo:context-telemetry:{Guid.NewGuid():N}",
                Scope: MemoryScope.Project,
                MemoryType: MemoryType.Fact,
                Title: "Working context telemetry fixture",
                Content: $"This fixture should appear in working context for query {query}.",
                Summary: "Working context telemetry fixture",
                SourceType: "document",
                SourceRef: "tests",
                Tags: ["telemetry", "context"],
                Importance: 0.85m,
                Confidence: 0.92m),
            CancellationToken.None);

        await processor.ProcessNextAsync(CancellationToken.None);

        var result = await memoryService.BuildWorkingContextAsync(
            new WorkingContextRequest(
                query,
                3,
                2,
                ProjectContext.DefaultProjectId,
                null,
                MemoryQueryMode.CurrentOnly,
                false,
                new RetrievalTelemetryContext("tests.working-context", "test", "integration working context")),
            CancellationToken.None);

        result.Facts.Should().NotBeEmpty();
        result.SavingsEstimate.Should().NotBeNull();
        result.SavingsEstimate!.BaselineTokenEstimate.Should().BeGreaterThan(0);
        result.SavingsEstimate.ReturnedTokenEstimate.Should().BeGreaterThan(0);

        var events = await dbContext.RetrievalEvents
            .Where(x => x.QueryText == query)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(CancellationToken.None);

        events.Should().ContainSingle(x => x.EntryPoint == "tests.working-context");
        events.Should().NotContain(x => x.EntryPoint == "build_working_context.search");

        var telemetryEvent = events.Single(x => x.EntryPoint == "tests.working-context");
        telemetryEvent.ResultCount.Should().BeGreaterThan(0);
        using var metadata = JsonDocument.Parse(telemetryEvent.MetadataJson);
        metadata.RootElement.TryGetProperty("savings", out var savings).Should().BeTrue();
        savings.GetProperty("baselineTokenEstimate").GetInt32().Should().BeGreaterThan(0);
        savings.GetProperty("returnedTokenEstimate").GetInt32().Should().BeGreaterThan(0);

        var telemetryHits = await dbContext.RetrievalHits
            .Where(x => x.RetrievalEventId == telemetryEvent.Id)
            .ToListAsync(CancellationToken.None);

        telemetryHits.Should().NotBeEmpty();
    }

    [DockerRequiredFact]
    public async Task Upserting_Same_User_Preference_Twice_Should_Update_In_Place_Without_Concurrency_Exception()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(scope.ServiceProvider);
        var memoryService = scope.ServiceProvider.GetRequiredService<IMemoryService>();

        var created = await memoryService.UpsertUserPreferenceAsync(
            new UserPreferenceUpsertRequest(
                Key: "preferred-language",
                Kind: UserPreferenceKind.CommunicationStyle,
                Title: "偏好繁體中文",
                Content: "回覆預設使用繁體中文。",
                Rationale: "初始偏好"),
            CancellationToken.None);

        var updated = await memoryService.UpsertUserPreferenceAsync(
            new UserPreferenceUpsertRequest(
                Key: "preferred-language",
                Kind: UserPreferenceKind.CommunicationStyle,
                Title: "偏好繁體中文",
                Content: "回覆預設使用繁體中文，技術名詞保留英文。",
                Rationale: "更新後偏好"),
            CancellationToken.None);

        updated.Id.Should().Be(created.Id);
        updated.Content.Should().Contain("技術名詞保留英文");
        updated.Rationale.Should().Be("更新後偏好");

        var preferences = await memoryService.ListUserPreferencesAsync(
            new UserPreferenceListRequest(UserPreferenceKind.CommunicationStyle, false, 10),
            CancellationToken.None);

        preferences.Should().ContainSingle(x => x.Key == "preferred-language");
    }

    [DockerRequiredFact]
    public async Task Enqueue_Reindex_Should_Write_New_Model_Key_Vectors()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(scope.ServiceProvider);
        var memoryService = scope.ServiceProvider.GetRequiredService<IMemoryService>();
        var processor = scope.ServiceProvider.GetRequiredService<IBackgroundJobProcessor>();
        var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var content = string.Join(
            Environment.NewLine + Environment.NewLine,
            Enumerable.Range(0, 48).Select(i => $"Section {i} " + string.Join(' ', Enumerable.Repeat("token", 120))));

        var created = await memoryService.UpsertAsync(
            new MemoryUpsertRequest(
                ExternalKey: "repo:model-switch:1",
                Scope: MemoryScope.Project,
                MemoryType: MemoryType.Artifact,
                Title: "Model switch fixture",
                Content: content,
                Summary: "Model switch fixture",
                SourceType: "document",
                SourceRef: "tests",
                Tags: ["reindex", "model-switch"],
                Importance: 0.8m,
                Confidence: 0.9m),
            CancellationToken.None);

        await processor.ProcessNextAsync(CancellationToken.None);

        var reindex = await memoryService.EnqueueReindexAsync(
            new EnqueueReindexRequest("intfloat/multilingual-e5-base", created.Id),
            CancellationToken.None);

        reindex.Status.Should().Be(MemoryJobStatus.Pending);
        await DrainConversationAutomationAsync(processor, dbContext, "conversation-1", CancellationToken.None);

        var chunkIds = await dbContext.MemoryItemChunks
            .Where(x => x.MemoryItemId == created.Id)
            .Select(x => x.Id)
            .ToListAsync(CancellationToken.None);
        chunkIds.Count.Should().BeGreaterThan(8);

        var vectors = await dbContext.MemoryChunkVectors
            .Where(x => chunkIds.Contains(x.ChunkId))
            .ToListAsync(CancellationToken.None);

        vectors.Should().Contain(x => x.ModelKey == "deterministic-384" && x.Status == VectorStatus.Active.ToString());
        vectors.Count(x => x.ModelKey == "intfloat/multilingual-e5-base" && x.Status == VectorStatus.Active.ToString())
            .Should().Be(chunkIds.Count);
    }

    [DockerRequiredFact]
    public async Task Conversation_Ingest_Should_Create_Insights_And_Promote_Them()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(scope.ServiceProvider);
        var automationService = scope.ServiceProvider.GetRequiredService<IConversationAutomationService>();
        var processor = scope.ServiceProvider.GetRequiredService<IBackgroundJobProcessor>();
        var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var conversationId = $"conversation-{Guid.NewGuid():N}";

        var result = await automationService.IngestAsync(
            new ConversationIngestRequest(
                ConversationId: conversationId,
                TurnId: "turn-1",
                EventType: ConversationEventType.SessionCheckpoint,
                SourceKind: ConversationSourceKind.HostEvent,
                SourceSystem: "codex",
                SourceRef: "tests/session-1",
                ProjectName: "ContextHub",
                UserMessageSummary: "使用者偏好回覆預設使用繁體中文。",
                AgentMessageSummary: "系統決定採用 shared summary layer 作為跨專案共用知識入口。",
                SessionSummary: "ContextHub 架構決定採用 shared summary layer。"),
            CancellationToken.None);

        result.AutomationScheduled.Should().BeTrue();
        result.JobId.Should().NotBeNull();

        await DrainConversationAutomationAsync(processor, dbContext, conversationId, CancellationToken.None);

        var insights = await dbContext.ConversationInsights
            .Where(x => x.ConversationId == conversationId)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(CancellationToken.None);
        insights.Should().NotBeEmpty();
        insights.Should().Contain(x => x.InsightType == ConversationInsightType.Decision && x.PromotionStatus == ConversationPromotionStatus.Promoted);

        var memories = await dbContext.MemoryItems.ToListAsync(CancellationToken.None);
        memories.Should().Contain(x =>
            x.ProjectId == ProjectContext.UserProjectId &&
            x.MemoryType == MemoryType.Preference &&
            x.SourceRef.StartsWith("conversation-auto:", StringComparison.Ordinal) &&
            x.Content.Contains("繁體中文", StringComparison.Ordinal));
        memories.Should().Contain(x =>
            x.ProjectId == ProjectContext.DefaultProjectId &&
            x.MemoryType == MemoryType.Decision &&
            x.SourceType == "conversation-auto" &&
            x.Content.Contains("shared summary layer", StringComparison.OrdinalIgnoreCase));
    }

    [DockerRequiredFact]
    public async Task Conversation_Ingest_Should_Recover_Stale_Running_Job_And_Promote()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(scope.ServiceProvider);
        var automationService = scope.ServiceProvider.GetRequiredService<IConversationAutomationService>();
        var processor = scope.ServiceProvider.GetRequiredService<IBackgroundJobProcessor>();
        var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var conversationId = $"conversation-stale-{Guid.NewGuid():N}";
        var summary = "決定：stale running conversation job 必須可自動修正並重試。";

        var result = await automationService.IngestAsync(
            new ConversationIngestRequest(
                ConversationId: conversationId,
                TurnId: "turn-1",
                EventType: ConversationEventType.SessionCheckpoint,
                SourceKind: ConversationSourceKind.AgentSupplemental,
                SourceSystem: "codex",
                SourceRef: "tests/stale-running",
                ProjectId: ProjectContext.DefaultProjectId,
                ProjectName: "ContextHub",
                AgentMessageSummary: summary,
                SessionSummary: summary),
            CancellationToken.None);

        var job = await dbContext.MemoryJobs.SingleAsync(x => x.Id == result.JobId, CancellationToken.None);
        job.Status = MemoryJobStatus.Running;
        job.StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        await dbContext.SaveChangesAsync(CancellationToken.None);

        await DrainConversationAutomationAsync(processor, dbContext, conversationId, CancellationToken.None);

        var recoveredJob = await dbContext.MemoryJobs.SingleAsync(x => x.Id == result.JobId, CancellationToken.None);
        recoveredJob.Status.Should().Be(MemoryJobStatus.Completed);
        recoveredJob.Error.Should().Be("Recovered stale running job for retry.");
        recoveredJob.CompletedAt.Should().NotBeNull();

        var insights = await dbContext.ConversationInsights
            .Where(x => x.ConversationId == conversationId)
            .ToListAsync(CancellationToken.None);
        insights.Should().Contain(x => x.PromotionStatus == ConversationPromotionStatus.Promoted);
    }

    private static async Task DrainConversationAutomationAsync(
        IBackgroundJobProcessor processor,
        MemoryDbContext dbContext,
        string conversationId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            await processor.ProcessNextAsync(cancellationToken);

            var promoted = await dbContext.ConversationInsights.AnyAsync(
                x => x.ConversationId == conversationId &&
                     x.PromotionStatus == ConversationPromotionStatus.Promoted,
                cancellationToken);

            if (promoted)
            {
                return;
            }
        }
    }

    private static void UseBootstrapActor(IServiceProvider services)
    {
        var dbContext = services.GetRequiredService<MemoryDbContext>();
        var user = dbContext.TenantUsers
            .Include(x => x.Tenant)
            .Single(x => x.Username == "contract-test-admin");

        services.GetRequiredService<IRequestActorAccessor>().Current = new ContextHubRequestActor(
            user.TenantId,
            user.Id,
            user.Username,
            user.Role,
            [
                SecurityScopes.MemoryRead,
                SecurityScopes.MemoryWrite,
                SecurityScopes.PreferencesRead,
                SecurityScopes.PreferencesWrite,
                SecurityScopes.TokenManage,
                SecurityScopes.SecurityManage,
                SecurityScopes.DashboardActAs
            ],
            [],
            IsAuthenticated: true);
    }

    private static MemoryItem CreateMemoryItem(string projectId, string externalKey, string title, DateTimeOffset timestamp)
        => new()
        {
            ProjectId = projectId,
            ExternalKey = externalKey,
            Scope = MemoryScope.Project,
            MemoryType = MemoryType.Fact,
            Title = title,
            Content = title,
            Summary = title,
            Tags = ["summary-refresh"],
            SourceType = "document",
            SourceRef = "tests",
            Importance = 0.8m,
            Confidence = 0.9m,
            Version = 1,
            Status = MemoryStatus.Active,
            MetadataJson = "{}",
            CreatedAt = timestamp,
            UpdatedAt = timestamp
        };
}
