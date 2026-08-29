using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
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
            UseBootstrapActor(scope.ServiceProvider);
            var memoryService = scope.ServiceProvider.GetRequiredService<IMemoryService>();
            var processor = scope.ServiceProvider.GetRequiredService<IBackgroundJobProcessor>();
            var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

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
        var contextPayload = await context.Content.ReadFromJsonAsync<WorkingContextResult>();
        var overview = await client.GetFromJsonAsync<DashboardOverviewResult>("/api/dashboard/overview");

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
        hits!.Single(x => x.Title == "API health contract").SourceTokenEstimate.Should().BeGreaterThan(0);
        context.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        contextPayload.Should().NotBeNull();
        contextPayload!.SavingsEstimate.Should().NotBeNull();
        contextPayload.SavingsEstimate!.ApproxBaselineTokens.Should().Be(contextPayload.SavingsEstimate.BaselineTokenEstimate);
        contextPayload.SavingsEstimate.TokenCountingMode.Should().NotBeNullOrWhiteSpace();
        overview.Should().NotBeNull();
        overview!.ContextSavings.Should().NotBeNull();
        overview.ContextSavings!.Windows.Should().NotBeNull();
        overview.ContextSavings.Windows!.Select(x => x.Key).Should().Contain(["24h", "3d", "7d", "30d"]);
    }

    [DockerRequiredFact]
    public async Task Search_Should_Scope_Hybrid_Candidates_To_Requested_Project_And_Record_Diagnostics()
    {
        var projectA = $"SearchScopeA-{Guid.NewGuid():N}";
        var projectB = $"SearchScopeB-{Guid.NewGuid():N}";
        var query = $"project scoped semantic needle {Guid.NewGuid():N}";

        using (var scope = environment.GetFactory().Services.CreateScope())
        {
            UseBootstrapActor(scope.ServiceProvider);
            var memoryService = scope.ServiceProvider.GetRequiredService<IMemoryService>();
            var processor = scope.ServiceProvider.GetRequiredService<IBackgroundJobProcessor>();

            await memoryService.UpsertAsync(
                new MemoryUpsertRequest(
                    ExternalKey: $"search-scope:a:{Guid.NewGuid():N}",
                    Scope: MemoryScope.Project,
                    MemoryType: MemoryType.Fact,
                    Title: "Project A scoped search fixture",
                    Content: $"{query} belongs to the authorized target project.",
                    Summary: "Project A search target",
                    SourceType: "test",
                    SourceRef: "api-contract",
                    Tags: ["search", "scope"],
                    Importance: 0.7m,
                    Confidence: 0.9m,
                    ProjectId: projectA),
                CancellationToken.None);

            await memoryService.UpsertAsync(
                new MemoryUpsertRequest(
                    ExternalKey: $"search-scope:b:{Guid.NewGuid():N}",
                    Scope: MemoryScope.Project,
                    MemoryType: MemoryType.Fact,
                    Title: "Project B scoped search distractor",
                    Content: $"{query} belongs to another project and must not dilute scoped retrieval.",
                    Summary: "Project B search distractor",
                    SourceType: "test",
                    SourceRef: "api-contract",
                    Tags: ["search", "scope"],
                    Importance: 0.99m,
                    Confidence: 0.99m,
                    ProjectId: projectB),
                CancellationToken.None);

            await processor.ProcessNextAsync(CancellationToken.None);
            await processor.ProcessNextAsync(CancellationToken.None);
        }

        using var client = environment.GetFactory().CreateClient();
        var encodedQuery = Uri.EscapeDataString(query);
        var hits = await client.GetFromJsonAsync<List<MemorySearchHit>>(
            $"/api/memories/search?query={encodedQuery}&projectId={Uri.EscapeDataString(projectA)}&limit=5");

        hits.Should().NotBeNull();
        hits!.Should().ContainSingle(x => x.Title == "Project A scoped search fixture");
        hits.Should().NotContain(x => x.ProjectId == projectB);

        using var verifyScope = environment.GetFactory().Services.CreateScope();
        var dbContext = verifyScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var telemetry = await dbContext.RetrievalEvents
            .Where(x => x.EntryPoint == "/api/memories/search" && x.ProjectId == projectA && x.QueryText == query)
            .OrderByDescending(x => x.CreatedAt)
            .FirstAsync(CancellationToken.None);
        using var metadata = JsonDocument.Parse(telemetry.MetadataJson);
        var diagnostics = metadata.RootElement.GetProperty("diagnostics");
        diagnostics.GetProperty("candidateMemoryCount").GetInt32().Should().BeGreaterThan(0);
        diagnostics.GetProperty("authorizedMemoryCount").GetInt32().Should().BeGreaterThan(0);
        diagnostics.GetProperty("keywordHitCount").GetInt32().Should().BeGreaterThan(0);
    }

    [DockerRequiredFact]
    public async Task Api_And_Health_Responses_Should_Not_Be_Cached_By_Cloudflare()
    {
        using var client = environment.GetFactory().CreateClient();

        using var liveResponse = await client.GetAsync("/health/live");
        using var statusResponse = await client.GetAsync("/api/status");

        liveResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        statusResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        AssertNoStoreHeaders(liveResponse);
        AssertNoStoreHeaders(statusResponse);
    }

    [DockerRequiredFact]
    public async Task Context_Bootstrap_Endpoint_Should_Describe_ContextHub_Without_Defaulting_ProjectId()
    {
        using var client = environment.GetFactory().CreateClient();

        var bootstrap = await client.GetFromJsonAsync<ContextHubBootstrapResult>("/api/context/bootstrap");
        var projectBootstrap = await client.GetFromJsonAsync<ContextHubBootstrapResult>("/api/context/bootstrap?projectId=ContextHub");

        bootstrap.Should().NotBeNull();
        bootstrap!.Service.Name.Should().Be("ContextHub");
        bootstrap.ToolCatalog.BackendToolCount.Should().Be(66);
        bootstrap.ToolCatalog.AppFacingToolCount.Should().Be(65);
        bootstrap.ToolCatalog.DeleteCapableToolCount.Should().Be(3);
        bootstrap.Project.ProjectIdProvided.Should().BeFalse();
        bootstrap.Project.ProjectId.Should().BeNull();
        bootstrap.Project.Guidance.Should().Contain("projectId");
        bootstrap.UserPreferences.BootstrapDisclosure.Should().Be("summary-and-policy");
        bootstrap.UserPreferences.AvailableKinds.Should().Contain(nameof(UserPreferenceKind.ToolingPreference));
        bootstrap.Warnings.Should().Contain(x => x.Contains("ProjectContext.DefaultProjectId", StringComparison.Ordinal));

        projectBootstrap.Should().NotBeNull();
        projectBootstrap!.Project.ProjectIdProvided.Should().BeTrue();
        projectBootstrap.Project.ProjectId.Should().Be("ContextHub");
        projectBootstrap.Project.RecommendedWorkingContextCall.Should().Contain("projectId=\"ContextHub\"");
    }

    [DockerRequiredFact]
    public async Task Log_Endpoints_Should_Query_Db_First_Runtime_Logs()
    {
        long logId;
        using (var scope = environment.GetFactory().Services.CreateScope())
        {
            UseBootstrapActor(scope.ServiceProvider);
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
            UseBootstrapActor(scope.ServiceProvider);
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
            UseBootstrapActor(scope.ServiceProvider);
            var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var actor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>().Current;
            var suite = new EvaluationSuite
            {
                TenantId = actor.TenantId,
                OwnerUserId = actor.UserId,
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
            UseBootstrapActor(scope.ServiceProvider);
            var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var actor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>().Current;
            dbContext.SourceConnections.Add(new SourceConnection
            {
                TenantId = actor.TenantId,
                OwnerUserId = actor.UserId,
                ProjectId = ProjectContext.DefaultProjectId,
                Name = "Stale repo source",
                SourceKind = SourceKind.LocalRepo,
                Enabled = true,
                ConfigJson = """{"rootPath":"C:/Repositories/Example/ContextHub"}""",
                SecretJsonProtected = string.Empty,
                LastSuccessfulSyncAt = null,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-2),
                UpdatedAt = DateTimeOffset.UtcNow.AddHours(-2)
            });
            dbContext.MemoryItems.Add(new MemoryItem
            {
                TenantId = actor.TenantId,
                OwnerUserId = actor.UserId,
                ProjectId = ProjectContext.DefaultProjectId,
                ExternalKey = $"governance-disposition:{Guid.NewGuid():N}",
                Scope = MemoryScope.Project,
                MemoryType = MemoryType.Fact,
                Title = "Governance disposition contract fixture",
                Content = "REMOVED",
                Summary = "Durable finding disposition contract fixture.",
                SourceType = "test",
                SourceRef = "api-contract",
                Importance = .1m,
                Confidence = .2m,
                Status = MemoryStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
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
        var memoryFinding = findings.Single(x => x.Type == GovernanceFindingType.LowValueMemoryCandidate);

        var dispositionRequest = new GovernanceFindingDispositionRequest(
            memoryFinding.Id,
            GovernanceFindingDisposition.RequiresUserDecision,
            "Contract test owner decision.",
            $"api-contract-{Guid.NewGuid():N}");
        using var dispositionResponse = await client.PostAsJsonAsync("/api/governance/findings/disposition", dispositionRequest);
        dispositionResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var disposed = await dispositionResponse.Content.ReadFromJsonAsync<GovernanceFindingResult>();
        disposed.Should().NotBeNull();
        disposed!.Status.Should().Be(GovernanceFindingStatus.RequiresUserDecision);
        disposed.GovernanceReason.Should().Be(dispositionRequest.Reason);
        disposed.GovernanceRunId.Should().Be(dispositionRequest.GovernanceRunId);
        disposed.GovernanceActor.Should().NotBeNullOrWhiteSpace();

        using var replayResponse = await client.PostAsJsonAsync("/api/governance/findings/disposition", dispositionRequest);
        replayResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var replayed = await replayResponse.Content.ReadFromJsonAsync<GovernanceFindingResult>();
        replayed!.GovernanceUpdatedAt.Should().BeCloseTo(disposed.GovernanceUpdatedAt!.Value, TimeSpan.FromMilliseconds(1));

        using var reopenResponse = await client.PostAsJsonAsync(
            "/api/governance/findings/reopen",
            new GovernanceFindingReopenRequest(memoryFinding.Id, "Explicit retry.", dispositionRequest.GovernanceRunId));
        reopenResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var reopened = await reopenResponse.Content.ReadFromJsonAsync<GovernanceFindingResult>();
        reopened!.Status.Should().Be(GovernanceFindingStatus.Open);
        reopened.GovernanceRetryCount.Should().Be(1);

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
            UseBootstrapActor(scope.ServiceProvider);
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
    public async Task Security_Endpoints_Should_Create_Tenant_User_Project_Grant_And_Token_Usage_Metadata()
    {
        using var client = environment.GetFactory().CreateClient();
        var slug = $"tenant-{Guid.NewGuid():N}"[..20];

        using var tenantResponse = await client.PostAsJsonAsync("/api/security/tenants", new TenantCreateRequest(slug, "External user tenant"));
        tenantResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var tenant = await tenantResponse.Content.ReadFromJsonAsync<TenantResult>();
        tenant.Should().NotBeNull();
        tenant!.Slug.Should().Be(slug);

        using var userResponse = await client.PostAsJsonAsync(
            $"/api/security/tenants/{tenant.Id}/users",
            new
            {
                username = "alice",
                displayName = "Alice",
                email = "alice@example.test",
                role = TenantUserRole.Owner
            });
        userResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var user = await userResponse.Content.ReadFromJsonAsync<TenantUserResult>();
        user.Should().NotBeNull();
        user!.TenantId.Should().Be(tenant.Id);

        using var grantResponse = await client.PutAsJsonAsync(
            $"/api/security/tenants/{tenant.Id}/project-grants/ContextHub",
            new
            {
                canRead = true,
                canWrite = true,
                canManageTokens = true
            });
        grantResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var grant = await grantResponse.Content.ReadFromJsonAsync<TenantProjectGrantResult>();
        grant.Should().NotBeNull();
        grant!.ProjectId.Should().Be("ContextHub");
        grant.CanManageTokens.Should().BeTrue();

        using var tokenResponse = await client.PostAsJsonAsync(
            $"/api/security/tenants/{tenant.Id}/tokens",
            new
            {
                ownerUserId = user.Id,
                name = "travel laptop",
                notes = "Used outside the intranet.",
                scopes = new[] { "memory:read", "memory:write", "token:manage" },
                allowedProjectIds = new[] { "ContextHub" }
            });
        tokenResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var createdToken = await tokenResponse.Content.ReadFromJsonAsync<ApiTokenCreatedResult>();
        createdToken.Should().NotBeNull();
        createdToken!.PlainToken.Should().StartWith("chub_");
        createdToken.Token.Notes.Should().Be("Used outside the intranet.");
        createdToken.Token.LastUsedAt.Should().BeNull();

        using var allProjectsTokenResponse = await client.PostAsJsonAsync(
            $"/api/security/tenants/{tenant.Id}/tokens",
            new
            {
                ownerUserId = user.Id,
                name = "all projects client",
                scopes = new[] { "memory:read" },
                allowedProjectIds = new[] { ProjectContext.AllProjectIdsSentinel }
            });
        allProjectsTokenResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var allProjectsToken = await allProjectsTokenResponse.Content.ReadFromJsonAsync<ApiTokenCreatedResult>();
        allProjectsToken.Should().NotBeNull();
        allProjectsToken!.Token.AllowedProjectIds.Should().BeEmpty();

        using var regeneratedTokenResponse = await client.PostAsync($"/api/security/tokens/{allProjectsToken.Token.Id}/regenerate", null);
        regeneratedTokenResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var regeneratedToken = await regeneratedTokenResponse.Content.ReadFromJsonAsync<ApiTokenCreatedResult>();
        regeneratedToken.Should().NotBeNull();
        regeneratedToken!.PlainToken.Should().StartWith("chub_");
        regeneratedToken.PlainToken.Should().NotBe(allProjectsToken.PlainToken);
        regeneratedToken.Token.TokenPrefix.Should().Be(regeneratedToken.PlainToken[..Math.Min(12, regeneratedToken.PlainToken.Length)]);
        regeneratedToken.Token.TokenLastFour.Should().Be(regeneratedToken.PlainToken[^4..]);
        regeneratedToken.Token.LastUsedAt.Should().BeNull();

        using var allProjectsUpdateResponse = await client.PatchAsJsonAsync(
            $"/api/security/tokens/{createdToken.Token.Id}",
            new
            {
                allowedProjectIds = new[] { ProjectContext.AllProjectIdsSentinel }
            });
        allProjectsUpdateResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var updatedAllProjectsToken = await allProjectsUpdateResponse.Content.ReadFromJsonAsync<ApiTokenResult>();
        updatedAllProjectsToken.Should().NotBeNull();
        updatedAllProjectsToken!.AllowedProjectIds.Should().BeEmpty();

        using var revokedTokenResponse = await client.PostAsJsonAsync(
            $"/api/security/tenants/{tenant.Id}/tokens",
            new
            {
                ownerUserId = user.Id,
                name = "revoked laptop",
                scopes = new[] { "memory:read" },
                allowedProjectIds = new[] { "ContextHub" }
            });
        revokedTokenResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var revokedToken = await revokedTokenResponse.Content.ReadFromJsonAsync<ApiTokenCreatedResult>();
        revokedToken.Should().NotBeNull();

        using (var scope = environment.GetFactory().Services.CreateScope())
        {
            UseBootstrapActor(scope.ServiceProvider);
            var securityService = scope.ServiceProvider.GetRequiredService<ITenantSecurityService>();
            var authResult = await securityService.AuthenticateTokenAsync(
                createdToken.PlainToken,
                "203.0.113.10",
                "api-contract-test",
                CancellationToken.None);
            authResult.Succeeded.Should().BeTrue();
            authResult.TenantId.Should().Be(tenant.Id);

            await securityService.RevokeTokenAsync(revokedToken!.Token.Id, CancellationToken.None);
        }

        var tokens = await client.GetFromJsonAsync<List<ApiTokenResult>>($"/api/security/tenants/{tenant.Id}/tokens");
        tokens.Should().NotBeNull();
        var tokenMetadata = tokens!.Single(x => x.Id == createdToken.Token.Id);
        tokenMetadata.LastUsedAt.Should().NotBeNull();
        tokenMetadata.LastUsedIp.Should().Be("203.0.113.10");
        tokenMetadata.LastUsedUserAgent.Should().Be("api-contract-test");

        var auditEvents = await client.GetFromJsonAsync<List<SecurityAuditEventResult>>($"/api/security/audit-events?tenantId={tenant.Id}");
        auditEvents.Should().NotBeNull();
        auditEvents!.Select(x => x.EventType).Should().Contain(SecurityAuditEventType.ApiTokenAuthenticated);

        const string bootstrapToken = "test-bootstrap-token-1234567890";
        await using var secureFactory = environment.GetFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ContextHub:Security:RequireAuthentication"] = "true",
                    ["ContextHub:Security:BootstrapToken"] = bootstrapToken,
                    ["ContextHub:Security:BootstrapTenantSlug"] = "bootstrap-team",
                    ["ContextHub:Security:BootstrapUsername"] = "dashboard-service",
                    ["ContextHub:Security:BootstrapAllowedProjectIds"] = "ContextHub"
                });
            });
        });
        using var anonymousClient = CreateAnonymousClient(secureFactory);
        using var deniedResponse = await anonymousClient.GetAsync("/api/status");
        deniedResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);

        using var invalidClient = secureFactory.CreateClient();
        invalidClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-valid-token");
        using var invalidResponse = await invalidClient.GetAsync("/api/status");
        invalidResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);

        using var bootstrapClient = secureFactory.CreateClient();
        bootstrapClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bootstrapToken);
        using var bootstrapResponse = await bootstrapClient.GetAsync("/api/status");
        bootstrapResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        using var forbiddenProjectResponse = await bootstrapClient.GetAsync("/api/memories/search?query=ContextHub&projectId=default");
        forbiddenProjectResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden);

        using var authenticatedClient = secureFactory.CreateClient();
        authenticatedClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", createdToken.PlainToken);
        using var authorizedResponse = await authenticatedClient.GetAsync("/api/status");
        authorizedResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        using var revokedClient = secureFactory.CreateClient();
        revokedClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", revokedToken!.PlainToken);
        using var revokedResponse = await revokedClient.GetAsync("/api/status");
        revokedResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);

        using (var scope = environment.GetFactory().Services.CreateScope())
        {
            UseBootstrapActor(scope.ServiceProvider);
            var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var token = await dbContext.ApiTokens.SingleAsync(x => x.Id == createdToken.Token.Id);
            token.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await dbContext.SaveChangesAsync(CancellationToken.None);
        }

        using var expiredClient = secureFactory.CreateClient();
        expiredClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", createdToken.PlainToken);
        using var expiredResponse = await expiredClient.GetAsync("/api/status");
        expiredResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
    }

    [DockerRequiredFact]
    public async Task Security_Invalid_Bearer_Token_Should_Return_Unauthorized()
    {
        const string bootstrapToken = "test-bootstrap-token-invalid-regression";
        await using var secureFactory = environment.GetFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ContextHub:Security:RequireAuthentication"] = "true",
                    ["ContextHub:Security:BootstrapToken"] = bootstrapToken,
                    ["ContextHub:Security:BootstrapTenantSlug"] = "bootstrap-team",
                    ["ContextHub:Security:BootstrapUsername"] = "dashboard-service",
                    ["ContextHub:Security:BootstrapAllowedProjectIds"] = "ContextHub"
                });
            });
        });

        using var invalidClient = secureFactory.CreateClient();
        invalidClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-valid-token");
        using var response = await invalidClient.GetAsync("/api/status");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
    }

    [DockerRequiredFact]
    public async Task Security_Token_Only_Surface_Should_Allow_Health_And_Reject_Anonymous_Api_Requests()
    {
        using var anonymousClient = CreateAnonymousClient(environment.GetFactory());

        using var liveResponse = await anonymousClient.GetAsync("/health/live");
        using var readyResponse = await anonymousClient.GetAsync("/health/ready");
        using var statusResponse = await anonymousClient.GetAsync("/api/status");
        using var searchResponse = await anonymousClient.GetAsync("/api/memories/search?query=token");
        using var summaryRefreshResponse = await anonymousClient.PostAsJsonAsync("/api/jobs/summary-refresh", new EnqueueSummaryRefreshRequest(
            ProjectId: null,
            IncludedProjectIds: null));
        using var conversationResponse = await anonymousClient.PostAsJsonAsync("/api/conversations/ingest", new ConversationIngestRequest(
            ConversationId: $"anonymous-{Guid.NewGuid():N}",
            TurnId: "turn-1",
            EventType: ConversationEventType.SessionCheckpoint,
            SourceKind: ConversationSourceKind.HostEvent,
            SourceSystem: "codex",
            SourceRef: "api-tests"));
        using var mcpResponse = await anonymousClient.PostAsync(
            "/mcp",
            new StringContent("""{"jsonrpc":"2.0","id":"anonymous","method":"tools/list"}""", Encoding.UTF8, "application/json"));

        liveResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        readyResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        statusResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
        searchResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
        summaryRefreshResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
        conversationResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
        mcpResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
    }

    [DockerRequiredFact]
    public async Task Security_Disabled_RequireAuthentication_Should_Not_Enable_Anonymous_Access()
    {
        await using var legacyConfiguredFactory = environment.GetFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ContextHub:Security:RequireAuthentication"] = "false",
                    ["ContextHub:Security:BootstrapToken"] = MemoryApplicationFactory.TestBootstrapToken,
                    ["ContextHub:Security:BootstrapTenantSlug"] = "contract-tests",
                    ["ContextHub:Security:BootstrapUsername"] = "contract-test-admin",
                    ["ContextHub:Security:BootstrapAllowedProjectIds"] = ProjectContext.AllProjectIdsSentinel
                });
            });
        });

        using var anonymousClient = CreateAnonymousClient(legacyConfiguredFactory);
        using var anonymousResponse = await anonymousClient.GetAsync("/api/status");
        anonymousResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);

        using var authenticatedClient = legacyConfiguredFactory.CreateClient();
        authenticatedClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", MemoryApplicationFactory.TestBootstrapToken);
        using var authenticatedResponse = await authenticatedClient.GetAsync("/api/status");
        authenticatedResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }

    [DockerRequiredFact]
    public async Task Security_Service_Writes_Should_Reject_Unrestricted_Actor()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var memoryService = scope.ServiceProvider.GetRequiredService<IMemoryService>();

        var act = async () => await memoryService.UpsertAsync(
            new MemoryUpsertRequest(
                ExternalKey: $"unrestricted-write:{Guid.NewGuid():N}",
                Scope: MemoryScope.Project,
                MemoryType: MemoryType.Fact,
                Title: "Unrestricted write should fail",
                Content: "Service writes must not create ownerless rows.",
                Summary: "Unrestricted write should fail",
                SourceType: "api-contract",
                SourceRef: "security",
                Tags: ["security"],
                Importance: 0.5m,
                Confidence: 0.9m,
                ProjectId: "ContextHub"),
            CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("Authentication is required.");
    }

    [DockerRequiredFact]
    public async Task Security_Authenticated_Service_Write_Should_Set_Owner()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(scope.ServiceProvider);
        var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var memoryService = scope.ServiceProvider.GetRequiredService<IMemoryService>();
        var actor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>().Current;
        var externalKey = $"authenticated-owner:{Guid.NewGuid():N}";

        var created = await memoryService.UpsertAsync(
            new MemoryUpsertRequest(
                ExternalKey: externalKey,
                Scope: MemoryScope.Project,
                MemoryType: MemoryType.Fact,
                Title: "Authenticated write should set owner",
                Content: "Authenticated writes must persist the token owner.",
                Summary: "Authenticated write should set owner",
                SourceType: "api-contract",
                SourceRef: "security",
                Tags: ["security"],
                Importance: 0.7m,
                Confidence: 0.9m,
                ProjectId: "ContextHub"),
            CancellationToken.None);

        var row = await dbContext.MemoryItems.SingleAsync(x => x.Id == created.Id);
        row.TenantId.Should().Be(actor.TenantId);
        row.OwnerUserId.Should().Be(actor.UserId);
    }

    [DockerRequiredFact]
    public async Task Dashboard_Memory_Details_Should_Respect_Actor_Owner_Filter()
    {
        var tenantId = Guid.Parse("91000000-0000-0000-0000-000000000001");
        var ownerUserId = Guid.Parse("91000000-0000-0000-0000-000000000002");
        var otherUserId = Guid.Parse("91000000-0000-0000-0000-000000000003");
        var projectId = $"OwnerFilter_{Guid.NewGuid():N}";
        var ownedMemoryId = Guid.NewGuid();
        var otherMemoryId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        using var scope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(scope.ServiceProvider);
        var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var actorAccessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        var dashboardService = scope.ServiceProvider.GetRequiredService<IDashboardQueryService>();

        dbContext.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Slug = $"owner-filter-{Guid.NewGuid():N}"[..24],
            DisplayName = "Owner Filter Tenant",
            CreatedAt = now,
            UpdatedAt = now
        });
        dbContext.TenantUsers.AddRange(
            new TenantUser
            {
                Id = ownerUserId,
                TenantId = tenantId,
                Username = "owner-filter-owner",
                DisplayName = "Owner Filter Owner",
                Role = TenantUserRole.Owner,
                CreatedAt = now,
                UpdatedAt = now
            },
            new TenantUser
            {
                Id = otherUserId,
                TenantId = tenantId,
                Username = "owner-filter-other",
                DisplayName = "Owner Filter Other",
                Role = TenantUserRole.Member,
                CreatedAt = now,
                UpdatedAt = now
            });
        await dbContext.SaveChangesAsync(CancellationToken.None);

        dbContext.MemoryItems.AddRange(
            new MemoryItem
            {
                Id = ownedMemoryId,
                TenantId = tenantId,
                OwnerUserId = ownerUserId,
                ProjectId = projectId,
                ExternalKey = $"owner-filter:owned:{ownedMemoryId:N}",
                Scope = MemoryScope.Project,
                MemoryType = MemoryType.Fact,
                Title = "Owner visible dashboard memory",
                Content = "This memory belongs to the current actor.",
                Summary = "Owner visible memory",
                SourceType = "test",
                SourceRef = "api-contract",
                Importance = 0.7m,
                Confidence = 0.9m,
                CreatedAt = now,
                UpdatedAt = now
            },
            new MemoryItem
            {
                Id = otherMemoryId,
                TenantId = tenantId,
                OwnerUserId = otherUserId,
                ProjectId = projectId,
                ExternalKey = $"owner-filter:other:{otherMemoryId:N}",
                Scope = MemoryScope.Project,
                MemoryType = MemoryType.Fact,
                Title = "Other owner dashboard memory",
                Content = "This memory must not be visible to the current actor.",
                Summary = "Other owner memory",
                SourceType = "test",
                SourceRef = "api-contract",
                Importance = 0.7m,
                Confidence = 0.9m,
                CreatedAt = now,
                UpdatedAt = now
            });
        await dbContext.SaveChangesAsync(CancellationToken.None);

        actorAccessor.Current = new ContextHubRequestActor(
            tenantId,
            ownerUserId,
            "owner",
            TenantUserRole.Owner,
            [SecurityScopes.MemoryRead],
            [],
            true);

        var list = await dashboardService.GetMemoriesAsync(
            new MemoryListRequest(ProjectId: projectId, PageSize: 10),
            CancellationToken.None);
        var ownedDetails = await dashboardService.GetMemoryDetailsAsync(ownedMemoryId, CancellationToken.None);
        var otherDetails = await dashboardService.GetMemoryDetailsAsync(otherMemoryId, CancellationToken.None);

        list.Items.Should().ContainSingle(x => x.Id == ownedMemoryId);
        list.Items.Should().NotContain(x => x.Id == otherMemoryId);
        ownedDetails.Should().NotBeNull();
        ownedDetails!.Document.Id.Should().Be(ownedMemoryId);
        otherDetails.Should().BeNull();
    }

    [DockerRequiredFact]
    public async Task Memory_Get_Should_Allow_Service_Actor_Project_Read_Interop()
    {
        var tenantId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var serviceUserId = Guid.NewGuid();
        var projectId = $"ServiceInterop_{Guid.NewGuid():N}";
        var deniedProjectId = $"ServiceInteropDenied_{Guid.NewGuid():N}";
        var memoryId = Guid.NewGuid();
        var deniedMemoryId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        using var scope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(scope.ServiceProvider);
        var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var actorAccessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        var memoryService = scope.ServiceProvider.GetRequiredService<IMemoryService>();

        dbContext.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Slug = $"svc-interop-{Guid.NewGuid():N}"[..24],
            DisplayName = "Service Interop Tenant",
            CreatedAt = now,
            UpdatedAt = now
        });
        dbContext.TenantUsers.AddRange(
            new TenantUser
            {
                Id = ownerUserId,
                TenantId = tenantId,
                Username = "service-interop-owner",
                DisplayName = "Service Interop Owner",
                Role = TenantUserRole.Member,
                CreatedAt = now,
                UpdatedAt = now
            },
            new TenantUser
            {
                Id = serviceUserId,
                TenantId = tenantId,
                Username = "service-interop-gateway",
                DisplayName = "Service Interop Gateway",
                Role = TenantUserRole.Member,
                CreatedAt = now,
                UpdatedAt = now
            });
        await dbContext.SaveChangesAsync(CancellationToken.None);

        dbContext.MemoryItems.AddRange(
            new MemoryItem
            {
                Id = memoryId,
                TenantId = tenantId,
                OwnerUserId = ownerUserId,
                ProjectId = projectId,
                ExternalKey = $"service-interop:allowed:{memoryId:N}",
                Scope = MemoryScope.Project,
                MemoryType = MemoryType.Fact,
                Title = "Service actor visible project memory",
                Content = "Project-gated service actors should read approved interop knowledge.",
                Summary = "Service actor visible project memory",
                SourceType = "test",
                SourceRef = "api-contract",
                Importance = 0.7m,
                Confidence = 0.9m,
                CreatedAt = now,
                UpdatedAt = now
            },
            new MemoryItem
            {
                Id = deniedMemoryId,
                TenantId = tenantId,
                OwnerUserId = ownerUserId,
                ProjectId = deniedProjectId,
                ExternalKey = $"service-interop:denied:{deniedMemoryId:N}",
                Scope = MemoryScope.Project,
                MemoryType = MemoryType.Fact,
                Title = "Service actor denied project memory",
                Content = "Project allowlist must still constrain service actor reads.",
                Summary = "Service actor denied project memory",
                SourceType = "test",
                SourceRef = "api-contract",
                Importance = 0.7m,
                Confidence = 0.9m,
                CreatedAt = now,
                UpdatedAt = now
            });
        await dbContext.SaveChangesAsync(CancellationToken.None);

        actorAccessor.Current = new ContextHubRequestActor(
            tenantId,
            serviceUserId,
            "service-interop-gateway",
            TenantUserRole.Member,
            [SecurityScopes.MemoryRead],
            [projectId],
            IsAuthenticated: true,
            IsServiceActor: true);

        var allowed = await memoryService.GetAsync(memoryId, CancellationToken.None);
        allowed.Should().NotBeNull();
        allowed!.Id.Should().Be(memoryId);

        var denied = async () => await memoryService.GetAsync(deniedMemoryId, CancellationToken.None);
        await denied.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [DockerRequiredFact]
    public async Task Domain_Owner_Repair_Should_Preview_And_Apply_Admin_Owner_Migration()
    {
        var adminTenantId = Guid.Parse("72000000-0000-0000-0000-000000000001");
        var adminUserId = Guid.Parse("73000000-0000-0000-0000-000000000001");
        var dashboardServiceUserId = Guid.Parse("209b1f29-a13c-494d-abec-723609e45a64");
        var legacyUserId = Guid.NewGuid();
        var projectId = $"OwnerRepair_{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var memoryId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var serviceSessionId = Guid.NewGuid();
        var checkpointId = Guid.NewGuid();
        var insightId = Guid.NewGuid();
        var retrievalEventId = Guid.NewGuid();

        using (var scope = environment.GetFactory().Services.CreateScope())
        {
            UseBootstrapActor(scope.ServiceProvider);
            var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            await EnsureAdminOwnerAsync(dbContext, adminTenantId, adminUserId, now);
            if (!await dbContext.TenantUsers.AnyAsync(x => x.Id == dashboardServiceUserId))
            {
                dbContext.TenantUsers.Add(new TenantUser
                {
                    Id = dashboardServiceUserId,
                    TenantId = adminTenantId,
                    Username = $"dashboard-service-{Guid.NewGuid():N}"[..32],
                    DisplayName = "Dashboard Service",
                    Role = TenantUserRole.Member,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }

            dbContext.TenantUsers.Add(new TenantUser
            {
                Id = legacyUserId,
                TenantId = adminTenantId,
                Username = $"owner-repair-legacy-{Guid.NewGuid():N}"[..32],
                DisplayName = "Owner Repair Legacy",
                Role = TenantUserRole.Member,
                CreatedAt = now,
                UpdatedAt = now
            });
            await dbContext.SaveChangesAsync(CancellationToken.None);

            dbContext.MemoryItems.Add(new MemoryItem
            {
                Id = memoryId,
                TenantId = null,
                OwnerUserId = legacyUserId,
                ProjectId = projectId,
                ExternalKey = $"owner-repair:{memoryId:N}",
                Scope = MemoryScope.Project,
                MemoryType = MemoryType.Fact,
                Title = "Legacy owner memory",
                Content = "This legacy row should be moved back to admin.",
                Summary = "Legacy owner memory",
                SourceType = "test",
                SourceRef = "api-contract",
                Importance = 0.7m,
                Confidence = 0.9m,
                CreatedAt = now,
                UpdatedAt = now
            });
            dbContext.MemoryJobs.Add(new MemoryJob
            {
                Id = jobId,
                TenantId = null,
                OwnerUserId = legacyUserId,
                ProjectId = projectId,
                JobType = MemoryJobType.Reindex,
                Status = MemoryJobStatus.Pending,
                CreatedAt = now
            });
            dbContext.ConversationSessions.Add(new ConversationSession
            {
                Id = sessionId,
                TenantId = null,
                OwnerUserId = legacyUserId,
                ConversationId = $"owner-repair-{Guid.NewGuid():N}",
                ProjectId = projectId,
                SourceSystem = "api-contract",
                StartedAt = now,
                LastCheckpointAt = now,
                UpdatedAt = now
            });
            dbContext.ConversationSessions.Add(new ConversationSession
            {
                Id = serviceSessionId,
                TenantId = adminTenantId,
                OwnerUserId = dashboardServiceUserId,
                ConversationId = $"owner-repair-service-{Guid.NewGuid():N}",
                ProjectId = projectId,
                SourceSystem = "api-contract",
                StartedAt = now,
                LastCheckpointAt = now,
                UpdatedAt = now
            });
            dbContext.ConversationCheckpoints.Add(new ConversationCheckpoint
            {
                Id = checkpointId,
                SessionId = sessionId,
                TenantId = null,
                OwnerUserId = legacyUserId,
                ConversationId = $"owner-repair-{Guid.NewGuid():N}",
                TurnId = "turn-1",
                ProjectId = projectId,
                SourceSystem = "api-contract",
                EventType = ConversationEventType.TurnCompleted,
                SourceKind = ConversationSourceKind.AgentSupplemental,
                DedupKey = $"owner-repair-checkpoint-{checkpointId:N}",
                CreatedAt = now
            });
            dbContext.ConversationInsights.Add(new ConversationInsight
            {
                Id = insightId,
                SessionId = sessionId,
                CheckpointId = checkpointId,
                TenantId = null,
                OwnerUserId = legacyUserId,
                ConversationId = $"owner-repair-{Guid.NewGuid():N}",
                TurnId = "turn-1",
                ProjectId = projectId,
                SourceSystem = "api-contract",
                SourceKind = ConversationSourceKind.AgentSupplemental,
                InsightType = ConversationInsightType.Fact,
                Title = "Legacy owner insight",
                Content = "This legacy insight should be moved back to admin.",
                Summary = "Legacy owner insight",
                DedupKey = $"owner-repair-insight-{insightId:N}",
                CreatedAt = now,
                UpdatedAt = now
            });
            var retrievalEvent = CreateRetentionEvent(retrievalEventId, now, $"owner-repair-{Guid.NewGuid():N}");
            retrievalEvent.TenantId = null;
            retrievalEvent.OwnerUserId = legacyUserId;
            retrievalEvent.ProjectId = projectId;
            dbContext.RetrievalEvents.Add(retrievalEvent);
            await dbContext.SaveChangesAsync(CancellationToken.None);
        }

        using var client = environment.GetFactory().CreateClient();
        using var previewResponse = await client.PostAsJsonAsync(
            "/api/maintenance/domain-owner-repair/preview",
            new DomainOwnerRepairRequest());
        previewResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var preview = await previewResponse.Content.ReadFromJsonAsync<DomainOwnerRepairResult>();
        preview.Should().NotBeNull();
        preview!.Applied.Should().BeFalse();
        preview.AffectedProjectIds.Should().Contain(projectId);
        preview.Conflicts.Should().BeEmpty();

        using var runResponse = await client.PostAsJsonAsync(
            "/api/maintenance/domain-owner-repair/run",
            new DomainOwnerRepairRequest(
                IncludeRetrievalEvents: true,
                RetrievalEventBatchSize: 1,
                MaxRetrievalEventBatches: 10,
                TriggeredBy: "api-contract-test"));
        runResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var result = await runResponse.Content.ReadFromJsonAsync<DomainOwnerRepairResult>();
        result.Should().NotBeNull();
        result!.Applied.Should().BeTrue();
        result.RunId.Should().NotBeNull();
        result.TableResults.Should().Contain(x => x.TableName == "memory_items" && x.UpdatedRows >= 1);
        result.TableResults.Should().Contain(x => x.TableName == "retrieval_events" && x.UpdatedRows >= 1);

        using var verifyScope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(verifyScope.ServiceProvider);
        var verifyDbContext = verifyScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await verifyDbContext.MemoryItems.SingleAsync(x => x.Id == memoryId)).Should().Match<MemoryItem>(x => x.TenantId == adminTenantId && x.OwnerUserId == adminUserId);
        (await verifyDbContext.MemoryJobs.SingleAsync(x => x.Id == jobId)).Should().Match<MemoryJob>(x => x.TenantId == adminTenantId && x.OwnerUserId == adminUserId);
        (await verifyDbContext.ConversationSessions.SingleAsync(x => x.Id == sessionId)).Should().Match<ConversationSession>(x => x.TenantId == adminTenantId && x.OwnerUserId == adminUserId);
        (await verifyDbContext.ConversationSessions.SingleAsync(x => x.Id == serviceSessionId)).Should().Match<ConversationSession>(x => x.TenantId == adminTenantId && x.OwnerUserId == dashboardServiceUserId);
        (await verifyDbContext.ConversationCheckpoints.SingleAsync(x => x.Id == checkpointId)).Should().Match<ConversationCheckpoint>(x => x.TenantId == adminTenantId && x.OwnerUserId == adminUserId);
        (await verifyDbContext.ConversationInsights.SingleAsync(x => x.Id == insightId)).Should().Match<ConversationInsight>(x => x.TenantId == adminTenantId && x.OwnerUserId == adminUserId);
        (await verifyDbContext.RetrievalEvents.SingleAsync(x => x.Id == retrievalEventId)).Should().Match<RetrievalEvent>(x => x.TenantId == adminTenantId && x.OwnerUserId == adminUserId);

        var run = await verifyDbContext.MaintenanceRuns.SingleAsync(x => x.Id == result.RunId);
        run.MaintenanceType.Should().Be(MaintenanceRunType.DomainOwnerRepair);
        run.Status.Should().Be(MaintenanceRunStatus.Completed);
    }

    [DockerRequiredFact]
    public async Task Domain_Owner_Repair_Run_Should_Stop_When_Memory_External_Key_Would_Conflict()
    {
        var adminTenantId = Guid.Parse("72000000-0000-0000-0000-000000000001");
        var adminUserId = Guid.Parse("73000000-0000-0000-0000-000000000001");
        var legacyUserId = Guid.NewGuid();
        var projectId = $"OwnerRepairConflict_{Guid.NewGuid():N}";
        var externalKey = $"owner-repair-conflict:{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var adminMemoryId = Guid.NewGuid();
        var legacyMemoryId = Guid.NewGuid();

        using var scope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(scope.ServiceProvider);
        var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        await EnsureAdminOwnerAsync(dbContext, adminTenantId, adminUserId, now);
        dbContext.TenantUsers.Add(new TenantUser
        {
            Id = legacyUserId,
            TenantId = adminTenantId,
            Username = $"owner-repair-conflict-{Guid.NewGuid():N}"[..32],
            DisplayName = "Owner Repair Conflict",
            Role = TenantUserRole.Member,
            CreatedAt = now,
            UpdatedAt = now
        });
        await dbContext.SaveChangesAsync(CancellationToken.None);

        dbContext.MemoryItems.AddRange(
            CreateOwnerRepairMemory(adminMemoryId, adminTenantId, adminUserId, projectId, externalKey, "Admin conflict memory", now),
            CreateOwnerRepairMemory(legacyMemoryId, adminTenantId, legacyUserId, projectId, externalKey, "Legacy conflict memory", now));
        await dbContext.SaveChangesAsync(CancellationToken.None);

        try
        {
            using var client = environment.GetFactory().CreateClient();
            using var response = await client.PostAsJsonAsync(
                "/api/maintenance/domain-owner-repair/run",
                new DomainOwnerRepairRequest(TriggeredBy: "api-contract-test"));
            response.StatusCode.Should().Be(System.Net.HttpStatusCode.Conflict);
            var result = await response.Content.ReadFromJsonAsync<DomainOwnerRepairResult>();
            result.Should().NotBeNull();
            result!.Applied.Should().BeFalse();
            result.Conflicts.Should().Contain(x => x.ProjectId == projectId && x.ExternalKey == externalKey);
        }
        finally
        {
            dbContext.MemoryItems.RemoveRange(dbContext.MemoryItems.Where(x => x.Id == adminMemoryId || x.Id == legacyMemoryId));
            await dbContext.SaveChangesAsync(CancellationToken.None);
        }
    }

    [DockerRequiredFact]
    public async Task Dashboard_Endpoints_Should_Return_Overview_Runtime_And_Storage_Payloads()
    {
        Guid memoryId;
        using (var scope = environment.GetFactory().Services.CreateScope())
        {
            UseBootstrapActor(scope.ServiceProvider);
            var memoryService = scope.ServiceProvider.GetRequiredService<IMemoryService>();
            var processor = scope.ServiceProvider.GetRequiredService<IBackgroundJobProcessor>();
            var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

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

            var retrievalEvent = new RetrievalEvent
            {
                Id = Guid.Parse("92000000-0000-0000-0000-000000000001"),
                ProjectId = ProjectContext.DefaultProjectId,
                Channel = "dashboard",
                EntryPoint = "storage-test",
                Purpose = "storage explorer",
                QueryText = new string('q', 5000),
                QueryHash = "storage-test-query",
                QueryMode = MemoryQueryMode.CurrentOnly.ToString(),
                IncludedProjectIds = [ProjectContext.DefaultProjectId],
                Limit = 5,
                ResultCount = 1,
                DurationMs = 12,
                Success = true,
                TraceId = "trace-storage-retrieval",
                RequestId = "request-storage-retrieval",
                MetadataJson = """{"payload":"storage explorer should truncate large telemetry cells"}""",
                CreatedAt = DateTimeOffset.UtcNow
            };
            dbContext.RetrievalEvents.Add(retrievalEvent);
            dbContext.RetrievalHits.Add(new RetrievalHit
            {
                RetrievalEventId = retrievalEvent.Id,
                Rank = 1,
                MemoryId = memoryId,
                Title = "Dashboard API fixture",
                MemoryType = MemoryType.Artifact.ToString(),
                SourceType = "document",
                SourceRef = "tests",
                Score = 0.95m,
                Excerpt = new string('h', 5000),
                ProjectId = ProjectContext.DefaultProjectId
            });
            await dbContext.SaveChangesAsync(CancellationToken.None);
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
        var retrievalRows = await client.GetFromJsonAsync<StorageTableRowsResult>("/api/storage/retrieval_events?query=trace-storage-retrieval&column=trace_id&page=1&pageSize=5");
        var retrievalHitRows = await client.GetFromJsonAsync<StorageTableRowsResult>("/api/storage/retrieval_hits?query=Dashboard%20API%20fixture&column=title&page=1&pageSize=5");

        overview.Should().NotBeNull();
        overview!.BuildVersion.Should().NotBeNullOrWhiteSpace();
        overview!.Metrics.Should().Contain(x => x.Key == "memoryItems");
        overview.Metrics.Single(x => x.Key == "memoryItems").CountingScope.Should().Be("InstanceInventory");
        overview.Metrics.Single(x => x.Key == "memoryItems").Label.Should().Be("全 Instance 記憶資料列");
        overview.Metrics.Should().Contain(x => x.Key == "defaultProjectMemoryItems");
        overview.MemoryInventory.Should().NotBeNull();
        overview.MemoryInventory!.MetricKey.Should().Be("memoryItemRows");
        overview.MemoryInventory.CountingScope.Should().Be("InstanceInventory");
        overview.MemoryInventory.ProjectPartitionInvariantSatisfied.Should().BeTrue();
        overview.MemoryInventory.OwnershipInvariantSatisfied.Should().BeTrue();
        overview.MemoryInventory.ScopeCounts.Values.Sum().Should().Be(overview.MemoryInventory.TotalMemoryItemRows);
        overview.MemoryInventory.MemoryTypeCounts.Values.Sum().Should().Be(overview.MemoryInventory.TotalMemoryItemRows);
        overview.MemoryInventory.StatusCounts.Values.Sum().Should().Be(overview.MemoryInventory.TotalMemoryItemRows);
        runtime.Should().NotBeNull();
        runtime!.BuildVersion.Should().NotBeNullOrWhiteSpace();
        runtime!.EmbeddingProfile.Should().Be("compact");
        monitoring.Should().NotBeNull();
        monitoring!.BuildVersion.Should().NotBeNullOrWhiteSpace();
        monitoring.Redis.Should().NotBeNull();
        monitoring.Postgres.Should().NotBeNull();
        monitoring.DependencyResources.Should().NotBeNull();
        monitoring.EmbeddingUsage.Should().NotBeNull();
        monitoring.EmbeddingUsage!.Select(x => x.Key).Should().Contain(["24h", "3d", "7d"]);
        monitoring.EmbeddingUsage.Should().OnlyContain(x => x.TruncationRatePercent >= 0);
        monitoring.ContextSavings.Should().NotBeNull();
        monitoring.ContextSavings!.WindowLabel.Should().NotBeNullOrWhiteSpace();
        monitoring.ContextSavings.Windows.Should().NotBeNull();
        monitoring.ContextSavings.Windows!.Select(x => x.Key).Should().Contain(["24h", "3d", "7d", "30d"]);
        monitoring.ContextSavings.TokenCountingMode.Should().NotBeNullOrWhiteSpace();
        monitoring.SnapshotStatus!.Sections.Should().Contain(x => x.Key == DashboardSnapshotKeys.ContextSavings);
        memories.Should().NotBeNull();
        memories!.Items.Should().Contain(x => x.Id == memoryId);
        details.Should().NotBeNull();
        details!.Document.Id.Should().Be(memoryId);
        details.Chunks.Should().NotBeEmpty();
        jobs.Should().NotBeNull();
        jobs!.Items.Should().NotBeEmpty();
        tables.Should().NotBeNull();
        tables!.Should().Contain(x => x.Name == "memory_items");
        tables.Should().Contain(x => x.Name == "maintenance_runs");
        tables.Should().Contain(x => x.Name == "retrieval_telemetry_daily_summaries");
        tables.Should().Contain(x => x.Name == "retrieval_telemetry_daily_hit_summaries");
        tables.Should().Contain(x => x.Name == "mcp_tool_call_events");
        rows.Should().NotBeNull();
        rows!.Table.Should().Be("memory_items");
        rows.Description.Should().NotBeNullOrWhiteSpace();
        rows.SearchableColumns.Should().Contain("title");
        rows.AppliedQuery.Should().Be("Dashboard");
        rows.Rows.Items.Should().NotBeEmpty();
        rows.Rows.Items.Should().Contain(x => x.Values["title"] == "Dashboard API fixture");
        retrievalRows.Should().NotBeNull();
        retrievalRows!.Columns.Should().Contain("tenant_id");
        retrievalRows.Columns.Should().Contain("owner_user_id");
        retrievalRows.Columns.Should().Contain("query_text");
        retrievalRows.Rows.Items.Should().Contain(x =>
            x.Values["trace_id"] == "trace-storage-retrieval" &&
            x.Values["query_text"]!.Length <= 4099 &&
            x.Values["query_text"]!.EndsWith("...", StringComparison.Ordinal));
        retrievalHitRows.Should().NotBeNull();
        retrievalHitRows!.Rows.Items.Should().Contain(x =>
            x.Values["title"] == "Dashboard API fixture" &&
            x.Values["excerpt"]!.Length <= 4099 &&
            x.Values["excerpt"]!.EndsWith("...", StringComparison.Ordinal));
    }

    [DockerRequiredFact]
    public async Task Maintenance_Endpoints_Should_Expose_Mode_And_Block_Mcp_When_Active()
    {
        using var client = environment.GetFactory().CreateClient();
        await client.DeleteAsync("/api/maintenance/mode");

        try
        {
            var inactive = await client.GetFromJsonAsync<MaintenanceModeStateResult>("/api/maintenance/status");
            inactive.Should().NotBeNull();
            inactive!.Active.Should().BeFalse();

            var enabledResponse = await client.PostAsJsonAsync(
                "/api/maintenance/mode",
                new MaintenanceModeRequest(
                    Reason: "VacuumFullReclaim",
                    Message: "Telemetry storage maintenance is running.",
                    EstimatedDurationMinutes: 90,
                    TriggeredBy: "api-contract-test"));
            enabledResponse.EnsureSuccessStatusCode();
            var enabled = await enabledResponse.Content.ReadFromJsonAsync<MaintenanceModeStateResult>();
            enabled.Should().NotBeNull();
            enabled!.Active.Should().BeTrue();
            enabled.RunId.Should().NotBeNull();

            using var statusResponse = await client.GetAsync("/api/status");
            statusResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

            using var mcpResponse = await client.PostAsync(
                "/mcp",
                new StringContent("""{"jsonrpc":"2.0","id":"blocked","method":"tools/list"}""", Encoding.UTF8, "application/json"));
            mcpResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.ServiceUnavailable);
            mcpResponse.Headers.TryGetValues("X-ContextHub-Maintenance", out var maintenanceHeaders).Should().BeTrue();
            maintenanceHeaders.Should().Contain("running");
            mcpResponse.Headers.RetryAfter.Should().NotBeNull();

            var vacuumResponse = await client.PostAsJsonAsync(
                "/api/maintenance/vacuum-full-reclaim/run",
                new VacuumFullReclaimRunRequest("api-contract-test"));
            vacuumResponse.EnsureSuccessStatusCode();
            var vacuum = await vacuumResponse.Content.ReadFromJsonAsync<VacuumFullReclaimRunResult>();
            vacuum.Should().NotBeNull();
            vacuum!.ResultJson.Should().Contain("vacuumFullCompleted");

            var disabledResponse = await client.DeleteAsync("/api/maintenance/mode");
            disabledResponse.EnsureSuccessStatusCode();
            var disabled = await disabledResponse.Content.ReadFromJsonAsync<MaintenanceModeStateResult>();
            disabled.Should().NotBeNull();
            disabled!.Active.Should().BeFalse();

            using var restoredMcpResponse = await client.PostAsync(
                "/mcp",
                new StringContent("""{"jsonrpc":"2.0","id":"after","method":"tools/list"}""", Encoding.UTF8, "application/json"));
            restoredMcpResponse.StatusCode.Should().NotBe(System.Net.HttpStatusCode.ServiceUnavailable);

            var runs = await client.GetFromJsonAsync<List<MaintenanceRunResult>>("/api/maintenance/runs?limit=10");
            runs.Should().NotBeNull();
            runs!.Should().Contain(x =>
                x.Id == enabled.RunId &&
                x.MaintenanceType == MaintenanceRunType.MaintenanceMode &&
                x.Status == MaintenanceRunStatus.Completed);
            runs.Should().Contain(x =>
                x.Id == vacuum.RunId &&
                x.MaintenanceType == MaintenanceRunType.VacuumFullReclaim &&
                x.Status == MaintenanceRunStatus.Completed);
        }
        finally
        {
            await client.DeleteAsync("/api/maintenance/mode");
        }
    }

    [DockerRequiredFact]
    public async Task Maintenance_Windows_Should_Drain_Leases_And_Block_New_Writes()
    {
        using var client = environment.GetFactory().CreateClient();
        await client.DeleteAsync("/api/maintenance/mode");
        await CompleteActiveMaintenanceLeasesAsync(client);

        try
        {
            var leaseResponse = await client.PostAsJsonAsync(
                "/api/maintenance/leases/heartbeat",
                new MaintenanceLeaseHeartbeatRequest(
                    AgentId: "api-contract-agent",
                    ProjectId: ProjectContext.DefaultProjectId,
                    ActivityKind: "contract-test",
                    TtlSeconds: 300));
            leaseResponse.EnsureSuccessStatusCode();
            var lease = await leaseResponse.Content.ReadFromJsonAsync<MaintenanceLeaseHeartbeatResult>();
            lease.Should().NotBeNull();
            lease!.Lease.BlocksMaintenance.Should().BeTrue();

            var scheduledResponse = await client.PostAsJsonAsync(
                "/api/maintenance/windows",
                new MaintenanceWindowRequest(
                    Reason: "KnowledgeBaseUpdate",
                    Message: "Waiting for active agents before maintenance.",
                    MaxDrainWaitMinutes: 15,
                    TriggeredBy: "api-contract-test"));
            scheduledResponse.EnsureSuccessStatusCode();
            var scheduled = await scheduledResponse.Content.ReadFromJsonAsync<MaintenanceStatusResult>();
            scheduled.Should().NotBeNull();
            scheduled!.Phase.Should().Be(MaintenancePhase.Scheduled);
            scheduled.RunId.Should().NotBeNull();

            var drainResponse = await client.PostAsync($"/api/maintenance/windows/{scheduled.RunId:D}/drain", null);
            drainResponse.EnsureSuccessStatusCode();
            var draining = await drainResponse.Content.ReadFromJsonAsync<MaintenanceStatusResult>();
            draining.Should().NotBeNull();
            draining!.Phase.Should().Be(MaintenancePhase.Draining);
            draining.ActiveLeaseCount.Should().BeGreaterThanOrEqualTo(1);
            draining.ActiveLeases.Should().Contain(x => x.LeaseId == lease.Lease.LeaseId);

            using var blockedWriteResponse = await client.PostAsJsonAsync(
                "/api/jobs/reindex",
                new EnqueueReindexRequest(ProjectId: ProjectContext.DefaultProjectId));
            blockedWriteResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.ServiceUnavailable);
            blockedWriteResponse.Headers.TryGetValues("X-ContextHub-Maintenance-Phase", out var phaseHeaders).Should().BeTrue();
            phaseHeaders.Should().Contain(nameof(MaintenancePhase.Draining));

            var prematureStartResponse = await client.PostAsync($"/api/maintenance/windows/{scheduled.RunId:D}/start", null);
            prematureStartResponse.EnsureSuccessStatusCode();
            var stillDraining = await prematureStartResponse.Content.ReadFromJsonAsync<MaintenanceStatusResult>();
            stillDraining.Should().NotBeNull();
            stillDraining!.Phase.Should().Be(MaintenancePhase.Draining);

            await CompleteActiveMaintenanceLeasesAsync(client);

            var startResponse = await client.PostAsync($"/api/maintenance/windows/{scheduled.RunId:D}/start", null);
            startResponse.EnsureSuccessStatusCode();
            var running = await startResponse.Content.ReadFromJsonAsync<MaintenanceStatusResult>();
            running.Should().NotBeNull();
            running!.Phase.Should().Be(MaintenancePhase.Running);
            running.Active.Should().BeTrue();

            var completeResponse = await client.PostAsync($"/api/maintenance/windows/{scheduled.RunId:D}/complete", null);
            completeResponse.EnsureSuccessStatusCode();
            var completed = await completeResponse.Content.ReadFromJsonAsync<MaintenanceStatusResult>();
            completed.Should().NotBeNull();
            completed!.Phase.Should().Be(MaintenancePhase.Completed);
            completed.Active.Should().BeFalse();
        }
        finally
        {
            await CompleteActiveMaintenanceLeasesAsync(client);
            await client.DeleteAsync("/api/maintenance/mode");
        }
    }

    [DockerRequiredFact]
    public async Task Retrieval_Telemetry_Retention_Should_Delete_Raw_Rows_And_Write_Daily_Summaries()
    {
        var oldEventId = Guid.Parse("93000000-0000-0000-0000-000000000001");
        var middleEventId = Guid.Parse("93000000-0000-0000-0000-000000000002");
        var recentEventId = Guid.Parse("93000000-0000-0000-0000-000000000003");
        var cascadeOnlyEventId = Guid.Parse("93000000-0000-0000-0000-000000000006");
        var oldAuditEventId = Guid.Parse("93000000-0000-0000-0000-000000000004");
        var oldMaintenanceRunId = Guid.Parse("93000000-0000-0000-0000-000000000005");
        var now = DateTimeOffset.UtcNow;

        using (var scope = environment.GetFactory().Services.CreateScope())
        {
            UseBootstrapActor(scope.ServiceProvider);
            var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            dbContext.RetrievalEvents.AddRange(
                CreateRetentionEvent(oldEventId, now.AddDays(-10), "retention-old"),
                CreateRetentionEvent(middleEventId, now.AddDays(-5), "retention-middle"),
                CreateRetentionEvent(recentEventId, now.AddDays(-1), "retention-recent"),
                CreateRetentionEvent(cascadeOnlyEventId, now.AddDays(-10), "retention-cascade-only"));
            dbContext.RetrievalHits.AddRange(Enumerable.Range(1, 3).Select(index => CreateRetentionHit(oldEventId, $"old hit {index}", now.AddDays(-10))));
            dbContext.RetrievalHits.AddRange(Enumerable.Range(1, 4).Select(index => CreateRetentionHit(middleEventId, $"middle hit {index}", now.AddDays(-5))));
            dbContext.RetrievalHits.Add(CreateRetentionHit(recentEventId, "recent hit", now.AddDays(-1)));
            dbContext.RetrievalHits.AddRange(Enumerable.Range(1, 2).Select(index => CreateRetentionHit(cascadeOnlyEventId, $"cascade hit {index}", null)));
            dbContext.SecurityAuditEvents.Add(new SecurityAuditEvent
            {
                Id = oldAuditEventId,
                EventType = SecurityAuditEventType.ApiTokenAuthenticationFailed,
                Outcome = "failure",
                CreatedAt = now.AddDays(-181)
            });
            dbContext.RuntimeLogEntries.Add(new RuntimeLogEntry
            {
                ServiceName = "retention-test",
                Category = "retention-test",
                Level = "Warning",
                Message = "old runtime log",
                CreatedAt = now.AddDays(-31)
            });
            dbContext.MaintenanceRuns.Add(new MaintenanceRun
            {
                Id = oldMaintenanceRunId,
                MaintenanceType = MaintenanceRunType.VacuumFullReclaim,
                Status = MaintenanceRunStatus.Completed,
                StartedAt = now.AddDays(-181),
                CompletedAt = now.AddDays(-181).AddMinutes(1),
                TriggeredBy = "retention-test"
            });
            await dbContext.SaveChangesAsync(CancellationToken.None);

            var service = scope.ServiceProvider.GetRequiredService<IRetrievalTelemetryRetentionService>();
            var result = await service.RunAsync(
                new RetrievalTelemetryRetentionRunRequest(
                    TriggeredBy: "api-contract-test",
                    BatchSize: 2,
                    EventBatchSize: 1,
                    TimeWindowDays: 3,
                    DelayBetweenBatchesMs: 0,
                    RunVacuumAnalyzeAfterRetention: false),
                "api-contract-test",
                CancellationToken.None);

            result.DeletedHits.Should().BeGreaterThanOrEqualTo(9);
            result.DeletedEvents.Should().BeGreaterThanOrEqualTo(1);
        }

        using (var scope = environment.GetFactory().Services.CreateScope())
        {
            UseBootstrapActor(scope.ServiceProvider);
            var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            (await dbContext.RetrievalEvents.AnyAsync(x => x.Id == oldEventId)).Should().BeFalse();
            (await dbContext.RetrievalHits.AnyAsync(x => x.RetrievalEventId == oldEventId)).Should().BeFalse();
            (await dbContext.RetrievalEvents.AnyAsync(x => x.Id == cascadeOnlyEventId)).Should().BeFalse();
            (await dbContext.RetrievalHits.AnyAsync(x => x.RetrievalEventId == cascadeOnlyEventId)).Should().BeFalse();

            (await dbContext.RetrievalEvents.AnyAsync(x => x.Id == middleEventId)).Should().BeTrue();
            (await dbContext.RetrievalHits.AnyAsync(x => x.RetrievalEventId == middleEventId)).Should().BeFalse();

            (await dbContext.RetrievalEvents.AnyAsync(x => x.Id == recentEventId)).Should().BeTrue();
            (await dbContext.RetrievalHits.AnyAsync(x => x.RetrievalEventId == recentEventId)).Should().BeTrue();
            (await dbContext.RetrievalTelemetryDailySummaries.AnyAsync(x => x.EntryPoint == "retention-test")).Should().BeTrue();
            (await dbContext.RetrievalTelemetryDailyHitSummaries.AnyAsync(x => x.EntryPoint == "retention-test")).Should().BeTrue();
            (await dbContext.SecurityAuditEvents.AnyAsync(x => x.Id == oldAuditEventId)).Should().BeFalse();
            (await dbContext.RuntimeLogEntries.AnyAsync(x => x.ServiceName == "retention-test" && x.Message == "old runtime log")).Should().BeFalse();
            (await dbContext.MaintenanceRuns.AnyAsync(x => x.Id == oldMaintenanceRunId)).Should().BeFalse();

            var run = await dbContext.MaintenanceRuns
                .OrderByDescending(x => x.StartedAt)
                .FirstAsync(x => x.MaintenanceType == MaintenanceRunType.RetrievalTelemetryRetention);
            run.Status.Should().Be(MaintenanceRunStatus.Completed);
            run.PolicyJson.Should().Contain("hitsRetentionDays");
            run.PolicyJson.Should().Contain("summaryRetentionDays");
            run.PolicyJson.Should().Contain("3");
            run.PolicyJson.Should().Contain("7");
            run.PolicyJson.Should().Contain("eventBatchSize");
            run.PolicyJson.Should().Contain("maxSummaryDaysPerRun");
            run.PolicyJson.Should().Contain("timeWindowDays");
            run.PolicyJson.Should().Contain("runVacuumAnalyzeAfterRetention");
            run.ResultJson.Should().Contain("deletedHits");
            run.ResultJson.Should().Contain("deletedHitsViaEventCascade");
            run.ResultJson.Should().Contain("upsertedEventSummaryRows");
            run.ResultJson.Should().Contain("upsertedHitSummaryRows");
            run.ResultJson.Should().Contain("otherTableRetention");
            run.ResultJson.Should().Contain("hitsWindowStartUtc");
            run.ResultJson.Should().Contain("eventsWindowStartUtc");
            run.ResultJson.Should().Contain("processedHitsWindows");
            run.ResultJson.Should().Contain("processedEventsWindows");
            run.ResultJson.Should().Contain("droppedHitPartitions");
            run.ResultJson.Should().Contain("droppedEventPartitions");
            run.ResultJson.Should().Contain("summaryBackfillError");
            run.ResultJson.Should().Contain("summaryBackfillErrorKind");
            run.ResultJson.Should().Contain("summaryBackfillFailedDay");
            run.ResultJson.Should().Contain("summaryBackfillFailureCount");
            run.ResultJson.Should().Contain("summaryBackfillLastExceptionType");
            run.ResultJson.Should().Contain("summaryEventBackfillError");
            run.ResultJson.Should().Contain("summaryHitBackfillError");
            run.ResultJson.Should().Contain("deletedEmbeddingUsageBuckets");
            run.ResultJson.Should().Contain("hitRetentionSkippedReason");
            run.ResultJson.Should().Contain("hitRetentionError");
            run.ResultJson.Should().Contain("vacuumAnalyzeRequested");
        }
    }

    [DockerRequiredFact]
    public async Task Retrieval_Telemetry_Retention_Run_Request_Should_Apply_Manual_Overrides()
    {
        using var client = environment.GetFactory().CreateClient();
        using var response = await client.PostAsJsonAsync(
            "/api/maintenance/retrieval-telemetry-retention/run",
            new RetrievalTelemetryRetentionRunRequest(
                TriggeredBy: "manual-override-test",
                BatchSize: 2,
                EventBatchSize: 1,
                TimeWindowDays: 1,
                DelayBetweenBatchesMs: 0,
                CommandTimeoutSeconds: 30,
                MaxDurationMinutes: 120,
                RunVacuumAnalyzeAfterRetention: false));

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<RetrievalTelemetryRetentionRunResult>();
        result.Should().NotBeNull();

        using var scope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(scope.ServiceProvider);
        var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var run = await dbContext.MaintenanceRuns.SingleAsync(x => x.Id == result!.RunId);
        using var policyDocument = JsonDocument.Parse(run.PolicyJson);
        var policy = policyDocument.RootElement;
        policy.GetProperty("batchSize").GetInt32().Should().Be(2);
        policy.GetProperty("eventBatchSize").GetInt32().Should().Be(1);
        policy.GetProperty("timeWindowDays").GetInt32().Should().Be(1);
        policy.GetProperty("delayBetweenBatchesMs").GetInt32().Should().Be(0);
        policy.GetProperty("commandTimeoutSeconds").GetInt32().Should().Be(30);
        policy.GetProperty("maxDurationMinutes").GetInt32().Should().Be(120);
        policy.GetProperty("runVacuumAnalyzeAfterRetention").GetBoolean().Should().BeFalse();
        policy.GetProperty("summaryRetentionDays").GetInt32().Should().Be(30);
        policy.GetProperty("maxSummaryDaysPerRun").GetInt32().Should().Be(3);
        policy.GetProperty("securityAuditRetentionDays").GetInt32().Should().Be(180);
        policy.GetProperty("runtimeLogRetentionDays").GetInt32().Should().Be(30);
        policy.GetProperty("maintenanceRunRetentionDays").GetInt32().Should().Be(180);

        using var resultDocument = JsonDocument.Parse(run.ResultJson);
        resultDocument.RootElement.GetProperty("vacuumAnalyzeRequested").GetBoolean().Should().BeFalse();
    }

    [DockerRequiredFact]
    public async Task Memory_Data_Retention_Run_Should_Preview_Then_Reject_Legacy_Direct_Delete()
    {
        var projectId = $"MemoryRetention_{Guid.NewGuid():N}";
        var autoDeleteId = Guid.Empty;
        var reviewArchivedId = Guid.Empty;
        var activeId = Guid.Empty;
        var reviewActiveId = Guid.Empty;
        CacheVersionStamp beforePreviewStamp;

        using (var scope = environment.GetFactory().Services.CreateScope())
        {
            UseBootstrapActor(scope.ServiceProvider);
            var memoryService = scope.ServiceProvider.GetRequiredService<IMemoryService>();
            var processor = scope.ServiceProvider.GetRequiredService<IBackgroundJobProcessor>();
            var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var cacheStore = scope.ServiceProvider.GetRequiredService<ICacheVersionStore>();
            var actor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>().Current;
            var now = DateTimeOffset.UtcNow;
            var oldArchivedCutoff = now.AddDays(-4_000);

            var autoDelete = await memoryService.UpsertAsync(
                new MemoryUpsertRequest(
                    ExternalKey: $"memory-retention-auto-{Guid.NewGuid():N}",
                    Scope: MemoryScope.Project,
                    MemoryType: MemoryType.Episode,
                    Title: "Memory retention auto-delete fixture",
                    Content: "Archived low-signal memory retention fixture used to create chunks and vectors.",
                    Summary: "Archived low-signal retention fixture",
                    SourceType: "document",
                    SourceRef: "tests/memory-retention",
                    Tags: ["retention", "archived", "sourceManaged"],
                    Importance: 0.2m,
                    Confidence: 0.4m,
                    MetadataJson: """{"sourceManaged":true,"missing":true}""",
                    ProjectId: projectId),
                CancellationToken.None);
            var reviewArchived = await memoryService.UpsertAsync(
                new MemoryUpsertRequest(
                    ExternalKey: $"memory-retention-review-archived-{Guid.NewGuid():N}",
                    Scope: MemoryScope.Project,
                    MemoryType: MemoryType.Decision,
                    Title: "Memory retention review archived fixture",
                    Content: "Important archived memory retention fixture should require review.",
                    Summary: "Review archived retention fixture",
                    SourceType: "document",
                    SourceRef: "tests/memory-retention",
                    Tags: ["retention", "archived"],
                    Importance: 0.95m,
                    Confidence: 0.95m,
                    ProjectId: projectId),
                CancellationToken.None);
            var active = await memoryService.UpsertAsync(
                new MemoryUpsertRequest(
                    ExternalKey: $"memory-retention-active-{Guid.NewGuid():N}",
                    Scope: MemoryScope.Project,
                    MemoryType: MemoryType.Artifact,
                    Title: "Memory retention active fixture",
                    Content: "Active memory retention fixture should not be deleted by retention.",
                    Summary: "Active retention fixture",
                    SourceType: "document",
                    SourceRef: "tests/memory-retention",
                    Tags: ["retention", "active"],
                    Importance: 0.7m,
                    Confidence: 0.9m,
                    ProjectId: projectId),
                CancellationToken.None);
            var reviewActive = await memoryService.UpsertAsync(
                new MemoryUpsertRequest(
                    ExternalKey: $"memory-retention-review-active-{Guid.NewGuid():N}",
                    Scope: MemoryScope.Project,
                    MemoryType: MemoryType.Episode,
                    Title: "Memory retention active review fixture",
                    Content: "Active low-signal memory retention fixture should require review before archive/delete.",
                    Summary: "Active review retention fixture",
                    SourceType: "document",
                    SourceRef: "tests/memory-retention",
                    Tags: ["retention", "low-signal"],
                    Importance: 0.2m,
                    Confidence: 0.4m,
                    ProjectId: projectId),
                CancellationToken.None);

            autoDeleteId = autoDelete.Id;
            reviewArchivedId = reviewArchived.Id;
            activeId = active.Id;
            reviewActiveId = reviewActive.Id;

            var reindex = await memoryService.EnqueueReindexAsync(
                new EnqueueReindexRequest(MemoryItemId: autoDelete.Id, ProjectId: projectId),
                CancellationToken.None);
            for (var attempt = 0; attempt < 100; attempt++)
            {
                await processor.ProcessNextAsync(CancellationToken.None);
                var job = await dbContext.MemoryJobs.AsNoTracking().SingleAsync(x => x.Id == reindex.JobId, CancellationToken.None);
                if (job.Status is MemoryJobStatus.Completed or MemoryJobStatus.Failed)
                {
                    break;
                }
            }
            (await dbContext.MemoryJobs.AsNoTracking().SingleAsync(x => x.Id == reindex.JobId, CancellationToken.None))
                .Status.Should().Be(MemoryJobStatus.Completed);

            var vectorReadyChunkIds = await dbContext.MemoryItemChunks
                .Where(x => x.MemoryItemId == autoDelete.Id)
                .Select(x => x.Id)
                .ToListAsync(CancellationToken.None);
            (await dbContext.MemoryChunkVectors.AnyAsync(x => vectorReadyChunkIds.Contains(x.ChunkId), CancellationToken.None))
                .Should().BeTrue();

            dbContext.MemoryLinks.Add(new MemoryLink
            {
                FromId = reviewArchived.Id,
                ToId = active.Id,
                LinkType = "retention-test",
                CreatedAt = now
            });

            var autoDeleteEntity = await dbContext.MemoryItems.SingleAsync(x => x.Id == autoDelete.Id);
            autoDeleteEntity.Status = MemoryStatus.Archived;
            autoDeleteEntity.UpdatedAt = oldArchivedCutoff;

            var reviewArchivedEntity = await dbContext.MemoryItems.SingleAsync(x => x.Id == reviewArchived.Id);
            reviewArchivedEntity.Status = MemoryStatus.Archived;
            reviewArchivedEntity.UpdatedAt = oldArchivedCutoff;

            var activeEntity = await dbContext.MemoryItems.SingleAsync(x => x.Id == active.Id);
            activeEntity.Status = MemoryStatus.Active;
            activeEntity.UpdatedAt = oldArchivedCutoff;

            var reviewActiveEntity = await dbContext.MemoryItems.SingleAsync(x => x.Id == reviewActive.Id);
            reviewActiveEntity.Status = MemoryStatus.Active;
            reviewActiveEntity.UpdatedAt = oldArchivedCutoff;

            await dbContext.SaveChangesAsync(CancellationToken.None);
            beforePreviewStamp = await cacheStore.GetVersionStampAsync([projectId], actor, includeShared: false, CancellationToken.None);
        }

        using var client = environment.GetFactory().CreateClient();
        using var classifyResponse = await client.PostAsJsonAsync(
            "/api/maintenance/memory-data-retention/run",
            new MemoryDataRetentionRunRequest(
                TriggeredBy: "memory-retention-classify-test",
                Mode: MemoryDataRetentionRunMode.Classify,
                ArchivedItemsRetentionDays: 3650,
                BatchSize: 1,
                DelayBetweenBatchesMs: 0,
                CommandTimeoutSeconds: 30,
                MaxDurationMinutes: 5,
                IncludeCandidateDetails: true));
        classifyResponse.EnsureSuccessStatusCode();
        var classify = await classifyResponse.Content.ReadFromJsonAsync<MemoryDataRetentionRunResult>();
        classify.Should().NotBeNull();
        classify!.Mode.Should().Be(MemoryDataRetentionRunMode.Classify);
        classify.DeletedMemoryItems.Should().Be(0);
        classify.AutoDeleteCandidateCount.Should().BeGreaterThanOrEqualTo(1);
        classify.ReviewCandidateCount.Should().BeGreaterThanOrEqualTo(2);
        classify.AutoDeleteCandidates.Select(x => x.MemoryId).Should().Contain(autoDeleteId);
        classify.ReviewCandidates.Select(x => x.MemoryId).Should().Contain(reviewArchivedId);
        classify.ReviewCandidates.Select(x => x.MemoryId).Should().Contain(reviewActiveId);

        using var previewResponse = await client.PostAsJsonAsync(
            "/api/maintenance/memory-data-retention/run",
            new MemoryDataRetentionRunRequest(
                TriggeredBy: "memory-retention-preview-test",
                Mode: MemoryDataRetentionRunMode.PreviewDelete,
                ArchivedItemsRetentionDays: 3650,
                BatchSize: 1,
                DelayBetweenBatchesMs: 0,
                CommandTimeoutSeconds: 30,
                MaxDurationMinutes: 5,
                IncludeCandidateDetails: true));
        previewResponse.EnsureSuccessStatusCode();
        var preview = await previewResponse.Content.ReadFromJsonAsync<MemoryDataRetentionRunResult>();
        preview.Should().NotBeNull();
        preview!.Mode.Should().Be(MemoryDataRetentionRunMode.PreviewDelete);
        preview.AffectedProjectIds.Should().Contain(projectId);
        preview.DeletedMemoryItems.Should().Be(1);
        preview.DeletedRevisions.Should().BeGreaterThanOrEqualTo(1);
        preview.DeletedChunks.Should().BeGreaterThanOrEqualTo(1);
        preview.DeletedVectors.Should().BeGreaterThanOrEqualTo(1);

        using (var scope = environment.GetFactory().Services.CreateScope())
        {
            UseBootstrapActor(scope.ServiceProvider);
            var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var cacheStore = scope.ServiceProvider.GetRequiredService<ICacheVersionStore>();
            var actor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>().Current;
            var afterPreviewStamp = await cacheStore.GetVersionStampAsync([projectId], actor, includeShared: false, CancellationToken.None);

            (await dbContext.MemoryItems.AnyAsync(x => x.Id == autoDeleteId)).Should().BeTrue();
            (await dbContext.MemoryItems.AnyAsync(x => x.Id == reviewArchivedId)).Should().BeTrue();
            afterPreviewStamp.ProjectVersions[projectId].Should().Be(beforePreviewStamp.ProjectVersions[projectId]);
        }

        using var applyResponse = await client.PostAsJsonAsync(
            "/api/maintenance/memory-data-retention/run",
            new MemoryDataRetentionRunRequest(
                TriggeredBy: "memory-retention-apply-test",
                Mode: MemoryDataRetentionRunMode.ApplyAutoDelete,
                ArchivedItemsRetentionDays: 3650,
                BatchSize: 1,
                DelayBetweenBatchesMs: 0,
                CommandTimeoutSeconds: 30,
                MaxDurationMinutes: 5));
        applyResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        (await applyResponse.Content.ReadAsStringAsync()).Should().Contain("quarantine").And.Contain("matured-delete");

        using (var scope = environment.GetFactory().Services.CreateScope())
        {
            UseBootstrapActor(scope.ServiceProvider);
            var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var cacheStore = scope.ServiceProvider.GetRequiredService<ICacheVersionStore>();
            var actor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>().Current;
            var afterApplyStamp = await cacheStore.GetVersionStampAsync([projectId], actor, includeShared: false, CancellationToken.None);

            (await dbContext.MemoryItems.AnyAsync(x => x.Id == autoDeleteId)).Should().BeTrue();
            (await dbContext.MemoryItemChunks.AnyAsync(x => x.MemoryItemId == autoDeleteId)).Should().BeTrue();
            (await dbContext.MemoryItems.AnyAsync(x => x.Id == reviewArchivedId)).Should().BeTrue();
            (await dbContext.MemoryItems.AnyAsync(x => x.Id == activeId)).Should().BeTrue();
            (await dbContext.MemoryItems.AnyAsync(x => x.Id == reviewActiveId)).Should().BeTrue();
            afterApplyStamp.ProjectVersions[projectId].Should().Be(beforePreviewStamp.ProjectVersions[projectId]);
            (await dbContext.MaintenanceRuns.AnyAsync(x => x.TriggeredBy == "memory-retention-apply-test")).Should().BeFalse();
        }
    }

    [DockerRequiredFact]
    public async Task Memory_Data_Retention_Cleanup_Should_Prune_Old_Revisions_And_Overflow_Chunks()
    {
        var memoryId = Guid.Parse("96000000-0000-0000-0000-000000000001");
        var projectId = "retention-cleanup-project";
        var now = DateTimeOffset.UtcNow;
        var chunkIds = Enumerable.Range(0, 5)
            .Select(index => Guid.Parse($"96000000-0000-0000-0001-{index + 1:000000000000}"))
            .ToArray();

        using (var scope = environment.GetFactory().Services.CreateScope())
        {
            UseBootstrapActor(scope.ServiceProvider);
            var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            dbContext.MemoryItems.Add(new MemoryItem
            {
                Id = memoryId,
                ProjectId = projectId,
                ExternalKey = "retention-cleanup-memory",
                Scope = MemoryScope.Project,
                MemoryType = MemoryType.Fact,
                Title = "Retention cleanup memory",
                Content = "Retention cleanup keeps the memory item while trimming maintenance data.",
                Summary = "Retention cleanup memory",
                Tags = ["retention-cleanup"],
                SourceType = "test",
                SourceRef = "api-contract",
                Importance = 0.8m,
                Confidence = 0.9m,
                Version = 5,
                Status = MemoryStatus.Active,
                MetadataJson = "{}",
                CreatedAt = now.AddDays(-40),
                UpdatedAt = now.AddDays(-1)
            });

            dbContext.MemoryItemRevisions.AddRange(Enumerable.Range(1, 5).Select(version => new MemoryItemRevision
            {
                MemoryItemId = memoryId,
                Version = version,
                Title = $"Revision {version}",
                Content = $"Revision content {version}",
                Summary = $"Revision summary {version}",
                MetadataJson = "{}",
                ChangedBy = "test",
                CreatedAt = now.AddDays(-40 + version)
            }));

            var chunks = chunkIds.Select((id, index) => new MemoryItemChunk
            {
                Id = id,
                MemoryItemId = memoryId,
                ChunkKind = ChunkKind.Document,
                ChunkIndex = index,
                ChunkText = $"chunk {index}",
                MetadataJson = "{}",
                CreatedAt = now.AddDays(-1)
            }).ToArray();
            dbContext.MemoryItemChunks.AddRange(chunks);

            await dbContext.SaveChangesAsync(CancellationToken.None);
            foreach (var chunk in chunks)
            {
                await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO memory_chunk_vectors (id, chunk_id, model_key, dimension, status, embedding, created_at)
                    VALUES ({Guid.NewGuid()}, {chunk.Id}, {"test-model"}, {3}, {VectorStatus.Active.ToString()}, {"[0,0,0]"}::vector, {now.AddDays(-1)});
                    """);
            }
        }

        using var client = environment.GetFactory().CreateClient();
        using var response = await client.PostAsJsonAsync(
            "/api/maintenance/memory-data-retention/run",
            new MemoryDataRetentionRunRequest(
                TriggeredBy: "memory-retention-cleanup-test",
                Mode: MemoryDataRetentionRunMode.ApplyMaintenanceCleanup,
                BatchSize: 100,
                DelayBetweenBatchesMs: 0,
                CommandTimeoutSeconds: 30,
                MaxDurationMinutes: 5,
                IncludeCandidateDetails: false,
                RevisionRetentionDays: 10,
                MinRevisionsToKeep: 2,
                MaxChunksPerMemoryItem: 3));
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<MemoryDataRetentionRunResult>();
        result.Should().NotBeNull();
        result!.Mode.Should().Be(MemoryDataRetentionRunMode.ApplyMaintenanceCleanup);
        result.DeletedMemoryItems.Should().Be(0);
        result.DeletedRevisions.Should().Be(3);
        result.DeletedChunks.Should().Be(2);
        result.DeletedVectors.Should().Be(2);
        result.AffectedProjectIds.Should().Contain(projectId);

        using (var scope = environment.GetFactory().Services.CreateScope())
        {
            UseBootstrapActor(scope.ServiceProvider);
            var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            (await dbContext.MemoryItems.AnyAsync(x => x.Id == memoryId)).Should().BeTrue();
            (await dbContext.MemoryItemRevisions.Where(x => x.MemoryItemId == memoryId).Select(x => x.Version).OrderBy(x => x).ToListAsync())
                .Should().Equal([4, 5]);
            (await dbContext.MemoryItemChunks.Where(x => x.MemoryItemId == memoryId).Select(x => x.ChunkIndex).OrderBy(x => x).ToListAsync())
                .Should().Equal([0, 1, 2]);
            (await dbContext.MemoryChunkVectors.CountAsync(x => chunkIds.Contains(x.ChunkId))).Should().Be(3);

            var run = await dbContext.MaintenanceRuns.SingleAsync(x => x.Id == result.RunId);
            using var resultDocument = JsonDocument.Parse(run.ResultJson);
            resultDocument.RootElement.GetProperty("prunedRevisions").GetInt64().Should().Be(3);
            resultDocument.RootElement.GetProperty("prunedChunks").GetInt64().Should().Be(2);
            resultDocument.RootElement.GetProperty("prunedVectors").GetInt64().Should().Be(2);
            resultDocument.RootElement.GetProperty("maxChunksPerMemoryItem").GetInt32().Should().Be(3);
            resultDocument.RootElement.GetProperty("minRevisionsToKeep").GetInt32().Should().Be(2);
        }
    }

    [DockerRequiredFact]
    public async Task Memories_Endpoint_Should_Allow_Querying_By_ProjectId_Without_Project_Filter()
    {
        using (var scope = environment.GetFactory().Services.CreateScope())
        {
            UseBootstrapActor(scope.ServiceProvider);
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
            UseBootstrapActor(scope.ServiceProvider);
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
            UseBootstrapActor(scope.ServiceProvider);
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

        await RefreshMemoryGraphIndexAsync();

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
            UseBootstrapActor(scope.ServiceProvider);
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

        await RefreshMemoryGraphIndexAsync();

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
            UseBootstrapActor(scope.ServiceProvider);
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

        await RefreshMemoryGraphIndexAsync();

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

        var initialPipeline = await client.GetFromJsonAsync<ConversationPipelineStatusResult>($"/api/conversations/checkpoints/{ingest.CheckpointId}/pipeline");
        initialPipeline.Should().NotBeNull();
        initialPipeline!.CheckpointId.Should().Be(ingest.CheckpointId);
        initialPipeline.PipelineStatus.Should().BeOneOf("checkpoint-only", "ingest-pending");

        using var processResponse = await client.PostAsync($"/api/conversations/checkpoints/{ingest.CheckpointId}/process", null);
        processResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var processed = await processResponse.Content.ReadFromJsonAsync<ConversationPipelineStatusResult>();
        processed.Should().NotBeNull();
        processed!.Insights.Should().NotBeEmpty();

        using var processAgainResponse = await client.PostAsync($"/api/conversations/checkpoints/{ingest.CheckpointId}/process", null);
        processAgainResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var processedAgain = await processAgainResponse.Content.ReadFromJsonAsync<ConversationPipelineStatusResult>();
        processedAgain.Should().NotBeNull();
        processedAgain!.Insights.Count.Should().Be(processed.Insights.Count);

        using var promoteResponse = await client.PostAsJsonAsync("/api/conversations/insights/promote", new ConversationPromotionRetryRequest(conversationId, null));
        promoteResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        using (var scope = environment.GetFactory().Services.CreateScope())
        {
            UseBootstrapActor(scope.ServiceProvider);
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

        var promotedPipeline = await client.GetFromJsonAsync<ConversationPipelineStatusResult>($"/api/conversations/checkpoints/{ingest.CheckpointId}/pipeline");
        promotedPipeline.Should().NotBeNull();
        promotedPipeline!.PipelineStatus.Should().Be("promoted");
        promotedPipeline.Insights.Should().Contain(x => x.PromotedMemoryId.HasValue);
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

    private async Task RefreshMemoryGraphIndexAsync()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(scope.ServiceProvider);
        var builder = scope.ServiceProvider.GetRequiredService<IDashboardMemoryGraphIndexBuilder>();
        var snapshotStore = scope.ServiceProvider.GetRequiredService<IDashboardSnapshotStore>();
        var capturedAtUtc = DateTimeOffset.UtcNow;
        var payload = await builder.BuildAsync(CancellationToken.None);

        await snapshotStore.SetAsync(
            new DashboardSnapshotEnvelope<DashboardMemoryGraphIndexSnapshotPayload>(
                DashboardSnapshotKeys.MemoryGraphIndex,
                capturedAtUtc,
                15,
                DashboardSnapshotStalenessPolicy.ComputeStaleAfter(capturedAtUtc, 15),
                string.Empty,
                payload),
            CancellationToken.None);
    }

    [DockerRequiredFact]
    public async Task Memory_Transfer_Endpoints_Should_Support_Encrypted_Export_Preview_And_Overwrite_Apply()
    {
        using (var scope = environment.GetFactory().Services.CreateScope())
        {
            UseBootstrapActor(scope.ServiceProvider);
            var memoryService = scope.ServiceProvider.GetRequiredService<IMemoryService>();
            var actor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>().Current;
            var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

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

            var otherUserId = Guid.NewGuid();
            dbContext.TenantUsers.Add(new TenantUser
            {
                Id = otherUserId,
                TenantId = actor.TenantId!.Value,
                Username = $"transfer-other-{otherUserId:N}"[..28],
                DisplayName = "Transfer Other User",
                Role = TenantUserRole.Member,
                Status = TenantUserStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await dbContext.SaveChangesAsync(CancellationToken.None);
            dbContext.MemoryItems.Add(new MemoryItem
            {
                TenantId = actor.TenantId,
                OwnerUserId = otherUserId,
                ProjectId = ProjectContext.DefaultProjectId,
                ExternalKey = "repo:transfer:1",
                Scope = MemoryScope.Project,
                MemoryType = MemoryType.Fact,
                Title = "Other user's confidential conflict title",
                Content = "This row must not participate in another user's import preview.",
                Summary = "Other user's confidential conflict",
                SourceType = "test",
                SourceRef = "ownership-regression",
                Status = MemoryStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await dbContext.SaveChangesAsync(CancellationToken.None);
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
        preview.Conflicts.Should().NotContain(x => x.ExistingTitle == "Other user's confidential conflict title");

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

        using (var cleanupScope = environment.GetFactory().Services.CreateScope())
        {
            var cleanupDbContext = cleanupScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var crossOwnerFixtures = await cleanupDbContext.MemoryItems
                .Where(x => x.Title == "Other user's confidential conflict title")
                .ToListAsync(CancellationToken.None);
            cleanupDbContext.MemoryItems.RemoveRange(crossOwnerFixtures);
            await cleanupDbContext.SaveChangesAsync(CancellationToken.None);
        }
    }

    private static async Task EnsureAdminOwnerAsync(MemoryDbContext dbContext, Guid adminTenantId, Guid adminUserId, DateTimeOffset now)
    {
        if (!await dbContext.Tenants.AnyAsync(x => x.Id == adminTenantId))
        {
            dbContext.Tenants.Add(new Tenant
            {
                Id = adminTenantId,
                Slug = "admin",
                DisplayName = "Admin",
                Status = TenantStatus.Active,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        if (!await dbContext.TenantUsers.AnyAsync(x => x.Id == adminUserId))
        {
            dbContext.TenantUsers.Add(new TenantUser
            {
                Id = adminUserId,
                TenantId = adminTenantId,
                Username = "admin",
                DisplayName = "Admin",
                Role = TenantUserRole.Owner,
                Status = TenantUserStatus.Active,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        await dbContext.SaveChangesAsync(CancellationToken.None);
    }

    private static MemoryItem CreateOwnerRepairMemory(
        Guid id,
        Guid tenantId,
        Guid ownerUserId,
        string projectId,
        string externalKey,
        string title,
        DateTimeOffset now)
        => new()
        {
            Id = id,
            TenantId = tenantId,
            OwnerUserId = ownerUserId,
            ProjectId = projectId,
            ExternalKey = externalKey,
            Scope = MemoryScope.Project,
            MemoryType = MemoryType.Fact,
            Title = title,
            Content = title,
            Summary = title,
            SourceType = "test",
            SourceRef = "api-contract",
            Importance = 0.7m,
            Confidence = 0.9m,
            CreatedAt = now,
            UpdatedAt = now
        };

    private static RetrievalEvent CreateRetentionEvent(Guid id, DateTimeOffset createdAt, string traceId)
        => new()
        {
            Id = id,
            ProjectId = ProjectContext.DefaultProjectId,
            Channel = "api-contract",
            EntryPoint = "retention-test",
            Purpose = "retention validation",
            QueryText = traceId,
            QueryHash = traceId,
            QueryMode = MemoryQueryMode.CurrentOnly.ToString(),
            IncludedProjectIds = [ProjectContext.DefaultProjectId],
            Limit = 5,
            ResultCount = 1,
            DurationMs = 1,
            Success = true,
            TraceId = traceId,
            RequestId = $"{traceId}-request",
            MetadataJson = "{}",
            CreatedAt = createdAt
        };

    private static void AssertNoStoreHeaders(HttpResponseMessage response)
    {
        response.Headers.CacheControl?.NoStore.Should().BeTrue();
        response.Headers.CacheControl?.NoCache.Should().BeTrue();
        response.Headers.TryGetValues("Cloudflare-CDN-Cache-Control", out var cloudflareValues).Should().BeTrue();
        cloudflareValues.Should().ContainSingle("no-store");
        response.Headers.TryGetValues("CDN-Cache-Control", out var cdnValues).Should().BeTrue();
        cdnValues.Should().ContainSingle("no-store");
    }

    [DockerRequiredFact]
    public async Task Discussion_Archive_Should_Default_Hide_Block_Mutations_And_Restore_Closed_Status()
    {
        using var client = environment.GetFactory().CreateClient();
        const string peerProjectId = "api-contract-discussion-peer";
        using var createResponse = await client.PostAsJsonAsync("/api/discussions/threads", new DiscussionThreadCreateRequest(
            ProjectContext.DefaultProjectId,
            ProjectContext.DefaultProjectId,
            "Archive lifecycle contract",
            [peerProjectId],
            "Initial message"));
        createResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<DiscussionThreadDetailResult>();
        created.Should().NotBeNull();

        using var closeResponse = await client.PostAsync($"/api/discussions/threads/{created!.Id:D}/close", null);
        closeResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        using var archiveResponse = await client.PostAsync($"/api/discussions/threads/{created.Id:D}/archive", null);
        archiveResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var archived = await archiveResponse.Content.ReadFromJsonAsync<DiscussionThreadResult>();
        archived.Should().NotBeNull();
        archived!.Status.Should().Be("Closed");
        archived.IsArchived.Should().BeTrue();

        var defaultList = await client.GetFromJsonAsync<List<DiscussionThreadResult>>(
            $"/api/discussions/threads?projectId={ProjectContext.DefaultProjectId}");
        defaultList.Should().NotContain(x => x.Id == created.Id);
        var archivedList = await client.GetFromJsonAsync<List<DiscussionThreadResult>>(
            $"/api/discussions/threads?projectId={ProjectContext.DefaultProjectId}&includeArchived=true");
        archivedList.Should().ContainSingle(x => x.Id == created.Id && x.IsArchived);

        using var rejectedReply = await client.PostAsJsonAsync(
            $"/api/discussions/threads/{created.Id:D}/messages",
            new { senderProjectId = ProjectContext.DefaultProjectId, content = "Archived reply" });
        rejectedReply.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);

        using var restoreResponse = await client.PostAsync($"/api/discussions/threads/{created.Id:D}/restore", null);
        restoreResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var restored = await restoreResponse.Content.ReadFromJsonAsync<DiscussionThreadResult>();
        restored.Should().NotBeNull();
        restored!.Status.Should().Be("Closed");
        restored.IsArchived.Should().BeFalse();

        var restoredList = await client.GetFromJsonAsync<List<DiscussionThreadResult>>(
            $"/api/discussions/threads?projectId={ProjectContext.DefaultProjectId}");
        restoredList.Should().ContainSingle(x => x.Id == created.Id && x.Status == "Closed");
    }

    [DockerRequiredFact]
    public async Task Work_Items_And_Knowledge_Review_Endpoints_Should_Keep_Project_Tasks_Separate_From_Governance()
    {
        using var client = environment.GetFactory().CreateClient();
        using var createResponse = await client.PostAsJsonAsync("/api/work-items", new ProjectWorkItemCreateRequest(
            ProjectContext.DefaultProjectId,
            "驗證分區整理 API",
            "確認專案代辦不會混入治理建議。",
            ChecklistItems: ["完成 checklist"],
            Priority: 80));
        createResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<ProjectWorkItemResult>();
        created.Should().NotBeNull();
        created!.Status.Should().Be(ProjectWorkItemStatus.Pending);

        using var guardedCompletionResponse = await client.PutAsJsonAsync($"/api/work-items/{created.Id:D}", new { status = ProjectWorkItemStatus.Completed });
        guardedCompletionResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);

        using var checklistResponse = await client.PutAsJsonAsync(
            $"/api/work-items/{created.Id:D}/checklist/{created.ChecklistItems.Single().Id:D}",
            new { isCompleted = true });
        checklistResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        using var updateResponse = await client.PutAsJsonAsync($"/api/work-items/{created.Id:D}", new { status = ProjectWorkItemStatus.Completed });
        updateResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        var listed = await client.GetFromJsonAsync<List<ProjectWorkItemResult>>($"/api/work-items?projectId={ProjectContext.DefaultProjectId}&status=Completed");
        listed.Should().ContainSingle(x => x.Id == created.Id);

        using var archiveResponse = await client.PostAsync($"/api/work-items/{created.Id:D}/archive", null);
        archiveResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var archived = await archiveResponse.Content.ReadFromJsonAsync<ProjectWorkItemResult>();
        archived.Should().NotBeNull();
        archived!.Status.Should().Be(ProjectWorkItemStatus.Completed);
        archived.IsArchived.Should().BeTrue();

        var defaultList = await client.GetFromJsonAsync<List<ProjectWorkItemResult>>($"/api/work-items?projectId={ProjectContext.DefaultProjectId}");
        defaultList.Should().NotContain(x => x.Id == created.Id);
        var archivedList = await client.GetFromJsonAsync<List<ProjectWorkItemResult>>($"/api/work-items?projectId={ProjectContext.DefaultProjectId}&includeArchived=true");
        archivedList.Should().ContainSingle(x => x.Id == created.Id && x.IsArchived);

        using var rejectedMutation = await client.PutAsJsonAsync($"/api/work-items/{created.Id:D}", new { priority = 99 });
        rejectedMutation.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);

        using var restoreResponse = await client.PostAsync($"/api/work-items/{created.Id:D}/restore", null);
        restoreResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var restored = await restoreResponse.Content.ReadFromJsonAsync<ProjectWorkItemResult>();
        restored.Should().NotBeNull();
        restored!.Status.Should().Be(ProjectWorkItemStatus.Completed);
        restored.IsArchived.Should().BeFalse();

        using var reviewResponse = await client.PostAsJsonAsync("/api/knowledge-reviews", new KnowledgeReviewRequest([ProjectContext.DefaultProjectId]));
        reviewResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var review = await reviewResponse.Content.ReadFromJsonAsync<KnowledgeReviewResult>();
        review.Should().NotBeNull();
        review!.Projects.Should().Contain(x => x.ProjectId == ProjectContext.DefaultProjectId);
        review.WorkItems.Should().Contain(x => x.Id == created.Id && x.Status == ProjectWorkItemStatus.Completed);
        review.Convergence.WorkItemActionableCount.Should().Be(0);
        review.DurableMemoryCoverage.Should().NotBeNull();
        review.DurableMemoryCoverage!.CoverageComplete.Should().BeTrue();
        review.DurableMemoryCoverage.ScannedCount.Should().Be(review.DurableMemoryCoverage.TotalCount);
        review.GovernanceCoverage.Should().NotBeNull();
        review.GovernanceCoverage!.CoverageComplete.Should().BeTrue();
        review.GovernanceCoverage.ProjectCoverage.ScannedCount.Should().Be(review.GovernanceCoverage.ProjectCoverage.TotalCount);
        review.GovernanceCoverage.HierarchyCoverage.CoverageComplete.Should().BeTrue();
        review.GovernanceCoverage.PreferenceCoverage.CoverageComplete.Should().BeTrue();
        review.GovernanceCoverage.ArtifactCoverage.CoverageComplete.Should().BeTrue();
        review.GovernanceCoverage.DiscussionCoverage.CoverageComplete.Should().BeTrue();
        review.GovernanceCoverage.WorkItemCoverage.CoverageComplete.Should().BeTrue();
        review.GovernanceCoverage.InsightCoverage.CoverageComplete.Should().BeTrue();
        review.GovernanceCoverage.SuggestedActionCoverage.CoverageComplete.Should().BeTrue();
        review.GovernanceCoverage.ProposalCoverage.CoverageComplete.Should().BeTrue();
        review.GovernanceCoverage.LogCoverage.CoverageComplete.Should().BeTrue();
    }

    [DockerRequiredFact]
    public async Task Work_Item_Governance_Exclusion_Endpoint_Should_Be_Explicit_Run_Scoped_And_Diagnostic()
    {
        using var client = environment.GetFactory().CreateClient();
        var projectId = $"api-tracker-{Guid.NewGuid():N}";
        var governanceRunId = $"api-tracker-run-{Guid.NewGuid():N}";
        using var createResponse = await client.PostAsJsonAsync("/api/work-items", new ProjectWorkItemCreateRequest(
            projectId, "API governance acceptance tracker"));
        createResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.Created);
        var tracker = (await createResponse.Content.ReadFromJsonAsync<ProjectWorkItemResult>())!;

        using var reviewResponse = await client.PostAsJsonAsync("/api/knowledge-reviews", new KnowledgeReviewRequest(
            [projectId], GovernanceRunId: governanceRunId, IsReReview: true));
        reviewResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var before = (await reviewResponse.Content.ReadFromJsonAsync<KnowledgeReviewResult>())!;
        before.Convergence.WorkItemActionableCount.Should().Be(1);

        using var exclusionResponse = await client.PutAsJsonAsync(
            $"/api/work-items/{tracker.Id:D}/governance-exclusion",
            new { projectId, governanceRunId, reason = "Tracks this exact API governance acceptance run.", excluded = true });
        exclusionResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var excluded = (await exclusionResponse.Content.ReadFromJsonAsync<ProjectWorkItemResult>())!;
        excluded.GovernanceExclusions.Should().ContainSingle(x => x.GovernanceRunId == governanceRunId && x.IsActive);

        using var reReviewResponse = await client.PostAsJsonAsync("/api/knowledge-reviews", new KnowledgeReviewRequest(
            [projectId], GovernanceRunId: governanceRunId, IsReReview: true));
        reReviewResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var after = (await reReviewResponse.Content.ReadFromJsonAsync<KnowledgeReviewResult>())!;
        after.Convergence.WorkItemActionableCount.Should().Be(0);
        after.Convergence.ExcludedGovernanceTrackerCount.Should().Be(1);
        after.Convergence.ActionableItemCount.Should().Be(0);
        after.Convergence.Status.Should().Be("ConvergedWithExceptions");
        after.Convergence.GovernedExceptionCount.Should().BeGreaterThan(0);
    }

    [DockerRequiredFact]
    public async Task Governance_Batch_Execute_Endpoint_Should_Expose_Contract_And_Fail_Closed_On_Snapshot_Mismatch()
    {
        using var client = environment.GetFactory().CreateClient();
        var projectId = $"api-governance-batch-{Guid.NewGuid():N}";
        var governanceRunId = $"api-governance-run-{Guid.NewGuid():N}";
        using var reviewResponse = await client.PostAsJsonAsync("/api/knowledge-reviews", new KnowledgeReviewRequest(
            [projectId], GovernanceRunId: governanceRunId));
        reviewResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var review = (await reviewResponse.Content.ReadFromJsonAsync<KnowledgeReviewResult>())!;

        var request = new GovernanceBatchExecuteRequest(
            governanceRunId,
            [projectId],
            review.DurableMemoryCoverage!.SnapshotToken,
            MaxMutations: 10,
            MaxDurationSeconds: 30,
            AllowedActionTypes: [GovernanceBatchActionType.Reindex],
            MaxRiskLevel: GovernanceBatchRiskLevel.Low,
            DryRun: false,
            AllowHardDelete: false,
            ExecutionMode: GovernanceBatchExecutionMode.Scheduled);
        using var executeResponse = await client.PostAsJsonAsync("/api/knowledge-reviews/execute", request);
        executeResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var result = await executeResponse.Content.ReadFromJsonAsync<GovernanceBatchExecuteResult>();
        result.Should().NotBeNull();
        result!.GovernanceRunId.Should().Be(governanceRunId);
        result.SnapshotToken.Should().Be(review.DurableMemoryCoverage.SnapshotToken);

        using var mismatchResponse = await client.PostAsJsonAsync("/api/knowledge-reviews/execute", request with
        {
            GovernanceRunId = $"wrong-run-{Guid.NewGuid():N}",
            SnapshotToken = review.DurableMemoryCoverage.SnapshotToken
        });
        mismatchResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.Conflict);
        using var mismatchProblem = JsonDocument.Parse(await mismatchResponse.Content.ReadAsStringAsync());
        mismatchProblem.RootElement.GetProperty("code").GetString()
            .Should().Be(nameof(GovernanceBatchErrorCode.CursorSnapshotMismatch));

        using var missingTombstone = await client.GetAsync(
            $"/api/knowledge-reviews/tombstones/{Guid.NewGuid():D}?projectId={Uri.EscapeDataString(projectId)}");
        missingTombstone.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
    }

    private static async Task CompleteActiveMaintenanceLeasesAsync(HttpClient client)
    {
        var status = await client.GetFromJsonAsync<MaintenanceStatusResult>("/api/maintenance/status");
        if (status is null)
        {
            return;
        }

        foreach (var lease in status.ActiveLeases)
        {
            var response = await client.PostAsJsonAsync(
                "/api/maintenance/leases/complete",
                new MaintenanceLeaseCompleteRequest(lease.LeaseId));
            response.EnsureSuccessStatusCode();
        }
    }

    private static HttpClient CreateAnonymousClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = null;
        return client;
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

    private static RetrievalHit CreateRetentionHit(Guid retrievalEventId, string title, DateTimeOffset? createdAt)
        => new()
        {
            RetrievalEventId = retrievalEventId,
            Rank = 1,
            MemoryId = null,
            Title = title,
            MemoryType = MemoryType.Fact.ToString(),
            SourceType = "test",
            SourceRef = "api-contract",
            Score = 0.5m,
            Excerpt = title,
            ProjectId = ProjectContext.DefaultProjectId,
            CreatedAt = createdAt
        };
}
