using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Memory.Application;
using Memory.Dashboard.Services;
using Memory.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Memory.DashboardTests;

public sealed class DashboardUiTests : IClassFixture<DashboardApplicationFactory>
{
    private readonly DashboardApplicationFactory _factory;

    public DashboardUiTests(DashboardApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Login_Page_Should_Render_NginxUi_Style_Shell()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        using var response = await client.GetAsync("/login");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("Context Hub");
        html.Should().Contain("login-card");
        html.Should().Contain("登入");
        html.Should().Contain("favicon.svg");
        html.Should().Contain("dashboard-viewport.js");
    }

    [Fact]
    public async Task Login_Page_Static_Assets_Should_Be_Served_With_NonHtml_Content()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        using var loginResponse = await client.GetAsync("/login");
        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await loginResponse.Content.ReadAsStringAsync();

        var cssPath = ExtractAssetPath(html, "<link rel=\"stylesheet\" href=\"([^\"]*app[^\"]*\\.css)\"");
        var blazorScriptPath = ExtractAssetPath(html, "<script src=\"([^\"]*blazor\\.web[^\"]*\\.js)\"");
        var viewportScriptPath = ExtractAssetPath(html, "<script type=\"module\" src=\"([^\"]*dashboard-viewport[^\"]*\\.js)\"");

        using var cssResponse = await client.GetAsync(cssPath);
        cssResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        cssResponse.Content.Headers.ContentType?.MediaType.Should().Be("text/css");
        (await cssResponse.Content.ReadAsStringAsync()).Should().NotBeNullOrWhiteSpace();

        using var blazorScriptResponse = await client.GetAsync(blazorScriptPath);
        blazorScriptResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        blazorScriptResponse.Content.Headers.ContentType?.MediaType.Should().Contain("javascript");
        (await blazorScriptResponse.Content.ReadAsStringAsync()).Should().NotStartWith("<!DOCTYPE html>", "framework script should not fall back to an HTML error page");

