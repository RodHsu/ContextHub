using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Playwright;

namespace Memory.DashboardTests;

public sealed class DashboardBrowserUiTests : IClassFixture<DashboardBrowserFixture>
{
    private static readonly DashboardTheme[] Themes =
    [
        DashboardTheme.Dark,
        DashboardTheme.Light
    ];

    private static readonly DashboardViewport[] Viewports =
    [
        new("desktop", 1440, 900),
        new("wide-2k", 2560, 1440),
        new("tablet", 1024, 1366),
        new("mobile", 390, 844)
    ];

    private static readonly DashboardRouteSpec[] Routes =
    [
        new("overview", "/", "總覽", [".metric-grid", ".dashboard-grid", ".resource-chart-grid"], [".page-header", ".metric-grid", ".dashboard-grid"], [".content", ".dashboard-grid.page-scroll-host"]),
        new("runtime", "/runtime", "執行參數", [".runtime-page-stack", ".runtime-main-panel", ".runtime-parameters-panel"], [".page-header", ".runtime-main-panel", ".runtime-parameters-panel"], [".content", ".runtime-page-stack"]),
        new("monitoring", "/monitoring", "狀態監控", [".monitoring-page-stack", ".monitoring-top-grid", ".monitoring-telemetry-grid"], [".page-header", ".monitoring-top-grid", ".monitoring-telemetry-grid"], [".content", ".monitoring-page-stack"]),
        new("memories", "/memories", "記憶資料", [".page-actions-secondary .info-popover", ".filter-panel", ".split-layout"], [".page-header", ".filter-panel", ".split-layout"], [".content", ".split-layout"]),
        new("graph", "/graph", "記憶圖譜", [".graph-workspace", ".graph-filter-panel", ".graph-scroll-shell"], [".page-header", ".graph-workspace"], [".content", ".graph-scroll-shell", ".graph-detail-panel"]),
        new("sources", "/sources", "資料來源", [".sources-page-stack", ".sources-setup-grid", ".sources-workspace-section"], [".page-header", ".sources-setup-grid", ".sources-workspace-section"], [".content", ".sources-page-stack", ".panel-scroll-body"]),
        new("governance", "/governance", "治理檢查", [".governance-page-stack", ".metric-grid", ".governance-workspace-section"], [".page-header", ".metric-grid", ".governance-workspace-section"], [".content", ".governance-page-stack", ".panel-scroll-body"]),
        new("evaluation", "/evaluation", "評估驗證", [".evaluation-page-stack", ".filter-panel", ".evaluation-workspace-section"], [".page-header", ".filter-panel", ".evaluation-workspace-section"], [".content", ".evaluation-page-stack", ".panel-scroll-body"]),
        new("inbox", "/inbox", "收件匣", [".inbox-page-stack", ".metric-grid", ".inbox-workspace-section"], [".page-header", ".metric-grid", ".inbox-workspace-section"], [".content", ".inbox-page-stack", ".panel-scroll-body"]),
        new("preferences", "/preferences", "使用者偏好", [".split-layout", ".preferences-list-panel", ".stack-scroll-shell"], [".page-header", ".split-layout"], [".content", ".stack-scroll-shell"]),
        new("logs", "/logs", "日誌", [".logs-filter-grid", ".split-layout", ".table-scroll-shell"], [".filter-panel", ".split-layout"], [".content", ".table-scroll-shell"]),
        new("jobs", "/jobs", "工作佇列", [".split-layout", ".data-table", ".detail-panel"], [".page-header", ".jobs-page-body > .split-layout:last-of-type"], [".content", ".panel-scroll-body"]),
        new("storage", "/storage", "資料庫檢視", [".storage-layout", ".storage-table-panel", ".storage-detail-panel"], [".storage-table-panel", ".storage-detail-panel"], [".content", ".storage-table-list", ".table-scroll-shell"]),
        new("performance", "/performance", "效能", [".performance-form-grid", ".performance-config-footer", ".empty-inline"], [".page-header", ".performance-page-body"], [".content", ".performance-results-shell"]),
        new("settings", "/settings", "系統設定", [".settings-layout", ".settings-form-grid", ".settings-transfer-panel"], [".settings-info-panel", ".settings-auth-panel"], [".content", ".settings-layout"])
    ];

    private static readonly DashboardRouteSpec[] DenseRoutes =
    [
        Routes.Single(route => route.Name == "overview"),
        Routes.Single(route => route.Name == "runtime"),
        Routes.Single(route => route.Name == "monitoring"),
        Routes.Single(route => route.Name == "memories"),
        Routes.Single(route => route.Name == "graph"),
        Routes.Single(route => route.Name == "logs"),
        Routes.Single(route => route.Name == "storage"),
        Routes.Single(route => route.Name == "performance")
    ];

    private static readonly DashboardRouteSpec[] EmptyRoutes =
    [
        Routes.Single(route => route.Name == "sources"),
        Routes.Single(route => route.Name == "governance"),
        Routes.Single(route => route.Name == "evaluation"),
        Routes.Single(route => route.Name == "inbox"),
        Routes.Single(route => route.Name == "graph"),
        Routes.Single(route => route.Name == "memories"),
        Routes.Single(route => route.Name == "preferences"),
        Routes.Single(route => route.Name == "logs"),
        Routes.Single(route => route.Name == "jobs"),
        Routes.Single(route => route.Name == "storage")
    ];

    private readonly DashboardBrowserFixture _fixture;

