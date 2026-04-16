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
    public async Task Upserting_Shared_Summary_Source_Type_Should_Enqueue_Summary_Refresh_Job()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
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
    public async Task Upserting_Same_User_Preference_Twice_Should_Update_In_Place_Without_Concurrency_Exception()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
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
        var automationService = scope.ServiceProvider.GetRequiredService<IConversationAutomationService>();
        var processor = scope.ServiceProvider.GetRequiredService<IBackgroundJobProcessor>();
        var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var conversationId = $"conversation-{Guid.NewGuid():N}";

        dbContext.InstanceSettings.Add(new InstanceSetting
        {
            InstanceId = ProjectContext.DefaultProjectId,
            SettingKey = "behavior",
            ValueJson = JsonSerializer.Serialize(new InstanceBehaviorSettingsResult(
                true,
                true,
                true,
                20,
                "Automatic",
                240,
                ProjectContext.DefaultProjectId,
                MemoryQueryMode.CurrentOnly,
                false,
                true,
                new DashboardSnapshotPollingSettingsResult(
                    30,
                    30,
                    10,
                    30,
                    5,
                    5,
                    1),
                10,
                5,
                8,
                10,
                30)),
            Revision = 1,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = "tests"
        });
        await dbContext.SaveChangesAsync(CancellationToken.None);

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
}