        using var viewportScriptResponse = await client.GetAsync(viewportScriptPath);
        viewportScriptResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        viewportScriptResponse.Content.Headers.ContentType?.MediaType.Should().Contain("javascript");
        (await viewportScriptResponse.Content.ReadAsStringAsync()).Should().NotStartWith("<!DOCTYPE html>", "dashboard module script should not fall back to an HTML error page");
    }

    [Fact]
    public async Task Anonymous_User_Should_Be_Redirected_To_Login()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        using var response = await client.GetAsync("/");
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.ToString().Should().StartWith("/login?returnUrl=");
    }

    [Fact]
    public async Task Anonymous_Blazor_Transport_Should_Not_Be_Redirected_To_Login()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        using var response = await client.PostAsync("/_blazor/negotiate?negotiateVersion=1", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
    }

    [Fact]
    public async Task Successful_Login_Should_Render_Dashboard_Pages_With_Internal_Scroll_Hosts()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        await LoginAsync(client);

        using var overviewResponse = await client.GetAsync("/");
        overviewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var overviewHtml = WebUtility.HtmlDecode(await overviewResponse.Content.ReadAsStringAsync());
        overviewHtml.Should().Contain("ContextHub 管理主控台");
        overviewHtml.Should().Contain("全部記憶條目");
        overviewHtml.Should().Contain("預設專案記憶");
        overviewHtml.Should().Contain("Docker 主機");
        overviewHtml.Should().Contain("資源狀態圖表");
        overviewHtml.Should().Contain("近期呼叫趨勢");
        overviewHtml.Should().Contain("近期平均");
        overviewHtml.Should().Contain("每 3 秒刷新");
        overviewHtml.Should().Contain("資源最近");
        overviewHtml.Should().Contain("呼叫最近 15 筆");
        overviewHtml.Should().Contain("進站 (Inbound)");
        overviewHtml.Should().Contain("傳出 (Outbound)");
        overviewHtml.Should().Contain("GMT");
        overviewHtml.Should().Contain("建置版本");
        overviewHtml.Should().Contain("2026.04.12-test");
        overviewHtml.Should().Contain("複製 JSON");
        overviewHtml.Should().Contain("Overview page sample error 4");
        overviewHtml.Should().Contain("Overview page sample error 3");
        overviewHtml.Should().Contain("Overview page sample error 2");
        overviewHtml.Should().NotContain("Overview page sample error 1");
        overviewHtml.Should().Contain("\"job\":\"reindex-4\"");
        overviewHtml.Should().Contain("\"job\":\"reindex-3\"");
        overviewHtml.Should().Contain("\"job\":\"reindex-2\"");
        overviewHtml.Should().NotContain("\"job\":\"reindex-1\"");
        overviewHtml.Should().Contain("最後更新");
        overviewHtml.Should().Contain("refresh-status-group");
        overviewHtml.Should().Contain("refresh-status-primary");
        overviewHtml.Should().Contain("refresh-status-live");
        overviewHtml.Should().NotContain("refresh-status-build");
        overviewHtml.Should().Contain("page-scroll-host");
        overviewHtml.IndexOf("sidebar-footer", StringComparison.Ordinal).Should().BeGreaterThan(0);
        overviewHtml.IndexOf("sidebar-build", StringComparison.Ordinal).Should().BeGreaterThan(0);
        overviewHtml.IndexOf("sidebar-footer", StringComparison.Ordinal).Should().BeLessThan(overviewHtml.IndexOf("sidebar-build", StringComparison.Ordinal));

        using var runtimeResponse = await client.GetAsync("/runtime");
        runtimeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var runtimeHtml = WebUtility.HtmlDecode(await runtimeResponse.Content.ReadAsStringAsync());
        runtimeHtml.Should().Contain("執行參數");
        runtimeHtml.Should().Contain("refresh-status-group");
        runtimeHtml.Should().Contain("refresh-status-live");
        runtimeHtml.Should().NotContain("refresh-status-build");
        runtimeHtml.Should().Contain("公開參數");
        runtimeHtml.Should().Contain("建置版本");
        runtimeHtml.Should().Contain("GMT");
        runtimeHtml.Should().Contain("2026.04.12-test");
        runtimeHtml.Should().Contain("runtime-page-stack");
        runtimeHtml.Should().Contain("向量執行環境");
        runtimeHtml.Should().NotContain("記憶資料匯入匯出");
        runtimeHtml.Should().NotContain("資料匯出 / 匯入");
        runtimeHtml.Should().NotContain("Docker 主機");
        runtimeHtml.Should().NotContain("依賴資源概況");
        runtimeHtml.Should().NotContain("依賴健康");

        using var monitoringResponse = await client.GetAsync("/monitoring");
        monitoringResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var monitoringHtml = WebUtility.HtmlDecode(await monitoringResponse.Content.ReadAsStringAsync());
        monitoringHtml.Should().Contain("狀態監控");
        monitoringHtml.Should().Contain("refresh-status-group");
        monitoringHtml.Should().Contain("Redis");
        monitoringHtml.Should().Contain("PostgreSQL");
        monitoringHtml.Should().Contain("資源趨勢");
        monitoringHtml.Should().Contain("Compose 服務資源");
        monitoringHtml.Should().Contain("Docker 主機");
        monitoringHtml.Should().Contain("Total Commands");
        monitoringHtml.Should().Contain("Connections");
        monitoringHtml.Should().Contain("monitoring-page-stack");
        monitoringHtml.Should().Contain("monitoring-telemetry-grid");
        monitoringHtml.Should().NotContain("未配置 Redis 專屬 volume");
        monitoringHtml.Should().NotContain("未偵測 PostgreSQL 專屬 volume");

        using var jobsResponse = await client.GetAsync("/jobs");
        jobsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var jobsHtml = WebUtility.HtmlDecode(await jobsResponse.Content.ReadAsStringAsync());
        jobsHtml.Should().Contain("工作細節");
        jobsHtml.Should().Contain("複製 JSON");

        using var logsResponse = await client.GetAsync("/logs");
        logsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var logsHtml = WebUtility.HtmlDecode(await logsResponse.Content.ReadAsStringAsync());
        logsHtml.Should().Contain("日誌");
        logsHtml.Should().Contain("logs-filter-grid");
        logsHtml.Should().Contain("filter-multiselect");
        logsHtml.Should().Contain("全部服務");
        logsHtml.Should().Contain("全部層級");
        logsHtml.Should().Contain("追蹤 Id");
        logsHtml.Should().Contain("日誌細節");

        using var performanceResponse = await client.GetAsync("/performance");
        performanceResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var performanceHtml = WebUtility.HtmlDecode(await performanceResponse.Content.ReadAsStringAsync());
        performanceHtml.Should().Contain("效能");
        performanceHtml.Should().Contain("開始量測");
        performanceHtml.Should().Contain("performance-results-shell");
        performanceHtml.Should().Contain("page-scroll-host");
        performanceHtml.Should().Contain("尚未執行量測，填好條件後點選開始量測。");

        using var settingsResponse = await client.GetAsync("/settings");
        settingsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var settingsHtml = WebUtility.HtmlDecode(await settingsResponse.Content.ReadAsStringAsync());
        settingsHtml.Should().Contain("系統設定");
        settingsHtml.Should().Contain("Instance 基本資訊");
        settingsHtml.Should().Contain("應用行為設定");
        settingsHtml.Should().Contain("整理與 ingestion");
        settingsHtml.Should().Contain("預設查詢");
        settingsHtml.Should().Contain("Dashboard 登入設定");
        settingsHtml.Should().Contain("維運操作");
        settingsHtml.Should().Contain("settings-layout");
        settingsHtml.Should().Contain("settings-form-grid");
        settingsHtml.Should().Contain("Snapshot Cadence");
        settingsHtml.Should().Contain("核心狀態");
        settingsHtml.Should().Contain("圖表與即時資料");
        settingsHtml.Should().Contain("近期維運摘要");
        settingsHtml.Should().Contain("Legacy Page Polling");
        settingsHtml.Should().Contain("資料匯出 / 匯入");
        settingsHtml.Should().Contain("匯出所選項目");
        settingsHtml.Should().Contain("預覽匯入");
        settingsHtml.Should().Contain("系統設定");
        settingsHtml.Should().Contain("記憶資料");
        settingsHtml.Should().Contain("使用者偏好");
        settingsHtml.Should().Contain("重啟 app 容器");

        using var memoriesResponse = await client.GetAsync("/memories");
        memoriesResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var memoriesHtml = WebUtility.HtmlDecode(await memoriesResponse.Content.ReadAsStringAsync());
        memoriesHtml.Should().Contain("記憶資料");
        memoriesHtml.Should().Contain("示範記憶");
        memoriesHtml.Should().Contain("全部範圍");
        memoriesHtml.Should().Contain("事實");
        memoriesHtml.Should().Contain("最後更新");
        memoriesHtml.Should().Contain("查看共用綜合層");
        memoriesHtml.Should().Contain("重建共用綜合層");
        memoriesHtml.Should().Contain("共用綜合層");
        memoriesHtml.Should().Contain("table-head-secondary");

        using var storageResponse = await client.GetAsync("/storage");
        storageResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var storageHtml = WebUtility.HtmlDecode(await storageResponse.Content.ReadAsStringAsync());
        storageHtml.Should().Contain("資料庫檢視");
        storageHtml.Should().Contain("memory_items");
        storageHtml.Should().Contain("關鍵字查詢");
        storageHtml.Should().Contain("所有可搜尋欄位");
        storageHtml.Should().Contain("可搜尋欄位");
        storageHtml.Should().Contain("storage-table-list");
        storageHtml.Should().Contain("storage-query-panel");
        storageHtml.Should().Contain("storage-info-panel");
        storageHtml.Should().Contain("storage-inspector-panel");
        storageHtml.Should().Contain("table-scroll-shell");

        using var preferencesResponse = await client.GetAsync("/preferences");
        preferencesResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var preferencesHtml = WebUtility.HtmlDecode(await preferencesResponse.Content.ReadAsStringAsync());
        preferencesHtml.Should().Contain("使用者偏好");
        preferencesHtml.Should().Contain("preferred-language");
        preferencesHtml.Should().Contain("回覆預設使用繁體中文。");
        preferencesHtml.Should().Contain("溝通風格 (1)");
        preferencesHtml.Should().Contain("stack-scroll-shell");
        preferencesHtml.Should().Contain("stack-item-split");
    }

    [Fact]
    public async Task Authenticated_Html_Pages_Should_Disable_Response_Caching()
    {
        using var isolatedFactory = new DashboardApplicationFactory();
        using var client = isolatedFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        await LoginAsync(client);

        using var response = await client.GetAsync("/");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.CacheControl.NoCache.Should().BeTrue();
        response.Headers.TryGetValues("Pragma", out var pragmaValues).Should().BeTrue();
        pragmaValues.Should().Contain("no-cache");
    }

    [Fact]
    public async Task Memories_Page_Should_Not_Prefill_ProjectId_Filter_And_Should_Query_Without_Default_Project()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        await LoginAsync(client);

        using var response = await client.GetAsync("/memories");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        html.Should().Contain("目前專案 (Project Id，可模糊搜尋)");
        html.Should().NotContain($"value=\"{ProjectContext.DefaultProjectId}\"");

        var apiClient = _factory.Services.GetRequiredService<IContextHubApiClient>().Should().BeOfType<FakeContextHubApiClient>().Subject;
        apiClient.LastMemoryListRequest.Should().NotBeNull();
        apiClient.LastMemoryListRequest!.ProjectId.Should().BeNull();
    }

    [Fact]
    public void Current_Project_Resolver_Should_Fallback_To_ContextHub_When_Runtime_Default_Is_Default()
    {
        DashboardProjectSelection.ResolveCurrentProjectId(ProjectContext.DefaultProjectId)
            .Should()
            .Be(DashboardProjectSelection.CurrentRepositoryProjectId);

        DashboardProjectSelection.ResolveCurrentProjectId("  custom-project  ")
            .Should()
            .Be("custom-project");

        DashboardProjectSelection.ResolveCurrentProjectId(null)
            .Should()
            .Be(DashboardProjectSelection.CurrentRepositoryProjectId);
    }

    [Fact]
    public void LogClipboardFormatter_Should_Output_Indented_Json_With_Structured_Payload()
    {
        var log = new LogEntryResult(
            42,
            "mcp-server",
            "HealthChecks",
            "Error",
            "Embedding health check failed",
            "System.Net.Http.HttpRequestException: Connection refused",
            "trace-42",
            "request-42",
            "{\"host\":\"embedding-service\",\"port\":8081}",
            DateTimeOffset.Parse("2026-04-11T08:15:00+00:00"));

        var json = LogClipboardFormatter.Format(log);
        using var document = JsonDocument.Parse(json);

        json.Should().Contain(Environment.NewLine);
        document.RootElement.GetProperty("id").GetInt64().Should().Be(42);
        document.RootElement.GetProperty("serviceName").GetString().Should().Be("mcp-server");
        document.RootElement.GetProperty("exception").GetString().Should().Contain("Connection refused");
        document.RootElement.GetProperty("payload").GetProperty("host").GetString().Should().Be("embedding-service");
        document.RootElement.GetProperty("payload").GetProperty("port").GetInt32().Should().Be(8081);
    }

    [Fact]
    public async Task Settings_Api_Should_Return_Snapshot_And_Restart_Result()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        await LoginAsync(client);

        var snapshot = await client.GetFromJsonAsync<InstanceSettingsSnapshot>("/api/settings/instance");
        snapshot.Should().NotBeNull();
        snapshot!.InstanceId.Should().Be("dashboard-test-instance");
        snapshot.DashboardAuth.AdminUsername.Should().Be("admin");
        snapshot.Behavior.DefaultProjectId.Should().Be(ProjectContext.DefaultProjectId);

        using var updateResponse = await client.PutAsJsonAsync("/api/settings/instance", new InstanceSettingsUpdateRequest(
            new InstanceBehaviorSettingsUpdateRequest(
                true,
                true,
                true,
                25,
                "Automatic",
                256,
                ProjectContext.DefaultProjectId,
                MemoryQueryMode.CurrentOnly,
                false,
                true,
                new DashboardSnapshotPollingSettingsUpdateRequest(
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
                30),
            new InstanceDashboardAuthUpdateRequest(
                "ops-admin",
                null,
                null,
                600)));

        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await updateResponse.Content.ReadFromJsonAsync<InstanceSettingsSnapshot>();
        updated.Should().NotBeNull();
        updated!.DashboardAuth.AdminUsername.Should().Be("ops-admin");

        using var restartResponse = await client.PostAsJsonAsync("/api/settings/restart-app", new RestartAppContainersRequest());
        restartResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var restart = await restartResponse.Content.ReadFromJsonAsync<RestartAppContainersResult>();
        restart.Should().NotBeNull();
        restart!.RestartedServices.Should().Contain("dashboard");
        restart.RestartedServices.Should().NotContain("postgres");
    }

    [Fact]
    public async Task Instance_Transfer_Service_Should_Export_And_Preview_Selected_Sections()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IInstanceTransferService>();

        var export = await service.ExportAsync(
            new InstanceTransferExportRequest(
                [InstanceTransferSection.SystemSettings, InstanceTransferSection.Memories, InstanceTransferSection.UserPreferences],
                "secret-passphrase"),
            CancellationToken.None);

        export.Encrypted.Should().BeTrue();
        export.Sections.Should().HaveCount(3);
        export.Sections.Select(section => section.Section).Should().BeEquivalentTo(
            [InstanceTransferSection.SystemSettings, InstanceTransferSection.Memories, InstanceTransferSection.UserPreferences]);

        var preview = await service.PreviewImportAsync(
            new InstanceTransferImportRequest(
                export.PayloadBase64,
                [InstanceTransferSection.SystemSettings, InstanceTransferSection.Memories, InstanceTransferSection.UserPreferences],
                "secret-passphrase"),
            CancellationToken.None);

        preview.Encrypted.Should().BeTrue();
        preview.Sections.Should().HaveCount(3);
        preview.Conflicts.Should().Contain(conflict => conflict.Section == InstanceTransferSection.SystemSettings);
        preview.Conflicts.Should().Contain(conflict => conflict.Section == InstanceTransferSection.Memories);
        preview.Conflicts.Should().Contain(conflict => conflict.Section == InstanceTransferSection.UserPreferences);
    }

    private static async Task LoginAsync(HttpClient client)
    {
        using var loginPage = await client.GetAsync("/login");
        loginPage.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await loginPage.Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(html);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/account/login")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["Username"] = "admin",
                ["Password"] = "ContextHub!123",
                ["ReturnUrl"] = "/"
            })
        };
        request.Headers.Add("Cookie", BuildAntiforgeryCookie(loginPage.Headers));

        using var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.ToString().Should().Be("/");
    }

    private static string ExtractAntiforgeryToken(string html)
    {
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        match.Success.Should().BeTrue("login page should render an antiforgery token");
        return match.Groups[1].Value;
    }

    private static string ExtractAssetPath(string html, string pattern)
    {
        var match = Regex.Match(html, pattern);
        match.Success.Should().BeTrue($"expected asset path matching pattern '{pattern}'");
        return match.Groups[1].Value;
    }

    private static string BuildAntiforgeryCookie(HttpResponseHeaders headers)
    {
        var setCookie = headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(x => x.Contains(".AspNetCore.Antiforgery", StringComparison.OrdinalIgnoreCase))
            : null;
        setCookie.Should().NotBeNull();
        return setCookie!.Split(';', 2)[0];
    }
}

