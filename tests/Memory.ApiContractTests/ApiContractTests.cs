using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

namespace Memory.ApiContractTests;

public sealed class ApiContractTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Status_And_Search_Endpoints_Should_Return_Expected_Payloads()
    {
        using (var scope = environment.GetFactory().Services.CreateScope())
        {
            var memoryService = scope.ServiceProvider.GetRequiredService<IMemoryService>();
            var processor = scope.ServiceProvider.GetRequiredService<IBackgroundJobProcessor>();

            await memoryService.UpsertAsync(
                new MemoryUpsertRequest(
                    ExternalKey: "repo:api:1",
                    Scope: MemoryScope.Project,
                    MemoryType: MemoryType.Fact,
                    Title: "API health contract",
                    Content: "The status endpoint is used for liveness and readiness verification.",
                    Summary: "Status endpoint contract",
                    SourceType: "document",
                    SourceRef: "README",
                    Tags: ["api", "health"],
                    Importance: 0.7m,
                    Confidence: 0.9m),
                CancellationToken.None);

            await processor.ProcessNextAsync(CancellationToken.None);
        }

        using var client = environment.GetFactory().CreateClient();
        var status = await client.GetFromJsonAsync<SystemStatusResult>("/api/status");
        var hits = await client.GetFromJsonAsync<List<MemorySearchHit>>("/api/memories/search?query=status%20endpoint");
        var context = await client.PostAsJsonAsync("/api/context/build", new WorkingContextRequest("status endpoint", 3, 3));

        status.Should().NotBeNull();
        status!.Service.Should().Be("mcp-server");
        status.BuildVersion.Should().NotBeNullOrWhiteSpace();
        status.EmbeddingProfile.Should().Be("compact");
        status.ExecutionProvider.Should().Be("Deterministic");
        status.MaxTokens.Should().Be(512);
        status.InferenceThreads.Should().BeGreaterThan(0);
        status.BatchSize.Should().Be(8);
        status.BatchingEnabled.Should().BeTrue();
        hits.Should().NotBeNull();
        hits!.Should().Contain(x => x.Title == "API health contract");
        context.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }

    [DockerRequiredFact]
    public async Task Log_Endpoints_Should_Query_Db_First_Runtime_Logs()
    {
        long logId;
        using (var scope = environment.GetFactory().Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var entries = new[]
            {
                new RuntimeLogEntry
                {
                    ServiceName = "mcp-server",
                    Category = "Memory.ApiContractTests",
                    Level = "Error",
                    Message = "Synthetic runtime failure for api contract validation.",
                    Exception = "System.InvalidOperationException: synthetic",
                    TraceId = "trace-api-log-1",
                    RequestId = "request-api-log-1",
                    PayloadJson = """{"kind":"test"}""",
                    CreatedAt = DateTimeOffset.UtcNow
                },
                new RuntimeLogEntry
                {
                    ServiceName = "worker",
                    Category = "Memory.ApiContractTests",
                    Level = "Warning",
                    Message = "Synthetic runtime failure for api contract validation in worker.",
                    Exception = string.Empty,
                    TraceId = "trace-api-log-2",
                    RequestId = "request-api-log-2",
                    PayloadJson = """{"kind":"worker-test"}""",
                    CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-1)
                }
            };

            dbContext.RuntimeLogEntries.AddRange(entries);
            await dbContext.SaveChangesAsync(CancellationToken.None);
            logId = entries[0].Id;
        }

        using var client = environment.GetFactory().CreateClient();
        var hits = await client.GetFromJsonAsync<List<LogEntryResult>>("/api/logs/search?query=runtime%20failure&serviceName=mcp-server&serviceName=worker&level=Error,Warning");
        var log = await client.GetFromJsonAsync<LogEntryResult>($"/api/logs/{logId}");

        hits.Should().NotBeNull();
        hits!.Select(x => x.TraceId).Should().BeEquivalentTo(["trace-api-log-1", "trace-api-log-2"]);
        log.Should().NotBeNull();
        log!.Id.Should().Be(logId);
        log.TraceId.Should().Be("trace-api-log-1");
    }

    [DockerRequiredFact]
    public async Task Log_Search_Should_Accept_NonUtc_Time_Range_Filters()
    {
        var createdAtUtc = new DateTimeOffset(2026, 4, 16, 12, 0, 0, TimeSpan.Zero);

        using (var scope = environment.GetFactory().Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            dbContext.RuntimeLogEntries.Add(new RuntimeLogEntry
            {
                ServiceName = "mcp-server",
                Category = "Memory.ApiContractTests",
                Level = "Error",
                Message = "Timezone normalization validation log entry.",
                Exception = string.Empty,
                TraceId = "trace-api-log-timezone",
                RequestId = "request-api-log-timezone",
                PayloadJson = """{"kind":"timezone-test"}""",
                CreatedAt = createdAtUtc
            });
            await dbContext.SaveChangesAsync(CancellationToken.None);
        }

        var fromLocal = createdAtUtc.ToOffset(TimeSpan.FromHours(8)).AddMinutes(-1);
        var toLocal = createdAtUtc.ToOffset(TimeSpan.FromHours(8)).AddMinutes(1);

        using var client = environment.GetFactory().CreateClient();
        var hits = await client.GetFromJsonAsync<List<LogEntryResult>>(
            $"/api/logs/search?traceId=trace-api-log-timezone&from={System.Uri.EscapeDataString(fromLocal.ToString("O"))}&to={System.Uri.EscapeDataString(toLocal.ToString("O"))}");

        hits.Should().NotBeNull();
        hits!.Should().ContainSingle(x => x.TraceId == "trace-api-log-timezone");
    }

    [DockerRequiredFact]
    public async Task Evaluation_Create_Suite_Should_Return_Validation_Error_When_Query_Is_Blank()
    {
        using var client = environment.GetFactory().CreateClient();
        using var response = await client.PostAsJsonAsync(
            "/api/evaluation/suites",
            new EvaluationSuiteCreateRequest(
                "Broken Suite",
                "Contains invalid case payload.",
                [
                    new EvaluateCaseUpsertRequest(
                        "Broken scenario",
                        "   ",
                        null,
                        ["demo-memory"])
                ],
                "ContextHub"));

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().Contain("Evaluation case");
        payload.Should().Contain("query is required");
    }

    [DockerRequiredFact]
    public async Task Evaluation_Run_Should_Return_Validation_Error_When_Suite_Contains_Empty_Query()
    {
        Guid suiteId;
        using (var scope = environment.GetFactory().Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var suite = new EvaluationSuite
            {
                ProjectId = "ContextHub",
                Name = "Broken Suite",
                Description = "Contains invalid case payload.",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            };
            var evaluationCase = new EvaluationCase
            {
                SuiteId = suite.Id,
                ProjectId = suite.ProjectId,
                ScenarioLabel = "Broken scenario",
                Query = "   ",
                ExpectedExternalKeys = ["demo-memory"],
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            };

            dbContext.EvaluationSuites.Add(suite);
            dbContext.EvaluationCases.Add(evaluationCase);
            await dbContext.SaveChangesAsync(CancellationToken.None);
            suiteId = suite.Id;
        }

        using var client = environment.GetFactory().CreateClient();
        using var response = await client.PostAsJsonAsync("/api/evaluation/runs", new EvaluationRunRequest(suiteId));

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().Contain("Evaluation case");
        payload.Should().Contain("query is required");
    }

    [DockerRequiredFact]
    public async Task Governance_Analyze_Endpoint_Should_Create_Findings_And_Suggested_Actions()
    {
        using (var scope = environment.GetFactory().Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            dbContext.SourceConnections.Add(new SourceConnection
            {
                ProjectId = ProjectContext.DefaultProjectId,
                Name = "Stale repo source",
                SourceKind = SourceKind.LocalRepo,
                Enabled = true,
                ConfigJson = """{"rootPath":"W:/Repositories/WJCY/ContextHub"}""",
                SecretJsonProtected = string.Empty,
                LastSuccessfulSyncAt = null,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-2),
                UpdatedAt = DateTimeOffset.UtcNow.AddHours(-2)
            });
            await dbContext.SaveChangesAsync(CancellationToken.None);
        }

        using var client = environment.GetFactory().CreateClient();

        using var analyzeResponse = await client.PostAsJsonAsync(
            "/api/governance/analyze",
            new GovernanceAnalyzeRequest(ProjectContext.DefaultProjectId));

        analyzeResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var analyzeResult = await analyzeResponse.Content.ReadFromJsonAsync<GovernanceAnalyzeResult>();
        analyzeResult.Should().NotBeNull();
        analyzeResult!.ProjectId.Should().Be(ProjectContext.DefaultProjectId);
        analyzeResult.OpenFindingCount.Should().BeGreaterThan(0);
        analyzeResult.PendingActionCount.Should().BeGreaterThan(0);

        var findings = await client.GetFromJsonAsync<List<GovernanceFindingResult>>(
            $"/api/governance/findings?projectId={ProjectContext.DefaultProjectId}&status=Open");
        findings.Should().NotBeNull();
        findings!.Should().Contain(x => x.Type == GovernanceFindingType.StaleSource);

        var actions = await client.GetFromJsonAsync<List<SuggestedActionResult>>(
            $"/api/actions?projectId={ProjectContext.DefaultProjectId}&status=Pending");
        actions.Should().NotBeNull();
        actions!.Should().Contain(x => x.Type == SuggestedActionType.SyncSourceNow);
    }

    [DockerRequiredFact]
    public async Task Performance_Measure_Endpoint_Should_Report_Current_Runtime_Characteristics()
    {
        using (var scope = environment.GetFactory().Services.CreateScope())
        {
            var memoryService = scope.ServiceProvider.GetRequiredService<IMemoryService>();
            var processor = scope.ServiceProvider.GetRequiredService<IBackgroundJobProcessor>();

            await memoryService.UpsertAsync(
                new MemoryUpsertRequest(
                    ExternalKey: "repo:api:perf:1",
                    Scope: MemoryScope.Project,
                    MemoryType: MemoryType.Artifact,
                    Title: "Performance benchmark fixture",
                    Content: """
                            The performance benchmark endpoint should measure chunking, embeddings, and hybrid search.

                            This document exists to seed vector and keyword indexes for the benchmark contract test.
                            """,
                    Summary: "Performance fixture",
                    SourceType: "document",
                    SourceRef: "tests",
                    Tags: ["api", "performance"],
                    Importance: 0.8m,
                    Confidence: 0.9m),
                CancellationToken.None);

            await processor.ProcessNextAsync(CancellationToken.None);
        }

        using var client = environment.GetFactory().CreateClient();
        using var response = await client.PostAsJsonAsync("/api/performance/measure", new PerformanceMeasureRequest(
            Query: "performance benchmark endpoint",
            Document: """
                      Measure the configured runtime using the current embedding model and current PostgreSQL state.

                      The benchmark should include chunking, embeddings, and hybrid search.
                      """,
            SearchLimit: 5,
            WarmupIterations: 0,
            MeasurementIterations: 2));

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<PerformanceMeasureResult>();
        result.Should().NotBeNull();
        result!.EmbeddingProvider.Should().Be("Deterministic");
        result.EmbeddingProfile.Should().Be("compact");
        result.ModelKey.Should().Be("deterministic-384");
        result.Dimensions.Should().Be(384);
        result.MeasurementMode.Should().Be(PerformanceMeasurementMode.Iterations);
        result.MeasurementIterations.Should().Be(2);
        result.ChunkCount.Should().BeGreaterThan(0);
        result.HybridHitCount.Should().BeGreaterThan(0);
        result.QueryEmbedding.Iterations.Should().Be(2);
        result.TotalMeasurementMilliseconds.Should().BeGreaterThan(0);
        result.HybridSearch.AverageMilliseconds.Should().BeGreaterThanOrEqualTo(0);
        result.DocumentEmbedding.ThroughputPerSecond.Should().BeGreaterThan(0);
    }

    [DockerRequiredFact]
    public async Task Performance_Measure_Endpoint_Should_Support_Duration_Mode()
    {
        using var client = environment.GetFactory().CreateClient();
        using var response = await client.PostAsJsonAsync("/api/performance/measure", new PerformanceMeasureRequest(
            Query: "duration performance benchmark",
            Document: "Run the performance probe in duration mode so the benchmark is not based on a single short burst.",
            SearchLimit: 3,
            WarmupIterations: 0,
            MeasurementIterations: 1,
            MeasurementMode: PerformanceMeasurementMode.Duration,
            MeasurementDurationSeconds: 1,
            MaxMeasurementIterations: 5000));

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<PerformanceMeasureResult>();
        result.Should().NotBeNull();
        result!.MeasurementMode.Should().Be(PerformanceMeasurementMode.Duration);
        result.RequestedMeasurementDurationSeconds.Should().Be(1);
        result.MeasurementIterations.Should().BeGreaterThan(0);
        result.TotalMeasurementMilliseconds.Should().BeGreaterThanOrEqualTo(900);
        result.QueryEmbedding.Iterations.Should().Be(result.MeasurementIterations);
    }

    [DockerRequiredFact]
    public async Task User_Preference_Endpoints_Should_Persist_And_Return_User_Profile_Context()
    {
        using var client = environment.GetFactory().CreateClient();
        using var createResponse = await client.PostAsJsonAsync("/api/user/preferences", new UserPreferenceUpsertRequest(
            Key: "response-style",
            Kind: UserPreferenceKind.CommunicationStyle,
            Title: "偏好繁體中文",
            Content: "回覆預設使用繁體中文，技術名詞保留英文。",
            Rationale: "這是長期偏好",
            Tags: ["language", "style"]));

        createResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var created = await createResponse.Content.ReadFromJsonAsync<UserPreferenceResult>();
        created.Should().NotBeNull();

        var preferences = await client.GetFromJsonAsync<List<UserPreferenceResult>>("/api/user/preferences?kind=CommunicationStyle&limit=10");
        preferences.Should().NotBeNull();
        preferences!.Should().ContainSingle(x => x.Key == "response-style");

        using var contextResponse = await client.PostAsJsonAsync("/api/context/build", new WorkingContextRequest("請依照我的回覆習慣整理工作上下文", 3, 3));
        contextResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var context = await contextResponse.Content.ReadFromJsonAsync<WorkingContextResult>();
        context.Should().NotBeNull();
        context!.UserPreferences.Should().ContainSingle(x => x.Key == "response-style");

        using var archiveResponse = await client.PatchAsJsonAsync($"/api/user/preferences/{created!.Id}", new { archived = true });
        archiveResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var archived = await archiveResponse.Content.ReadFromJsonAsync<UserPreferenceResult>();
        archived.Should().NotBeNull();
        archived!.Status.Should().Be(MemoryStatus.Archived);
    }

    [DockerRequiredFact]
    public async Task User_Preference_Endpoints_Should_Allow_Repeated_Upsert_For_Same_Key()
    {
        using var client = environment.GetFactory().CreateClient();

        using var createResponse = await client.PostAsJsonAsync("/api/user/preferences", new UserPreferenceUpsertRequest(
            Key: "preferred-language",
            Kind: UserPreferenceKind.CommunicationStyle,
            Title: "偏好繁體中文",
            Content: "回覆預設使用繁體中文。",
            Rationale: "初始偏好"));

        createResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var created = await createResponse.Content.ReadFromJsonAsync<UserPreferenceResult>();
        created.Should().NotBeNull();

        using var updateResponse = await client.PostAsJsonAsync("/api/user/preferences", new UserPreferenceUpsertRequest(
            Key: "preferred-language",
            Kind: UserPreferenceKind.CommunicationStyle,
            Title: "偏好繁體中文",
            Content: "回覆預設使用繁體中文，技術名詞保留英文。",
            Rationale: "更新偏好"));

        updateResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var updated = await updateResponse.Content.ReadFromJsonAsync<UserPreferenceResult>();
        updated.Should().NotBeNull();
        updated!.Id.Should().Be(created!.Id);
        updated.Content.Should().Contain("技術名詞保留英文");
        updated.Rationale.Should().Be("更新偏好");
    }

    [DockerRequiredFact]
    public async Task Dashboard_Endpoints_Should_Return_Overview_Runtime_And_Storage_Payloads()
    {
        Guid memoryId;
        using (var scope = environment.GetFactory().Services.CreateScope())
        {
            var memoryService = scope.ServiceProvider.GetRequiredService<IMemoryService>();
            var processor = scope.ServiceProvider.GetRequiredService<IBackgroundJobProcessor>();

            var created = await memoryService.UpsertAsync(
                new MemoryUpsertRequest(
                    ExternalKey: "repo:dashboard:1",
                    Scope: MemoryScope.Project,
                    MemoryType: MemoryType.Artifact,
                    Title: "Dashboard API fixture",
                    Content: "Dashboard endpoints should expose overview cards, memory list pages, and storage rows.",
                    Summary: "Dashboard API fixture",
                    SourceType: "document",
                    SourceRef: "tests",
                    Tags: ["dashboard", "api"],
                    Importance: 0.8m,
                    Confidence: 0.9m),
                CancellationToken.None);

            memoryId = created.Id;
            await processor.ProcessNextAsync(CancellationToken.None);
            await memoryService.EnqueueReindexAsync(new EnqueueReindexRequest(), CancellationToken.None);
        }

        using var client = environment.GetFactory().CreateClient();
        var overview = await client.GetFromJsonAsync<DashboardOverviewResult>("/api/dashboard/overview");
        var runtime = await client.GetFromJsonAsync<DashboardRuntimeResult>("/api/dashboard/runtime");
        var monitoring = await client.GetFromJsonAsync<DashboardMonitoringResult>("/api/dashboard/monitoring");
        var memories = await client.GetFromJsonAsync<PagedResult<MemoryListItemResult>>("/api/memories?page=1&pageSize=10");
        var details = await client.GetFromJsonAsync<MemoryDetailsResult>($"/api/memories/{memoryId}/details");
        var jobs = await client.GetFromJsonAsync<PagedResult<JobListItemResult>>("/api/jobs?page=1&pageSize=10");
        var tables = await client.GetFromJsonAsync<List<StorageTableSummaryResult>>("/api/storage/tables");
        var rows = await client.GetFromJsonAsync<StorageTableRowsResult>("/api/storage/memory_items?query=Dashboard&page=1&pageSize=5");

        overview.Should().NotBeNull();
        overview!.BuildVersion.Should().NotBeNullOrWhiteSpace();
        overview!.Metrics.Should().Contain(x => x.Key == "memoryItems");
        overview.Metrics.Should().Contain(x => x.Key == "defaultProjectMemoryItems");
        runtime.Should().NotBeNull();
        runtime!.BuildVersion.Should().NotBeNullOrWhiteSpace();
        runtime!.EmbeddingProfile.Should().Be("compact");
        monitoring.Should().NotBeNull();
        monitoring!.BuildVersion.Should().NotBeNullOrWhiteSpace();
        monitoring.Redis.Should().NotBeNull();
        monitoring.Postgres.Should().NotBeNull();
        monitoring.DependencyResources.Should().NotBeNull();
        memories.Should().NotBeNull();
        memories!.Items.Should().Contain(x => x.Id == memoryId);
        details.Should().NotBeNull();
        details!.Document.Id.Should().Be(memoryId);
        details.Chunks.Should().NotBeEmpty();
        jobs.Should().NotBeNull();
        jobs!.Items.Should().NotBeEmpty();
        tables.Should().NotBeNull();
        tables!.Should().Contain(x => x.Name == "memory_items");
        rows.Should().NotBeNull();
        rows!.Table.Should().Be("memory_items");
        rows.Description.Should().NotBeNullOrWhiteSpace();
        rows.SearchableColumns.Should().Contain("title");
        rows.AppliedQuery.Should().Be("Dashboard");
        rows.Rows.Items.Should().NotBeEmpty();
        rows.Rows.Items.Should().Contain(x => x.Values["title"] == "Dashboard API fixture");
    }

    [DockerRequiredFact]
    public async Task Memories_Endpoint_Should_Allow_Querying_By_ProjectId_Without_Project_Filter()
    {
        using (var scope = environment.GetFactory().Services.CreateScope())
        {
            var memoryService = scope.ServiceProvider.GetRequiredService<IMemoryService>();

            await memoryService.UpsertAsync(
                new MemoryUpsertRequest(
                    ExternalKey: "repo:vital:1",
                    Scope: MemoryScope.Project,
                    MemoryType: MemoryType.Artifact,
                    Title: "Vital document summary",
                    Content: "This memory belongs to the Vital AirMeet document repository.",
                    Summary: "Vital AirMeet artifact",
                    SourceType: "document",
                    SourceRef: "tests",
                    Tags: ["vital"],
                    Importance: 0.8m,
                    Confidence: 0.9m,
                    ProjectId: "Vital_AirMeet_Document"),
                CancellationToken.None);

            await memoryService.UpsertAsync(
                new MemoryUpsertRequest(
                    ExternalKey: "repo:other:1",
                    Scope: MemoryScope.Project,
                    MemoryType: MemoryType.Artifact,
                    Title: "Other project summary",
                    Content: "This memory belongs to another project.",
                    Summary: "Other artifact",
                    SourceType: "document",
                    SourceRef: "tests",
                    Tags: ["other"],
                    Importance: 0.7m,
                    Confidence: 0.9m,
                    ProjectId: "Other_Project"),
                CancellationToken.None);
        }

        using var client = environment.GetFactory().CreateClient();
        var result = await client.GetFromJsonAsync<PagedResult<MemoryListItemResult>>("/api/memories?query=Vital_AirMeet_Document&page=1&pageSize=10");

        result.Should().NotBeNull();
        result!.Items.Should().ContainSingle(x => x.ProjectId == "Vital_AirMeet_Document");
    }

    [DockerRequiredFact]
    public async Task Memory_Project_Suggestions_Endpoint_Should_Support_Fuzzy_Search()
    {
        using (var scope = environment.GetFactory().Services.CreateScope())
        {
            var memoryService = scope.ServiceProvider.GetRequiredService<IMemoryService>();

            await memoryService.UpsertAsync(
                new MemoryUpsertRequest(
                    ExternalKey: "repo:project-suggestion:1",
                    Scope: MemoryScope.Project,
                    MemoryType: MemoryType.Artifact,
                    Title: "Project suggestion fixture",
                    Content: "Used to validate fuzzy project suggestions.",
                    Summary: "Project suggestion fixture",
                    SourceType: "document",
                    SourceRef: "tests",
                    Tags: ["project", "suggestion"],
                    Importance: 0.7m,
                    Confidence: 0.9m,
                    ProjectId: "Vital_AirMeet_Document"),
                CancellationToken.None);
        }

        using var client = environment.GetFactory().CreateClient();
        var result = await client.GetFromJsonAsync<List<ProjectSuggestionResult>>("/api/memories/projects?query=Vital&limit=10");

        result.Should().NotBeNull();
        result!.Should().ContainSingle(x => x.ProjectId == "Vital_AirMeet_Document");
    }

    [DockerRequiredFact]
    public async Task Memory_Graph_Endpoint_Should_Return_Explicit_And_Similarity_Edges_With_Source_Fallbacks()
    {
        var projectId = $"GraphProject_{Guid.NewGuid():N}";
        var sourceConnectionId = Guid.NewGuid();

        using (var scope = environment.GetFactory().Services.CreateScope())
        {
            var memoryService = scope.ServiceProvider.GetRequiredService<IMemoryService>();
            var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

            dbContext.SourceConnections.Add(new SourceConnection
            {
                Id = sourceConnectionId,
                ProjectId = projectId,
                Name = "Graph Seed Source",
                SourceKind = SourceKind.LocalDocs,
                Enabled = true,
                ConfigJson = "{}",
                SecretJsonProtected = string.Empty,
                LastSuccessfulSyncAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
                UpdatedAt = DateTimeOffset.UtcNow.AddHours(-1)
            });

            var seed = await memoryService.UpsertAsync(
                new MemoryUpsertRequest(
                    ExternalKey: "graph-seed",
                    Scope: MemoryScope.Project,
                    MemoryType: MemoryType.Decision,
                    Title: "Graph API seed",
                    Content: "Graph API seed node content for seeded graph exploration.",
                    Summary: "Graph API seed node used for explicit and similarity edges.",
                    SourceType: "codex",
                    SourceRef: "tests/graph",
                    Tags: ["graph", "seed"],
                    Importance: 0.95m,
                    Confidence: 0.93m,
                    MetadataJson: $$"""{"connectorId":"{{sourceConnectionId}}","originPathOrUrl":"https://example.com/docs/graph","lineage":["tests","seeded-graph"]}""",
                    ProjectId: projectId),
                CancellationToken.None);

            var linked = await memoryService.UpsertAsync(
                new MemoryUpsertRequest(
                    ExternalKey: "graph-linked",
                    Scope: MemoryScope.Project,
                    MemoryType: MemoryType.Artifact,
                    Title: "Graph API linked artifact",
                    Content: "Graph API linked artifact content for explicit edge validation.",
                    Summary: "Graph API linked artifact remains close to the seed.",
                    SourceType: "codex",
                    SourceRef: "tests/graph",
                    Tags: ["graph", "artifact"],
                    Importance: 0.86m,
                    Confidence: 0.91m,
                    MetadataJson: $$"""{"connectorId":"{{sourceConnectionId}}","originPathOrUrl":"https://example.com/assets/graph.png","lineage":["tests","graph-image"]}""",
                    ProjectId: projectId),
                CancellationToken.None);

            await memoryService.UpsertAsync(
                new MemoryUpsertRequest(
                    ExternalKey: "graph-similar",
                    Scope: MemoryScope.Project,
                    MemoryType: MemoryType.Fact,
                    Title: "Graph API similar fact",
                    Content: "Graph API similar fact helps validate similarity edges in the graph endpoint.",
                    Summary: "Graph API similar fact shares seeded graph vocabulary and query terms.",
                    SourceType: "codex",
                    SourceRef: "tests/graph",
                    Tags: ["graph", "fact"],
                    Importance: 0.81m,
                    Confidence: 0.9m,
                    ProjectId: projectId),
                CancellationToken.None);

            dbContext.MemoryLinks.Add(new MemoryLink
            {
                FromId = seed.Id,
                ToId = linked.Id,
                LinkType = "related",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await dbContext.SaveChangesAsync(CancellationToken.None);
        }

        using var client = environment.GetFactory().CreateClient();
        var result = await client.GetFromJsonAsync<MemoryGraphResult>($"/api/memories/graph?query=Graph%20API&projectId={projectId}&graphMode=Seeded&includeSimilarity=true");

        result.Should().NotBeNull();
        result!.Nodes.Should().HaveCountGreaterThanOrEqualTo(2);
        result.Edges.Should().Contain(edge => edge.EdgeType == "explicit" && edge.Label == "related");
        result.Edges.Should().Contain(edge => edge.EdgeType == "similar");
        result.Nodes.Should().Contain(node => node.FaviconUrl == "https://example.com/favicon.ico");
        result.Nodes.Should().Contain(node => node.ThumbnailUrl == "https://example.com/assets/graph.png");
    }

    [DockerRequiredFact]
    public async Task Memory_Graph_Endpoint_Should_Report_Truncation_For_ProjectFull_Mode()
    {
        var projectId = $"GraphProject_{Guid.NewGuid():N}";

        using (var scope = environment.GetFactory().Services.CreateScope())
        {
            var memoryService = scope.ServiceProvider.GetRequiredService<IMemoryService>();

            foreach (var index in Enumerable.Range(0, 4))
            {
                await memoryService.UpsertAsync(
                    new MemoryUpsertRequest(
                        ExternalKey: $"graph-projectfull-{index}",
                        Scope: MemoryScope.Project,
                        MemoryType: index % 2 == 0 ? MemoryType.Decision : MemoryType.Fact,
                        Title: $"ProjectFull graph node {index}",
                        Content: $"ProjectFull node {index} content.",
                        Summary: $"ProjectFull node {index} summary.",
                        SourceType: "codex",
                        SourceRef: "tests/projectfull",
                        Tags: ["graph", $"cluster-{index % 2}"],
                        Importance: 0.7m + (index * 0.05m),
                        Confidence: 0.88m,
                        ProjectId: projectId),
                    CancellationToken.None);
            }
        }

        using var client = environment.GetFactory().CreateClient();
        var result = await client.GetFromJsonAsync<MemoryGraphResult>($"/api/memories/graph?projectId={projectId}&graphMode=ProjectFull&maxNodes=2");

        result.Should().NotBeNull();
        result!.Stats.Truncated.Should().BeTrue();
        result.Stats.NodeCount.Should().Be(2);
        result.Stats.TruncationReason.Should().NotBeNullOrWhiteSpace();
    }

    [DockerRequiredFact]
    public async Task Memory_Graph_Endpoint_Should_Use_ProjectItems_For_AllProjects_Integrated_View()
    {
        var projectA = $"GraphIntegratedA_{Guid.NewGuid():N}";
        var projectB = $"GraphIntegratedB_{Guid.NewGuid():N}";
        var tag = $"graph-default-all-{Guid.NewGuid():N}";

        using (var scope = environment.GetFactory().Services.CreateScope())
        {
            var memoryService = scope.ServiceProvider.GetRequiredService<IMemoryService>();

            await memoryService.UpsertAsync(
                new MemoryUpsertRequest(
                    ExternalKey: "graph-integrated-a-primary",
                    Scope: MemoryScope.Project,
                    MemoryType: MemoryType.Decision,
                    Title: "Integrated graph project A primary node",
                    Content: "Primary node for project A in all-project graph verification.",
                    Summary: "Project A primary node should remain visible in integrated graph.",
                    SourceType: "codex",
                    SourceRef: "tests/graph-integrated",
                    Tags: [tag, "graph", "project-a"],
                    Importance: 0.96m,
                    Confidence: 0.92m,
                    ProjectId: projectA),
                CancellationToken.None);

            await memoryService.UpsertAsync(
                new MemoryUpsertRequest(
                    ExternalKey: "graph-integrated-a-secondary",
                    Scope: MemoryScope.Project,
                    MemoryType: MemoryType.Fact,
                    Title: "Integrated graph project A secondary node",
                    Content: "Secondary node for project A in all-project graph verification.",
                    Summary: "Project A secondary node should not crowd out other projects.",
                    SourceType: "codex",
                    SourceRef: "tests/graph-integrated",
                    Tags: [tag, "graph", "project-a"],
                    Importance: 0.91m,
                    Confidence: 0.9m,
                    ProjectId: projectA),
                CancellationToken.None);

            await memoryService.UpsertAsync(
                new MemoryUpsertRequest(
                    ExternalKey: "graph-integrated-b-primary",
                    Scope: MemoryScope.Project,
                    MemoryType: MemoryType.Artifact,
                    Title: "Integrated graph project B primary node",
                    Content: "Primary node for project B in all-project graph verification.",
                    Summary: "Project B primary node should also appear in integrated graph.",
                    SourceType: "codex",
                    SourceRef: "tests/graph-integrated",
                    Tags: [tag, "graph", "project-b"],
                    Importance: 0.72m,
                    Confidence: 0.89m,
                    ProjectId: projectB),
                CancellationToken.None);

            await memoryService.UpsertAsync(
                new MemoryUpsertRequest(
                    ExternalKey: "graph-integrated-shared",
                    Scope: MemoryScope.Project,
                    MemoryType: MemoryType.Summary,
                    Title: "Integrated graph shared summary node",
                    Content: "Shared summary node that should be excluded from all-project integrated graph defaults.",
                    Summary: "Shared summary node for integrated graph exclusion test.",
                    SourceType: "codex",
                    SourceRef: "tests/graph-integrated",
                    Tags: [tag, "graph", "shared"],
                    Importance: 0.99m,
                    Confidence: 0.95m,
                    ProjectId: ProjectContext.SharedProjectId),
                CancellationToken.None);
        }

        using var client = environment.GetFactory().CreateClient();
        var result = await client.GetFromJsonAsync<MemoryGraphResult>($"/api/memories/graph?tag={tag}&graphMode=Seeded&includeSimilarity=false&maxNodes=4");

        result.Should().NotBeNull();
        result!.Nodes.Should().Contain(node => node.ProjectId == projectA);
        result.Nodes.Should().Contain(node => node.ProjectId == projectB);
        result.Nodes.Should().NotContain(node => node.ProjectId == ProjectContext.SharedProjectId);
    }

    [DockerRequiredFact]
    public async Task Summary_Refresh_Endpoint_Should_Enqueue_Refresh_Summary_Job()
    {
        using var client = environment.GetFactory().CreateClient();

        using var response = await client.PostAsJsonAsync("/api/jobs/summary-refresh", new EnqueueSummaryRefreshRequest(
            ProjectId: null,
            IncludedProjectIds: null));

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<EnqueueSummaryRefreshResult>();
        result.Should().NotBeNull();
        result!.Status.Should().Be(MemoryJobStatus.Pending);
    }

    [DockerRequiredFact]
    public async Task Conversation_Ingest_Endpoints_Should_Create_Checkpoints_Insights_And_List_Them()
    {
        var conversationId = $"api-conversation-{Guid.NewGuid():N}";

        using (var scope = environment.GetFactory().Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
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
        }

        using var client = environment.GetFactory().CreateClient();
        using var ingestResponse = await client.PostAsJsonAsync("/api/conversations/ingest", new ConversationIngestRequest(
            ConversationId: conversationId,
            TurnId: "turn-1",
            EventType: ConversationEventType.SessionCheckpoint,
            SourceKind: ConversationSourceKind.HostEvent,
            SourceSystem: "codex",
            SourceRef: "api-tests",
            ProjectName: "ContextHub",
            UserMessageSummary: "使用者偏好回覆預設使用繁體中文。",
            AgentMessageSummary: "系統決定採用 shared summary layer。"));

        ingestResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var ingest = await ingestResponse.Content.ReadFromJsonAsync<ConversationIngestResult>();
        ingest.Should().NotBeNull();
        ingest!.AutomationScheduled.Should().BeTrue();

        using (var scope = environment.GetFactory().Services.CreateScope())
        {
            var processor = scope.ServiceProvider.GetRequiredService<IBackgroundJobProcessor>();
            var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            await DrainConversationAutomationAsync(processor, dbContext, conversationId, CancellationToken.None);
        }

        var sessions = await client.GetFromJsonAsync<List<ConversationSessionResult>>($"/api/conversations/sessions?conversationId={conversationId}");
        sessions.Should().NotBeNull();
        sessions!.Should().ContainSingle(x => x.ConversationId == conversationId);

        var insights = await client.GetFromJsonAsync<List<ConversationInsightResult>>($"/api/conversations/insights?conversationId={conversationId}");
        insights.Should().NotBeNull();
        insights!.Should().Contain(x => x.PromotionStatus == ConversationPromotionStatus.Promoted);
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

    [DockerRequiredFact]
    public async Task Memory_Transfer_Endpoints_Should_Support_Encrypted_Export_Preview_And_Overwrite_Apply()
    {
        using (var scope = environment.GetFactory().Services.CreateScope())
        {
            var memoryService = scope.ServiceProvider.GetRequiredService<IMemoryService>();

            await memoryService.UpsertAsync(
                new MemoryUpsertRequest(
                    ExternalKey: "repo:transfer:1",
                    Scope: MemoryScope.Project,
                    MemoryType: MemoryType.Fact,
                    Title: "Transfer fixture one",
                    Content: "Exported memory package should be importable.",
                    Summary: "Transfer fixture one",
                    SourceType: "document",
                    SourceRef: "tests",
                    Tags: ["transfer", "api"],
                    Importance: 0.7m,
                    Confidence: 0.9m),
                CancellationToken.None);

            await memoryService.UpsertAsync(
                new MemoryUpsertRequest(
                    ExternalKey: "repo:transfer:2",
                    Scope: MemoryScope.Project,
                    MemoryType: MemoryType.Artifact,
                    Title: "Transfer fixture two",
                    Content: "Encrypted export should require a passphrase during import preview.",
                    Summary: "Transfer fixture two",
                    SourceType: "document",
                    SourceRef: "tests",
                    Tags: ["transfer", "api"],
                    Importance: 0.7m,
                    Confidence: 0.9m),
                CancellationToken.None);
        }

        using var client = environment.GetFactory().CreateClient();

        using var exportResponse = await client.PostAsJsonAsync("/api/memories/export", new MemoryExportRequest(
            Query: "Transfer fixture",
            Passphrase: "secret-passphrase"));
        exportResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var exported = await exportResponse.Content.ReadFromJsonAsync<MemoryTransferDownloadResult>();
        exported.Should().NotBeNull();
        exported!.Encrypted.Should().BeTrue();
        exported.ItemCount.Should().Be(2);

        using var missingPassphrasePreview = await client.PostAsJsonAsync("/api/memories/import/preview", new MemoryImportRequest(exported.PayloadBase64));
        missingPassphrasePreview.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);

        using var previewResponse = await client.PostAsJsonAsync("/api/memories/import/preview", new MemoryImportRequest(exported.PayloadBase64, "secret-passphrase"));
        previewResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var preview = await previewResponse.Content.ReadFromJsonAsync<MemoryImportPreviewResult>();
        preview.Should().NotBeNull();
        preview!.ConflictItems.Should().Be(2);
        preview.Conflicts.Should().Contain(x => x.ExternalKey == "repo:transfer:1");

        using var applyRejectedResponse = await client.PostAsJsonAsync("/api/memories/import/apply", new MemoryImportRequest(exported.PayloadBase64, "secret-passphrase"));
        applyRejectedResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);

        using var applyResponse = await client.PostAsJsonAsync("/api/memories/import/apply", new MemoryImportRequest(exported.PayloadBase64, "secret-passphrase", ForceOverwrite: true));
        applyResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var applied = await applyResponse.Content.ReadFromJsonAsync<MemoryImportApplyResult>();
        applied.Should().NotBeNull();
        applied!.ImportedItems.Should().Be(2);
        applied.OverwrittenItems.Should().Be(2);

        using var verifyMemoriesResponse = await client.GetAsync("/api/memories?query=Transfer%20fixture&page=1&pageSize=10");
        verifyMemoriesResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var verifyMemories = await verifyMemoriesResponse.Content.ReadFromJsonAsync<PagedResult<MemoryListItemResult>>();
        verifyMemories.Should().NotBeNull();
        verifyMemories!.Items.Should().HaveCount(2);
    }
}