    public DashboardBrowserUiTests(DashboardBrowserFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Dashboard_Pages_Should_Render_Cleanly_Across_Themes_Desktop_Tablet_And_Mobile()
    {
        var failures = new List<string>();

        foreach (var theme in Themes)
        {
            foreach (var viewport in Viewports)
            {
                foreach (var route in Routes)
                {
                    try
                    {
                        await ValidateRouteAsync(route, DashboardUiProfile.Normal, viewport, theme);
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{theme.Name} / {viewport.Name} / {route.Name}: {ex}");
                    }
                }
            }
        }

        failures.Should().BeEmpty(string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public async Task Dense_Data_Pages_Should_Stay_Usable_After_Real_Interactions()
    {
        var failures = new List<string>();
        var viewport = Viewports[0];

        foreach (var route in DenseRoutes)
        {
            try
            {
                await ValidateRouteAsync(route, DashboardUiProfile.Dense, viewport, DashboardTheme.Dark, enableInteractions: true);
            }
            catch (Exception ex)
            {
                failures.Add($"dense / {route.Name}: {ex}");
            }
        }

        failures.Should().BeEmpty(string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public async Task Empty_State_Pages_Should_Remain_Readable_Without_Broken_Layout()
    {
        var failures = new List<string>();
        var viewport = Viewports[2];

        foreach (var route in EmptyRoutes)
        {
            try
            {
                await ValidateRouteAsync(route, DashboardUiProfile.Empty, viewport, DashboardTheme.Dark);
            }
            catch (Exception ex)
            {
                failures.Add($"empty / {route.Name}: {ex}");
            }
        }

        failures.Should().BeEmpty(string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public async Task Evaluation_Create_Suite_Form_Should_Render_Without_Horizontal_Overflow_On_Desktop()
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(Viewports[0]);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/evaluation?uiProfile=normal");

        var layoutJson = await page.EvaluateAsync<string>(
            @"() => JSON.stringify({
                topRowCount: [...new Set(Array.from(document.querySelectorAll('.evaluation-form-grid > label'))
                    .map(item => Math.round(item.getBoundingClientRect().top)))].length,
                formScrollWidth: document.querySelector('.evaluation-form-grid')?.scrollWidth ?? 0,
                formClientWidth: document.querySelector('.evaluation-form-grid')?.clientWidth ?? 0,
                caseScrollWidth: document.querySelector('.evaluation-case-grid')?.scrollWidth ?? 0,
                caseClientWidth: document.querySelector('.evaluation-case-grid')?.clientWidth ?? 0,
                workspaceScrollWidth: document.querySelector('.evaluation-split-layout')?.scrollWidth ?? 0,
                workspaceClientWidth: document.querySelector('.evaluation-split-layout')?.clientWidth ?? 0
            })");

        using var document = JsonDocument.Parse(layoutJson);
        document.RootElement.GetProperty("topRowCount").GetInt32().Should().Be(1);
        document.RootElement.GetProperty("formScrollWidth").GetInt32().Should().BeLessThanOrEqualTo(document.RootElement.GetProperty("formClientWidth").GetInt32() + 1);
        document.RootElement.GetProperty("caseScrollWidth").GetInt32().Should().BeLessThanOrEqualTo(document.RootElement.GetProperty("caseClientWidth").GetInt32() + 1);
        document.RootElement.GetProperty("workspaceScrollWidth").GetInt32().Should().BeLessThanOrEqualTo(document.RootElement.GetProperty("workspaceClientWidth").GetInt32() + 1);
    }

    [Fact]
    public async Task Sources_Create_Panel_Should_Finish_Before_Workspace_Section_Starts()
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(Viewports[0]);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/sources?uiProfile=normal");

        var layoutJson = await page.EvaluateAsync<string>(
            @"() => JSON.stringify({
                panelBottom: Math.round(document.querySelector('#source-config-panel')?.getBoundingClientRect().bottom ?? 0),
                actionBottom: Math.round(document.querySelector('#source-config-panel .inline-actions')?.getBoundingClientRect().bottom ?? 0),
                workspaceTop: Math.round(document.querySelector('.sources-workspace-section')?.getBoundingClientRect().top ?? 0)
            })");

        using var document = JsonDocument.Parse(layoutJson);
        var panelBottom = document.RootElement.GetProperty("panelBottom").GetInt32();
        var actionBottom = document.RootElement.GetProperty("actionBottom").GetInt32();
        var workspaceTop = document.RootElement.GetProperty("workspaceTop").GetInt32();

        panelBottom.Should().BeGreaterThanOrEqualTo(actionBottom - 1);
        workspaceTop.Should().BeGreaterThanOrEqualTo(panelBottom - 1);
    }

    [Fact]
    public async Task Evaluation_Create_Form_Should_Finish_Before_Workspace_Section_Starts()
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(Viewports[0]);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/evaluation?uiProfile=normal");

        var layoutJson = await page.EvaluateAsync<string>(
            @"() => JSON.stringify({
                formBottom: Math.round(document.querySelector('#evaluation-suite-form')?.getBoundingClientRect().bottom ?? 0),
                actionBottom: Math.round(document.querySelector('#evaluation-suite-form .inline-actions')?.getBoundingClientRect().bottom ?? 0),
                workspaceTop: Math.round(document.querySelector('.evaluation-workspace-section')?.getBoundingClientRect().top ?? 0)
            })");

        using var document = JsonDocument.Parse(layoutJson);
        var formBottom = document.RootElement.GetProperty("formBottom").GetInt32();
        var actionBottom = document.RootElement.GetProperty("actionBottom").GetInt32();
        var workspaceTop = document.RootElement.GetProperty("workspaceTop").GetInt32();

        formBottom.Should().BeGreaterThanOrEqualTo(actionBottom - 1);
        workspaceTop.Should().BeGreaterThanOrEqualTo(formBottom - 1);
    }

    [Fact]
    public async Task Evaluation_Create_Suite_Should_Show_Client_Validation_For_Missing_Required_Fields()
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(Viewports[0]);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/evaluation?uiProfile=normal");

        await page.GetByRole(AriaRole.Button, new() { Name = "建立評測集" }).ClickAsync();

        var summary = page.Locator(".validation-summary");
        await summary.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15000
        });

        var text = await summary.InnerTextAsync();
        text.Should().Contain("請填寫評測組名稱");
        text.Should().Contain("請填寫案例標籤");
        text.Should().Contain("請填寫查詢字串");
        text.Should().Contain("請至少提供一個 expected external key");
    }

    [Fact]
    public async Task Copy_Action_Should_Show_Resolved_Toast_Message()
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(Viewports[0]);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/logs?uiProfile=normal");
        await page.Locator(".data-table-clickable tbody tr").First.ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "複製 JSON" }).ClickAsync();

        var toast = page.Locator(".toast").First;
        await toast.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15000
        });

        var text = await toast.InnerTextAsync();
        text.Should().Contain("已複製日誌 #");
        text.Should().NotContain("_message");
    }

    [Fact]
    public async Task Login_Page_Display_Copy_Should_Not_Be_User_Selectable()
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(Viewports[0]);
        var page = await context.NewPageAsync();

        await page.GotoAsync(new Uri(_fixture.BaseUri, "/login").ToString());
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var stylesJson = await page.EvaluateAsync<string>(
            @"() => JSON.stringify({
                title: getComputedStyle(document.querySelector('.login-title') ?? document.body).userSelect,
                cardTitle: getComputedStyle(document.querySelector('.login-card-title') ?? document.body).userSelect,
                chip: getComputedStyle(document.querySelector('.login-chip') ?? document.body).userSelect,
                footer: getComputedStyle(document.querySelector('.login-footer') ?? document.body).userSelect,
                usernameInput: getComputedStyle(document.querySelector('input[name=""Username""]') ?? document.body).userSelect,
                error: getComputedStyle(document.querySelector('.toast-error') ?? document.body).userSelect
            })");

        using var document = JsonDocument.Parse(stylesJson);
        document.RootElement.GetProperty("title").GetString().Should().Be("none");
        document.RootElement.GetProperty("cardTitle").GetString().Should().Be("none");
        document.RootElement.GetProperty("chip").GetString().Should().Be("none");
        document.RootElement.GetProperty("footer").GetString().Should().Be("none");
        document.RootElement.GetProperty("usernameInput").GetString().Should().NotBe("none");
        document.RootElement.GetProperty("error").GetString().Should().NotBe("none");
    }

    [Fact]
    public async Task Memories_Project_Suggestions_Should_Hide_When_Field_Loses_Focus()
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(Viewports[0]);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/memories?uiProfile=normal");

        var projectInput = page.GetByPlaceholder("目前專案 (Project Id，可模糊搜尋)");
        var queryInput = page.GetByPlaceholder("搜尋標題 / 摘要 / 來源參照");
        var suggestionList = page.Locator(".project-suggestion-list");

        await projectInput.ClickAsync();
        await suggestionList.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15000
        });

        await queryInput.ClickAsync();
        await suggestionList.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Hidden,
            Timeout = 15000
        });
    }

    [Fact]
    public async Task Memories_Return_To_Current_Project_Should_Not_Backfill_Project_Input_When_No_Explicit_Project_Was_Selected()
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(Viewports[0]);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/memories?uiProfile=normal");

        var projectInput = page.GetByPlaceholder("目前專案 (Project Id，可模糊搜尋)");
        projectInput.Should().NotBeNull();

        (await projectInput.InputValueAsync()).Should().BeEmpty();

        await page.GetByRole(AriaRole.Button, new() { Name = "查看共用綜合層" }).ClickAsync();
        await page.WaitForTimeoutAsync(400);

        await page.GetByRole(AriaRole.Button, new() { Name = "回到目前專案" }).ClickAsync();
        await page.WaitForTimeoutAsync(400);

        (await projectInput.InputValueAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Memories_Return_To_Current_Project_Should_Restore_Explicit_Project_Filter_When_One_Was_Selected()
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(Viewports[0]);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/memories?uiProfile=normal");

        var projectInput = page.GetByPlaceholder("目前專案 (Project Id，可模糊搜尋)");
        await projectInput.ClickAsync();
        await page.Locator(".project-suggestion-item").First.ClickAsync();
        await page.WaitForTimeoutAsync(400);
        var selectedProjectId = await projectInput.InputValueAsync();
        selectedProjectId.Should().NotBeNullOrWhiteSpace();

        await page.GetByRole(AriaRole.Button, new() { Name = "查看共用綜合層" }).ClickAsync();
        await page.WaitForTimeoutAsync(400);

        await page.GetByRole(AriaRole.Button, new() { Name = "回到目前專案" }).ClickAsync();
        await page.WaitForTimeoutAsync(400);

        (await projectInput.InputValueAsync()).Should().Be(selectedProjectId);
    }

    [Fact]
    public async Task Memories_Scope_Shortcuts_Should_Be_Mutually_Exclusive()
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(Viewports[0]);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/memories?uiProfile=normal");

        var viewSharedButton = page.GetByRole(AriaRole.Button, new() { Name = "查看共用綜合層" });
        var returnButton = page.GetByRole(AriaRole.Button, new() { Name = "回到目前專案" });

        (await viewSharedButton.CountAsync()).Should().Be(1);
        (await returnButton.CountAsync()).Should().Be(0);

        await viewSharedButton.ClickAsync();
        await page.WaitForTimeoutAsync(400);

        (await page.GetByRole(AriaRole.Button, new() { Name = "查看共用綜合層" }).CountAsync()).Should().Be(0);
        (await page.GetByRole(AriaRole.Button, new() { Name = "回到目前專案" }).CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Memories_Project_Suggestion_Field_Should_Not_Overflow_On_Fhd_Viewport()
    {
        await _fixture.EnsureDashboardRunningAsync();
        var viewport = new DashboardViewport("fhd-1080p", 1920, 1080);
        await using var context = await _fixture.CreateContextAsync(viewport);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/memories?uiProfile=dense");

        var projectInput = page.GetByPlaceholder("目前專案 (Project Id，可模糊搜尋)");
        var suggestionList = page.Locator(".project-suggestion-list");

        await projectInput.ClickAsync();
        await suggestionList.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15000
        });

        var layoutJson = await page.EvaluateAsync<string>(
            @"() => JSON.stringify({
                fieldWidth: Math.round(document.querySelector('.project-suggestion-field')?.getBoundingClientRect().width ?? 0),
                listWidth: Math.round(document.querySelector('.project-suggestion-list')?.getBoundingClientRect().width ?? 0),
                fieldRight: Math.round(document.querySelector('.project-suggestion-field')?.getBoundingClientRect().right ?? 0),
                listRight: Math.round(document.querySelector('.project-suggestion-list')?.getBoundingClientRect().right ?? 0),
                gridScrollWidth: document.querySelector('.memories-filter-grid')?.scrollWidth ?? 0,
                gridClientWidth: document.querySelector('.memories-filter-grid')?.clientWidth ?? 0
            })");

        using var document = JsonDocument.Parse(layoutJson);
        document.RootElement.GetProperty("fieldWidth").GetInt32().Should().BeGreaterThan(0);
        document.RootElement.GetProperty("listWidth").GetInt32().Should().BeLessThanOrEqualTo(document.RootElement.GetProperty("fieldWidth").GetInt32() + 1);
        document.RootElement.GetProperty("listRight").GetInt32().Should().BeLessThanOrEqualTo(document.RootElement.GetProperty("fieldRight").GetInt32() + 1);
        document.RootElement.GetProperty("gridScrollWidth").GetInt32().Should().BeLessThanOrEqualTo(document.RootElement.GetProperty("gridClientWidth").GetInt32() + 1);
    }

    [Fact]
    public async Task Memories_Filter_Should_Render_In_Two_Rows_On_Fhd_Viewport()
    {
        await _fixture.EnsureDashboardRunningAsync();
        var viewport = new DashboardViewport("fhd-1080p", 1920, 1080);
        await using var context = await _fixture.CreateContextAsync(viewport);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/memories?uiProfile=dense");

        var layoutJson = await page.EvaluateAsync<string>(
            @"() => JSON.stringify({
                uniqueRows: [...new Set(Array.from(document.querySelectorAll('.memories-filter-grid > *'))
                    .map(node => Math.round(node.getBoundingClientRect().top)))].length,
                gridScrollWidth: document.querySelector('.memories-filter-grid')?.scrollWidth ?? 0,
                gridClientWidth: document.querySelector('.memories-filter-grid')?.clientWidth ?? 0
            })");

        using var document = JsonDocument.Parse(layoutJson);
        document.RootElement.GetProperty("uniqueRows").GetInt32().Should().Be(2);
        document.RootElement.GetProperty("gridScrollWidth").GetInt32().Should().BeLessThanOrEqualTo(document.RootElement.GetProperty("gridClientWidth").GetInt32() + 1);
    }

    [Fact]
    public async Task Memories_Table_Should_Keep_Localized_Timestamps_And_Compact_Row_Height_After_Reload()
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(Viewports[0]);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/memories?uiProfile=dense");

        var firstUpdatedCell = page.Locator(".memories-table tbody tr td:last-child").First;
        await firstUpdatedCell.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15000
        });

        var initialUpdatedText = (await firstUpdatedCell.InnerTextAsync()).Trim();
        initialUpdatedText.Should().NotBeNullOrWhiteSpace();
        initialUpdatedText.Should().Contain("GMT");

        await page.GetByRole(AriaRole.Button, new() { Name = "查看共用綜合層" }).ClickAsync();
        await page.WaitForTimeoutAsync(500);

        var refreshedUpdatedText = (await firstUpdatedCell.InnerTextAsync()).Trim();
        refreshedUpdatedText.Should().NotBeNullOrWhiteSpace();
        refreshedUpdatedText.Should().Contain("GMT");

        var rowHeights = await page.EvaluateAsync<double[]>(
            "() => Array.from(document.querySelectorAll('.memories-table tbody tr')).slice(0, 4).map(row => row.getBoundingClientRect().height)");

        rowHeights.Should().NotBeEmpty();
        rowHeights.Max().Should().BeLessThan(140d);
    }

    [Fact]
    public async Task Graph_Node_Selection_Should_Update_Detail_Panel()
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(Viewports[0]);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/graph?uiProfile=dense");

        var cards = page.Locator(".graph-node-card");
        await cards.Nth(1).WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15000
        });

        var expectedTitle = await cards.Nth(1).Locator(".graph-node-title").InnerTextAsync();
        await cards.Nth(1).ClickAsync();
        await page.WaitForFunctionAsync(
            "(title) => document.querySelector('.graph-detail-panel')?.innerText?.includes(title) === true",
            expectedTitle);

        var detailPanel = page.Locator(".graph-detail-panel");
        var detailText = await detailPanel.InnerTextAsync();
        detailText.Should().Contain(expectedTitle);

        var className = await cards.Nth(1).GetAttributeAsync("class");
        className.Should().NotBeNull();
        className.Should().MatchRegex("selected");
    }

    [Fact]
    public async Task Graph_Dense_Layout_Should_Avoid_Card_Overlap_On_Desktop()
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(Viewports[0]);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/graph?uiProfile=dense");
        await page.WaitForFunctionAsync("() => (document.querySelectorAll('.graph-node-card').length ?? 0) >= 4");

        var overlapJson = await page.EvaluateAsync<string>(
            @"() => {
                const cards = Array.from(document.querySelectorAll('.graph-node-card')).map(card => {
                    const rect = card.getBoundingClientRect();
                    return {
                        left: rect.left,
                        top: rect.top,
                        right: rect.right,
                        bottom: rect.bottom,
                        width: rect.width,
                        height: rect.height
                    };
                });

                let maxIntersectionArea = 0;
                for (let i = 0; i < cards.length; i += 1) {
                    for (let j = i + 1; j < cards.length; j += 1) {
                        const left = Math.max(cards[i].left, cards[j].left);
                        const right = Math.min(cards[i].right, cards[j].right);
                        const top = Math.max(cards[i].top, cards[j].top);
                        const bottom = Math.min(cards[i].bottom, cards[j].bottom);
                        if (right > left && bottom > top) {
                            maxIntersectionArea = Math.max(maxIntersectionArea, (right - left) * (bottom - top));
                        }
                    }
                }

                return JSON.stringify({
                    count: cards.length,
                    maxIntersectionArea
                });
            }");

        using var document = JsonDocument.Parse(overlapJson);
        document.RootElement.GetProperty("count").GetInt32().Should().BeGreaterThanOrEqualTo(4);
        document.RootElement.GetProperty("maxIntersectionArea").GetDouble().Should().BeLessThan(8);
    }

    [Fact]
    public async Task Graph_Project_Dropdown_Should_Support_AllProjects_Integrated_View()
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(Viewports[0]);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/graph?uiProfile=dense");

        var projectSelect = page.Locator("select[aria-label='專案檢視']");
        await projectSelect.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15000
        });

        var optionTexts = await projectSelect.Locator("option").AllInnerTextsAsync();
        optionTexts.Should().Contain(text => text.Contains("全部專案整合視圖", StringComparison.Ordinal));
        (await projectSelect.InputValueAsync()).Should().Be("__all__");

        await projectSelect.SelectOptionAsync(AllProjectsSelectionValue());
        await page.GetByRole(AriaRole.Button, new() { Name = "更新圖譜" }).ClickAsync();

        var statusStrip = page.Locator(".graph-status-strip");
        await statusStrip.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15000
        });

        var statusText = await statusStrip.InnerTextAsync();
        statusText.Should().Contain("全部專案整合視圖");
    }

    [Fact]
    public async Task Graph_Integrated_View_Should_Render_Project_Overview_Without_Initial_Focus_Clipping()
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(Viewports[0]);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/graph?uiProfile=dense");
        await page.WaitForFunctionAsync("() => (document.querySelectorAll('.graph-node-card').length ?? 0) >= 8");
        await page.WaitForFunctionAsync("() => Number(document.querySelector('.graph-scroll-shell')?.dataset.scale ?? 0) > 0");

        var layoutJson = await page.EvaluateAsync<string>(
            @"() => {
                const shell = document.querySelector('.graph-scroll-shell')?.getBoundingClientRect();
                const cards = Array.from(document.querySelectorAll('.graph-node-card')).map(card => {
                    const rect = card.getBoundingClientRect();
                    return {
                        left: rect.left,
                        top: rect.top,
                        right: rect.right,
                        bottom: rect.bottom
                    };
                });
                const clippedCount = shell
                    ? cards.filter(card =>
                        card.left < shell.left - 1 ||
                        card.top < shell.top - 1 ||
                        card.right > shell.right + 1 ||
                        card.bottom > shell.bottom + 1).length
                    : cards.length;

                return JSON.stringify({
                    count: cards.length,
                    clippedCount,
                    scale: Number(document.querySelector('.graph-scroll-shell')?.dataset.scale ?? 0),
                    statusText: document.querySelector('.graph-status-strip')?.textContent ?? '',
                    focusText: document.querySelector('.graph-detail-actions .ghost-button')?.textContent ?? ''
                });
            }");

        using var document = JsonDocument.Parse(layoutJson);
        document.RootElement.GetProperty("count").GetInt32().Should().BeGreaterThanOrEqualTo(8);
        document.RootElement.GetProperty("clippedCount").GetInt32().Should().Be(0);
        document.RootElement.GetProperty("scale").GetDouble().Should().BeGreaterThan(0.48d);
        document.RootElement.GetProperty("statusText").GetString().Should().Contain("ProjectFull 模式");
        document.RootElement.GetProperty("focusText").GetString().Should().Contain("聚焦此節點");
    }

    [Fact]
    public async Task Graph_Viewport_Should_Support_Wheel_Zoom_And_Drag_Pan()
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(Viewports[0]);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/graph?uiProfile=dense");
        var shell = page.Locator(".graph-scroll-shell");
        await shell.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15000
        });
        await page.WaitForFunctionAsync("() => Number(document.querySelector('.graph-scroll-shell')?.dataset.scale ?? 0) > 0");

        var box = await shell.BoundingBoxAsync();
        box.Should().NotBeNull();

        var beforeJson = await page.EvaluateAsync<string>(
            @"() => JSON.stringify({
                scale: Number(document.querySelector('.graph-scroll-shell')?.dataset.scale ?? 0),
                panX: Number(document.querySelector('.graph-scroll-shell')?.dataset.panX ?? 0),
                panY: Number(document.querySelector('.graph-scroll-shell')?.dataset.panY ?? 0)
            })");

        await page.Mouse.MoveAsync(box!.X + box.Width - 42, box.Y + 42);
        await page.Mouse.WheelAsync(0, -720);
        await page.WaitForTimeoutAsync(180);

        var afterZoomJson = await page.EvaluateAsync<string>(
            @"() => JSON.stringify({
                scale: Number(document.querySelector('.graph-scroll-shell')?.dataset.scale ?? 0),
                panX: Number(document.querySelector('.graph-scroll-shell')?.dataset.panX ?? 0),
                panY: Number(document.querySelector('.graph-scroll-shell')?.dataset.panY ?? 0)
            })");

        using var beforeDocument = JsonDocument.Parse(beforeJson);
        using var afterZoomDocument = JsonDocument.Parse(afterZoomJson);
        var beforeScale = beforeDocument.RootElement.GetProperty("scale").GetDouble();
        var afterZoomScale = afterZoomDocument.RootElement.GetProperty("scale").GetDouble();
        afterZoomScale.Should().BeGreaterThan(beforeScale + 0.05d);

        var startX = box.X + 18;
        var startY = box.Y + 18;
        await page.Mouse.MoveAsync(startX, startY);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(startX + 132, startY + 96, new MouseMoveOptions
        {
            Steps = 8
        });
        await page.Mouse.UpAsync();
        await page.WaitForTimeoutAsync(120);

        var afterPanJson = await page.EvaluateAsync<string>(
            @"() => JSON.stringify({
                scale: Number(document.querySelector('.graph-scroll-shell')?.dataset.scale ?? 0),
                panX: Number(document.querySelector('.graph-scroll-shell')?.dataset.panX ?? 0),
                panY: Number(document.querySelector('.graph-scroll-shell')?.dataset.panY ?? 0)
            })");

        using var afterPanDocument = JsonDocument.Parse(afterPanJson);
        var afterZoomPanX = afterZoomDocument.RootElement.GetProperty("panX").GetDouble();
        var afterZoomPanY = afterZoomDocument.RootElement.GetProperty("panY").GetDouble();
        var afterPanPanX = afterPanDocument.RootElement.GetProperty("panX").GetDouble();
        var afterPanPanY = afterPanDocument.RootElement.GetProperty("panY").GetDouble();

        Math.Abs(afterPanPanX - afterZoomPanX).Should().BeGreaterThan(40);
        Math.Abs(afterPanPanY - afterZoomPanY).Should().BeGreaterThan(24);
    }

    [Fact]
    public async Task Graph_Normal_View_Should_Keep_Small_Graphs_Readable_On_First_Render()
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(Viewports[0]);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/graph?uiProfile=normal");
        await page.WaitForFunctionAsync("() => Number(document.querySelector('.graph-scroll-shell')?.dataset.scale ?? 0) > 0");

        var layoutJson = await page.EvaluateAsync<string>(
            @"() => JSON.stringify({
                scale: Number(document.querySelector('.graph-scroll-shell')?.dataset.scale ?? 0),
                nodeWidth: Math.round(document.querySelector('.graph-node-card')?.getBoundingClientRect().width ?? 0),
                nodeHeight: Math.round(document.querySelector('.graph-node-card')?.getBoundingClientRect().height ?? 0),
                shellWidth: Math.round(document.querySelector('.graph-scroll-shell')?.getBoundingClientRect().width ?? 0)
            })");

        using var document = JsonDocument.Parse(layoutJson);
        document.RootElement.GetProperty("scale").GetDouble().Should().BeGreaterThan(0.58d);
        document.RootElement.GetProperty("nodeWidth").GetInt32().Should().BeGreaterThan(150);
        document.RootElement.GetProperty("nodeHeight").GetInt32().Should().BeGreaterThan(100);
        document.RootElement.GetProperty("shellWidth").GetInt32().Should().BeGreaterThan(360);
    }

    [Fact]
    public async Task Graph_Should_Support_Fullscreen_Expansion()
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(Viewports[0]);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/graph?uiProfile=dense");

        var panel = page.Locator(".graph-canvas-panel");
        await panel.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15000
        });

        var beforeBox = await panel.BoundingBoxAsync();
        beforeBox.Should().NotBeNull();

        await page.GetByRole(AriaRole.Button, new() { Name = "全螢幕" }).ClickAsync();
        await page.WaitForFunctionAsync("() => document.querySelector('.graph-canvas-panel')?.classList.contains('graph-canvas-panel-expanded') === true");

        var expandedBox = await panel.BoundingBoxAsync();
        expandedBox.Should().NotBeNull();
        expandedBox!.Width.Should().BeGreaterThan(beforeBox!.Width + 120);
        expandedBox.Height.Should().BeGreaterThan(beforeBox.Height + 120);

        await page.GetByRole(AriaRole.Button, new() { Name = "收合圖表" }).ClickAsync();
        await page.WaitForFunctionAsync("() => document.querySelector('.graph-canvas-panel')?.classList.contains('graph-canvas-panel-expanded') === false");
    }

    private static string[] AllProjectsSelectionValue()
        => ["__all__"];

    [Fact]
    public async Task Sources_Page_Should_Not_Overflow_On_Fhd_Viewport()
    {
        await _fixture.EnsureDashboardRunningAsync();
        var viewport = new DashboardViewport("fhd-1080p", 1920, 1080);
        await using var context = await _fixture.CreateContextAsync(viewport);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/sources?uiProfile=dense");

        var layoutJson = await page.EvaluateAsync<string>(
            @"() => JSON.stringify({
                metricScrollWidth: document.querySelector('.sources-metric-grid')?.scrollWidth ?? 0,
                metricClientWidth: document.querySelector('.sources-metric-grid')?.clientWidth ?? 0,
                setupScrollWidth: document.querySelector('.sources-setup-grid')?.scrollWidth ?? 0,
                setupClientWidth: document.querySelector('.sources-setup-grid')?.clientWidth ?? 0,
                filterScrollWidth: document.querySelector('.sources-filter-grid')?.scrollWidth ?? 0,
                filterClientWidth: document.querySelector('.sources-filter-grid')?.clientWidth ?? 0,
                textareaScrollWidth: document.querySelector('.sources-textarea-grid')?.scrollWidth ?? 0,
                textareaClientWidth: document.querySelector('.sources-textarea-grid')?.clientWidth ?? 0,
                splitScrollWidth: document.querySelector('.sources-split-layout')?.scrollWidth ?? 0,
                splitClientWidth: document.querySelector('.sources-split-layout')?.clientWidth ?? 0,
                sectionTops: Array.from(document.querySelectorAll('.sources-page-stack > *'))
                    .map(node => Math.round(node.getBoundingClientRect().top))
            })");

        using var document = JsonDocument.Parse(layoutJson);
        document.RootElement.GetProperty("metricScrollWidth").GetInt32().Should().BeLessThanOrEqualTo(document.RootElement.GetProperty("metricClientWidth").GetInt32() + 1);
        document.RootElement.GetProperty("setupScrollWidth").GetInt32().Should().BeLessThanOrEqualTo(document.RootElement.GetProperty("setupClientWidth").GetInt32() + 1);
        document.RootElement.GetProperty("filterScrollWidth").GetInt32().Should().BeLessThanOrEqualTo(document.RootElement.GetProperty("filterClientWidth").GetInt32() + 1);
        document.RootElement.GetProperty("textareaScrollWidth").GetInt32().Should().BeLessThanOrEqualTo(document.RootElement.GetProperty("textareaClientWidth").GetInt32() + 1);
        document.RootElement.GetProperty("splitScrollWidth").GetInt32().Should().BeLessThanOrEqualTo(document.RootElement.GetProperty("splitClientWidth").GetInt32() + 1);

        var sectionTops = document.RootElement.GetProperty("sectionTops")
            .EnumerateArray()
            .Select(static value => value.GetInt32())
            .ToArray();

        sectionTops.Should().HaveCountGreaterThanOrEqualTo(3);
        sectionTops.Should().BeInAscendingOrder();
    }

    [Theory]
    [InlineData("/sources?uiProfile=normal", ".sources-page-stack", ".sources-page-stack > .metric-grid", ".sources-page-stack > .sources-setup-grid", ".sources-page-stack > .sources-workspace-section")]
    [InlineData("/governance?uiProfile=normal", ".governance-page-stack", ".governance-page-stack > .metric-grid", ".governance-page-stack > .governance-workspace-section")]
    [InlineData("/evaluation?uiProfile=normal", ".evaluation-page-stack", ".evaluation-page-stack > .metric-grid", ".evaluation-page-stack > #evaluation-suite-form", ".evaluation-page-stack > .evaluation-workspace-section")]
    [InlineData("/inbox?uiProfile=normal", ".inbox-page-stack", ".inbox-page-stack > .metric-grid", ".inbox-page-stack > .inbox-workspace-section")]
    public async Task Workspace_Pages_Should_Flow_From_Summary_To_Workspace_Without_Section_Overlap(string route, string stackSelector, params string[] sectionSelectors)
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(Viewports[0]);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, route);

        await page.Locator(stackSelector).WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15000
        });

        var layoutJson = await page.EvaluateAsync<string>(
            @"selectors => JSON.stringify(selectors.map(selector => {
                const element = document.querySelector(selector);
                if (!element) {
                    return { selector, exists: false, top: 0, bottom: 0 };
                }

                const rect = element.getBoundingClientRect();
                return {
                    selector,
                    exists: true,
                    top: Math.round(rect.top),
                    bottom: Math.round(rect.bottom)
                };
            }))",
            sectionSelectors);

        using var document = JsonDocument.Parse(layoutJson);
        var sections = document.RootElement.EnumerateArray().ToArray();
        sections.Should().NotBeEmpty();
        sections.All(section => section.GetProperty("exists").GetBoolean()).Should().BeTrue();

        var previousBottom = 0;
        foreach (var section in sections)
        {
            var top = section.GetProperty("top").GetInt32();
            var bottom = section.GetProperty("bottom").GetInt32();
            top.Should().BeGreaterThanOrEqualTo(previousBottom - 1, $"{section.GetProperty("selector").GetString()} should not overlap the previous section");
            bottom.Should().BeGreaterThan(top);
            previousBottom = bottom;
        }
    }

    [Fact]
    public async Task Monitoring_Workspace_Should_Render_Divider_Between_Summary_And_Scroll_Section()
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(Viewports[0]);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/monitoring?uiProfile=normal");

        var layoutJson = await page.EvaluateAsync<string>(
            @"() => JSON.stringify({
                borderTopWidth: getComputedStyle(document.querySelector('.monitoring-workspace-section') ?? document.body).borderTopWidth,
                paddingTop: getComputedStyle(document.querySelector('.monitoring-workspace-section') ?? document.body).paddingTop
            })");

        using var document = JsonDocument.Parse(layoutJson);
        document.RootElement.GetProperty("borderTopWidth").GetString().Should().NotBeNullOrWhiteSpace().And.NotBe("0px");
        document.RootElement.GetProperty("paddingTop").GetString().Should().NotBeNullOrWhiteSpace().And.NotBe("0px");
    }

    [Fact]
    public async Task Overview_Chrome_Text_Should_Be_NonSelectable_While_Log_Content_Remains_Selectable()
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(Viewports[0]);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/?uiProfile=normal");

        var selectionJson = await page.EvaluateAsync<string>(
            @"() => JSON.stringify({
                panelTitle: getComputedStyle(document.querySelector('.panel-title')).userSelect,
                chartMeta: getComputedStyle(document.querySelector('.resource-chart-meta')).userSelect,
                logCopy: getComputedStyle(document.querySelector('.stack-item-copy')).userSelect
            })");

        using var document = JsonDocument.Parse(selectionJson);
        document.RootElement.GetProperty("panelTitle").GetString().Should().Be("none");
        document.RootElement.GetProperty("chartMeta").GetString().Should().Be("none");
        document.RootElement.GetProperty("logCopy").GetString().Should().NotBe("none");
    }

    [Fact]
    public async Task Runtime_Chrome_Text_Should_Be_NonSelectable_While_Parameter_Values_Remain_Selectable()
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(Viewports[0]);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/runtime?uiProfile=dense");

        var selectionJson = await page.EvaluateAsync<string>(
            @"() => JSON.stringify({
                panelTitle: getComputedStyle(document.querySelector('.panel-title')).userSelect,
                pageTitle: getComputedStyle(document.querySelector('.page-header h1')).userSelect,
                parameterValue: getComputedStyle(document.querySelector('.runtime-parameters-panel tbody td:last-child')).userSelect
            })");

        using var document = JsonDocument.Parse(selectionJson);
        document.RootElement.GetProperty("panelTitle").GetString().Should().Be("none");
        document.RootElement.GetProperty("pageTitle").GetString().Should().Be("none");
        document.RootElement.GetProperty("parameterValue").GetString().Should().NotBe("none");
    }

    [Fact]
    public async Task Runtime_Page_Should_Only_Show_Runtime_Panels_And_Sidebar_Should_Show_Dashboard_Build_Metadata()
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(Viewports[0]);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/runtime?uiProfile=dense");

        var layoutJson = await page.EvaluateAsync<string>(
            @"() => JSON.stringify({
                sidebarFooterTop: document.querySelector('.sidebar-footer')?.getBoundingClientRect().top ?? 0,
                sidebarBuildTop: document.querySelector('.sidebar-build')?.getBoundingClientRect().top ?? 0,
                sidebarBuildLabel: document.querySelector('.sidebar-build-label')?.textContent?.trim() ?? '',
                sidebarBuildValue: document.querySelector('.sidebar-build-value')?.textContent?.trim() ?? '',
                sidebarBuildTime: document.querySelector('.sidebar-build-time')?.textContent?.trim() ?? '',
                sidebarBuildAlign: getComputedStyle(document.querySelector('.sidebar-build') ?? document.body).textAlign,
                sidebarInnerBottom: document.querySelector('.sidebar-inner')?.getBoundingClientRect().bottom ?? 0,
                sidebarBuildBottom: document.querySelector('.sidebar-build')?.getBoundingClientRect().bottom ?? 0,
                refreshBuildExists: !!document.querySelector('.refresh-status-build'),
                mainTop: document.querySelector('.runtime-main-panel')?.getBoundingClientRect().top ?? 0,
                parametersTop: document.querySelector('.runtime-parameters-panel')?.getBoundingClientRect().top ?? 0,
                mainWidth: document.querySelector('.runtime-main-panel')?.getBoundingClientRect().width ?? 0,
                parametersWidth: document.querySelector('.runtime-parameters-panel')?.getBoundingClientRect().width ?? 0,
                hostExists: !!document.querySelector('.runtime-host-panel'),
                dependenciesExists: !!document.querySelector('.runtime-dependencies-panel'),
                healthExists: !!document.querySelector('.runtime-health-panel')
            })");

        using var document = JsonDocument.Parse(layoutJson);
        var sidebarFooterTop = document.RootElement.GetProperty("sidebarFooterTop").GetDouble();
        var sidebarBuildTop = document.RootElement.GetProperty("sidebarBuildTop").GetDouble();
        document.RootElement.GetProperty("sidebarBuildLabel").GetString().Should().Be("Dashboard UI");
        document.RootElement.GetProperty("sidebarBuildValue").GetString().Should().NotBeNullOrWhiteSpace();
        document.RootElement.GetProperty("sidebarBuildTime").GetString().Should().NotBeNullOrWhiteSpace();
        document.RootElement.GetProperty("sidebarBuildAlign").GetString().Should().Be("center");
        var sidebarInnerBottom = document.RootElement.GetProperty("sidebarInnerBottom").GetDouble();
        var sidebarBuildBottom = document.RootElement.GetProperty("sidebarBuildBottom").GetDouble();
        sidebarFooterTop.Should().BeLessThan(sidebarBuildTop);
        (sidebarInnerBottom - sidebarBuildBottom).Should().BeLessThanOrEqualTo(2d);
        document.RootElement.GetProperty("refreshBuildExists").GetBoolean().Should().BeFalse();
        var mainTop = document.RootElement.GetProperty("mainTop").GetDouble();
        var parametersTop = document.RootElement.GetProperty("parametersTop").GetDouble();
        var mainWidth = document.RootElement.GetProperty("mainWidth").GetDouble();
        var parametersWidth = document.RootElement.GetProperty("parametersWidth").GetDouble();
        parametersTop.Should().BeApproximately(mainTop, 2d);
        Math.Abs(mainWidth - parametersWidth).Should().BeLessThan(40d);
        document.RootElement.GetProperty("hostExists").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("dependenciesExists").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("healthExists").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Refresh_Status_Shell_Should_Not_Shift_When_Page_Is_Refreshing()
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(Viewports[0]);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/?uiProfile=normal");

        const string rectScript = @"() => {
            const shell = document.querySelector('.refresh-status-shell');
            const live = document.querySelector('.refresh-status-live');
            const rect = shell?.getBoundingClientRect();
            return JSON.stringify({
                width: rect ? Math.round(rect.width) : 0,
                height: rect ? Math.round(rect.height) : 0,
                liveExists: !!live,
                buildExists: !!document.querySelector('.refresh-status-build')
            });
        }";

        var beforeJson = await page.EvaluateAsync<string>(rectScript);
        await page.GetByRole(AriaRole.Button, new() { Name = "刷新" }).ClickAsync(new LocatorClickOptions { NoWaitAfter = true });
        await page.WaitForTimeoutAsync(150);
        var duringJson = await page.EvaluateAsync<string>(rectScript);
        await page.WaitForFunctionAsync("() => !document.querySelector('button.primary-button[disabled]')");
        var afterJson = await page.EvaluateAsync<string>(rectScript);

        using var before = JsonDocument.Parse(beforeJson);
        using var during = JsonDocument.Parse(duringJson);
        using var after = JsonDocument.Parse(afterJson);

        before.RootElement.GetProperty("width").GetInt32().Should().Be(during.RootElement.GetProperty("width").GetInt32());
        before.RootElement.GetProperty("height").GetInt32().Should().Be(during.RootElement.GetProperty("height").GetInt32());
        during.RootElement.GetProperty("liveExists").GetBoolean().Should().BeTrue();
        during.RootElement.GetProperty("buildExists").GetBoolean().Should().BeFalse();
        after.RootElement.GetProperty("width").GetInt32().Should().Be(before.RootElement.GetProperty("width").GetInt32());
        after.RootElement.GetProperty("height").GetInt32().Should().Be(before.RootElement.GetProperty("height").GetInt32());
    }

    [Fact]
    public async Task Runtime_Dense_Page_Should_Stay_Stable_On_Wide_2k_Viewport()
    {
        var wideViewport = Viewports.Single(viewport => viewport.Name == "wide-2k");
        await ValidateRouteAsync(Routes.Single(route => route.Name == "runtime"), DashboardUiProfile.Dense, wideViewport, DashboardTheme.Dark);
    }

    [Fact]
    public async Task Runtime_Page_Should_Stack_On_1080_Width_While_Logs_Table_Keeps_Compact_Wrapping()
    {
        await _fixture.EnsureDashboardRunningAsync();
        var viewport = new DashboardViewport("notebook-1080", 1080, 1080);
        await using var context = await _fixture.CreateContextAsync(viewport);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/runtime?uiProfile=dense");

        var runtimeLayoutJson = await page.EvaluateAsync<string>(
            @"() => JSON.stringify({
                mainTop: Math.round(document.querySelector('.runtime-main-panel')?.getBoundingClientRect().top ?? 0),
                parametersTop: Math.round(document.querySelector('.runtime-parameters-panel')?.getBoundingClientRect().top ?? 0),
                mainWidth: Math.round(document.querySelector('.runtime-main-panel')?.getBoundingClientRect().width ?? 0),
                parametersWidth: Math.round(document.querySelector('.runtime-parameters-panel')?.getBoundingClientRect().width ?? 0)
            })");

        using (var runtimeLayout = JsonDocument.Parse(runtimeLayoutJson))
        {
            runtimeLayout.RootElement.GetProperty("parametersTop").GetInt32()
                .Should().BeGreaterThan(runtimeLayout.RootElement.GetProperty("mainTop").GetInt32());
            Math.Abs(runtimeLayout.RootElement.GetProperty("mainWidth").GetInt32() -
                     runtimeLayout.RootElement.GetProperty("parametersWidth").GetInt32())
                .Should().BeLessThan(8);
        }

        await LoginAndOpenAsync(page, "/logs?uiProfile=dense");

        var logsLayoutJson = await page.EvaluateAsync<string>(
            @"() => JSON.stringify({
                timeHeights: Array.from(document.querySelectorAll('.logs-time-cell .client-local-time')).slice(0, 4).map(item => Math.round(item.getBoundingClientRect().height))
            })");

        using var logsLayout = JsonDocument.Parse(logsLayoutJson);
        logsLayout.RootElement.GetProperty("timeHeights").EnumerateArray().All(item => item.GetInt32() < 70).Should().BeTrue();
    }

    [Fact]
    public async Task Monitoring_Telemetry_Panels_Should_Stay_On_A_Single_Row_On_Wide_2k_Viewport()
    {
        await _fixture.EnsureDashboardRunningAsync();
        var viewport = Viewports.Single(candidate => candidate.Name == "wide-2k");
        await using var context = await _fixture.CreateContextAsync(viewport);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/monitoring?uiProfile=dense");

        var layoutJson = await page.EvaluateAsync<string>(
            @"() => JSON.stringify(Array.from(document.querySelectorAll('.monitoring-telemetry-panel')).map(card => ({
                top: Math.round(card.getBoundingClientRect().top),
                width: Math.round(card.getBoundingClientRect().width)
            })))");

        using var document = JsonDocument.Parse(layoutJson);
        var cards = document.RootElement.EnumerateArray().ToArray();
        cards.Should().HaveCount(2);
        cards.Select(card => card.GetProperty("top").GetInt32()).Distinct().Should().HaveCount(1);
        cards.All(card => card.GetProperty("width").GetInt32() > 200).Should().BeTrue();
    }

    [Fact]
    public async Task Monitoring_Top_Panels_Should_Stay_On_A_Single_Row_On_Wide_2k_Viewport()
    {
        await _fixture.EnsureDashboardRunningAsync();
        var viewport = Viewports.Single(candidate => candidate.Name == "wide-2k");
        await using var context = await _fixture.CreateContextAsync(viewport);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/monitoring?uiProfile=dense");

        var layoutJson = await page.EvaluateAsync<string>(
            @"() => JSON.stringify(Array.from(document.querySelectorAll('.monitoring-top-grid > .panel')).map(card => ({
                top: Math.round(card.getBoundingClientRect().top),
                width: Math.round(card.getBoundingClientRect().width)
            })))");

        using var document = JsonDocument.Parse(layoutJson);
        var cards = document.RootElement.EnumerateArray().ToArray();
        cards.Should().HaveCount(2);
        cards.Select(card => card.GetProperty("top").GetInt32()).Distinct().Should().HaveCount(1);
        cards.All(card => card.GetProperty("width").GetInt32() > 150).Should().BeTrue();
    }

    [Fact]
    public async Task Monitoring_Docker_Host_Cards_Should_Stay_On_A_Single_Row_On_Wide_2k_Viewport()
    {
        await _fixture.EnsureDashboardRunningAsync();
        var viewport = Viewports.Single(candidate => candidate.Name == "wide-2k");
        await using var context = await _fixture.CreateContextAsync(viewport);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/monitoring?uiProfile=dense");

        var layoutJson = await page.EvaluateAsync<string>(
            @"() => JSON.stringify(Array.from(document.querySelectorAll('.runtime-host-card')).map(card => ({
                top: Math.round(card.getBoundingClientRect().top),
                width: Math.round(card.getBoundingClientRect().width)
            })))");

        using var document = JsonDocument.Parse(layoutJson);
        var cards = document.RootElement.EnumerateArray().ToArray();
        cards.Should().HaveCount(4);
        cards.Select(card => card.GetProperty("top").GetInt32()).Distinct().Should().HaveCount(1);
        cards.All(card => card.GetProperty("width").GetInt32() > 150).Should().BeTrue();
    }

    [Fact]
    public async Task Settings_Transfer_Cards_Should_Stay_On_A_Single_Row_And_Behavior_Groups_Should_Render_Cleanly_On_Wide_2k_Viewport()
    {
        await _fixture.EnsureDashboardRunningAsync();
        var viewport = Viewports.Single(candidate => candidate.Name == "wide-2k");
        await using var context = await _fixture.CreateContextAsync(viewport);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/settings?uiProfile=dense");

        var layoutJson = await page.EvaluateAsync<string>(
            @"() => JSON.stringify({
                transferCards: Array.from(document.querySelectorAll('.settings-transfer-card')).map(card => ({
                    top: Math.round(card.getBoundingClientRect().top),
                    width: Math.round(card.getBoundingClientRect().width)
                })),
                behaviorCards: Array.from(document.querySelectorAll('.settings-behavior-card')).map(card => ({
                    width: Math.round(card.getBoundingClientRect().width),
                    height: Math.round(card.getBoundingClientRect().height)
                })),
                ingestionToggles: Array.from(document.querySelectorAll('.settings-toggle-grid-4 > .toggle-field')).map(item => Math.round(item.getBoundingClientRect().top)),
                queryFields: Array.from(document.querySelectorAll('.settings-query-grid > label, .settings-query-grid > .settings-checkbox-field')).map(item => Math.round(item.getBoundingClientRect().top))
            })");

        using var document = JsonDocument.Parse(layoutJson);
        var transferCards = document.RootElement.GetProperty("transferCards").EnumerateArray().ToArray();
        transferCards.Should().HaveCount(2);
        transferCards.Select(card => card.GetProperty("top").GetInt32()).Distinct().Should().HaveCount(1);
        transferCards.All(card => card.GetProperty("width").GetInt32() > 180).Should().BeTrue();

        var behaviorCards = document.RootElement.GetProperty("behaviorCards").EnumerateArray().ToArray();
        behaviorCards.Should().HaveCount(2);
        behaviorCards.All(card => card.GetProperty("width").GetInt32() > 400).Should().BeTrue();
        behaviorCards.All(card => card.GetProperty("height").GetInt32() > 120).Should().BeTrue();

        document.RootElement.GetProperty("ingestionToggles").EnumerateArray().Select(item => item.GetInt32()).Distinct().Should().HaveCount(1);
        document.RootElement.GetProperty("queryFields").EnumerateArray().Select(item => item.GetInt32()).Distinct().Should().HaveCount(1);
    }

    [Fact]
    public async Task Settings_Transfer_Scope_Chips_Should_Stay_Three_Columns_And_Not_Collapse_At_1080_Width()
    {
        await _fixture.EnsureDashboardRunningAsync();
        var viewport = new DashboardViewport("notebook-1080", 1080, 1080);
        await using var context = await _fixture.CreateContextAsync(viewport);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/settings?uiProfile=dense");

        var layoutJson = await page.EvaluateAsync<string>(
            @"() => JSON.stringify({
                chipTops: Array.from(document.querySelectorAll('.transfer-scope-chip')).map(item => Math.round(item.getBoundingClientRect().top)),
                chipWidths: Array.from(document.querySelectorAll('.transfer-scope-chip')).map(item => Math.round(item.getBoundingClientRect().width)),
                titleHeights: Array.from(document.querySelectorAll('.transfer-scope-chip > span')).map(item => Math.round(item.getBoundingClientRect().height)),
                gridScrollWidth: document.querySelector('.transfer-scope-grid')?.scrollWidth ?? 0,
                gridClientWidth: document.querySelector('.transfer-scope-grid')?.clientWidth ?? 0
            })");

        using var document = JsonDocument.Parse(layoutJson);
        document.RootElement.GetProperty("chipTops").EnumerateArray().Select(item => item.GetInt32()).Distinct().Should().HaveCount(1);
        document.RootElement.GetProperty("chipWidths").EnumerateArray().Should().HaveCount(3);
        document.RootElement.GetProperty("titleHeights").EnumerateArray().Select(item => item.GetInt32()).All(height => height < 56).Should().BeTrue();
        document.RootElement.GetProperty("gridScrollWidth").GetInt32().Should().BeLessThanOrEqualTo(document.RootElement.GetProperty("gridClientWidth").GetInt32() + 1);
    }

    private async Task ValidateRouteAsync(
        DashboardRouteSpec route,
        DashboardUiProfile profile,
        DashboardViewport viewport,
        DashboardTheme theme,
        bool enableInteractions = false,
        bool expectScrollableOverflow = false)
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(viewport);
        await context.AddInitScriptAsync(
            $@"(() => {{
                localStorage.setItem('contextHub.dashboard.theme', '{theme.PreferenceValue}');
                document.documentElement.dataset.themePreference = '{theme.PreferenceValue}';
                document.documentElement.dataset.theme = '{theme.PreferenceValue}';
                document.documentElement.style.colorScheme = '{theme.PreferenceValue}';
            }})();");
        var page = await context.NewPageAsync();

        var targetUrl = BuildRouteUrl(route.Route, profile);
        await LoginAndOpenAsync(page, targetUrl);

        var heading = page.GetByRole(AriaRole.Heading, new() { Name = route.Title });
        await heading.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15000 });

        foreach (var selector in route.RequiredSelectors)
        {
            await page.Locator(selector).First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 15000
            });
        }

        if (enableInteractions)
        {
            await PerformInteractiveChecksAsync(page, route.Name);
        }

        var screenshotPath = await CaptureScreenshotAsync(page, route.Name, profile, viewport, theme);
        var snapshot = await AnalyzeLayoutAsync(page, route.OverlapSelectors, route.ScrollSelectors);

        snapshot.ResolvedTheme.Should().Be(theme.PreferenceValue,
            $"theme mismatch on {route.Name} / {viewport.Name}; screenshot: {screenshotPath}");

        snapshot.DocumentScrollWidth.Should().BeLessThanOrEqualTo(snapshot.ViewportWidth + 1,
            $"unexpected horizontal overflow on {route.Name} / {viewport.Name}; screenshot: {screenshotPath}");
        snapshot.BodyScrollWidth.Should().BeLessThanOrEqualTo(snapshot.ViewportWidth + 1,
            $"body width overflow on {route.Name} / {viewport.Name}; screenshot: {screenshotPath}");

        snapshot.MissingSelectors.Should().BeEmpty($"missing expected selectors on {route.Name} / {viewport.Name}; screenshot: {screenshotPath}");
        snapshot.OverlapWarnings.Should().BeEmpty($"detected overlapping panels on {route.Name} / {viewport.Name}; screenshot: {screenshotPath}");
        snapshot.VisibleRectCount.Should().BeGreaterThan(0, $"no visible key panels detected on {route.Name} / {viewport.Name}; screenshot: {screenshotPath}");

        if (expectScrollableOverflow)
        {
            snapshot.ScrollTargets.Any(target => target.CanScrollY).Should().BeTrue(
                $"expected at least one scrollable container with vertical overflow on {route.Name}; screenshot: {screenshotPath}");
        }
        else
        {
            snapshot.ScrollTargets.Should().NotBeEmpty($"missing scroll targets on {route.Name} / {viewport.Name}; screenshot: {screenshotPath}");
        }
    }

    private async Task LoginAndOpenAsync(IPage page, string relativeUrlWithProfile)
    {
        var loginUrl = new Uri(_fixture.BaseUri, $"/login?returnUrl={Uri.EscapeDataString(relativeUrlWithProfile)}");
        await page.GotoAsync(loginUrl.ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.WaitForTimeoutAsync(600);
        await page.Locator("input[name='Username']").FillAsync("admin");
        await page.Locator("input[name='Password']").FillAsync("ContextHub!123");
        await page.GetByRole(AriaRole.Button, new() { Name = "登入" }).ClickAsync(new LocatorClickOptions { NoWaitAfter = true });
        await page.WaitForURLAsync($"**{relativeUrlWithProfile}*", new PageWaitForURLOptions { Timeout = 15000 });
        await page.WaitForTimeoutAsync(400);
    }

    private static string BuildRouteUrl(string route, DashboardUiProfile profile)
        => route.Contains('?', StringComparison.Ordinal)
            ? $"{route}&uiProfile={profile.ToString().ToLowerInvariant()}"
            : $"{route}?uiProfile={profile.ToString().ToLowerInvariant()}";

    private async Task<string> CaptureScreenshotAsync(IPage page, string routeName, DashboardUiProfile profile, DashboardViewport viewport, DashboardTheme theme)
    {
        var fileName = $"{Sanitize(routeName)}-{profile.ToString().ToLowerInvariant()}-{theme.Name}-{viewport.Name}.png";
        var path = Path.Combine(_fixture.ArtifactDirectory, fileName);
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = path,
            FullPage = false
        });
        return path;
    }

    private static string Sanitize(string value)
        => Regex.Replace(value, "[^a-zA-Z0-9_-]+", "-");

    private static async Task PerformInteractiveChecksAsync(IPage page, string routeName)
    {
        switch (routeName)
        {
            case "memories":
                await page.Locator(".data-table-clickable tbody tr").First.ClickAsync();
                await page.Locator(".memory-detail-body").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15000 });
                break;

            case "logs":
                await page.Locator(".data-table-clickable tbody tr").First.ClickAsync();
                await page.Locator(".detail-actions").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15000 });
                break;

            case "storage":
                await page.Locator(".storage-row-table tbody tr").First.ClickAsync();
                await page.Locator(".storage-inspector-panel .code-block").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15000 });
                break;

            case "performance":
                await page.GetByRole(AriaRole.Button, new() { Name = "開始量測" }).ClickAsync();
                await page.Locator(".performance-results-panel").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15000 });
                break;
        }

        await page.WaitForTimeoutAsync(350);
    }

    private static async Task<LayoutSnapshot> AnalyzeLayoutAsync(IPage page, IReadOnlyList<string> overlapSelectors, IReadOnlyList<string> scrollSelectors)
    {
        var snapshotJson = await page.EvaluateAsync<string>(
            @"({ overlapSelectors, scrollSelectors }) => {
                const root = document.documentElement;
                const body = document.body;
                const content = document.querySelector('.content');
                const overlaps = [];
                const missingSelectors = [];
                const rects = overlapSelectors.map(selector => {
                    const element = document.querySelector(selector);
                    if (!element) {
                        missingSelectors.push(selector);
                        return null;
                    }

                    const rect = element.getBoundingClientRect();
                    if (rect.width <= 0 || rect.height <= 0) {
                        return { selector, visible: false, left: rect.left, top: rect.top, right: rect.right, bottom: rect.bottom };
                    }

                    return { selector, visible: true, left: rect.left, top: rect.top, right: rect.right, bottom: rect.bottom };
                }).filter(Boolean);

                for (let i = 0; i < rects.length; i++) {
                    for (let j = i + 1; j < rects.length; j++) {
                        const a = rects[i];
                        const b = rects[j];
                        if (!a.visible || !b.visible) {
                            continue;
                        }

                        const intersectionWidth = Math.min(a.right, b.right) - Math.max(a.left, b.left);
                        const intersectionHeight = Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top);
                        if (intersectionWidth > 2 && intersectionHeight > 2) {
                            overlaps.push(`${a.selector} overlaps ${b.selector}`);
                        }
                    }
                }

                const scrollTargets = scrollSelectors.map(selector => {
                    const element = document.querySelector(selector);
                    if (!element) {
                        return { selector, exists: false, canScrollY: false };
                    }

                    return {
                        selector,
                        exists: true,
                        clientHeight: element.clientHeight,
                        scrollHeight: element.scrollHeight,
                        clientWidth: element.clientWidth,
                        scrollWidth: element.scrollWidth,
                        canScrollY: element.scrollHeight > element.clientHeight + 1
                    };
                });

                return JSON.stringify({
                    viewportWidth: window.innerWidth,
                    documentScrollWidth: root.scrollWidth,
                    bodyScrollWidth: body.scrollWidth,
                    contentClientWidth: content ? content.clientWidth : 0,
                    contentScrollWidth: content ? content.scrollWidth : 0,
                    resolvedTheme: root.dataset.theme || '',
                    visibleRectCount: rects.filter(rect => rect.visible).length,
                    overlapWarnings: overlaps,
                    missingSelectors,
                    scrollTargets
                });
            }",
            new { overlapSelectors, scrollSelectors });

        var snapshot = JsonSerializer.Deserialize<LayoutSnapshot>(snapshotJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new LayoutSnapshot();
        snapshot.OverlapWarnings ??= [];
        snapshot.MissingSelectors ??= [];
        snapshot.ScrollTargets ??= [];
        return snapshot;
    }

    private sealed class LayoutSnapshot
    {
        public int ViewportWidth { get; set; }
        public int DocumentScrollWidth { get; set; }
        public int BodyScrollWidth { get; set; }
        public int ContentClientWidth { get; set; }
        public int ContentScrollWidth { get; set; }
        public string ResolvedTheme { get; set; } = string.Empty;
        public int VisibleRectCount { get; set; }
        public List<string> OverlapWarnings { get; set; } = [];
        public List<string> MissingSelectors { get; set; } = [];
        public List<ScrollTargetSnapshot> ScrollTargets { get; set; } = [];
    }

    private sealed class ScrollTargetSnapshot
    {
        public string Selector { get; set; } = string.Empty;
        public bool Exists { get; set; }
        public bool CanScrollY { get; set; }
        public int ClientHeight { get; set; }
        public int ScrollHeight { get; set; }
        public int ClientWidth { get; set; }
        public int ScrollWidth { get; set; }
    }
}