public sealed class DashboardApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Dashboard:BaseUrl"] = "http://fake-context-hub",
                ["ContextHub:InstanceId"] = "dashboard-test-instance",
                ["Dashboard:AdminUsername"] = "admin",
                ["Dashboard:AdminPasswordHash"] = "AQAAAAIAAYagAAAAEIbguUQEApMQehlC51gjy+uGulsE4ahRI7UtbdAlSsGMynNrNM3J3KfsJL+3IuBUxQ==",
                ["Dashboard:SessionTimeoutMinutes"] = "480",
                ["Dashboard:ComposeProject"] = "contexthub",
                ["Dashboard:DataProtectionPath"] = Path.Combine(Path.GetTempPath(), "contexthub-dashboard-tests", Guid.NewGuid().ToString("N")),
                ["Memory:Namespace"] = "context-hub-test",
                ["ConnectionStrings:Postgres"] = "Host=127.0.0.1;Port=5432;Database=contexthub;Username=contexthub;Password=contexthub"
            });
        });
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IContextHubApiClient>();
            services.RemoveAll<IDockerMetricsService>();
            services.RemoveAll<IInstanceSettingsService>();
            services.AddSingleton<IContextHubApiClient, FakeContextHubApiClient>();
            services.AddSingleton<IDockerMetricsService, FakeDockerMetricsService>();
            services.AddSingleton<IInstanceSettingsService, FakeInstanceSettingsService>();
        });
    }
}

