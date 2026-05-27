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
        new("security", "/security", "安全管理", [".security-layout", ".settings-form-grid", ".table-scroll-shell"], [".page-header", ".security-layout"], [".content", ".security-layout"]),
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
    public async Task Theme_Switcher_Menu_Should_Be_Clickable_At_App_Browser_Size()
    {
        await _fixture.EnsureDashboardRunningAsync();
        var viewport = new DashboardViewport("app-browser-1031", 1031, 1270);
        await using var context = await _fixture.CreateContextAsync(viewport);
        await context.AddInitScriptAsync(
            @"(() => {
                localStorage.setItem('contextHub.dashboard.theme', 'dark');
                document.documentElement.dataset.themePreference = 'dark';
                document.documentElement.dataset.theme = 'dark';
                document.documentElement.style.colorScheme = 'dark';
            })();");
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/preferences");
        var themeToggle = page.Locator(".theme-switcher-toggle");
        (await themeToggle.EvaluateAsync<string>("element => getComputedStyle(element).cursor"))
            .Should()
            .Be("pointer");

        await themeToggle.ClickAsync();
        var lightOption = page.GetByRole(AriaRole.Menuitemradio, new() { Name = "淺色" });
        await lightOption.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 5000
        });

        var hitTestJson = await page.EvaluateAsync<string>(
            @"() => {
                const option = Array.from(document.querySelectorAll('.theme-switcher-option'))
                    .find(item => item.textContent?.includes('淺色'));
                const menu = document.querySelector('.theme-switcher-menu');
                const topbar = document.querySelector('.topbar');
                const pageHeader = document.querySelector('.page-header');
                const rect = option?.getBoundingClientRect();
                const x = rect ? rect.left + (rect.width / 2) : 0;
                const y = rect ? rect.top + (rect.height / 2) : 0;
                const hit = rect ? document.elementFromPoint(x, y) : null;
                return JSON.stringify({
                    optionVisible: !!option,
                    hitThemeOption: !!hit?.closest('.theme-switcher-option'),
                    hitClass: hit?.className?.toString() ?? '',
                    menuZ: Number(getComputedStyle(menu).zIndex || 0),
                    topbarZ: Number(getComputedStyle(topbar).zIndex || 0),
                    pageHeaderZ: Number(getComputedStyle(pageHeader).zIndex || 0)
                });
            }");

        using var hitTestDocument = JsonDocument.Parse(hitTestJson);
        var hitTest = hitTestDocument.RootElement;
        hitTest.GetProperty("optionVisible").GetBoolean().Should().BeTrue();
        hitTest.GetProperty("hitThemeOption").GetBoolean().Should().BeTrue($"theme menu must not be covered at 1031x1270, hit test was {hitTestJson}");
        hitTest.GetProperty("topbarZ").GetInt32().Should().BeGreaterThan(hitTest.GetProperty("pageHeaderZ").GetInt32());
        hitTest.GetProperty("menuZ").GetInt32().Should().BeGreaterThan(100);

        await lightOption.ClickAsync();
        await page.WaitForFunctionAsync("() => document.documentElement.dataset.theme === 'light'");
        var toggleText = await themeToggle.InnerTextAsync();
        toggleText.Should().Contain("淺色");
    }

    [Fact]
    public async Task Reconnect_Modal_Status_Text_Should_Be_Centered()
    {
        await _fixture.EnsureDashboardRunningAsync();
        var viewport = new DashboardViewport("app-browser-1196", 1196, 1270);
        await using var context = await _fixture.CreateContextAsync(viewport);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/security?uiProfile=normal");

        var layoutJson = await page.EvaluateAsync<string>(
            @"() => {
                const modal = document.querySelector('#components-reconnect-modal');
                modal.className = 'components-reconnect-retrying';
                if (!modal.open) {
                    modal.showModal();
                }

                const body = modal.querySelector('.components-reconnect-body');
                const visibleParagraphs = Array.from(body.querySelectorAll('p'))
                    .filter(item => getComputedStyle(item).display !== 'none')
                    .map(item => {
                        const rect = item.getBoundingClientRect();
                        const bodyRect = body.getBoundingClientRect();
                        return {
                            text: item.textContent?.trim() ?? '',
                            textAlign: getComputedStyle(item).textAlign,
                            leftOffset: Math.round(rect.left - bodyRect.left),
                            rightOffset: Math.round(bodyRect.right - rect.right)
                        };
                    });

                return JSON.stringify({
                    bodyTextAlign: getComputedStyle(body).textAlign,
                    bodyJustifyItems: getComputedStyle(body).justifyItems,
                    visibleParagraphs
                });
            }");

        using var document = JsonDocument.Parse(layoutJson);
        var root = document.RootElement;
        root.GetProperty("bodyTextAlign").GetString().Should().Be("center", $"reconnect modal body should center status copy: {layoutJson}");
        root.GetProperty("bodyJustifyItems").GetString().Should().Be("center", $"reconnect modal body should center paragraph blocks: {layoutJson}");

        var paragraphs = root.GetProperty("visibleParagraphs").EnumerateArray().ToArray();
        paragraphs.Should().NotBeEmpty();
        paragraphs.Select(item => item.GetProperty("textAlign").GetString()).Should().OnlyContain(value => value == "center");
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

        var footerVersion = await page.Locator(".login-footer-version").InnerTextAsync();
        footerVersion.Should().StartWith("UI v");
        footerVersion.Should().NotContain("vv");
    }

    [Fact]
    public async Task Login_Page_Highlights_Should_Stack_And_Center_In_Narrow_Desktop()
    {
        await _fixture.EnsureDashboardRunningAsync();
        var viewport = new DashboardViewport("login-narrow-desktop", 1031, 1270);
        await using var context = await _fixture.CreateContextAsync(viewport);
        var page = await context.NewPageAsync();

        await page.GotoAsync(new Uri(_fixture.BaseUri, "/login").ToString());
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var layoutJson = await page.EvaluateAsync<string>(
            @"() => {
                const viewportWidth = window.innerWidth;
                const scene = document.querySelector('.login-scene')?.getBoundingClientRect();
                const brand = document.querySelector('.login-brand-panel')?.getBoundingClientRect();
                const card = document.querySelector('.login-card')?.getBoundingClientRect();
                const chipRow = document.querySelector('.login-chip-row')?.getBoundingClientRect();
                const title = document.querySelector('.login-title');
                const highlights = Array.from(document.querySelectorAll('.login-highlight')).map(item => {
                    const rect = item.getBoundingClientRect();
                    return {
                        left: Math.round(rect.left),
                        top: Math.round(rect.top),
                        bottom: Math.round(rect.bottom),
                        width: Math.round(rect.width)
                    };
                });

                return JSON.stringify({
                    viewportWidth,
                    scene: scene ? {
                        left: Math.round(scene.left),
                        right: Math.round(scene.right),
                        width: Math.round(scene.width)
                    } : null,
                    brand: brand ? {
                        left: Math.round(brand.left),
                        right: Math.round(brand.right),
                        width: Math.round(brand.width)
                    } : null,
                    card: card ? {
                        left: Math.round(card.left),
                        right: Math.round(card.right),
                        width: Math.round(card.width)
                    } : null,
                    chipRow: chipRow ? {
                        left: Math.round(chipRow.left),
                        right: Math.round(chipRow.right),
                        width: Math.round(chipRow.width)
                    } : null,
                    titleTextAlign: title ? getComputedStyle(title).textAlign : '',
                    highlights
                });
            }");

        using var document = JsonDocument.Parse(layoutJson);
        var root = document.RootElement;
        var scene = root.GetProperty("scene");
        var brand = root.GetProperty("brand");
        var card = root.GetProperty("card");
        var chipRow = root.GetProperty("chipRow");
        var highlights = root.GetProperty("highlights").EnumerateArray().ToArray();

        highlights.Should().HaveCount(2);
        highlights[1].GetProperty("top").GetInt32().Should().BeGreaterThan(highlights[0].GetProperty("bottom").GetInt32());
        Math.Abs(highlights[1].GetProperty("left").GetInt32() - highlights[0].GetProperty("left").GetInt32()).Should().BeLessThanOrEqualTo(2);
        Math.Abs(highlights[1].GetProperty("width").GetInt32() - highlights[0].GetProperty("width").GetInt32()).Should().BeLessThanOrEqualTo(2);

        var viewportCenter = root.GetProperty("viewportWidth").GetInt32() / 2d;
        var sceneCenter = (scene.GetProperty("left").GetInt32() + scene.GetProperty("right").GetInt32()) / 2d;
        var brandCenter = (brand.GetProperty("left").GetInt32() + brand.GetProperty("right").GetInt32()) / 2d;
        var cardCenter = (card.GetProperty("left").GetInt32() + card.GetProperty("right").GetInt32()) / 2d;
        var chipRowCenter = (chipRow.GetProperty("left").GetInt32() + chipRow.GetProperty("right").GetInt32()) / 2d;
        Math.Abs(sceneCenter - viewportCenter).Should().BeLessThanOrEqualTo(2d);
        Math.Abs(brandCenter - sceneCenter).Should().BeLessThanOrEqualTo(2d);
        Math.Abs(cardCenter - sceneCenter).Should().BeLessThanOrEqualTo(2d);
        Math.Abs(chipRowCenter - brandCenter).Should().BeLessThanOrEqualTo(2d);
        scene.GetProperty("width").GetInt32().Should().BeLessThan(root.GetProperty("viewportWidth").GetInt32());
        card.GetProperty("width").GetInt32().Should().BeLessThan(scene.GetProperty("width").GetInt32());
        root.GetProperty("titleTextAlign").GetString().Should().Be("left");
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
    public async Task Memories_Filter_Should_Keep_Quick_And_Advanced_Rows_Inside_Fhd_Viewport()
    {
        await _fixture.EnsureDashboardRunningAsync();
        var viewport = new DashboardViewport("fhd-1080p", 1920, 1080);
        await using var context = await _fixture.CreateContextAsync(viewport);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/memories?uiProfile=dense");

        var layoutJson = await page.EvaluateAsync<string>(
            @"() => JSON.stringify({
                quickRows: [...new Set(Array.from(document.querySelectorAll('.memories-filter-grid > *'))
                    .map(node => Math.round(node.getBoundingClientRect().top)))].length,
                gridScrollWidth: document.querySelector('.memories-filter-grid')?.scrollWidth ?? 0,
                gridClientWidth: document.querySelector('.memories-filter-grid')?.clientWidth ?? 0,
                advancedOpen: document.querySelector('.memories-filter-panel .filter-advanced')?.open ?? false,
                advancedScrollWidth: document.querySelector('.memories-advanced-filter-grid')?.scrollWidth ?? 0,
                advancedClientWidth: document.querySelector('.memories-advanced-filter-grid')?.clientWidth ?? 0
            })");

        using var document = JsonDocument.Parse(layoutJson);
        document.RootElement.GetProperty("quickRows").GetInt32().Should().BeLessThanOrEqualTo(2);
        document.RootElement.GetProperty("advancedOpen").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("gridScrollWidth").GetInt32().Should().BeLessThanOrEqualTo(document.RootElement.GetProperty("gridClientWidth").GetInt32() + 1);
        document.RootElement.GetProperty("advancedScrollWidth").GetInt32().Should().BeLessThanOrEqualTo(document.RootElement.GetProperty("advancedClientWidth").GetInt32() + 1);
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

        var nodes = page.Locator(".graph-view-node");
        await nodes.Nth(1).WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15000
        });

        var expectedTitle = await nodes.Nth(1).Locator(".graph-node-title").TextContentAsync();
        expectedTitle.Should().NotBeNullOrWhiteSpace();
        await nodes.Nth(1).Locator("circle").ClickAsync();
        await page.WaitForFunctionAsync(
            "(title) => document.querySelector('.graph-detail-panel')?.innerText?.includes(title) === true",
            expectedTitle);

        var detailPanel = page.Locator(".graph-detail-panel");
        var detailText = await detailPanel.InnerTextAsync();
        detailText.Should().Contain(expectedTitle);

        var className = await nodes.Nth(1).GetAttributeAsync("class");
        className.Should().NotBeNull();
        className.Should().MatchRegex("selected");
    }

    [Fact]
    public async Task Graph_Dense_Layout_Should_Avoid_Node_Overlap_On_Desktop()
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(Viewports[0]);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/graph?uiProfile=dense");
        await page.WaitForFunctionAsync("() => (document.querySelectorAll('.graph-view-node').length ?? 0) >= 4");

        var overlapJson = await page.EvaluateAsync<string>(
            @"() => {
                const nodes = Array.from(document.querySelectorAll('.graph-view-node circle')).map(node => {
                    const rect = node.getBoundingClientRect();
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
                for (let i = 0; i < nodes.length; i += 1) {
                    for (let j = i + 1; j < nodes.length; j += 1) {
                        const left = Math.max(nodes[i].left, nodes[j].left);
                        const right = Math.min(nodes[i].right, nodes[j].right);
                        const top = Math.max(nodes[i].top, nodes[j].top);
                        const bottom = Math.min(nodes[i].bottom, nodes[j].bottom);
                        if (right > left && bottom > top) {
                            maxIntersectionArea = Math.max(maxIntersectionArea, (right - left) * (bottom - top));
                        }
                    }
                }

                return JSON.stringify({
                    count: nodes.length,
                    maxIntersectionArea
                });
            }");

        using var document = JsonDocument.Parse(overlapJson);
        document.RootElement.GetProperty("count").GetInt32().Should().BeGreaterThanOrEqualTo(4);
        document.RootElement.GetProperty("maxIntersectionArea").GetDouble().Should().BeLessThan(8);
    }

    [Fact]
    public async Task Graph_Filter_Toggles_Should_Keep_Checkbox_And_Text_On_One_Line()
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(Viewports[0]);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/graph?uiProfile=graphdemo");

        var layoutJson = await page.EvaluateAsync<string>(
            @"() => {
                const labels = ['顯示 similarity 邊', '一併讀取綜合層'].map(text => {
                    const label = Array.from(document.querySelectorAll('.graph-filter-stack > .toggle-field'))
                        .find(item => item.textContent?.includes(text));
                    const input = label?.querySelector('input[type=""checkbox""]')?.getBoundingClientRect();
                    const caption = label?.querySelector('span')?.getBoundingClientRect();
                    return {
                        text,
                        display: label ? getComputedStyle(label).display : '',
                        inputCenterY: input ? input.top + (input.height / 2) : 0,
                        captionCenterY: caption ? caption.top + (caption.height / 2) : 0,
                        inputRight: input?.right ?? 0,
                        captionLeft: caption?.left ?? 0,
                        captionWidth: caption?.width ?? 0
                    };
                });

                return JSON.stringify(labels);
            }");

        using var document = JsonDocument.Parse(layoutJson);
        var labels = document.RootElement.EnumerateArray().ToArray();
        labels.Should().HaveCount(2);

        foreach (var label in labels)
        {
            label.GetProperty("display").GetString().Should().Be("flex", $"toggle layout was {layoutJson}");
            Math.Abs(label.GetProperty("inputCenterY").GetDouble() - label.GetProperty("captionCenterY").GetDouble())
                .Should().BeLessThanOrEqualTo(3d, $"toggle layout was {layoutJson}");
            label.GetProperty("captionLeft").GetDouble().Should().BeGreaterThan(label.GetProperty("inputRight").GetDouble());
            label.GetProperty("captionWidth").GetDouble().Should().BeGreaterThan(60d);
        }
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
    public async Task Graph_Mode_Info_Popover_Should_Render_Above_Graph_Panels()
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(Viewports[0]);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/graph?uiProfile=graphdemo&selected=10000000-0000-0000-0000-000000000003");

        var trigger = page.GetByRole(AriaRole.Button, new() { Name = "顯示圖譜模式說明" });
        await trigger.FocusAsync();
        await page.WaitForFunctionAsync(
            "() => getComputedStyle(document.querySelector('.page-actions .info-popover-panel')).visibility === 'visible'");
        await page.WaitForFunctionAsync(
            "() => Number(getComputedStyle(document.querySelector('.page-actions .info-popover-panel')).opacity) > 0.95");

        var overlayJson = await page.EvaluateAsync<string>(
            @"() => {
                const panel = document.querySelector('.page-actions .info-popover-panel');
                const rect = panel?.getBoundingClientRect();
                const x = Math.round((rect?.left ?? 0) + ((rect?.width ?? 0) / 2));
                const y = Math.round((rect?.top ?? 0) + ((rect?.height ?? 0) / 2));
                const topElement = document.elementFromPoint(x, y);
                const header = document.querySelector('.page-header');
                const actions = document.querySelector('.page-actions');
                const popover = document.querySelector('.page-actions .info-popover');

                return JSON.stringify({
                    visible: panel ? getComputedStyle(panel).visibility : '',
                    opacity: panel ? Number(getComputedStyle(panel).opacity) : 0,
                    panelTop: Math.round(rect?.top ?? 0),
                    panelBottom: Math.round(rect?.bottom ?? 0),
                    panelHeight: Math.round(rect?.height ?? 0),
                    topElementClass: topElement?.className?.toString() ?? '',
                    topElementTag: topElement?.tagName ?? '',
                    isPanelOnTop: !!topElement?.closest?.('.info-popover-panel'),
                    headerZIndex: header ? getComputedStyle(header).zIndex : '',
                    actionsZIndex: actions ? getComputedStyle(actions).zIndex : '',
                    popoverZIndex: popover ? getComputedStyle(popover).zIndex : '',
                    graphWorkspaceTop: Math.round(document.querySelector('.graph-workspace')?.getBoundingClientRect().top ?? 0)
                });
            }");

        using var document = JsonDocument.Parse(overlayJson);
        var root = document.RootElement;
        root.GetProperty("visible").GetString().Should().Be("visible", $"popover layout was {overlayJson}");
        root.GetProperty("opacity").GetDouble().Should().BeGreaterThan(0.95d, $"popover layout was {overlayJson}");
        root.GetProperty("panelHeight").GetInt32().Should().BeGreaterThan(80, $"popover layout was {overlayJson}");
        root.GetProperty("panelBottom").GetInt32().Should().BeGreaterThan(root.GetProperty("graphWorkspaceTop").GetInt32());
        root.GetProperty("isPanelOnTop").GetBoolean().Should().BeTrue($"popover should not be covered by graph panels: {overlayJson}");
        root.GetProperty("headerZIndex").GetString().Should().Be("70");
        root.GetProperty("actionsZIndex").GetString().Should().Be("72");
        root.GetProperty("popoverZIndex").GetString().Should().Be("100");
    }

    [Fact]
    public async Task Graph_Integrated_View_Should_Render_Project_Overview_Without_Initial_Focus_Clipping()
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(Viewports[0]);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/graph?uiProfile=dense");
        await page.WaitForFunctionAsync("() => (document.querySelectorAll('.graph-view-node').length ?? 0) >= 8");
        await page.WaitForFunctionAsync("() => Number(document.querySelector('.graph-scroll-shell')?.dataset.scale ?? 0) > 0");

        var layoutJson = await page.EvaluateAsync<string>(
            @"() => {
                const shell = document.querySelector('.graph-scroll-shell')?.getBoundingClientRect();
                const nodes = Array.from(document.querySelectorAll('.graph-view-node circle')).map(node => {
                    const rect = node.getBoundingClientRect();
                    return {
                        left: rect.left,
                        top: rect.top,
                        right: rect.right,
                        bottom: rect.bottom
                    };
                });
                const clippedCount = shell
                    ? nodes.filter(node =>
                        node.left < shell.left - 1 ||
                        node.top < shell.top - 1 ||
                        node.right > shell.right + 1 ||
                        node.bottom > shell.bottom + 1).length
                    : nodes.length;

                return JSON.stringify({
                    count: nodes.length,
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

        var nodeBox = await page.Locator(".graph-view-node circle").First.BoundingBoxAsync();
        nodeBox.Should().NotBeNull();

        await page.Mouse.MoveAsync(nodeBox!.X + (nodeBox.Width / 2), nodeBox.Y + (nodeBox.Height / 2));
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(nodeBox.X + (nodeBox.Width / 2) - 112, nodeBox.Y + (nodeBox.Height / 2) + 72, new MouseMoveOptions
        {
            Steps = 8
        });
        await page.Mouse.UpAsync();
        await page.WaitForTimeoutAsync(120);

        var afterNodePanJson = await page.EvaluateAsync<string>(
            @"() => JSON.stringify({
                panX: Number(document.querySelector('.graph-scroll-shell')?.dataset.panX ?? 0),
                panY: Number(document.querySelector('.graph-scroll-shell')?.dataset.panY ?? 0)
            })");

        using var afterNodePanDocument = JsonDocument.Parse(afterNodePanJson);
        var afterNodePanX = afterNodePanDocument.RootElement.GetProperty("panX").GetDouble();
        var afterNodePanY = afterNodePanDocument.RootElement.GetProperty("panY").GetDouble();

        Math.Abs(afterNodePanX - afterPanPanX).Should().BeGreaterThan(32);
        Math.Abs(afterNodePanY - afterPanPanY).Should().BeGreaterThan(20);
    }

    [Fact]
    public async Task Graph_Normal_View_Should_Keep_Small_Graphs_Readable_On_First_Render()
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(Viewports[0]);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/graph?uiProfile=normal");
        await page.Locator(".graph-view-node").First.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15000
        });
        await page.WaitForFunctionAsync("() => Number(document.querySelector('.graph-scroll-shell')?.dataset.scale ?? 0) > 0");

        var layoutJson = await page.EvaluateAsync<string>(
            @"() => JSON.stringify({
                scale: Number(document.querySelector('.graph-scroll-shell')?.dataset.scale ?? 0),
                nodeWidth: Math.round(document.querySelector('.graph-view-node')?.getBoundingClientRect().width ?? 0),
                nodeHeight: Math.round(document.querySelector('.graph-view-node')?.getBoundingClientRect().height ?? 0),
                circleRadius: Number(document.querySelector('.graph-view-node circle')?.getAttribute('r') ?? 0),
                titleWidth: Math.round(document.querySelector('.graph-view-node .graph-node-title')?.getBoundingClientRect().width ?? 0),
                legacyLabelCount: document.querySelectorAll('.graph-view-node-label').length,
                nodeTagName: document.querySelector('.graph-view-node')?.tagName ?? '',
                shellWidth: Math.round(document.querySelector('.graph-scroll-shell')?.getBoundingClientRect().width ?? 0),
                shellHeight: Math.round(document.querySelector('.graph-scroll-shell')?.getBoundingClientRect().height ?? 0),
                contentWidth: Math.round(document.querySelector('.graph-pan-content')?.offsetWidth ?? 0),
                contentHeight: Math.round(document.querySelector('.graph-pan-content')?.offsetHeight ?? 0)
            })");

        using var document = JsonDocument.Parse(layoutJson);
        document.RootElement.GetProperty("scale").GetDouble().Should().BeGreaterThan(0.58d, $"layout was {layoutJson}");
        document.RootElement.GetProperty("nodeWidth").GetInt32().Should().BeGreaterThanOrEqualTo(28);
        document.RootElement.GetProperty("nodeHeight").GetInt32().Should().BeGreaterThanOrEqualTo(18);
        document.RootElement.GetProperty("nodeHeight").GetInt32().Should().BeLessThan(46);
        document.RootElement.GetProperty("circleRadius").GetDouble().Should().BeGreaterThanOrEqualTo(9d);
        document.RootElement.GetProperty("titleWidth").GetInt32().Should().BeGreaterThan(40);
        document.RootElement.GetProperty("legacyLabelCount").GetInt32().Should().Be(0);
        document.RootElement.GetProperty("nodeTagName").GetString().Should().Be("a");
        document.RootElement.GetProperty("shellWidth").GetInt32().Should().BeGreaterThan(360);
    }

    [Fact]
    public async Task Graph_Canvas_Should_Follow_Vital_Document_Relation_Presentation()
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(Viewports[0]);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/graph?uiProfile=graphdemo&selected=10000000-0000-0000-0000-000000000001");
        await page.WaitForFunctionAsync("() => document.querySelectorAll('.graph-view-node').length >= 8");
        await page.WaitForFunctionAsync("() => Number(document.querySelector('.graph-scroll-shell')?.dataset.scale ?? 0) > 0");

        var layoutJson = await page.EvaluateAsync<string>(
            @"() => {
                const shell = document.querySelector('.graph-scroll-shell')?.getBoundingClientRect();
                const circles = Array.from(document.querySelectorAll('.graph-view-node circle')).map(node => {
                    const rect = node.getBoundingClientRect();
                    return {
                        left: rect.left,
                        top: rect.top,
                        right: rect.right,
                        bottom: rect.bottom
                    };
                });
                const clippedCircleCount = shell
                    ? circles.filter(node =>
                        node.left < shell.left - 1 ||
                        node.top < shell.top - 1 ||
                        node.right > shell.right + 1 ||
                        node.bottom > shell.bottom + 1).length
                    : circles.length;
                const firstLine = document.querySelector('.graph-edge');

                return JSON.stringify({
                    nodeCount: document.querySelectorAll('.graph-view-node').length,
                    circleCount: document.querySelectorAll('.graph-view-node circle').length,
                    nodeTextCount: document.querySelectorAll('.graph-view-node .graph-node-title').length,
                    edgeCount: document.querySelectorAll('.graph-edge').length,
                    edgeLabelCount: document.querySelectorAll('.graph-edge-label').length,
                    markerCount: document.querySelectorAll('marker').length,
                    markerEnd: firstLine ? getComputedStyle(firstLine).markerEnd : '',
                    clippedCircleCount,
                    scale: Number(document.querySelector('.graph-scroll-shell')?.dataset.scale ?? 0)
                });
            }");

        using var document = JsonDocument.Parse(layoutJson);
        var root = document.RootElement;
        root.GetProperty("nodeCount").GetInt32().Should().BeGreaterThanOrEqualTo(8);
        root.GetProperty("circleCount").GetInt32().Should().Be(root.GetProperty("nodeCount").GetInt32());
        root.GetProperty("nodeTextCount").GetInt32().Should().Be(root.GetProperty("nodeCount").GetInt32());
        root.GetProperty("edgeCount").GetInt32().Should().BeGreaterThan(0);
        root.GetProperty("edgeLabelCount").GetInt32().Should().Be(0, $"Vital-style graph keeps relation labels in the details panel, layout was {layoutJson}");
        root.GetProperty("markerCount").GetInt32().Should().Be(0, $"Vital-style graph uses plain relationship lines, layout was {layoutJson}");
        root.GetProperty("markerEnd").GetString().Should().Be("none");
        root.GetProperty("clippedCircleCount").GetInt32().Should().Be(0, $"layout was {layoutJson}");
        root.GetProperty("scale").GetDouble().Should().BeGreaterThan(0.24d);
    }

    [Fact]
    public async Task Graph_Demo_Should_Render_Precomputed_Relationship_Examples()
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(Viewports[0]);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/graph?uiProfile=graphdemo");
        await page.WaitForFunctionAsync(
            "() => document.querySelectorAll('.graph-view-node').length >= 12 && document.querySelectorAll('.graph-edge-explicit').length >= 25");

        var graphJson = await page.EvaluateAsync<string>(
            @"() => JSON.stringify({
                nodeCount: document.querySelectorAll('.graph-view-node').length,
                edgeCount: document.querySelectorAll('.graph-edge').length,
                explicitEdgeCount: document.querySelectorAll('.graph-edge-explicit').length,
                similarEdgeCount: document.querySelectorAll('.graph-edge-similar').length,
                edgeLabelCount: document.querySelectorAll('.graph-edge-label').length,
                toolbarText: document.querySelector('.graph-toolbar')?.innerText ?? ''
            })");

        using var document = JsonDocument.Parse(graphJson);
        var root = document.RootElement;
        root.GetProperty("nodeCount").GetInt32().Should().BeGreaterThanOrEqualTo(12);
        root.GetProperty("edgeCount").GetInt32().Should().Be(25, $"demo graph should read the precomputed browser-test graph index, layout was {graphJson}");
        root.GetProperty("explicitEdgeCount").GetInt32().Should().Be(25);
        root.GetProperty("similarEdgeCount").GetInt32().Should().Be(0);
        root.GetProperty("edgeLabelCount").GetInt32().Should().Be(0);
        root.GetProperty("toolbarText").GetString().Should().Contain("25 邊");
    }

    [Fact]
    public async Task Graph_Demo_Selected_Node_Url_Should_Load_Demo_Profile_Without_Profile_Query()
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(Viewports[0]);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/graph?selected=10000000-0000-0000-0000-000000000003");
        await page.WaitForFunctionAsync(
            "() => document.querySelectorAll('.graph-view-node').length >= 12 && document.querySelectorAll('.graph-edge-explicit').length >= 25");

        var graphJson = await page.EvaluateAsync<string>(
            @"() => JSON.stringify({
                nodeCount: document.querySelectorAll('.graph-view-node').length,
                edgeCount: document.querySelectorAll('.graph-edge').length,
                selectedCount: document.querySelectorAll('.graph-view-node.selected').length,
                selectedLabel: document.querySelector('.graph-view-node.selected')?.getAttribute('aria-label') ?? ''
            })");

        using var document = JsonDocument.Parse(graphJson);
        var root = document.RootElement;
        root.GetProperty("nodeCount").GetInt32().Should().BeGreaterThanOrEqualTo(12);
        root.GetProperty("edgeCount").GetInt32().Should().Be(25, $"selected demo URL should not fall back to the one-node normal profile, layout was {graphJson}");
        root.GetProperty("selectedCount").GetInt32().Should().Be(1);
        root.GetProperty("selectedLabel").GetString().Should().Contain("Memory Graph API contract");
    }

    [Fact]
    public async Task Graph_View_Toolbar_Should_Filter_And_Switch_Display_Mode()
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(Viewports[0]);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/graph?uiProfile=dense");
        await page.WaitForFunctionAsync("() => document.querySelectorAll('.graph-view-node').length >= 4");

        var initialNodeCount = await page.Locator(".graph-view-node").CountAsync();
        initialNodeCount.Should().BeGreaterThan(3);

        await page.Locator(".graph-view-search-field input").FillAsync("Dense Memory Item 05");
        await page.WaitForFunctionAsync("() => document.querySelectorAll('.graph-view-node').length === 1");

        var toolbarText = await page.Locator(".graph-view-toolbar").InnerTextAsync();
        toolbarText.Should().Contain("搜尋");
        toolbarText.Should().Contain("顯示");
        toolbarText.Should().Contain("關聯節點");

        await page.Locator(".graph-view-mode-field select").SelectOptionAsync("all");
        await page.Locator(".graph-view-search-field input").FillAsync(string.Empty);
        await page.WaitForFunctionAsync(
            "(count) => document.querySelectorAll('.graph-view-node').length >= count",
            initialNodeCount);
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

        await page.GetByRole(AriaRole.Button, new() { Name = "全螢幕", Exact = true }).ClickAsync();
        await page.WaitForFunctionAsync("() => document.querySelector('.graph-canvas-panel')?.classList.contains('graph-canvas-panel-expanded') === true");

        var expandedBox = await panel.BoundingBoxAsync();
        expandedBox.Should().NotBeNull();
        var viewportJson = await page.EvaluateAsync<string>(
            @"() => JSON.stringify({
                width: window.innerWidth,
                height: window.innerHeight
            })");
        using var viewportDocument = JsonDocument.Parse(viewportJson);
        var viewportWidth = viewportDocument.RootElement.GetProperty("width").GetDouble();
        var viewportHeight = viewportDocument.RootElement.GetProperty("height").GetDouble();

        expandedBox!.X.Should().BeLessThan(24);
        expandedBox.Y.Should().BeLessThan(24);
        expandedBox!.Width.Should().BeGreaterThan(beforeBox!.Width + 120);
        expandedBox.Width.Should().BeGreaterThan((float)(viewportWidth * 0.95d));
        expandedBox.Height.Should().BeGreaterThan((float)(viewportHeight * 0.92d));

        await page.GetByRole(AriaRole.Button, new() { Name = "收合圖表" }).ClickAsync();
        await page.WaitForFunctionAsync("() => document.querySelector('.graph-canvas-panel')?.classList.contains('graph-canvas-panel-expanded') === false");
    }

    [Fact]
    public async Task Graph_Should_Background_Refresh_Without_Resetting_Selected_Node()
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(Viewports[0]);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/graph?uiProfile=graphdemo");
        await page.WaitForFunctionAsync("() => document.querySelectorAll('.graph-view-node').length >= 8");

        var secondNode = page.Locator(".graph-view-node").Nth(1);
        await secondNode.Locator("circle").ClickAsync();
        var selectedNodeId = await secondNode.GetAttributeAsync("data-graph-node-id");
        selectedNodeId.Should().NotBeNullOrWhiteSpace();
        var firstRefreshTimestamp = await page.Locator(".refresh-status-time .client-local-time").GetAttributeAsync("data-local-iso");
        firstRefreshTimestamp.Should().NotBeNullOrWhiteSpace();

        await page.WaitForFunctionAsync(
            @"({ selectedNodeId, firstRefreshTimestamp }) => {
                const selected = document.querySelector('.graph-view-node.selected');
                const refreshedAt = document.querySelector('.refresh-status-time .client-local-time')?.getAttribute('data-local-iso');
                return selected?.getAttribute('data-graph-node-id') === selectedNodeId &&
                    refreshedAt &&
                    refreshedAt !== firstRefreshTimestamp;
            }",
            new { selectedNodeId, firstRefreshTimestamp },
            new PageWaitForFunctionOptions { Timeout = 5000 });
    }

    [Theory]
    [InlineData("app-browser-1092", 1092, 1270, true)]
    [InlineData("comment-retina-2182", 2182, 2538, false)]
    public async Task Graph_Responsive_Layout_Should_Not_Overlap_When_Focused_And_Expanded_At_App_Browser_Sizes(
        string viewportName,
        int width,
        int height,
        bool expectSingleColumn)
    {
        await _fixture.EnsureDashboardRunningAsync();
        var viewport = new DashboardViewport(viewportName, width, height);
        await using var context = await _fixture.CreateContextAsync(viewport);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/graph?uiProfile=graphdemo");
        await page.WaitForFunctionAsync("() => document.querySelectorAll('.graph-view-node').length >= 8");

        await page.Locator(".graph-view-node").First.Locator("circle").ClickAsync();
        var focusButton = page.GetByRole(AriaRole.Button, new() { Name = "聚焦此節點" });
        try
        {
            await focusButton.ClickAsync(new LocatorClickOptions { Timeout = 3000 });
        }
        catch (TimeoutException ex)
        {
            var diagnosticJson = await page.EvaluateAsync<string>(
                @"() => {
                    const rectOf = selector => {
                        const element = document.querySelector(selector);
                        if (!element) {
                            return null;
                        }

                        const rect = element.getBoundingClientRect();
                        return {
                            left: Math.round(rect.left),
                            top: Math.round(rect.top),
                            right: Math.round(rect.right),
                            bottom: Math.round(rect.bottom),
                            width: Math.round(rect.width),
                            height: Math.round(rect.height)
                        };
                    };
                    const workspace = document.querySelector('.graph-workspace');
                    const style = workspace ? getComputedStyle(workspace) : null;
                    const button = Array.from(document.querySelectorAll('button')).find(item => item.textContent?.trim() === '聚焦此節點');
                    const buttonRect = button?.getBoundingClientRect();
                    const centerX = buttonRect ? Math.round(buttonRect.left + (buttonRect.width / 2)) : 0;
                    const centerY = buttonRect ? Math.round(buttonRect.top + (buttonRect.height / 2)) : 0;
                    const blocker = document.elementFromPoint(centerX, centerY);

                    return JSON.stringify({
                        viewportWidth: window.innerWidth,
                        areas: style?.gridTemplateAreas ?? '',
                        columns: style?.gridTemplateColumns ?? '',
                        rows: style?.gridTemplateRows ?? '',
                        alignContent: style?.alignContent ?? '',
                        workspace: rectOf('.graph-workspace'),
                        filter: rectOf('.graph-filter-panel'),
                        canvas: rectOf('.graph-canvas-panel'),
                        detail: rectOf('.graph-detail-panel'),
                        shell: rectOf('.graph-scroll-shell'),
                        button: button ? rectOf('button.ghost-button') : null,
                        centerX,
                        centerY,
                        blockerTag: blocker?.tagName ?? '',
                        blockerClass: blocker?.className?.toString() ?? '',
                        blockerPanelClass: blocker?.closest('section')?.className?.toString() ?? ''
                    });
                }");
            throw new InvalidOperationException($"Graph focus action is visually blocked at {viewportName}: {diagnosticJson}", ex);
        }
        await page.GetByRole(AriaRole.Button, new() { Name = "展開二階鄰居" }).ClickAsync();
        await page.WaitForTimeoutAsync(350);

        var layoutJson = await page.EvaluateAsync<string>(
            @"() => {
                const rectOf = selector => {
                    const element = document.querySelector(selector);
                    if (!element) {
                        return null;
                    }

                    const rect = element.getBoundingClientRect();
                    return {
                        left: Math.round(rect.left),
                        top: Math.round(rect.top),
                        right: Math.round(rect.right),
                        bottom: Math.round(rect.bottom),
                        width: Math.round(rect.width),
                        height: Math.round(rect.height)
                    };
                };
                const overlapArea = (a, b) => {
                    if (!a || !b) {
                        return 0;
                    }

                    const width = Math.max(0, Math.min(a.right, b.right) - Math.max(a.left, b.left));
                    const height = Math.max(0, Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top));
                    return Math.round(width * height);
                };

                const workspace = document.querySelector('.graph-workspace');
                const root = document.documentElement;
                const body = document.body;
                const filter = rectOf('.graph-filter-panel');
                const canvas = rectOf('.graph-canvas-panel');
                const detail = rectOf('.graph-detail-panel');
                const shell = rectOf('.graph-scroll-shell');
                const style = workspace ? getComputedStyle(workspace) : null;

                return JSON.stringify({
                    viewportWidth: window.innerWidth,
                    documentScrollWidth: root.scrollWidth,
                    bodyScrollWidth: body.scrollWidth,
                    contentScrollWidth: document.querySelector('.content')?.scrollWidth ?? 0,
                    contentClientWidth: document.querySelector('.content')?.clientWidth ?? 0,
                    areas: style?.gridTemplateAreas ?? '',
                    columns: style?.gridTemplateColumns ?? '',
                    filter,
                    canvas,
                    detail,
                    shell,
                    filterCanvasOverlap: overlapArea(filter, canvas),
                    canvasDetailOverlap: overlapArea(canvas, detail),
                    filterDetailOverlap: overlapArea(filter, detail),
                    visibleNodeCount: document.querySelectorAll('.graph-view-node').length
                });
            }");

        using var document = JsonDocument.Parse(layoutJson);
        var root = document.RootElement;
        root.GetProperty("documentScrollWidth").GetInt32().Should().BeLessThanOrEqualTo(root.GetProperty("viewportWidth").GetInt32() + 1);
        root.GetProperty("bodyScrollWidth").GetInt32().Should().BeLessThanOrEqualTo(root.GetProperty("viewportWidth").GetInt32() + 1);
        root.GetProperty("contentScrollWidth").GetInt32().Should().BeLessThanOrEqualTo(root.GetProperty("contentClientWidth").GetInt32() + 1);
        root.GetProperty("visibleNodeCount").GetInt32().Should().BeGreaterThanOrEqualTo(5);
        root.GetProperty("filterCanvasOverlap").GetInt32().Should().Be(0);
        root.GetProperty("canvasDetailOverlap").GetInt32().Should().Be(0);
        root.GetProperty("filterDetailOverlap").GetInt32().Should().Be(0);
        root.GetProperty("shell").GetProperty("width").GetInt32().Should().BeGreaterThan(420);
        root.GetProperty("shell").GetProperty("right").GetInt32().Should().BeLessThanOrEqualTo(root.GetProperty("viewportWidth").GetInt32() + 1);

        if (expectSingleColumn)
        {
            root.GetProperty("areas").GetString().Should().Contain("\"filter\"");
            root.GetProperty("areas").GetString().Should().Contain("\"canvas\"");
            root.GetProperty("areas").GetString().Should().Contain("\"detail\"");
            root.GetProperty("columns").GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(1);
            root.GetProperty("canvas").GetProperty("top").GetInt32().Should().BeGreaterThan(root.GetProperty("filter").GetProperty("bottom").GetInt32());
            root.GetProperty("detail").GetProperty("top").GetInt32().Should().BeGreaterThan(root.GetProperty("canvas").GetProperty("top").GetInt32());
        }
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
    public async Task Security_Form_Controls_Should_Use_Consistent_Field_Heights_On_App_Browser_Viewport()
    {
        await _fixture.EnsureDashboardRunningAsync();
        var viewport = new DashboardViewport("app-browser-1196", 1196, 1270);
        await using var context = await _fixture.CreateContextAsync(viewport);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/security?uiProfile=normal");

        var layoutJson = await page.EvaluateAsync<string>(
            @"() => JSON.stringify(Array.from(document.querySelectorAll('.security-layout .settings-form-grid')).map((grid, gridIndex) => {
                const controls = Array.from(grid.children).map((field, fieldIndex) => {
                    const control = field.matches('.toggle-field')
                        ? field
                        : field.querySelector(':scope > input, :scope > select, :scope > .toggle-field');
                    if (!control || field.matches('.settings-field-span')) {
                        return null;
                    }

                    const rect = control.getBoundingClientRect();
                    const fieldRect = field.getBoundingClientRect();
                    if (rect.width <= 0 || rect.height <= 0 || fieldRect.width <= 0 || fieldRect.height <= 0) {
                        return null;
                    }

                    return {
                        gridIndex,
                        fieldIndex,
                        tag: control.tagName.toLowerCase(),
                        className: control.className,
                        height: Math.round(rect.height),
                        top: Math.round(rect.top),
                        fieldTop: Math.round(fieldRect.top)
                    };
                }).filter(Boolean);

                return { gridIndex, controls };
            }).filter(group => group.controls.length > 1))");

        using var document = JsonDocument.Parse(layoutJson);
        var groups = document.RootElement.EnumerateArray().ToArray();
        groups.Should().NotBeEmpty();

        foreach (var group in groups)
        {
            var controls = group.GetProperty("controls").EnumerateArray().ToArray();
            var heights = controls.Select(control => control.GetProperty("height").GetInt32()).ToArray();

            heights.Should().OnlyContain(height => height >= 46 && height <= 50, $"security controls should stay near the 48px field baseline: {layoutJson}");
            (heights.Max() - heights.Min()).Should().BeLessThanOrEqualTo(1, $"security controls in the same form row should have matching heights: {layoutJson}");
        }
    }

    [Fact]
    public async Task Security_My_Token_Panel_Should_Not_Overlap_Next_Panel_And_Reload_Should_Sit_Next_To_Status()
    {
        await _fixture.EnsureDashboardRunningAsync();
        var viewport = new DashboardViewport("app-browser-1196", 1196, 1270);
        await using var context = await _fixture.CreateContextAsync(viewport);
        var page = await context.NewPageAsync();

        await LoginAndOpenAsync(page, "/security?uiProfile=normal");

        var layoutJson = await page.EvaluateAsync<string>(
            @"() => {
                const rectOf = selector => {
                    const element = document.querySelector(selector);
                    if (!element) {
                        return null;
                    }

                    const rect = element.getBoundingClientRect();
                    return {
                        left: Math.round(rect.left),
                        top: Math.round(rect.top),
                        right: Math.round(rect.right),
                        bottom: Math.round(rect.bottom),
                        width: Math.round(rect.width),
                        height: Math.round(rect.height)
                    };
                };

                return JSON.stringify({
                    myTokenPanel: rectOf('.security-layout > .security-my-token-panel'),
                    myTokenTable: rectOf('.security-my-token-table-shell'),
                    tenantPanel: rectOf('.security-layout > .panel:nth-of-type(2)'),
                    refreshStatus: rectOf('.security-header-actions .refresh-status-shell'),
                    reloadButton: rectOf('.security-header-actions .ghost-button')
                });
            }");

        using var document = JsonDocument.Parse(layoutJson);
        var root = document.RootElement;
        var myTokenPanel = root.GetProperty("myTokenPanel");
        var myTokenTable = root.GetProperty("myTokenTable");
        var tenantPanel = root.GetProperty("tenantPanel");
        var refreshStatus = root.GetProperty("refreshStatus");
        var reloadButton = root.GetProperty("reloadButton");

        myTokenPanel.GetProperty("bottom").GetInt32().Should().BeLessThanOrEqualTo(tenantPanel.GetProperty("top").GetInt32(), $"my token panel must not overlap the tenant panel: {layoutJson}");
        myTokenTable.GetProperty("bottom").GetInt32().Should().BeLessThanOrEqualTo(myTokenPanel.GetProperty("bottom").GetInt32(), $"my token table must stay inside its panel: {layoutJson}");
        reloadButton.GetProperty("left").GetInt32().Should().BeGreaterThan(refreshStatus.GetProperty("right").GetInt32() - 1, $"reload button should sit to the right of refresh status: {layoutJson}");
        Math.Abs(reloadButton.GetProperty("top").GetInt32() - refreshStatus.GetProperty("top").GetInt32()).Should().BeLessThanOrEqualTo(2, $"reload button should align with refresh status: {layoutJson}");
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
    Dense,
    GraphDemo
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
        startInfo.Environment["Dashboard__Polling__GraphSeconds"] = "1";
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