internal enum DashboardUiProfile
{
    Normal,
    Empty,
    Dense
}

internal sealed record DashboardRouteSpec(
    string Name,
    string Route,
    string Title,
    string[] RequiredSelectors,
    string[] OverlapSelectors,
    string[] ScrollSelectors);

internal sealed record DashboardViewport(string Name, int Width, int Height);

internal sealed record DashboardTheme(string Name, string PreferenceValue)
{
    public static readonly DashboardTheme Dark = new("dark", "dark");
    public static readonly DashboardTheme Light = new("light", "light");
}

public sealed class DashboardBrowserFixture : IAsyncLifetime
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private Process? _process;
    private readonly int _port = GetFreeTcpPort();

    public Uri BaseUri => new($"http://127.0.0.1:{_port}/");

    public string ArtifactDirectory { get; } = Path.Combine(Path.GetTempPath(), "contexthub-dashboard-browser-artifacts", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(ArtifactDirectory);
        StartDashboardProcess();
        await WaitForDashboardAsync();

        var executablePath = FindBrowserExecutable()
            ?? throw new InvalidOperationException("No Chromium-based browser executable was found for dashboard browser tests.");

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            ExecutablePath = executablePath
        });
    }

    internal async Task EnsureDashboardRunningAsync()
    {
        if (_process is not null && !_process.HasExited)
        {
            return;
        }

        if (_process is not null)
        {
            _process.Dispose();
            _process = null;
        }

        StartDashboardProcess();
        await WaitForDashboardAsync();
    }

    public async Task DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.CloseAsync();
            _browser = null;
        }

        if (_process is not null && !_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
            _process.Dispose();
            _process = null;
        }

        _playwright?.Dispose();
    }

    internal async Task<IBrowserContext> CreateContextAsync(DashboardViewport viewport)
    {
        if (_browser is null)
        {
            throw new InvalidOperationException("Browser fixture was not initialized.");
        }

        return await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize
            {
                Width = viewport.Width,
                Height = viewport.Height
            },
            TimezoneId = "Asia/Taipei"
        });
    }

    private static string? FindBrowserExecutable()
    {
        var candidates = new[]
        {
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
            "/usr/bin/microsoft-edge",
            "/usr/bin/google-chrome",
            "/usr/bin/chromium-browser",
            "/usr/bin/chromium",
            "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
            "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private void StartDashboardProcess()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var dashboardProject = Path.Combine(repoRoot, "src", "Memory.Dashboard", "Memory.Dashboard.csproj");
        if (!File.Exists(dashboardProject))
        {
            throw new FileNotFoundException("Dashboard project for browser tests was not found.", dashboardProject);
        }

        var dataProtectionPath = Path.Combine(Path.GetTempPath(), "contexthub-dashboard-browser-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataProtectionPath);

        var startInfo = new ProcessStartInfo("dotnet", $"run --no-build --project \"{dashboardProject}\" -- --urls {BaseUri.AbsoluteUri.TrimEnd('/')}")
        {
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Testing";
        startInfo.Environment["Dashboard__UseBrowserTestDoubles"] = "true";
        startInfo.Environment["ContextHub__InstanceId"] = "browser-test-instance";
        startInfo.Environment["Dashboard__BaseUrl"] = "http://fake-context-hub";
        startInfo.Environment["Dashboard__AdminUsername"] = "admin";
        startInfo.Environment["Dashboard__AdminPasswordHash"] = "AQAAAAIAAYagAAAAEIbguUQEApMQehlC51gjy+uGulsE4ahRI7UtbdAlSsGMynNrNM3J3KfsJL+3IuBUxQ==";
        startInfo.Environment["Dashboard__SessionTimeoutMinutes"] = "480";
        startInfo.Environment["Dashboard__ComposeProject"] = "contexthub";
        startInfo.Environment["Dashboard__DataProtectionPath"] = dataProtectionPath;
        startInfo.Environment["Memory__Namespace"] = "context-hub-browser";

        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start dashboard process for browser tests.");
        _ = _process.StandardOutput.ReadToEndAsync();
        _ = _process.StandardError.ReadToEndAsync();
    }

    private async Task WaitForDashboardAsync()
    {
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(3)
        };

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (_process is not null && _process.HasExited)
            {
                throw new InvalidOperationException($"Dashboard process exited early with code {_process.ExitCode}.");
            }

            try
            {
                using var response = await client.GetAsync(new Uri(BaseUri, "/health/live"));
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch
            {
            }

            await Task.Delay(500);
        }

        throw new TimeoutException("Timed out waiting for dashboard browser test host to become ready.");
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