internal sealed class FakeContextHubApiClient : IContextHubApiClient
{
    private readonly IReadOnlyList<UserPreferenceResult> _preferences =
    [
        new UserPreferenceResult(
            Guid.Parse("7f930e28-5bf3-4e1d-b851-ae9d28c3cc2f"),
            "preferred-language",
            UserPreferenceKind.CommunicationStyle,
            "偏好繁體中文",
            "回覆預設使用繁體中文。",
            "長期偏好",
            ["language", "style"],
            0.95m,
            0.95m,
            MemoryStatus.Active,
            DateTimeOffset.UtcNow.AddDays(-3),
            DateTimeOffset.UtcNow.AddHours(-5))
    ];

    private readonly MemoryDocument _memory = new(
        Guid.Parse("49e0d4e5-5189-4f33-85a9-bbef596e6f9d"),
        "demo-memory",
        MemoryScope.Project,
        MemoryType.Fact,
        "示範記憶",
        "這是一筆提供給 dashboard UI 測試的示範記憶內容。",
        "示範記憶摘要",
        "document",
        "tests",
        ["demo", "dashboard"],
        0.8m,
        0.9m,
        2,
        MemoryStatus.Active,
        "{\"kind\":\"demo\"}",
        DateTimeOffset.UtcNow.AddDays(-1),
        DateTimeOffset.UtcNow);

    public MemoryListRequest? LastMemoryListRequest { get; private set; }

