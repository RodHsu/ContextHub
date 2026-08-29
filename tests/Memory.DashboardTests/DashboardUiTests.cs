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
    internal const string DashboardApiToken = "dashboard-test-api-token";
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
        AssertNoStoreHeaders(response);

        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("ContextHub");
        html.Should().Contain("登入 ContextHub");
        html.Should().Contain("login-card");
        html.Should().Contain("登入");
        html.Should().Contain("name=\"Username\" autocomplete=\"username\" placeholder=\"admin\" autofocus");
        html.Should().Contain("UI v");
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
        var sessionScriptPath = ExtractAssetPath(html, "<script src=\"([^\"]*dashboard-session[^\"]*\\.js)\"");

        using var cssResponse = await client.GetAsync(cssPath);
        cssResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertStaticAssetCacheHeaders(cssResponse);
        cssResponse.Content.Headers.ContentType?.MediaType.Should().Be("text/css");
        (await cssResponse.Content.ReadAsStringAsync()).Should().NotBeNullOrWhiteSpace();

        using var blazorScriptResponse = await client.GetAsync(blazorScriptPath);
        blazorScriptResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertStaticAssetCacheHeaders(blazorScriptResponse);
        blazorScriptResponse.Content.Headers.ContentType?.MediaType.Should().Contain("javascript");
        (await blazorScriptResponse.Content.ReadAsStringAsync()).Should().NotStartWith("<!DOCTYPE html>", "framework script should not fall back to an HTML error page");

        using var viewportScriptResponse = await client.GetAsync(viewportScriptPath);
        viewportScriptResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertStaticAssetCacheHeaders(viewportScriptResponse);
        viewportScriptResponse.Content.Headers.ContentType?.MediaType.Should().Contain("javascript");
        (await viewportScriptResponse.Content.ReadAsStringAsync()).Should().NotStartWith("<!DOCTYPE html>", "dashboard module script should not fall back to an HTML error page");

        using var sessionScriptResponse = await client.GetAsync(sessionScriptPath);
        sessionScriptResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertStaticAssetCacheHeaders(sessionScriptResponse);
        var sessionScript = await sessionScriptResponse.Content.ReadAsStringAsync();
        sessionScript.Should().Contain("/account/session/refresh");
    }

    [Fact]
    public void Dashboard_Api_Client_Should_Send_Service_Token_When_Configured()
    {
        using var client = new HttpClient();
        DashboardApiClientHttpClient.Configure(client, new DashboardOptions
        {
            BaseUrl = "http://fake-context-hub",
            ApiToken = " service-token "
        });

        client.BaseAddress.Should().Be(new Uri("http://fake-context-hub"));
        client.DefaultRequestHeaders.Authorization.Should().NotBeNull();
        client.DefaultRequestHeaders.Authorization!.Scheme.Should().Be("Bearer");
        client.DefaultRequestHeaders.Authorization.Parameter.Should().Be("service-token");
        client.DefaultRequestHeaders.GetValues(RequestTrafficConstants.DashboardRequestHeader)
            .Should()
            .ContainSingle(RequestTrafficConstants.DashboardRequestHeaderValue);
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
    public async Task Authenticated_User_Can_Refresh_Session_Through_NoStore_Endpoint()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        await LoginAsync(client);

        using var response = await client.GetAsync("/account/session/refresh");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        AssertNoStoreHeaders(response);
    }

    [Fact]
    public async Task Database_User_Login_Should_Issue_Configured_Twelve_Hour_Session()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        await LoginAsync(client);
    }

    [Fact]
    public async Task Context_Savings_Windows_Should_Render_Explicit_Call_Counts()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        await LoginAsync(client);

        using var overviewResponse = await client.GetAsync("/");
        overviewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertContextSavingsCallCounts(WebUtility.HtmlDecode(await overviewResponse.Content.ReadAsStringAsync()));

        using var monitoringResponse = await client.GetAsync("/monitoring");
        monitoringResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertContextSavingsCallCounts(WebUtility.HtmlDecode(await monitoringResponse.Content.ReadAsStringAsync()));
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
        AssertNoStoreHeaders(response);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
    }

    [Fact]
    public async Task Dashboard_Health_Should_Not_Be_Cached_By_Cloudflare()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        using var anonymousResponse = await client.GetAsync("/health/live");
        anonymousResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        AssertNoStoreHeaders(anonymousResponse);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", DashboardApiToken);
        using var response = await client.GetAsync("/health/live");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertNoStoreHeaders(response);
    }

    [Fact]
    public void Snapshot_Warning_Should_Not_Show_Generic_Stale_Message_When_Page_Is_Not_Stale()
    {
        var snapshot = new DashboardPageSnapshotStatusResult(
            DateTimeOffset.UtcNow.AddSeconds(-45),
            false,
            string.Empty,
            []);

        var warning = DashboardUiErrorFormatter.BuildSnapshotWarning(snapshot, "總覽");

        warning.Should().BeNull();
    }

    [Fact]
    public void Snapshot_Warning_Should_Show_Generic_Stale_Message_When_Page_Is_Stale()
    {
        var snapshot = new DashboardPageSnapshotStatusResult(
            DateTimeOffset.UtcNow.AddSeconds(-18),
            true,
            string.Empty,
            []);

        var warning = DashboardUiErrorFormatter.BuildSnapshotWarning(snapshot, "總覽");

        warning.Should().NotBeNull();
        warning.Should().Contain("資料延遲");
    }

    [Fact]
    public async Task ContextHubApiClient_Should_Retry_Transient_Get_Failure()
    {
        using var httpClient = new HttpClient(new SequencedHttpMessageHandler(
            _ => throw new HttpRequestException("Response ended prematurely."),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(CreateSystemStatusResult())
            }))
        {
            BaseAddress = new Uri("http://context-hub.test")
        };
        var apiClient = new ContextHubApiClient(httpClient);

        var status = await apiClient.GetStatusAsync(CancellationToken.None);

        status.Service.Should().Be("mcp-server");
    }

    [Fact]
    public async Task ContextHubApiClient_Should_Retry_Transient_Get_Status_Code()
    {
        using var httpClient = new HttpClient(new SequencedHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(CreateSystemStatusResult())
            }))
        {
            BaseAddress = new Uri("http://context-hub.test")
        };
        var apiClient = new ContextHubApiClient(httpClient);

        var status = await apiClient.GetStatusAsync(CancellationToken.None);

        status.Service.Should().Be("mcp-server");
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
        overviewHtml.Should().Contain("靜默訊號");
        overviewHtml.Should().Contain("維運與知識治理");
        overviewHtml.Should().Contain("記憶條目");
        overviewHtml.Should().Contain("預設專案記憶");
        overviewHtml.Should().Contain("Docker 主機");
        overviewHtml.Should().Contain("評估摘要");
        overviewHtml.Should().Contain("<dt>狀態</dt><dd>失敗</dd>");
        overviewHtml.Should().NotContain("<dt>狀態</dt><dd>Failed</dd>");
        overviewHtml.Should().Contain("資源狀態圖表");
        overviewHtml.Should().NotContain("Agent MCP 延遲");
        overviewHtml.Should().NotContain("Agent P95");
        overviewHtml.Should().NotContain("home-signal-lane-divider");
        overviewHtml.Should().NotContain("<text class=\"home-signal-lane-label\" x=\"12\" y=\"112\">Agent P95</text>");
        overviewHtml.Should().Contain("近期呼叫趨勢");
        overviewHtml.Should().Contain("Redis 狀態監控");
        overviewHtml.Should().Contain("resource-redis-chart");
        overviewHtml.Should().Contain("Redis resource status chart");
        overviewHtml.Should().Contain("Token 節省量");
        overviewHtml.Should().Contain("context-savings-strip");
        overviewHtml.Should().Contain("24H");
        overviewHtml.Should().Contain("3D");
        overviewHtml.Should().Contain("7D");
        overviewHtml.Should().Contain("30D");
        overviewHtml.Should().Contain("24H 節省量 / 快取命中率");
        overviewHtml.Should().Contain("3D 節省量 / 快取命中率");
        overviewHtml.Should().Contain("7D 節省量 / 快取命中率");
        overviewHtml.Should().Contain("30D 節省量 / 快取命中率");
        overviewHtml.Should().Contain("有效樣本：18 次");
        overviewHtml.Should().Contain("有效樣本：54 次");
        overviewHtml.Should().Contain("有效樣本：126 次");
        overviewHtml.Should().Contain("有效樣本：540 次");
        overviewHtml.Should().Contain("實際呼叫次數：96 次");
        overviewHtml.Should().Contain("實際呼叫次數：280 次");
        overviewHtml.Should().Contain("實際呼叫次數：640 次");
        overviewHtml.Should().Contain("實際呼叫次數：2,500 次");
        overviewHtml.Should().Contain("精準 token");
        overviewHtml.Should().NotContain("context-savings-panel");
        overviewHtml.Should().Contain("contexthub-redis-1");
        overviewHtml.Should().Contain("近期平均");
        overviewHtml.Should().Contain("每 5 秒刷新");
        overviewHtml.Should().Contain("資源最近");
        overviewHtml.Should().Contain("呼叫最近 15 筆");
        overviewHtml.Should().Contain("進站 (Inbound)");
        overviewHtml.Should().Contain("傳出 (Outbound)");
        overviewHtml.Should().Contain("/5s");
        overviewHtml.Should().Contain("client-local-time");
        overviewHtml.Should().Contain("data-local-iso");
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
        overviewHtml.Should().Contain("資料快照");
        overviewHtml.Should().Contain("refresh-status-group");
        overviewHtml.Should().Contain("refresh-status-primary");
        overviewHtml.Should().Contain("refresh-status-live");
        overviewHtml.Should().NotContain("refresh-status-build");
        overviewHtml.Should().Contain("page-scroll-host");
        overviewHtml.IndexOf("Docker 主機", StringComparison.Ordinal).Should().BeLessThan(overviewHtml.IndexOf("評估摘要", StringComparison.Ordinal));
        overviewHtml.IndexOf("sidebar-build", StringComparison.Ordinal).Should().BeGreaterThan(0);
        overviewHtml.Should().NotContain("sidebar-footer");
        overviewHtml.IndexOf("狀態監控", StringComparison.Ordinal).Should().BeLessThan(overviewHtml.IndexOf("執行參數", StringComparison.Ordinal));
        overviewHtml.IndexOf("專案工作區", StringComparison.Ordinal).Should().BeLessThan(overviewHtml.IndexOf("專案樹狀圖", StringComparison.Ordinal));
        overviewHtml.IndexOf("專案樹狀圖", StringComparison.Ordinal).Should().BeLessThan(overviewHtml.IndexOf("記憶圖譜", StringComparison.Ordinal));
        overviewHtml.IndexOf("記憶圖譜", StringComparison.Ordinal).Should().BeLessThan(overviewHtml.IndexOf("記憶資料", StringComparison.Ordinal));
        overviewHtml.IndexOf("記憶資料", StringComparison.Ordinal).Should().BeLessThan(overviewHtml.IndexOf("記憶整理", StringComparison.Ordinal));
        overviewHtml.IndexOf("記憶整理", StringComparison.Ordinal).Should().BeLessThan(overviewHtml.IndexOf("資料來源", StringComparison.Ordinal));
        overviewHtml.IndexOf("日誌", StringComparison.Ordinal).Should().BeLessThan(overviewHtml.IndexOf("記憶資料", StringComparison.Ordinal));
        overviewHtml.IndexOf("專案待辦", StringComparison.Ordinal).Should().BeLessThan(overviewHtml.IndexOf("偏好", StringComparison.Ordinal));
        overviewHtml.IndexOf("資料庫檢視", StringComparison.Ordinal).Should().BeLessThan(overviewHtml.IndexOf("安全管理", StringComparison.Ordinal));
        overviewHtml.IndexOf("安全管理", StringComparison.Ordinal).Should().BeLessThan(overviewHtml.IndexOf("MCP 說明", StringComparison.Ordinal));
        overviewHtml.IndexOf("MCP 說明", StringComparison.Ordinal).Should().BeLessThan(overviewHtml.IndexOf("系統設定", StringComparison.Ordinal));

        using var runtimeResponse = await client.GetAsync("/runtime");
        runtimeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var runtimeHtml = WebUtility.HtmlDecode(await runtimeResponse.Content.ReadAsStringAsync());
        runtimeHtml.Should().Contain("執行參數");
        runtimeHtml.Should().Contain("refresh-status-group");
        runtimeHtml.Should().Contain("資料快照");
        runtimeHtml.Should().Contain("refresh-status-live");
        runtimeHtml.Should().NotContain("refresh-status-build");
        runtimeHtml.Should().Contain("公開參數");
        runtimeHtml.Should().Contain("建置版本");
        runtimeHtml.Should().Contain("client-local-time");
        runtimeHtml.Should().Contain("data-local-iso");
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
        monitoringHtml.Should().Contain("資料快照");
        monitoringHtml.Should().Contain("Redis");
        monitoringHtml.Should().Contain("PostgreSQL");
        monitoringHtml.Should().Contain("Token 節省量");
        monitoringHtml.Should().Contain("monitoring-context-savings-panel");
        monitoringHtml.Should().Contain("24H 節省量 / 快取命中率");
        monitoringHtml.Should().Contain("3D 節省量 / 快取命中率");
        monitoringHtml.Should().Contain("7D 節省量 / 快取命中率");
        monitoringHtml.Should().Contain("30D 節省量 / 快取命中率");
        monitoringHtml.Should().Contain("有效樣本：18 次");
        monitoringHtml.Should().Contain("有效樣本：54 次");
        monitoringHtml.Should().Contain("有效樣本：126 次");
        monitoringHtml.Should().Contain("有效樣本：540 次");
        monitoringHtml.Should().Contain("實際呼叫次數：96 次");
        monitoringHtml.Should().Contain("實際呼叫次數：280 次");
        monitoringHtml.Should().Contain("實際呼叫次數：640 次");
        monitoringHtml.Should().Contain("實際呼叫次數：2,500 次");
        monitoringHtml.Should().Contain("精準 token");
        monitoringHtml.Should().NotContain("24H 樣本");
        monitoringHtml.Should().Contain("來源覆蓋率");
        monitoringHtml.Should().Contain("Redis 命中率");
        monitoringHtml.Should().Contain("應用快取命中率");
        monitoringHtml.Should().Contain("Redis 命中 / 未命中");
        monitoringHtml.Should().Contain("快取略過 / 錯誤");
        monitoringHtml.Should().Contain("緩衝命中率");
        monitoringHtml.Should().Contain("次資料區塊存取");
        monitoringHtml.Should().Contain("資源趨勢");
        monitoringHtml.Should().NotContain("Agent MCP 延遲");
        monitoringHtml.Should().NotContain("Agent 最近");
        monitoringHtml.Should().Contain("Compose 服務資源");
        monitoringHtml.Should().Contain("Docker 主機");
        monitoringHtml.Should().Contain("命令總量");
        monitoringHtml.Should().Contain("連線數");
        monitoringHtml.Should().Contain("交易已提交");
        monitoringHtml.Should().Contain("交易已回滾");
        monitoringHtml.Should().Contain("掃描列數");
        monitoringHtml.Should().Contain("顯示資料庫大小說明");
        monitoringHtml.Should().Contain("目前 ContextHub PostgreSQL 資料庫的實際資料大小");
        monitoringHtml.Should().Contain("顯示暫存檔說明");
        monitoringHtml.Should().Contain("查詢排序、hash join 或中間結果超出記憶體");
        monitoringHtml.Should().Contain("儲存目標");
        monitoringHtml.Should().Contain("容器磁碟 I/O");
        monitoringHtml.Should().Contain("monitoring-page-stack");
        monitoringHtml.Should().Contain("monitoring-telemetry-grid");
        monitoringHtml.Should().NotContain("未配置 Redis 專屬 volume");
        monitoringHtml.Should().NotContain("未偵測 PostgreSQL 專屬 volume");
        monitoringHtml.Should().NotContain("Docker volume 使用量不可用");
        monitoringHtml.Should().NotContain("儲存量暫以 Redis used_memory 顯示邏輯使用量");
        monitoringHtml.Should().NotContain("儲存量暫以目前資料庫大小顯示邏輯使用量");

        using var jobsResponse = await client.GetAsync("/jobs");
        jobsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var jobsHtml = WebUtility.HtmlDecode(await jobsResponse.Content.ReadAsStringAsync());
        jobsHtml.Should().Contain("工作細節");
        jobsHtml.Should().Contain("複製 JSON");
        jobsHtml.Should().Contain("Memory Retention");
        jobsHtml.Should().Contain("開啟記憶整理");
        jobsHtml.Should().Contain("快速產生清單");
        jobsHtml.Should().Contain("完整檢視、篩選、逐筆 action 與 note 編輯請到記憶整理頁。");

        using var retentionResponse = await client.GetAsync("/retention");
        retentionResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var retentionHtml = WebUtility.HtmlDecode(await retentionResponse.Content.ReadAsStringAsync());
        retentionHtml.Should().Contain("記憶整理");
        retentionHtml.Should().Contain("記憶整理審核工作區");
        retentionHtml.Should().Contain("整理候選");
        retentionHtml.Should().Contain("Expired low signal memory");
        retentionHtml.Should().Contain("Important archived decision");
        retentionHtml.Should().Contain("審核備註");
        retentionHtml.Should().Contain("複製整理計畫");
        retentionHtml.Should().Contain("開啟記憶資料");

        using var projectTreeResponse = await client.GetAsync("/project-tree");
        projectTreeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var projectTreeHtml = WebUtility.HtmlDecode(await projectTreeResponse.Content.ReadAsStringAsync());
        projectTreeHtml.Should().Contain("專案樹狀圖");
        projectTreeHtml.Should().Contain("project-tree-workspace");
        projectTreeHtml.Should().Contain("Dashboard test");
        projectTreeHtml.Should().Contain("dashboard-test-secondary");
        projectTreeHtml.Should().Contain("專案工作區");

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

        using var mcpToolsResponse = await client.GetAsync("/mcp-tools");
        mcpToolsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var mcpToolsHtml = WebUtility.HtmlDecode(await mcpToolsResponse.Content.ReadAsStringAsync());
        mcpToolsHtml.Should().Contain("功能與 MCP API");
        mcpToolsHtml.Should().Contain("知識協作能力");
        mcpToolsHtml.Should().Contain("專案知識");
        mcpToolsHtml.Should().Contain("共用知識");
        mcpToolsHtml.Should().Contain("使用者偏好");
        mcpToolsHtml.Should().Contain("跨專案討論");
        mcpToolsHtml.Should().Contain("專案待辦事項");
        mcpToolsHtml.Should().Contain("suggested_actions_list");
        mcpToolsHtml.Should().Contain("連線面總覽");
        mcpToolsHtml.Should().Contain("目前發布數量");
        mcpToolsHtml.Should().Contain("Direct MCP");
        mcpToolsHtml.Should().Contain("66</strong> 支工具");
        mcpToolsHtml.Should().Contain("ChatGPT App-facing");
        mcpToolsHtml.Should().Contain("65</strong> 支工具");
        mcpToolsHtml.Should().Contain("可能刪除");
        mcpToolsHtml.Should().Contain("3</strong> 支工具");
        mcpToolsHtml.Should().Contain("Direct MCP 工具");
        mcpToolsHtml.Should().Contain("ChatGPT Gateway 工具");
        mcpToolsHtml.Should().Contain("REST API 摘要");
        mcpToolsHtml.Should().Contain("project_cleanup_preview");
        mcpToolsHtml.Should().Contain("project_cleanup_apply");
        mcpToolsHtml.Should().Contain("discussion_threads_list / discussion_thread_get");
        mcpToolsHtml.Should().Contain("discussion_thread_create / discussion_thread_close / discussion_thread_archive / discussion_thread_restore / discussion_message_create");
        mcpToolsHtml.Should().Contain("project_work_item_create / project_work_item_update / project_work_item_checklist_update / project_work_item_archive / project_work_item_restore / project_work_items_list");
        mcpToolsHtml.Should().Contain("project_hierarchy_get_children / project_hierarchy_set_children");
        mcpToolsHtml.Should().Contain("/api/projects/hierarchy/*, /api/discussions/*");
        mcpToolsHtml.Should().Contain("memory_delete");
        mcpToolsHtml.Should().Contain("需核准");
        mcpToolsHtml.Should().Contain("中文說明");
        mcpToolsHtml.Should().Contain("mcp-tools-page-stack");
        mcpToolsHtml.Should().Contain("mcp-capability-grid");

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
        memoriesHtml.Should().Contain("記憶內容");
        memoriesHtml.Should().Contain("專案與範圍");
        memoriesHtml.Should().Contain("類型與狀態");
        memoriesHtml.Should().Contain("來源與標籤");
        memoriesHtml.Should().Contain("memories-list-panel");
        memoriesHtml.Should().Contain("memories-detail-panel");
        memoriesHtml.Should().Contain("memories-table-scroll-shell");
        memoriesHtml.Should().Contain("查詢記憶條目，展開版本紀錄、內容片段與向量。");
        memoriesHtml.Should().Contain("點選左側記憶條目後顯示版本紀錄、內容片段與向量。");
        memoriesHtml.Should().NotContain("展開 revisions、chunks 與 vectors");
        memoriesHtml.Should().NotContain("點選左側 memory item 後顯示 revisions、chunks 與 vectors");

        using var projectInformationResponse = await client.GetAsync("/project-information");
        projectInformationResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var projectInformationHtml = WebUtility.HtmlDecode(await projectInformationResponse.Content.ReadAsStringAsync());
        projectInformationHtml.Should().Contain("project-studio");
        projectInformationHtml.Should().Contain("Context contract");
        projectInformationHtml.Should().Contain("system:project-information");
        projectInformationHtml.Should().Contain("下屬專案");
        projectInformationHtml.Should().Contain("child-projects-picker");
        projectInformationHtml.Should().Contain("直接勾選");
        projectInformationHtml.Should().NotContain("<details");
        projectInformationHtml.Should().Contain("專案工作區");
        projectInformationHtml.Should().Contain("project-workspace-links");
        projectInformationHtml.Should().Contain("跨專案討論");
        projectInformationHtml.Should().Contain("系統維運");
        projectInformationHtml.Should().Contain("專案工作與知識");
        projectInformationHtml.Should().Contain("治理與審核");
        projectInformationHtml.Should().Contain("系統與個人設定");
        projectInformationHtml.Should().Contain("背景注入");
        projectInformationHtml.Should().Contain("顯示範圍");

        using var discussionsResponse = await client.GetAsync("/discussions");
        discussionsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var discussionsHtml = WebUtility.HtmlDecode(await discussionsResponse.Content.ReadAsStringAsync());
        discussionsHtml.Should().Contain("跨專案討論");
        discussionsHtml.Should().Contain("建立跨專案討論");
        discussionsHtml.Should().Contain("相關討論");
        discussionsHtml.Should().Contain("依專案、參與者、狀態或內容篩選");
        discussionsHtml.Should().Contain("讀取身分");
        discussionsHtml.Should().Contain("搜尋討論內容或主題");
        discussionsHtml.Should().Contain("顯示");
        discussionsHtml.Should().Contain("顯示已封存");
        discussionsHtml.Should().Contain("參與專案");
        discussionsHtml.Should().Contain("discussions-workspace");

        using var projectWorkItemsResponse = await client.GetAsync("/project-work-items");
        projectWorkItemsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var projectWorkItemsHtml = WebUtility.HtmlDecode(await projectWorkItemsResponse.Content.ReadAsStringAsync());
        projectWorkItemsHtml.Should().Contain("相關代辦");
        projectWorkItemsHtml.Should().Contain("所有專案");
        projectWorkItemsHtml.Should().Contain("project-work-items-workspace");
        projectWorkItemsHtml.Should().Contain("project-work-items-list-column-labels");
        projectWorkItemsHtml.Should().Contain("檢核清單執行面板");
        projectWorkItemsHtml.Should().Contain("完成進度");
        projectWorkItemsHtml.Should().Contain("顯示已封存");
        projectWorkItemsHtml.Should().Contain("封存");

        using var graphResponse = await client.GetAsync("/graph");
        graphResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var graphHtml = WebUtility.HtmlDecode(await graphResponse.Content.ReadAsStringAsync());
        graphHtml.Should().Contain("記憶圖譜");
        graphHtml.Should().Contain("記憶關聯");
        graphHtml.Should().Contain("展開鄰居");
        graphHtml.Should().Contain("回到種子");
        graphHtml.Should().Contain("關聯探索");
        graphHtml.Should().Contain("相鄰探索");
        graphHtml.Should().Contain("專案檢視");
        graphHtml.Should().Contain("全部專案整合視圖");
        graphHtml.Should().Contain("全螢幕");
        graphHtml.Should().Contain("graph-workspace");

        using var sourcesResponse = await client.GetAsync("/sources");
        sourcesResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var sourcesHtml = WebUtility.HtmlDecode(await sourcesResponse.Content.ReadAsStringAsync());
        sourcesHtml.Should().Contain("資料來源");
        sourcesHtml.Should().Contain("來源設定");
        sourcesHtml.Should().Contain("來源清單");
        sourcesHtml.Should().Contain("來源細節");
        sourcesHtml.Should().Contain("SourceConnections");
        sourcesHtml.Should().Contain("建立來源後請執行一次同步");

        using var governanceResponse = await client.GetAsync("/governance");
        governanceResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var governanceHtml = WebUtility.HtmlDecode(await governanceResponse.Content.ReadAsStringAsync());
        governanceHtml.Should().Contain("治理檢查");
        governanceHtml.Should().Contain("治理清單");
        governanceHtml.Should().Contain("治理細節");
        governanceHtml.Should().Contain("執行治理分析");
        governanceHtml.Should().Contain("不會自動塞示範資料");

        using var evaluationResponse = await client.GetAsync("/evaluation");
        evaluationResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var evaluationHtml = WebUtility.HtmlDecode(await evaluationResponse.Content.ReadAsStringAsync());
        evaluationHtml.Should().Contain("評估驗證");
        evaluationHtml.Should().Contain("建立最小評測集");
        evaluationHtml.Should().Contain("評測組清單");
        evaluationHtml.Should().Contain("評測細節");
        evaluationHtml.Should().Contain("預期外部鍵");
        evaluationHtml.Should().Contain("查詢字串會直接送進檢索");

        using var inboxResponse = await client.GetAsync("/inbox");
        inboxResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var inboxHtml = WebUtility.HtmlDecode(await inboxResponse.Content.ReadAsStringAsync());
        inboxHtml.Should().Contain("專案待辦");
        inboxHtml.Should().Contain("待辦清單");
        inboxHtml.Should().Contain("待辦細節");
        inboxHtml.Should().Contain("治理分析與評測回歸");
        inboxHtml.Should().Contain("suggested actions");

        using var storageResponse = await client.GetAsync("/storage");
        storageResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var storageHtml = WebUtility.HtmlDecode(await storageResponse.Content.ReadAsStringAsync());
        storageHtml.Should().Contain("資料庫檢視");
        storageHtml.Should().Contain("memory_items");
        storageHtml.Should().Contain("關鍵字查詢");
        storageHtml.Should().Contain("所有可搜尋欄位");
        storageHtml.Should().Contain("可搜尋欄位");
        storageHtml.Should().Contain("頁碼跳轉");
        storageHtml.Should().Contain("第一頁");
        storageHtml.Should().Contain("最後頁");
        storageHtml.Should().Contain("storage-table-list");
        storageHtml.Should().Contain("storage-query-panel");
        storageHtml.Should().Contain("storage-info-panel");
        storageHtml.Should().Contain("storage-inspector-panel");
        storageHtml.Should().Contain("table-scroll-shell");
        storageHtml.Should().NotContain("尚未同步");
        storageHtml.Should().NotContain("同步失敗");

        using var securityResponse = await client.GetAsync("/security");
        securityResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var securityHtml = WebUtility.HtmlDecode(await securityResponse.Content.ReadAsStringAsync());
        securityHtml.Should().Contain("安全管理");
        securityHtml.Should().Contain("我的 Token");
        securityHtml.Should().Contain("租戶");
        securityHtml.Should().Contain("帳戶管理");
        securityHtml.Should().Contain("Project 授權");
        securityHtml.Should().Contain("Token 管理");
        securityHtml.Should().Contain("安全稽核");
        securityHtml.Should().Contain("Context Team");
        securityHtml.Should().Contain("MCP Client（記憶與偏好讀寫）");
        securityHtml.Should().Contain("全部");
        securityHtml.Should().Contain("最後使用");
        securityHtml.Should().Contain("更多 Token 操作");
        securityHtml.Should().Contain("重新產生");
        securityHtml.Should().Contain("ApiTokenAuthenticated");
        securityHtml.Should().Contain("<select");
        securityHtml.Should().Contain("只讀記憶");
        securityHtml.Should().Contain("Dashboard Service");
        securityHtml.Should().NotContain("尚未同步");
        securityHtml.Should().NotContain("同步失敗");

        using var accountTokensResponse = await client.GetAsync("/account/tokens");
        accountTokensResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var accountTokensHtml = WebUtility.HtmlDecode(await accountTokensResponse.Content.ReadAsStringAsync());
        accountTokensHtml.Should().Contain("我的存取權杖");
        accountTokensHtml.Should().Contain("<select");
        accountTokensHtml.Should().Contain("ContextHub");
        accountTokensHtml.Should().Contain("MCP Client（記憶與偏好讀寫）");
        accountTokensHtml.Should().Contain("全部");
        accountTokensHtml.Should().Contain("個人完整權限");
        accountTokensHtml.Should().Contain("編輯");
        accountTokensHtml.Should().Contain("更多存取權杖操作");
        accountTokensHtml.Should().Contain("重新產生");
        accountTokensHtml.Should().Contain("撤銷");
        accountTokensHtml.Should().NotContain("尚未同步");
        accountTokensHtml.Should().NotContain("同步失敗");

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
    public async Task Main_Navigation_Should_Hide_Agent_Latency_Entry()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        await LoginAsync(client);

        using var response = await client.GetAsync("/");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        html.Should().NotContain("href=\"/connectivity\"");
        html.Should().NotContain("Agent 延遲");
    }

    [Fact]
    public void Empty_State_Ctas_Should_Target_Page_Specific_Fragments()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var sourcesPagePath = Path.Combine(repoRoot, "src", "Memory.Dashboard", "Components", "Pages", "Sources.razor");
        var evaluationPagePath = Path.Combine(repoRoot, "src", "Memory.Dashboard", "Components", "Pages", "Evaluation.razor");

        File.ReadAllText(sourcesPagePath).Should().Contain("href=\"/sources#source-config-panel\"");
        File.ReadAllText(evaluationPagePath).Should().Contain("href=\"/evaluation#evaluation-suite-form\"");
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
    public async Task Graph_Page_Should_Default_To_AllProjects_Integrated_View()
    {
        using var isolatedFactory = new DashboardApplicationFactory();
        using var client = isolatedFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        await LoginAsync(client);

        using var response = await client.GetAsync("/graph");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var apiClient = isolatedFactory.Services.GetRequiredService<IContextHubApiClient>().Should().BeOfType<FakeContextHubApiClient>().Subject;
        apiClient.LastMemoryGraphRequest.Should().NotBeNull();
        apiClient.LastMemoryGraphRequest!.ProjectId.Should().BeNull();
        apiClient.LastMemoryGraphRequest.IncludedProjectIds.Should().BeNull();
        apiClient.LastMemoryGraphRequest.QueryMode.Should().Be(MemoryQueryMode.CurrentOnly);
        apiClient.LastMemoryGraphRequest.UseSummaryLayer.Should().BeFalse();
        apiClient.LastMemoryGraphRequest.IncludeSimilarity.Should().BeFalse();
        apiClient.LastMemoryGraphRequest.GraphMode.Should().Be(MemoryGraphMode.ProjectFull);
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
        AssertDashboardSessionCookieLifetime(response.Headers);
    }

    private static void AssertDashboardSessionCookieLifetime(HttpResponseHeaders headers)
    {
        headers.TryGetValues("Set-Cookie", out var values).Should().BeTrue();
        var dashboardCookie = values!
            .Single(value => value.StartsWith("contexthub.dashboard=", StringComparison.Ordinal));
        var expiresMatch = Regex.Match(dashboardCookie, @"(?:^|;\s*)expires=([^;]+)", RegexOptions.IgnoreCase);
        expiresMatch.Success.Should().BeTrue("the persistent dashboard cookie should declare its expiration");

        var expiresAt = DateTimeOffset.Parse(
            expiresMatch.Groups[1].Value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal);
        var remaining = expiresAt - DateTimeOffset.UtcNow;
        remaining.Should().BeGreaterThan(TimeSpan.FromHours(11.5));
        remaining.Should().BeLessThanOrEqualTo(TimeSpan.FromHours(12));
    }

    private static void AssertContextSavingsCallCounts(string html)
    {
        html.Should().Contain("有效樣本：18 次");
        html.Should().Contain("有效樣本：54 次");
        html.Should().Contain("有效樣本：126 次");
        html.Should().Contain("有效樣本：540 次");
        html.Should().Contain("實際呼叫次數：96 次");
        html.Should().Contain("實際呼叫次數：280 次");
        html.Should().Contain("實際呼叫次數：640 次");
        html.Should().Contain("實際呼叫次數：2,500 次");
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

    private static void AssertNoStoreHeaders(HttpResponseMessage response)
    {
        response.Headers.CacheControl?.NoStore.Should().BeTrue();
        response.Headers.CacheControl?.NoCache.Should().BeTrue();
        response.Headers.TryGetValues("Cloudflare-CDN-Cache-Control", out var cloudflareValues).Should().BeTrue();
        cloudflareValues.Should().ContainSingle("no-store");
        response.Headers.TryGetValues("CDN-Cache-Control", out var cdnValues).Should().BeTrue();
        cdnValues.Should().ContainSingle("no-store");
    }

    private static void AssertStaticAssetCacheHeaders(HttpResponseMessage response)
    {
        response.Headers.CacheControl?.Public.Should().BeTrue();
        response.Headers.CacheControl?.MaxAge.Should().Be(TimeSpan.FromDays(365));
        response.Headers.CacheControl?.Extensions.Should().Contain(x => string.Equals(x.Name, "immutable", StringComparison.OrdinalIgnoreCase));
        response.Headers.TryGetValues("Cloudflare-CDN-Cache-Control", out var cloudflareValues).Should().BeTrue();
        cloudflareValues.Should().ContainSingle("public, max-age=31536000");
        response.Headers.TryGetValues("CDN-Cache-Control", out var cdnValues).Should().BeTrue();
        cdnValues.Should().ContainSingle("public, max-age=31536000");
    }

    private static SystemStatusResult CreateSystemStatusResult()
        => new(
            "mcp-server",
            ProjectContext.DefaultProjectId,
            "test",
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
            1,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            30,
            false,
            string.Empty,
            string.Empty);

    private static string BuildAntiforgeryCookie(HttpResponseHeaders headers)
    {
        var setCookie = headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(x => x.Contains(".AspNetCore.Antiforgery", StringComparison.OrdinalIgnoreCase))
            : null;
        setCookie.Should().NotBeNull();
        return setCookie!.Split(';', 2)[0];
    }
}

