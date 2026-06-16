using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
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
        overview.Should().NotBeNull();
        overview!.ContextSavings.Should().NotBeNull();
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
        using var anonymousClient = secureFactory.CreateClient();
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
        tables.Should().Contain(x => x.Name == "maintenance_runs");
        tables.Should().Contain(x => x.Name == "retrieval_telemetry_daily_summaries");
        tables.Should().Contain(x => x.Name == "retrieval_telemetry_daily_hit_summaries");
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
            maintenanceHeaders.Should().Contain("true");
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
    public async Task Retrieval_Telemetry_Retention_Should_Delete_Raw_Rows_And_Write_Daily_Summaries()
    {
        var oldEventId = Guid.Parse("93000000-0000-0000-0000-000000000001");
        var middleEventId = Guid.Parse("93000000-0000-0000-0000-000000000002");
        var recentEventId = Guid.Parse("93000000-0000-0000-0000-000000000003");
        var oldAuditEventId = Guid.Parse("93000000-0000-0000-0000-000000000004");
        var oldMaintenanceRunId = Guid.Parse("93000000-0000-0000-0000-000000000005");
        var now = DateTimeOffset.UtcNow;

        using (var scope = environment.GetFactory().Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            dbContext.RetrievalEvents.AddRange(
                CreateRetentionEvent(oldEventId, now.AddDays(-10), "retention-old"),
                CreateRetentionEvent(middleEventId, now.AddDays(-5), "retention-middle"),
                CreateRetentionEvent(recentEventId, now.AddDays(-1), "retention-recent"));
            dbContext.RetrievalHits.AddRange(Enumerable.Range(1, 3).Select(index => CreateRetentionHit(oldEventId, $"old hit {index}")));
            dbContext.RetrievalHits.AddRange(Enumerable.Range(1, 4).Select(index => CreateRetentionHit(middleEventId, $"middle hit {index}")));
            dbContext.RetrievalHits.Add(CreateRetentionHit(recentEventId, "recent hit"));
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

            result.DeletedHits.Should().BeGreaterThanOrEqualTo(7);
            result.DeletedEvents.Should().BeGreaterThanOrEqualTo(1);
        }

        using (var scope = environment.GetFactory().Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            (await dbContext.RetrievalEvents.AnyAsync(x => x.Id == oldEventId)).Should().BeFalse();
            (await dbContext.RetrievalHits.AnyAsync(x => x.RetrievalEventId == oldEventId)).Should().BeFalse();

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
            run.PolicyJson.Should().Contain("timeWindowDays");
            run.PolicyJson.Should().Contain("runVacuumAnalyzeAfterRetention");
            run.ResultJson.Should().Contain("deletedHits");
            run.ResultJson.Should().Contain("upsertedEventSummaryRows");
            run.ResultJson.Should().Contain("upsertedHitSummaryRows");
            run.ResultJson.Should().Contain("otherTableRetention");
            run.ResultJson.Should().Contain("hitsWindowStartUtc");
            run.ResultJson.Should().Contain("eventsWindowStartUtc");
            run.ResultJson.Should().Contain("processedHitsWindows");
            run.ResultJson.Should().Contain("processedEventsWindows");
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
                MaxDurationMinutes: 30,
                RunVacuumAnalyzeAfterRetention: false));

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<RetrievalTelemetryRetentionRunResult>();
        result.Should().NotBeNull();

        using var scope = environment.GetFactory().Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var run = await dbContext.MaintenanceRuns.SingleAsync(x => x.Id == result!.RunId);
        using var policyDocument = JsonDocument.Parse(run.PolicyJson);
        var policy = policyDocument.RootElement;
        policy.GetProperty("batchSize").GetInt32().Should().Be(2);
        policy.GetProperty("eventBatchSize").GetInt32().Should().Be(1);
        policy.GetProperty("timeWindowDays").GetInt32().Should().Be(1);
        policy.GetProperty("delayBetweenBatchesMs").GetInt32().Should().Be(0);
        policy.GetProperty("commandTimeoutSeconds").GetInt32().Should().Be(30);
        policy.GetProperty("maxDurationMinutes").GetInt32().Should().Be(30);
        policy.GetProperty("runVacuumAnalyzeAfterRetention").GetBoolean().Should().BeFalse();
        policy.GetProperty("summaryRetentionDays").GetInt32().Should().Be(30);
        policy.GetProperty("securityAuditRetentionDays").GetInt32().Should().Be(180);
        policy.GetProperty("runtimeLogRetentionDays").GetInt32().Should().Be(30);
        policy.GetProperty("maintenanceRunRetentionDays").GetInt32().Should().Be(180);

        using var resultDocument = JsonDocument.Parse(run.ResultJson);
        resultDocument.RootElement.GetProperty("vacuumAnalyzeRequested").GetBoolean().Should().BeFalse();
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

    private static RetrievalHit CreateRetentionHit(Guid retrievalEventId, string title)
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
            ProjectId = ProjectContext.DefaultProjectId
        };
}