    public Task<SystemStatusResult> GetStatusAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return Task.FromResult(new SystemStatusResult(
            "mcp-server",
            "test",
            "2026.04.12-test",
            DateTimeOffset.Parse("2026-04-12T00:30:00+00:00"),
            "Http",
            "CPUExecutionProvider",
            "compact",
            "intfloat/multilingual-e5-small",
            384,
            512,
            6,
            8,
            true,
            12,
            now,
            now.AddSeconds(-1),
            3,
            false,
            string.Empty,
            string.Empty));
    }

    public Task<DashboardOverviewResult> GetOverviewAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var traffic = BuildTrafficSamples();
        return Task.FromResult(new DashboardOverviewResult(
            "test",
            "2026.04.12-test",
            DateTimeOffset.Parse("2026-04-12T00:30:00+00:00"),
            "compact",
            "intfloat/multilingual-e5-small",
            384,
            512,
            12,
            [
                new DashboardServiceHealthResult("postgres", "Healthy", ""),
                new DashboardServiceHealthResult("redis", "Healthy", ""),
                new DashboardServiceHealthResult("embeddings", "Healthy", "")
            ],
            [
                new DashboardOverviewMetricResult("memoryItems", "全部記憶條目", 24, "items"),
                new DashboardOverviewMetricResult("defaultProjectMemoryItems", "預設專案記憶", 4, "items"),
                new DashboardOverviewMetricResult("userPreferences", "使用者偏好", 3, "items"),
                new DashboardOverviewMetricResult("activeJobs", "背景工作", 4, "jobs"),
                new DashboardOverviewMetricResult("errorLogs", "錯誤日誌", 4, "logs")
            ],
            traffic,
            BuildOverviewJobs(),
            BuildOverviewErrors(),
            now,
            BuildPageSnapshotStatus(now),
            BuildDockerHost(now),
            BuildDependencyResources(),
            BuildResourceSamples(traffic)));
    }

    private static IReadOnlyList<RequestTrafficSampleResult> BuildTrafficSamples()
        => Enumerable.Range(0, 15)
            .Select(index => new RequestTrafficSampleResult(
                DateTimeOffset.UtcNow.AddSeconds(index - 14),
                index % 4 + 1,
                index % 3 + 1))
            .ToArray();

    private static IReadOnlyList<JobListItemResult> BuildOverviewJobs()
        => Enumerable.Range(1, 4)
            .Select(index => new JobListItemResult(
                Guid.Parse($"00000000-0000-0000-0000-{index:000000000000}"),
                MemoryJobType.Reindex,
                index % 2 == 0 ? MemoryJobStatus.Running : MemoryJobStatus.Pending,
                $$"""{"job":"reindex-{{index}}","modelKey":"intfloat/multilingual-e5-small"}""",
                string.Empty,
                DateTimeOffset.UtcNow.AddMinutes(-10 + index),
                DateTimeOffset.UtcNow.AddMinutes(-9 + index),
                null))
            .ToArray();

    private static IReadOnlyList<LogEntryResult> BuildOverviewErrors()
        => Enumerable.Range(1, 4)
            .Select(index => new LogEntryResult(
                index,
                "mcp-server",
                "Tests",
                "Error",
                $"Overview page sample error {index}",
                string.Empty,
                $"trace-{index}",
                $"request-{index}",
                "{}",
                DateTimeOffset.UtcNow.AddMinutes(-10 + index)))
            .ToArray();

    public Task<DashboardRuntimeResult> GetRuntimeAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return Task.FromResult(new DashboardRuntimeResult(
            "test",
            "2026.04.12-test",
            DateTimeOffset.Parse("2026-04-12T00:30:00+00:00"),
            "Http",
            "CPUExecutionProvider",
            "compact",
            "intfloat/multilingual-e5-small",
            384,
            512,
            6,
            8,
            true,
            [
                new DashboardServiceHealthResult("postgres", "Healthy", ""),
                new DashboardServiceHealthResult("redis", "Healthy", ""),
                new DashboardServiceHealthResult("embeddings", "Healthy", "")
            ],
            [
                new DashboardRuntimeParameterResult("Embeddings", "Profile", "compact", false),
                new DashboardRuntimeParameterResult("Embeddings", "Dimensions", "384", false),
                new DashboardRuntimeParameterResult("Embeddings", "Execution Provider", "CPUExecutionProvider", false),
                new DashboardRuntimeParameterResult("Embeddings", "Batch Size", "8", false),
                new DashboardRuntimeParameterResult("Embeddings", "Batching Enabled", "true", false)
            ],
            now,
            BuildPageSnapshotStatus(now),
            BuildDockerHost(now),
            BuildDependencyResources()));
    }

    public Task<DashboardMonitoringResult> GetMonitoringAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var traffic = BuildTrafficSamples();
        return Task.FromResult(new DashboardMonitoringResult(
            "test",
            "2026.04.12-test",
            DateTimeOffset.Parse("2026-04-12T00:30:00+00:00"),
            [
                new DashboardServiceHealthResult("postgres", "Healthy", ""),
                new DashboardServiceHealthResult("redis", "Healthy", ""),
                new DashboardServiceHealthResult("embeddings", "Healthy", "")
            ],
            now,
            BuildRedisTelemetry(),
            BuildPostgresTelemetry(),
            BuildPageSnapshotStatus(now),
            BuildDockerHost(now),
            BuildDependencyResources(),
            BuildResourceSamples(traffic)));
    }

    private static DashboardPageSnapshotStatusResult BuildPageSnapshotStatus(DateTimeOffset snapshotAtUtc)
        => new(
            snapshotAtUtc,
            false,
            string.Empty,
            [
                new DashboardSnapshotSectionStatusResult("statusCore", "核心狀態", snapshotAtUtc, 30, false, string.Empty, string.Empty),
                new DashboardSnapshotSectionStatusResult("dependencyResources", "Compose 服務資源", snapshotAtUtc, 5, false, string.Empty, string.Empty)
            ]);

    private static DashboardDockerHostResult BuildDockerHost(DateTimeOffset capturedAtUtc)
        => new(
            "Healthy",
            string.Empty,
            new Memory.Application.DockerHostSummaryResult(
                "docker-host",
                "28.1",
                "Docker Desktop",
                "linux",
                8,
                8L * 1024 * 1024 * 1024,
                5L * 1024 * 1024 * 1024,
                5,
                3,
                2,
                capturedAtUtc));

    private static DashboardDependencyResourcesResult BuildDependencyResources()
        => new(
            "Healthy",
            string.Empty,
            [
                new Memory.Application.DockerContainerMetricResult("contexthub-postgres-1", "postgres", "pgvector/pgvector:pg17", "running", "healthy", 0, 0.8, 1536L * 1024 * 1024, 4096L * 1024 * 1024, 24_000, 22_000, 18_000, 12_000),
                new Memory.Application.DockerContainerMetricResult("contexthub-redis-1", "redis", "redis:7.4-alpine", "running", "healthy", 1, 0.3, 192L * 1024 * 1024, 1024L * 1024 * 1024, 9_000, 8_500, 1_200, 900),
                new Memory.Application.DockerContainerMetricResult("contexthub-embedding-service-1", "embedding-service", "context-hub/embedding-service:local", "running", "healthy", 0, 3.2, 1024L * 1024 * 1024, 4096L * 1024 * 1024, 15_000, 13_500, 6_000, 4_800),
                new Memory.Application.DockerContainerMetricResult("contexthub-mcp-server-1", "mcp-server", "context-hub/mcp", "running", "healthy", 0, 1.2, 512L * 1024 * 1024, 1024L * 1024 * 1024, 12_000, 16_000, 4_000, 3_500)
            ],
            [
                new Memory.Application.DockerVolumeSummaryResult("contexthub_postgres-data", "local", 1024L * 1024 * 1024, "/var/lib/docker/volumes/contexthub_postgres-data"),
                new Memory.Application.DockerVolumeSummaryResult("contexthub_redis-data", "local", 256L * 1024 * 1024, "/var/lib/docker/volumes/contexthub_redis-data")
            ]);

    private static IReadOnlyList<DashboardResourceSampleResult> BuildResourceSamples(IReadOnlyList<RequestTrafficSampleResult> trafficSamples)
        => trafficSamples
            .Select((sample, index) => new DashboardResourceSampleResult(
                sample.TimestampUtc,
                24 + (index % 4 * 6),
                32 + (index % 3 * 9),
                (640L + (index * 32L)) * 1024 * 1024,
                30_000 + (index * 900),
                26_000 + (index * 800),
                8_000 + (index * 220),
                7_000 + (index * 180),
                sample.InboundRequests,
                sample.OutboundRequests))
            .ToArray();

    private static DashboardRedisTelemetryResult BuildRedisTelemetry()
        => new(
            "Healthy",
            string.Empty,
            196L * 1024 * 1024,
            256L * 1024 * 1024,
            96,
            42_000,
            16L * 1024 * 1024,
            14L * 1024 * 1024,
            8.6,
            7.4,
            12,
            0,
            9_000,
            8_500,
            1_200,
            900,
            256L * 1024 * 1024,
            "contexthub_redis-data");

    private static DashboardPostgresTelemetryResult BuildPostgresTelemetry()
        => new(
            "Healthy",
            string.Empty,
            4,
            42_000,
            2,
            24_000,
            420_000,
            180_000,
            24_000,
            640,
            320,
            42,
            42L * 1024 * 1024,
            0,
            24_000,
            22_000,
            18_000,
            12_000,
            0,
            1024L * 1024 * 1024,
            "contexthub_postgres-data",
            96L * 1024 * 1024);

    public Task<PagedResult<MemoryListItemResult>> GetMemoriesAsync(MemoryListRequest request, CancellationToken cancellationToken)
    {
        LastMemoryListRequest = request;
        return Task.FromResult(new PagedResult<MemoryListItemResult>(
        [
            new MemoryListItemResult(_memory.Id, _memory.ProjectId, _memory.ExternalKey, _memory.Scope, _memory.MemoryType, _memory.Title, _memory.Summary, _memory.SourceType, _memory.SourceRef, _memory.Tags, _memory.Importance, _memory.Confidence, _memory.Version, _memory.Status, _memory.UpdatedAt, _memory.IsReadOnly)
        ],
        1,
        25,
        1));
    }

    public Task<IReadOnlyList<ProjectSuggestionResult>> GetMemoryProjectsAsync(string? query, int limit, CancellationToken cancellationToken)
    {
        IReadOnlyList<ProjectSuggestionResult> projects =
        [
            new ProjectSuggestionResult("ContextHub", 12),
            new ProjectSuggestionResult("Vital_AirMeet_Document", 8),
            new ProjectSuggestionResult("Other_Project", 3)
        ];

        if (!string.IsNullOrWhiteSpace(query))
        {
            projects = projects.Where(project => project.ProjectId.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        return Task.FromResult<IReadOnlyList<ProjectSuggestionResult>>(projects.Take(limit).ToArray());
    }

    public Task<MemoryDetailsResult?> GetMemoryDetailsAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult<MemoryDetailsResult?>(new MemoryDetailsResult(
            _memory,
            [
                new MemoryRevisionResult(Guid.NewGuid(), 2, "示範記憶", "示範記憶摘要", "update", DateTimeOffset.UtcNow.AddHours(-4))
            ],
            [
                new MemoryChunkResult(Guid.NewGuid(), ChunkKind.Document, 0, "這是一個示範 chunk。", "{}", DateTimeOffset.UtcNow.AddHours(-4), [
                    new MemoryVectorResult(Guid.NewGuid(), "intfloat/multilingual-e5-small", 384, "Active", DateTimeOffset.UtcNow.AddHours(-4))
                ])
            ]));

    public Task<MemoryTransferDownloadResult> ExportMemoriesAsync(MemoryExportRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new MemoryTransferDownloadResult("demo-export.json", "application/json", Convert.ToBase64String("{}"u8.ToArray()), 1, !string.IsNullOrWhiteSpace(request.Passphrase)));

    public Task<MemoryImportPreviewResult> PreviewMemoryImportAsync(MemoryImportRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new MemoryImportPreviewResult("test", 1, 0, 1, false, false, [
            new MemoryImportConflictResult(_memory.ProjectId, "demo-memory", _memory.Id, _memory.Title, _memory.Title, _memory.UpdatedAt)
        ]));

    public Task<MemoryImportApplyResult> ApplyMemoryImportAsync(MemoryImportRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new MemoryImportApplyResult(1, request.ForceOverwrite ? 1 : 0, [_memory.Id]));

    public Task<IReadOnlyList<UserPreferenceResult>> GetPreferencesAsync(UserPreferenceKind? kind, bool includeArchived, int limit, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<UserPreferenceResult>>(_preferences);

    public Task<UserPreferenceResult> UpsertPreferenceAsync(UserPreferenceUpsertRequest request, CancellationToken cancellationToken)
        => Task.FromResult(_preferences[0]);

    public Task<UserPreferenceResult> ArchivePreferenceAsync(Guid id, bool archived, CancellationToken cancellationToken)
        => Task.FromResult(_preferences[0] with { Status = archived ? MemoryStatus.Archived : MemoryStatus.Active });

    public Task<IReadOnlyList<LogEntryResult>> SearchLogsAsync(LogQueryRequest request, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<LogEntryResult>>(
        [
            new LogEntryResult(10, "mcp-server", "Tests", "Error", "示範 log", "System.Exception: demo", "trace-1", "request-1", "{\"kind\":\"demo\"}", DateTimeOffset.UtcNow.AddMinutes(-2))
        ]);

    public Task<LogEntryResult?> GetLogAsync(long id, CancellationToken cancellationToken)
        => Task.FromResult<LogEntryResult?>(new LogEntryResult(id, "mcp-server", "Tests", "Error", "示範 log", "System.Exception: demo", "trace-1", "request-1", "{\"kind\":\"demo\"}", DateTimeOffset.UtcNow.AddMinutes(-2)));

    public Task<PagedResult<JobListItemResult>> GetJobsAsync(JobListRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new PagedResult<JobListItemResult>(
            [
                new JobListItemResult(Guid.NewGuid(), MemoryJobType.Reindex, MemoryJobStatus.Running, "{\"modelKey\":\"intfloat/multilingual-e5-small\"}", "", DateTimeOffset.UtcNow.AddMinutes(-4), DateTimeOffset.UtcNow.AddMinutes(-3), null)
            ],
            1,
            25,
            1));

    public Task<EnqueueReindexResult> EnqueueReindexAsync(EnqueueReindexRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new EnqueueReindexResult(Guid.NewGuid(), MemoryJobStatus.Pending));

    public Task<EnqueueSummaryRefreshResult> EnqueueSummaryRefreshAsync(EnqueueSummaryRefreshRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new EnqueueSummaryRefreshResult(Guid.NewGuid(), MemoryJobStatus.Pending));

    public Task<IReadOnlyList<StorageTableSummaryResult>> GetStorageTablesAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<StorageTableSummaryResult>>(
        [
            new StorageTableSummaryResult("memory_items", "記憶主體與 metadata", 24, ["id", "title", "content", "summary"]),
            new StorageTableSummaryResult("runtime_log_entries", "DB-first runtime logs", 4, ["id", "service_name", "message"])
        ]);

    public Task<StorageTableRowsResult> GetStorageRowsAsync(StorageRowsRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new StorageTableRowsResult(
            request.Table,
            "記憶主體與 metadata",
            ["id", "title", "content", "summary"],
            ["title", "content", "summary"],
            request.Query,
            request.Column,
            new PagedResult<StorageRowResult>(
                [
                    new StorageRowResult(new Dictionary<string, string?>
                    {
                        ["id"] = _memory.Id.ToString(),
                        ["title"] = _memory.Title,
                        ["content"] = _memory.Content,
                        ["summary"] = _memory.Summary
                    })
                ],
                request.Page,
                request.PageSize,
                1)));

    public Task<PerformanceMeasureResult> MeasurePerformanceAsync(PerformanceMeasureRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new PerformanceMeasureResult(
            "Http",
            "compact",
            "intfloat/multilingual-e5-small",
            384,
            request.SearchLimit,
            request.IncludeArchived,
            request.WarmupIterations,
            request.MeasurementIterations,
            2,
            42,
            1,
            1,
            1,
            request.MeasurementMode,
            request.MeasurementDurationSeconds,
            request.MaxMeasurementIterations,
            request.MeasurementMode == PerformanceMeasurementMode.Duration
                ? request.MeasurementDurationSeconds * 1000
                : request.MeasurementIterations * 6,
            new PerformanceMetricResult("ms", request.MeasurementIterations, 1, 1, 1, 1, 1),
            new PerformanceMetricResult("ms", request.MeasurementIterations, 2, 2, 2, 2, 1),
            new PerformanceMetricResult("ms", request.MeasurementIterations, 3, 3, 3, 3, 1),
            new PerformanceMetricResult("ms", request.MeasurementIterations, 4, 4, 4, 4, 1),
            new PerformanceMetricResult("ms", request.MeasurementIterations, 5, 5, 5, 5, 1),
            new PerformanceMetricResult("ms", request.MeasurementIterations, 6, 6, 6, 6, 1),
            DateTimeOffset.UtcNow));
}

internal sealed class FakeDockerMetricsService : IDockerMetricsService
{
    public Task<DockerStackSnapshotResult> GetSnapshotAsync(CancellationToken cancellationToken)
        => Task.FromResult(new DockerStackSnapshotResult(
            "Healthy",
            string.Empty,
            new Memory.Dashboard.Services.DockerHostSummaryResult("docker-host", "28.1", "Docker Desktop", "linux", 8, 8L * 1024 * 1024 * 1024, 5L * 1024 * 1024 * 1024, 5, 3, 2, DateTimeOffset.UtcNow),
            [
                new Memory.Dashboard.Services.DockerContainerMetricResult("contexthub-postgres-1", "postgres", "pgvector/pgvector:pg17", "running", "healthy", 0, 0.8, 1536L * 1024 * 1024, 4096L * 1024 * 1024, 24_000, 22_000, 18_000, 12_000),
                new Memory.Dashboard.Services.DockerContainerMetricResult("contexthub-redis-1", "redis", "redis:7.4-alpine", "running", "healthy", 1, 0.3, 192L * 1024 * 1024, 1024L * 1024 * 1024, 9_000, 8_500, 1_200, 900),
                new Memory.Dashboard.Services.DockerContainerMetricResult("contexthub-embedding-service-1", "embedding-service", "context-hub/embedding-service:local", "running", "healthy", 0, 3.2, 1024L * 1024 * 1024, 4096L * 1024 * 1024, 15_000, 13_500, 6_000, 4_800),
                new Memory.Dashboard.Services.DockerContainerMetricResult("contexthub-mcp-server-1", "mcp-server", "context-hub/mcp", "running", "healthy", 0, 1.2, 512L * 1024 * 1024, 1024L * 1024 * 1024, 12_000, 16_000, 4_000, 3_500)
            ],
            [
                new DockerImageSummaryResult("image-1", "context-hub/mcp:local", 512L * 1024 * 1024, 1)
            ],
            [
                new Memory.Dashboard.Services.DockerVolumeSummaryResult("contexthub_postgres-data", "local", 1024L * 1024 * 1024, "/var/lib/docker/volumes/contexthub_postgres-data"),
                new Memory.Dashboard.Services.DockerVolumeSummaryResult("contexthub_redis-data", "local", 256L * 1024 * 1024, "/var/lib/docker/volumes/contexthub_redis-data")
            ]));

    public Task<RestartAppContainersResult> RestartAppContainersAsync(RestartAppContainersRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new RestartAppContainersResult(
            "dashboard-test-instance",
            "contexthub",
            ["dashboard", "mcp-server", "worker", "embedding-service"],
            [],
            DateTimeOffset.UtcNow));
}

internal sealed class FakeInstanceSettingsService(IOptions<DashboardOptions> dashboardOptionsAccessor) : IInstanceSettingsService
{
    private InstanceSettingsSnapshot _snapshot = CreateSnapshot(dashboardOptionsAccessor.Value, "admin");

    public Task<InstanceSettingsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        => Task.FromResult(_snapshot);

    public Task<InstanceSettingsSnapshot> UpdateAsync(InstanceSettingsUpdateRequest request, string updatedBy, CancellationToken cancellationToken)
    {
        _snapshot = _snapshot with
        {
            SettingsRevision = _snapshot.SettingsRevision + 1,
            SettingsUpdatedAtUtc = DateTimeOffset.UtcNow,
            Behavior = new InstanceBehaviorSettingsResult(
                request.Behavior.ConversationAutomationEnabled,
                request.Behavior.HostEventIngestionEnabled,
                request.Behavior.AgentSupplementalIngestionEnabled,
                request.Behavior.IdleThresholdMinutes,
                request.Behavior.PromotionMode,
                request.Behavior.ExcerptMaxLength,
                request.Behavior.DefaultProjectId,
                request.Behavior.DefaultQueryMode,
                request.Behavior.DefaultUseSummaryLayer,
                request.Behavior.SharedSummaryAutoRefreshEnabled,
                new DashboardSnapshotPollingSettingsResult(
                    request.Behavior.SnapshotPolling.StatusCoreSeconds,
                    request.Behavior.SnapshotPolling.EmbeddingRuntimeSeconds,
                    request.Behavior.SnapshotPolling.DependenciesHealthSeconds,
                    request.Behavior.SnapshotPolling.DockerHostSeconds,
                    request.Behavior.SnapshotPolling.DependencyResourcesSeconds,
                    request.Behavior.SnapshotPolling.RecentOperationsSeconds,
                    request.Behavior.SnapshotPolling.ResourceChartSeconds),
                request.Behavior.OverviewPollingSeconds,
                request.Behavior.MetricsPollingSeconds,
                request.Behavior.JobsPollingSeconds,
                request.Behavior.LogsPollingSeconds,
                request.Behavior.PerformancePollingSeconds),
            DashboardAuth = new InstanceDashboardAuthSettingsResult(
                request.DashboardAuth.AdminUsername,
                request.DashboardAuth.SessionTimeoutMinutes)
        };

        _ = updatedBy;
        return Task.FromResult(_snapshot);
    }

    public Task<InstanceSettingsSnapshot> ResetAsync(string updatedBy, CancellationToken cancellationToken)
    {
        _snapshot = _snapshot with
        {
            SettingsRevision = 0,
            SettingsUpdatedAtUtc = null,
            DashboardAuth = new InstanceDashboardAuthSettingsResult("admin", 480)
        };

        _ = updatedBy;
        return Task.FromResult(_snapshot);
    }

    public Task<DashboardAuthenticationSettings> GetDashboardAuthenticationSettingsAsync(CancellationToken cancellationToken)
        => Task.FromResult(new DashboardAuthenticationSettings(
            _snapshot.DashboardAuth.AdminUsername,
            "AQAAAAIAAYagAAAAEIbguUQEApMQehlC51gjy+uGulsE4ahRI7UtbdAlSsGMynNrNM3J3KfsJL+3IuBUxQ==",
            _snapshot.DashboardAuth.SessionTimeoutMinutes));

    private static InstanceSettingsSnapshot CreateSnapshot(DashboardOptions options, string username)
        => new(
            options.InstanceId,
            "context-hub-test",
            options.ComposeProject,
            "2026.04.12-test",
            DateTimeOffset.Parse("2026-04-12T00:30:00+00:00"),
            2,
            DateTimeOffset.UtcNow.AddMinutes(-15),
            new InstanceBehaviorSettingsResult(
                false,
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
                    3),
                options.Polling.OverviewSeconds,
                options.Polling.MetricsSeconds,
                options.Polling.JobsSeconds,
                options.Polling.LogsSeconds,
                options.Polling.PerformanceSeconds),
            new InstanceDashboardAuthSettingsResult(username, 480),
            new ConversationAutomationStatusResult(0, 0, 0, string.Empty));
}