internal sealed class SequencedHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses;

    public SequencedHttpMessageHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
    {
        _responses = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(responses);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _responses.Count.Should().BeGreaterThan(0);
        var response = _responses.Dequeue().Invoke(request);
        return Task.FromResult(response);
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
                ["Dashboard:ApiToken"] = DashboardUiTests.DashboardApiToken,
                ["Dashboard:SessionTimeoutMinutes"] = "720",
                ["Dashboard:ComposeProject"] = "contexthub",
                ["Dashboard:DataProtectionPath"] = CreateRepoTestDataPath("dataprotection", Guid.NewGuid().ToString("N")),
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

    private static string CreateRepoTestDataPath(params string[] segments)
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var pathSegments = new[] { repoRoot, ".agent", "local", "test-results", "dashboard-tests" }
            .Concat(segments)
            .ToArray();
        var path = Path.Combine(pathSegments);
        Directory.CreateDirectory(path);
        return path;
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
    public MemoryGraphRequest? LastMemoryGraphRequest { get; private set; }

    public Task<IReadOnlyList<ProjectWorkItemResult>> GetProjectWorkItemsAsync(ProjectWorkItemListRequest request, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<ProjectWorkItemResult>>
        ([
            new ProjectWorkItemResult(
                Guid.Parse("e367c784-a8f8-4a17-a94b-8f6f09e2653a"),
                request.ProjectId,
                "驗證專案工作區",
                "確認專案代辦清單與檢核面板皆可使用。",
                ["Dashboard", "QA"],
                [
                    new ProjectWorkItemChecklistItemResult(Guid.Parse("6f593ffd-d345-48d2-b8b1-f7dddff8687b"), "完成清單樣式檢視", true, 0),
                    new ProjectWorkItemChecklistItemResult(Guid.Parse("b2f65089-5444-4fbe-a456-c90d2d698ff2"), "確認完成按鈕狀態", false, 1)
                ],
                ProjectWorkItemStatus.InProgress,
                0,
                null,
                DateTimeOffset.UtcNow.AddHours(-2),
                DateTimeOffset.UtcNow,
                null)
        ]);

    public Task<ProjectWorkItemResult> SetProjectWorkItemArchivedAsync(Guid id, bool archived, CancellationToken cancellationToken)
        => throw new NotSupportedException();
    public Task<ProjectWorkItemResult> CreateProjectWorkItemAsync(ProjectWorkItemCreateRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new ProjectWorkItemResult(Guid.NewGuid(), request.ProjectId, request.Title, request.Description ?? string.Empty, request.Tags ?? [], [], ProjectWorkItemStatus.Pending, request.Priority, request.DueAt, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null));
    public Task<ProjectWorkItemResult> UpdateProjectWorkItemAsync(ProjectWorkItemUpdateRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new ProjectWorkItemResult(request.Id, "ContextHub", request.Title ?? string.Empty, request.Description ?? string.Empty, request.Tags ?? [], [], request.Status ?? ProjectWorkItemStatus.Pending, request.Priority ?? 0, request.DueAt, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null));
    public Task<ProjectWorkItemResult> SetProjectWorkItemChecklistCompletionAsync(Guid workItemId, Guid checklistItemId, bool isCompleted, CancellationToken cancellationToken)
        => UpdateProjectWorkItemAsync(new ProjectWorkItemUpdateRequest(workItemId), cancellationToken);

    public Task<IReadOnlyList<ChatGptProposalResult>> GetChatGptProposalsAsync(ChatGptProposalListRequest request, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var projectId = string.IsNullOrWhiteSpace(request.ProjectId) ? "ContextHub" : request.ProjectId;
        IReadOnlyList<ChatGptProposalResult> proposals =
        [
            new ChatGptProposalResult(
                Guid.Parse("a4e69c1e-74a3-47a2-8c76-a823a8ff7e8d"),
                "memory_upsert",
                ChatGptProposalStatus.Pending,
                projectId,
                projectId,
                "ChatGPT proposal",
                "Pending ChatGPT memory proposal.",
                "{\"title\":\"ChatGPT proposal\"}",
                "chatgpt-test-user",
                "chatgpt@example.test",
                "ChatGPT Test User",
                null,
                string.Empty,
                now.AddMinutes(-10),
                now)
        ];

        return Task.FromResult(proposals);
    }

    public Task<ChatGptProposalResult> ApproveChatGptProposalAsync(Guid id, string note, CancellationToken cancellationToken)
        => Task.FromResult(BuildDecidedChatGptProposal(id, ChatGptProposalStatus.Applied));

    public Task<ChatGptProposalResult> RejectChatGptProposalAsync(Guid id, string note, CancellationToken cancellationToken)
        => Task.FromResult(BuildDecidedChatGptProposal(id, ChatGptProposalStatus.Rejected));

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

    private static ChatGptProposalResult BuildDecidedChatGptProposal(Guid id, ChatGptProposalStatus status)
    {
        var now = DateTimeOffset.UtcNow;
        return new ChatGptProposalResult(
            id,
            "memory_upsert",
            status,
            "ContextHub",
            "ContextHub",
            "ChatGPT proposal",
            "Reviewed ChatGPT memory proposal.",
            "{\"title\":\"ChatGPT proposal\"}",
            "chatgpt-test-user",
            "chatgpt@example.test",
            "ChatGPT Test User",
            status == ChatGptProposalStatus.Applied ? Guid.Parse("9ad7235d-6b10-4a2e-b06e-3291d73e878d") : null,
            string.Empty,
            now.AddMinutes(-10),
            now);
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
                new DashboardOverviewMetricResult("memoryItems", "記憶條目", 24, "items"),
                new DashboardOverviewMetricResult("defaultProjectMemoryItems", "預設專案記憶", 4, "items"),
                new DashboardOverviewMetricResult("userPreferences", "使用者偏好", 3, "items"),
                new DashboardOverviewMetricResult("activeJobs", "背景工作", 4, "jobs"),
                new DashboardOverviewMetricResult("errorLogs", "近 24 小時錯誤", 4, "logs")
            ],
            traffic,
            BuildOverviewJobs(),
            BuildOverviewErrors(),
            now,
            BuildPageSnapshotStatus(now),
            BuildDockerHost(now),
            BuildDependencyResources(),
            BuildResourceSamples(traffic),
            new DashboardEvaluationSummaryResult(
                Guid.Parse("7a000000-0000-0000-0000-000000000001"),
                Guid.Parse("7a000000-0000-0000-0000-000000000002"),
                "Dashboard regression",
                EvaluationRunStatus.Failed,
                0.5m,
                0.6m,
                0.4m,
                42d,
                now.AddMinutes(-10),
                now.AddMinutes(-9)),
            ContextSavings: BuildContextSavings(now)));
    }

    private static DashboardContextSavingsResult BuildContextSavings(DateTimeOffset now)
    {
        var trend = Enumerable.Range(0, 12)
            .Select(index => new DashboardContextSavingsTrendPointResult(
                now.AddMinutes(-55 + (index * 5)),
                2_800 + (index * 40),
                620 + (index * 8),
                2_180 + (index * 32),
                77.86d + (index * 0.18d)))
            .ToArray();
        var windows = new[]
        {
            new DashboardContextSavingsWindowResult("24h", "24H", true, 18, 52_400, 11_680, 40_720, 77.71d, ContextSavingsEstimator.HighConfidence, 88.9d, 55.6d, now.AddHours(-24), now, now.AddMinutes(-3), 88.9d, TokenCountingModes.Exact, 96),
            new DashboardContextSavingsWindowResult("3d", "3D", true, 54, 157_200, 35_040, 122_160, 77.71d, ContextSavingsEstimator.HighConfidence, 88.9d, 55.6d, now.AddDays(-3), now, now.AddMinutes(-3), 88.9d, TokenCountingModes.Exact, 280),
            new DashboardContextSavingsWindowResult("7d", "7D", true, 126, 366_800, 81_760, 285_040, 77.71d, ContextSavingsEstimator.HighConfidence, 88.9d, 55.6d, now.AddDays(-7), now, now.AddMinutes(-3), 88.9d, TokenCountingModes.Exact, 640),
            new DashboardContextSavingsWindowResult("30d", "30D", true, 540, 1_572_000, 350_400, 1_221_600, 77.71d, ContextSavingsEstimator.HighConfidence, 88.9d, 55.6d, now.AddDays(-30), now, now.AddMinutes(-3), 88.9d, TokenCountingModes.Exact, 2_500)
        };

        return new DashboardContextSavingsResult(
            true,
            18,
            52_400,
            11_680,
            40_720,
            77.71d,
            ContextSavingsEstimator.HighConfidence,
            88.9d,
            55.6d,
            now.AddHours(-24),
            now,
            trend,
            true,
            now.AddMinutes(-3),
            "24H",
            windows,
            88.9d,
            TokenCountingModes.Exact);
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
            BuildResourceSamples(traffic),
            BuildContextSavings(now)));
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
            "contexthub_redis-data",
            7_200,
            800,
            1_200,
            42,
            3,
            96_000,
            4_000,
            100_000,
            96.0,
            8_000,
            90.0);

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
            96L * 1024 * 1024,
            444_000,
            94.59);

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

    public Task<IReadOnlyList<ConversationCheckpointSearchResult>> SearchConversationCheckpointsAsync(ConversationCheckpointSearchRequest request, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<ConversationCheckpointSearchResult>>([]);

    public Task<MemoryGraphResult> GetMemoryGraphAsync(MemoryGraphRequest request, CancellationToken cancellationToken)
    {
        LastMemoryGraphRequest = request;
        return Task.FromResult(new MemoryGraphResult(
        [
            new MemoryGraphNodeResult(
                _memory.Id,
                _memory.Title,
                _memory.Summary,
                _memory.ProjectId,
                _memory.MemoryType,
                _memory.Scope,
                _memory.Status,
                _memory.Tags,
                _memory.SourceType,
                _memory.SourceRef,
                _memory.UpdatedAt,
                _memory.Importance,
                _memory.Confidence,
                _memory.IsReadOnly,
                null,
                "https://example.com/favicon.ico",
                _memory.SourceType,
                1,
                request.IncludeSimilarity ? 1 : 0)
        ],
        [],
        new MemoryGraphStatsResult(1, 1, 0, false)));
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
            ],
            [
                new MemoryLinkResult(Guid.Parse("b1000000-0000-0000-0000-000000000001"), _memory.Id, _memory.Id, "related", DateTimeOffset.UtcNow.AddHours(-2))
            ],
            null,
            new MemorySourceContextResult(
                Guid.Parse("a1000000-0000-0000-0000-000000000001"),
                "Fake Dashboard Source",
                "cursor",
                "v1",
                "https://example.com/docs/context-hub",
                DateTimeOffset.UtcNow.AddMinutes(-30),
                DateTimeOffset.UtcNow.AddMinutes(-20),
                ["dashboard-ui-tests"])));

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

    public Task<MaintenanceStatusResult> GetMaintenanceStatusAsync(CancellationToken cancellationToken)
        => Task.FromResult(BuildInactiveMaintenanceStatus());

    public Task<MaintenanceStatusResult> ScheduleMaintenanceAsync(MaintenanceWindowRequest request, CancellationToken cancellationToken)
        => Task.FromResult(BuildInactiveMaintenanceStatus() with
        {
            Phase = MaintenancePhase.Scheduled,
            Reason = request.Reason ?? "Maintenance",
            Message = request.Message ?? "Scheduled maintenance",
            RunId = Guid.NewGuid()
        });

    public Task<MaintenanceStatusResult> StartMaintenanceDrainAsync(Guid? runId, CancellationToken cancellationToken)
        => Task.FromResult(BuildInactiveMaintenanceStatus() with { Phase = MaintenancePhase.Draining, RunId = runId ?? Guid.NewGuid() });

    public Task<MaintenanceStatusResult> StartMaintenanceAsync(Guid? runId, CancellationToken cancellationToken)
        => Task.FromResult(BuildInactiveMaintenanceStatus() with { Phase = MaintenancePhase.Running, Active = true, RunId = runId ?? Guid.NewGuid() });

    public Task<MaintenanceStatusResult> CompleteMaintenanceAsync(Guid? runId, CancellationToken cancellationToken)
        => Task.FromResult(BuildInactiveMaintenanceStatus() with { Phase = MaintenancePhase.Completed, RunId = runId });

    public Task<MaintenanceStatusResult> CancelMaintenanceAsync(Guid? runId, CancellationToken cancellationToken)
        => Task.FromResult(BuildInactiveMaintenanceStatus() with { Phase = MaintenancePhase.Cancelled, RunId = runId });

    public Task<IReadOnlyList<MaintenanceRunResult>> GetMaintenanceRunsAsync(int limit, CancellationToken cancellationToken)
    {
        var result = BuildRetentionResult(MemoryDataRetentionRunMode.Classify);
        return Task.FromResult<IReadOnlyList<MaintenanceRunResult>>(
        [
            new MaintenanceRunResult(
                result.RunId,
                MaintenanceRunType.MemoryDataRetention,
                MaintenanceRunStatus.Completed,
                result.StartedAtUtc,
                result.CompletedAtUtc,
                "dashboard-test",
                """{"mode":"Classify"}""",
                result.ResultJson,
                string.Empty)
        ]);
    }

    public Task<ProjectInformationResult?> GetProjectInformationAsync(string projectId, CancellationToken cancellationToken)
        => Task.FromResult<ProjectInformationResult?>(new ProjectInformationResult(
            Guid.Parse("cccccccc-0000-0000-0000-000000000001"),
            projectId,
            projectId,
              "Dashboard UI test project information.",
              DateTimeOffset.UtcNow));

    public Task<IReadOnlyList<ProjectInformationListItem>> GetProjectInformationProjectsAsync(bool includeInactive, CancellationToken cancellationToken)
    {
        IReadOnlyList<ProjectInformationListItem> projects =
        [
            new(new ProjectInformationResult(Guid.Parse("cccccccc-0000-0000-0000-000000000001"), "dashboard-test", "Dashboard test", "Dashboard UI test project information.", DateTimeOffset.UtcNow), 3),
            new(new ProjectInformationResult(Guid.Parse("cccccccc-0000-0000-0000-000000000002"), "dashboard-test-secondary", "Dashboard test secondary", "Secondary Dashboard UI test project information.", DateTimeOffset.UtcNow), 2)
        ];
        return Task.FromResult(projects);
    }

    public Task<ProjectInformationResult> UpsertProjectInformationAsync(ProjectInformationUpdateRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new ProjectInformationResult(
            Guid.Parse("cccccccc-0000-0000-0000-000000000001"),
            request.ProjectId,
            string.IsNullOrWhiteSpace(request.DisplayName) ? request.ProjectId : request.DisplayName.Trim(),
              request.Description,
              DateTimeOffset.UtcNow));

    public Task<ProjectInformationResult> UpdateProjectLifecycleAsync(ProjectLifecycleUpdateRequest request, CancellationToken cancellationToken)
    {
        DateTimeOffset? archivedAt = request.Action == ProjectLifecycleAction.Archive ? DateTimeOffset.UtcNow : null;
        return Task.FromResult(new ProjectInformationResult(
            Guid.Parse("cccccccc-0000-0000-0000-000000000001"),
            request.ProjectId,
            request.ProjectId,
            "Dashboard UI test project information.",
            DateTimeOffset.UtcNow,
            request.Action == ProjectLifecycleAction.Hide,
            archivedAt,
            archivedAt?.AddDays(7)));
    }

    public Task<ProjectHierarchyResult> GetProjectChildrenAsync(string parentProjectId, CancellationToken cancellationToken)
        => Task.FromResult(new ProjectHierarchyResult(
            parentProjectId,
            string.Equals(parentProjectId, "dashboard-test", StringComparison.OrdinalIgnoreCase)
                ? ["dashboard-test-secondary"]
                : [],
            DateTimeOffset.UtcNow));

    public Task<ProjectHierarchyResult> SetProjectChildrenAsync(ProjectHierarchySetChildrenRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new ProjectHierarchyResult(request.ParentProjectId, request.ChildProjectIds, DateTimeOffset.UtcNow));

    public Task<IReadOnlyList<DiscussionThreadResult>> GetDiscussionThreadsAsync(DiscussionThreadListRequest request, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<DiscussionThreadResult>>([]);

    public Task<DiscussionThreadDetailResult?> GetDiscussionThreadAsync(Guid threadId, string readerProjectId, CancellationToken cancellationToken)
        => Task.FromResult<DiscussionThreadDetailResult?>(null);

    public Task<DiscussionThreadResult?> CloseDiscussionThreadAsync(Guid threadId, CancellationToken cancellationToken)
        => Task.FromResult<DiscussionThreadResult?>(null);

    public Task<DiscussionThreadResult?> SetDiscussionThreadArchivedAsync(Guid threadId, bool archived, CancellationToken cancellationToken)
        => Task.FromResult<DiscussionThreadResult?>(null);

    public Task<DiscussionThreadResult?> AdvanceDiscussionThreadReadCursorAsync(Guid threadId, string readerProjectId, Guid lastReadMessageId, CancellationToken cancellationToken)
        => Task.FromResult<DiscussionThreadResult?>(null);

    public Task<DiscussionThreadDetailResult> CreateDiscussionThreadAsync(DiscussionThreadCreateRequest request, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<DiscussionMessageResult> CreateDiscussionMessageAsync(DiscussionMessageCreateRequest request, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<MemoryDataRetentionRunResult> RunMemoryDataRetentionAsync(MemoryDataRetentionRunRequest request, CancellationToken cancellationToken)
        => Task.FromResult(BuildRetentionResult(request.Mode));

    public Task<IReadOnlyList<SourceConnectionResult>> GetSourcesAsync(SourceListRequest request, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<SourceConnectionResult>>(
        [
            new SourceConnectionResult(Guid.NewGuid(), request.ProjectId, "Local Repo", SourceKind.LocalRepo, true, """{"rootPath":"W:/Repositories/WJCY/ContextHub"}""", false, string.Empty, DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow)
        ]);

    private static MaintenanceStatusResult BuildInactiveMaintenanceStatus()
        => new(
            MaintenancePhase.Inactive,
            false,
            string.Empty,
            string.Empty,
            null,
            null,
            null,
            null,
            string.Empty,
            15,
            0,
            []);

    private static MemoryDataRetentionRunResult BuildRetentionResult(MemoryDataRetentionRunMode mode)
    {
        var now = DateTimeOffset.UtcNow;
        var thresholds = new MemoryDataRetentionPolicyThresholds(365, 180, 0, 0, 0.55m, 0.70m, 50, 90, 20, 5000);
        var autoDeleteCandidates = new[]
        {
            new MemoryDataRetentionCandidateResult(
                Guid.Parse("62000000-0000-0000-0000-000000000001"),
                ProjectContext.DefaultProjectId,
                "Expired low signal memory",
                MemoryType.Episode,
                MemoryStatus.Archived,
                0.20m,
                0.40m,
                now.AddDays(-400),
                0,
                0,
                MemoryRetentionRecommendedAction.Delete,
                ["archivedRetentionExpired", "lowImportance", "lowConfidence"],
                [])
        };
        var reviewCandidates = new[]
        {
            new MemoryDataRetentionCandidateResult(
                Guid.Parse("62000000-0000-0000-0000-000000000002"),
                ProjectContext.DefaultProjectId,
                "Important archived decision",
                MemoryType.Decision,
                MemoryStatus.Archived,
                0.95m,
                0.90m,
                now.AddDays(-500),
                1,
                2,
                MemoryRetentionRecommendedAction.Keep,
                ["archivedRetentionExpired"],
                ["protectedType", "linkedMemory"])
        };
        var deletedItems = mode == MemoryDataRetentionRunMode.ApplyAutoDelete ? 1 : 0;
        var resultJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            mode,
            autoDeleteCandidateCount = autoDeleteCandidates.Length,
            reviewCandidateCount = reviewCandidates.Length,
            autoDeleteCandidates,
            reviewCandidates,
            deletedItems,
            blockedReasons = new[] { "protectedType", "linkedMemory" },
            policyThresholds = thresholds
        }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        return new MemoryDataRetentionRunResult(
            Guid.NewGuid(),
            now.AddDays(-365),
            deletedItems,
            deletedItems,
            deletedItems,
            deletedItems,
            deletedItems,
            [ProjectContext.DefaultProjectId],
            mode == MemoryDataRetentionRunMode.PreviewDelete,
            mode,
            thresholds,
            autoDeleteCandidates.Length,
            reviewCandidates.Length,
            autoDeleteCandidates,
            reviewCandidates,
            ["archivedRetentionExpired", "lowImportance", "lowConfidence"],
            ["protectedType", "linkedMemory"],
            now.AddSeconds(-2),
            now,
            resultJson);
    }

    public Task<SourceConnectionResult> CreateSourceAsync(SourceConnectionCreateRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new SourceConnectionResult(Guid.NewGuid(), request.ProjectId, request.Name, request.SourceKind, request.Enabled, request.ConfigJson, !string.IsNullOrWhiteSpace(request.SecretJson), string.Empty, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

    public Task<SourceConnectionResult> UpdateSourceAsync(SourceConnectionUpdateRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new SourceConnectionResult(request.Id, request.ProjectId ?? ProjectContext.DefaultProjectId, request.Name ?? "Updated Source", SourceKind.LocalRepo, request.Enabled ?? true, request.ConfigJson ?? "{}", !string.IsNullOrWhiteSpace(request.SecretJson), string.Empty, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow));

    public Task<EnqueueSourceSyncResult> SyncSourceAsync(Guid id, SourceSyncRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new EnqueueSourceSyncResult(Guid.NewGuid(), MemoryJobStatus.Pending));

    public Task<IReadOnlyList<SourceSyncRunResult>> GetSourceRunsAsync(Guid id, string? projectId, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<SourceSyncRunResult>>(
        [
            new SourceSyncRunResult(Guid.NewGuid(), id, projectId ?? ProjectContext.DefaultProjectId, SourceSyncTrigger.Manual, SourceSyncStatus.Completed, 8, 4, 1, 0, "before", "after", string.Empty, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(-4))
        ]);

    public Task<IReadOnlyList<GovernanceFindingResult>> GetGovernanceFindingsAsync(GovernanceFindingListRequest request, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<GovernanceFindingResult>>(
        [
            new GovernanceFindingResult(Guid.NewGuid(), request.ProjectId, null, Guid.NewGuid(), null, GovernanceFindingType.ReindexRequired, GovernanceFindingStatus.Open, "需要重新索引：示範記憶", "目前向量資料未對齊。", "{}", "demo", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow)
        ]);

    public Task<GovernanceAnalyzeResult> AnalyzeGovernanceAsync(GovernanceAnalyzeRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new GovernanceAnalyzeResult(request.ProjectId, 1, 1, DateTimeOffset.UtcNow));

    public Task<GovernanceFindingResult> AcceptGovernanceFindingAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult(new GovernanceFindingResult(id, ProjectContext.DefaultProjectId, null, Guid.NewGuid(), null, GovernanceFindingType.ReindexRequired, GovernanceFindingStatus.Accepted, "接受 finding", "accepted", "{}", "demo", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow));

    public Task<GovernanceFindingResult> DismissGovernanceFindingAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult(new GovernanceFindingResult(id, ProjectContext.DefaultProjectId, null, Guid.NewGuid(), null, GovernanceFindingType.ReindexRequired, GovernanceFindingStatus.Dismissed, "忽略 finding", "dismissed", "{}", "demo", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow));

    public Task<IReadOnlyList<EvaluationSuiteResult>> GetEvaluationSuitesAsync(string projectId, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<EvaluationSuiteResult>>(
        [
            new EvaluationSuiteResult(Guid.NewGuid(), projectId, "Dashboard Test Suite", "Demo suite", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, [new EvaluationCaseResult(Guid.NewGuid(), Guid.NewGuid(), projectId, "Scenario", "demo query", [], ["demo-memory"], DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow)])
        ]);

    public Task<EvaluationSuiteResult> CreateEvaluationSuiteAsync(EvaluationSuiteCreateRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new EvaluationSuiteResult(Guid.NewGuid(), request.ProjectId, request.Name, request.Description, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []));

    public Task<EvaluationRunResult> RunEvaluationAsync(EvaluationRunRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new EvaluationRunResult(Guid.NewGuid(), request.SuiteId, ProjectContext.DefaultProjectId, EvaluationRunStatus.Completed, "compact", request.QueryMode, request.UseSummaryLayer, request.TopK, 1m, 1m, 1m, 10d, string.Empty, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []));

    public Task<EvaluationRunResult?> GetEvaluationRunAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult<EvaluationRunResult?>(new EvaluationRunResult(id, Guid.NewGuid(), ProjectContext.DefaultProjectId, EvaluationRunStatus.Completed, "compact", MemoryQueryMode.CurrentOnly, false, 5, 1m, 1m, 1m, 10d, string.Empty, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []));

    public Task<IReadOnlyList<SuggestedActionResult>> GetSuggestedActionsAsync(SuggestedActionListRequest request, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<SuggestedActionResult>>(
        [
            new SuggestedActionResult(Guid.NewGuid(), request.ProjectId, SuggestedActionType.ReindexProject, SuggestedActionStatus.Pending, "重新索引專案", "評測品質回退。", "{}", string.Empty, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, null)
        ]);

    public Task<SuggestedActionMutationResult> AcceptSuggestedActionAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult(new SuggestedActionMutationResult(new SuggestedActionResult(id, ProjectContext.DefaultProjectId, SuggestedActionType.ReindexProject, SuggestedActionStatus.Executed, "重新索引專案", "已執行。", "{}", string.Empty, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), Guid.NewGuid()));

    public Task<SuggestedActionResult> DismissSuggestedActionAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult(new SuggestedActionResult(id, ProjectContext.DefaultProjectId, SuggestedActionType.ReindexProject, SuggestedActionStatus.Dismissed, "重新索引專案", "已忽略。", "{}", string.Empty, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, null));

    public Task<IReadOnlyList<TenantResult>> GetTenantsAsync(bool includeArchived, int limit, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<TenantResult>>(
        [
            DemoTenant()
        ]);

    public Task<TenantResult> CreateTenantAsync(TenantCreateRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new TenantResult(Guid.NewGuid(), request.Slug, request.DisplayName, TenantStatus.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

    public Task<IReadOnlyList<TenantUserResult>> GetTenantUsersAsync(Guid tenantId, bool includeArchived, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<TenantUserResult>>(
        [
            DemoUser(tenantId)
        ]);

    public Task<TenantUserResult> CreateTenantUserAsync(TenantUserCreateRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new TenantUserResult(Guid.NewGuid(), request.TenantId, request.Username, request.DisplayName, request.Email, request.Role, TenantUserStatus.Active, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

    public Task<IReadOnlyList<TenantProjectGrantResult>> GetTenantProjectGrantsAsync(Guid tenantId, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<TenantProjectGrantResult>>(
        [
            new TenantProjectGrantResult(Guid.Parse("74000000-0000-0000-0000-000000000001"), tenantId, "ContextHub", true, true, true, DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow)
        ]);

    public Task<TenantProjectGrantResult> UpsertTenantProjectGrantAsync(TenantProjectGrantUpsertRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new TenantProjectGrantResult(Guid.NewGuid(), request.TenantId, request.ProjectId, request.CanRead, request.CanWrite, request.CanManageTokens, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

    public Task<IReadOnlyList<ApiTokenResult>> GetApiTokensAsync(Guid tenantId, bool includeRevoked, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<ApiTokenResult>>(
        [
            new ApiTokenResult(Guid.Parse("75000000-0000-0000-0000-000000000001"), tenantId, DemoUser(tenantId).Id, "Codex MCP", "外部連線", "ctxh_demo", "9F0A", ["memory:read"], ["ContextHub"], null, null, DateTimeOffset.UtcNow.AddMinutes(-10), "203.0.113.42", "codex-mcp-client/1.0", DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow)
        ]);

    public Task<ApiTokenCreatedResult> CreateApiTokenAsync(ApiTokenCreateRequest request, CancellationToken cancellationToken)
    {
        var token = new ApiTokenResult(Guid.NewGuid(), request.TenantId, request.OwnerUserId, request.Name, request.Notes ?? string.Empty, "ctxh_demo", "9F0A", request.Scopes ?? ["memory:read"], request.AllowedProjectIds ?? [], request.ExpiresAt, null, null, string.Empty, string.Empty, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        return Task.FromResult(new ApiTokenCreatedResult(token, "ctxh_demo_plain_token_9F0A"));
    }

    public Task<ApiTokenResult> UpdateApiTokenAsync(Guid tokenId, ApiTokenUpdateRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new ApiTokenResult(tokenId, DemoTenant().Id, DemoUser(DemoTenant().Id).Id, request.Name ?? "Updated Token", request.Notes ?? string.Empty, "ctxh_demo", "9F0A", request.Scopes ?? ["memory:read"], request.AllowedProjectIds ?? [], request.ExpiresAt, null, null, string.Empty, string.Empty, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow));

    public Task<ApiTokenResult> RevokeApiTokenAsync(Guid tokenId, CancellationToken cancellationToken)
        => Task.FromResult(new ApiTokenResult(tokenId, DemoTenant().Id, DemoUser(DemoTenant().Id).Id, "Revoked Token", "已撤銷", "ctxh_demo", "9F0A", ["memory:read"], ["ContextHub"], null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(-10), "203.0.113.42", "codex-mcp-client/1.0", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow));

    public Task<ApiTokenCreatedResult> RegenerateApiTokenAsync(Guid tokenId, CancellationToken cancellationToken)
    {
        var token = new ApiTokenResult(tokenId, DemoTenant().Id, DemoUser(DemoTenant().Id).Id, "Regenerated Token", string.Empty, "ctxh_new", "1A2B", ["memory:read"], ["ContextHub"], null, null, null, string.Empty, string.Empty, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow);
        return Task.FromResult(new ApiTokenCreatedResult(token, "ctxh_new_plain_token_1A2B"));
    }

    public Task<CurrentUserResult> GetCurrentUserAsync(CancellationToken cancellationToken)
    {
        var tenant = DemoTenant();
        var user = DemoUser(tenant.Id);
        return Task.FromResult(new CurrentUserResult(tenant.Id, user.Id, user.Username, user.DisplayName, user.Email, user.Role));
    }

    public Task<IReadOnlyList<ApiTokenResult>> GetMyApiTokensAsync(bool includeRevoked, CancellationToken cancellationToken)
        => GetApiTokensAsync(DemoTenant().Id, includeRevoked, cancellationToken);

    public Task<ApiTokenCreatedResult> CreateMyApiTokenAsync(ApiTokenCreateRequest request, CancellationToken cancellationToken)
        => CreateApiTokenAsync(request, cancellationToken);

    public Task<ApiTokenResult> UpdateMyApiTokenAsync(Guid tokenId, ApiTokenUpdateRequest request, CancellationToken cancellationToken)
        => UpdateApiTokenAsync(tokenId, request, cancellationToken);

    public Task<ApiTokenCreatedResult> RegenerateMyApiTokenAsync(Guid tokenId, CancellationToken cancellationToken)
        => RegenerateApiTokenAsync(tokenId, cancellationToken);

    public Task<ApiTokenResult> RevokeMyApiTokenAsync(Guid tokenId, CancellationToken cancellationToken)
        => RevokeApiTokenAsync(tokenId, cancellationToken);

    public Task<IReadOnlyList<SecurityAuditEventResult>> GetSecurityAuditEventsAsync(Guid? tenantId, int limit, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<SecurityAuditEventResult>>(
        [
            new SecurityAuditEventResult(Guid.NewGuid(), tenantId ?? DemoTenant().Id, DemoUser(tenantId ?? DemoTenant().Id).Id, Guid.Parse("75000000-0000-0000-0000-000000000001"), SecurityAuditEventType.ApiTokenAuthenticated, "Succeeded", "203.0.113.42", "codex-mcp-client/1.0", """{"name":"Codex MCP"}""", DateTimeOffset.UtcNow.AddMinutes(-10))
        ]);

    private static TenantResult DemoTenant()
        => new(Guid.Parse("72000000-0000-0000-0000-000000000001"), "context-team", "Context Team", TenantStatus.Active, DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow);

    private static TenantUserResult DemoUser(Guid tenantId)
        => new(Guid.Parse("73000000-0000-0000-0000-000000000001"), tenantId, "admin", "Admin User", "admin@example.com", TenantUserRole.Owner, TenantUserStatus.Active, DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow);

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

    public Task<AgentConnectivitySettingsResult> GetAgentConnectivitySettingsAsync(CancellationToken cancellationToken)
        => Task.FromResult(new AgentConnectivitySettingsResult(true, AgentConnectivityTelemetryProfile.Balanced, 0.2, 1.0, 60, 15, 100, 60, 7, 14));

    public Task<AgentConnectivityStatusResult> GetAgentConnectivityStatusAsync(string? projectId, CancellationToken cancellationToken)
        => Task.FromResult(new AgentConnectivityStatusResult(
            string.IsNullOrWhiteSpace(projectId) ? "ContextHub" : projectId,
            AgentConnectivityStatus.Healthy,
            DateTimeOffset.UtcNow.AddSeconds(-15),
            24,
            1,
            0.04,
            180,
            "Recent agent connectivity telemetry is healthy."));

    public Task<IReadOnlyList<AgentConnectivitySummaryResult>> GetAgentConnectivitySummariesAsync(
        AgentConnectivitySummaryQuery request,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        IReadOnlyList<AgentConnectivitySummaryResult> rows =
        [
            new(
                now.AddMinutes(-1),
                1,
                string.IsNullOrWhiteSpace(request.ProjectId) ? "ContextHub" : request.ProjectId,
                "stdio-bridge",
                "context-hub.local",
                "mcp-streamable-http",
                "tools/call",
                "memory_search",
                12,
                11,
                1,
                0,
                0,
                1,
                120,
                180,
                260,
                now.AddSeconds(-20),
                AgentConnectivityStatus.Degraded)
        ];
        return Task.FromResult(rows);
    }

    public Task<IReadOnlyList<AgentConnectivityRecentObservationResult>> GetRecentAgentConnectivityObservationsAsync(
        string? projectId,
        string? agentId,
        int limit,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AgentConnectivityRecentObservationResult> rows =
        [
            new(
                Guid.Parse("aaaaaaaa-1111-2222-3333-000000000001"),
                string.IsNullOrWhiteSpace(projectId) ? "ContextHub" : projectId,
                string.IsNullOrWhiteSpace(agentId) ? "stdio-bridge" : agentId,
                "context-hub.local",
                "tools/call",
                "memory_search",
                true,
                null,
                string.Empty,
                132,
                false,
                "dashboard-test",
                DateTimeOffset.UtcNow.AddSeconds(-30))
        ];
        return Task.FromResult(rows);
    }
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
            DashboardAuth = new InstanceDashboardAuthSettingsResult(
                "admin",
                dashboardOptionsAccessor.Value.SessionTimeoutMinutes)
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
                    5,
                    5,
                    5,
                    5,
                    5,
                    5,
                    5),
                options.Polling.OverviewSeconds,
                options.Polling.MetricsSeconds,
                options.Polling.JobsSeconds,
                options.Polling.LogsSeconds,
                options.Polling.PerformanceSeconds),
            new InstanceDashboardAuthSettingsResult(username, options.SessionTimeoutMinutes),
            new ConversationAutomationStatusResult(0, 0, 0, string.Empty));
}
