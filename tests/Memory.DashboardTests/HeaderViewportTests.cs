using System.Text.Json;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Memory.DashboardTests;

// Real browser and shipped UI; the existing fixture supplies local API doubles, not backend acceptance.
public sealed class HeaderViewportTests(DashboardBrowserFixture fixture, ITestOutputHelper output) : IClassFixture<DashboardBrowserFixture>
{
    [Theory]
    [InlineData(390, 844, 150)]
    [InlineData(1024, 600, 150)]
    [InlineData(1366, 768, 150)]
    [InlineData(320, 844, 100)]
    [InlineData(320, 844, 150)]
    public async Task Header_controls_should_remain_visible_and_keyboard_operable_after_dense_graph_navigation(int width, int height, int fontPercent)
    {
        await using var context = await fixture.CreateContextAsync(new($"header-{width}-{fontPercent}", width, height));
        var page = await LoginGraphAsync(context);
        await page.EvaluateAsync("percent => document.documentElement.style.fontSize=percent+'%'", fontPercent);
        await page.WaitForFunctionAsync("() => Number(document.querySelector('.graph-scroll-shell')?.dataset.scale)>0");

        await AssertHeaderAsync(page, "all-projects-before-selection");
        const string projectId = "context-hub-dev-project-with-long-name";
        await page.GetByRole(AriaRole.Combobox, new() { Name = "專案檢視", Exact = true }).SelectOptionAsync(projectId);
        await page.GetByRole(AriaRole.Button, new() { Name = "更新圖譜", Exact = true }).ClickAsync();
        await page.WaitForFunctionAsync("() => !document.querySelector('.page-actions-primary button').disabled && document.querySelector('.graph-view-node')!==null");
        await AssertHeaderAsync(page, "long-project-after-update");
        var projectChip = page.Locator(".graph-status-chip").First;
        Assert.Equal($"Project · {projectId}", (await projectChip.InnerTextAsync()).Trim());
        Assert.True(await projectChip.IsVisibleAsync());
        Assert.True(await projectChip.EvaluateAsync<bool>("""
            element => {
                const range=document.createRange(); range.selectNodeContents(element);
                const box=element.getBoundingClientRect();
                return element.scrollWidth<=element.clientWidth+1 && element.scrollHeight<=element.clientHeight+1 && [...range.getClientRects()].every(rect=>rect.left>=box.left-1 && rect.right<=box.right+1 && rect.top>=box.top-1 && rect.bottom<=box.bottom+1);
            }
            """), "Full project label must remain inside its chip without text clipping.");

        var idsBefore = await page.Locator("[data-graph-node-id]").EvaluateAllAsync<string[]>("nodes => nodes.map(n=>n.dataset.graphNodeId).sort()");
        Assert.NotEmpty(idsBefore);
        Assert.Equal(3, await page.Locator(".graph-view-field").CountAsync());
        Assert.Equal(3, await page.Locator(".graph-view-stat-chip").CountAsync());
        var labelsBefore = await page.Locator(".graph-node-title").AllTextContentsAsync();
        Assert.NotEmpty(labelsBefore);
        foreach (var item in await page.Locator(".graph-view-field, .graph-view-stat-chip").AllAsync()) Assert.True(await item.IsVisibleAsync());
        await AssertHeaderAsync(page, "before-native-navigation");
        var node = page.Locator(".graph-view-node").First;
        var selectedId = await node.GetAttributeAsync("data-graph-node-id");
        await node.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForFunctionAsync("id => document.querySelector('.graph-view-node.selected')?.dataset.graphNodeId===id", selectedId);
        await AssertHeaderAsync(page, "after-native-node-focus");
        // SVG anchor bounding boxes can include unpainted space; retain the natural attempt, then use its painted title.
        try { await node.ClickAsync(new() { Timeout = 1500 }); }
        catch (TimeoutException) { output.WriteLine("Native anchor click reached unpainted-space timeout; painted title remains the selection target."); }
        await AssertHeaderAsync(page, "after-native-anchor-click");
        await node.Locator(".graph-node-title").ClickAsync();
        await page.Locator(".graph-detail-content").ScrollIntoViewIfNeededAsync();
        await AssertHeaderAsync(page, "after-detail-scroll");
        Assert.Equal(idsBefore, await page.Locator("[data-graph-node-id]").EvaluateAllAsync<string[]>("nodes => nodes.map(n=>n.dataset.graphNodeId).sort()"));
        Assert.Equal(labelsBefore, await page.Locator(".graph-node-title").AllTextContentsAsync());

        var command = page.GetByRole(AriaRole.Button, new() { Name = "搜尋功能", Exact = true });
        await command.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await page.GetByRole(AriaRole.Dialog, new() { Name = "快速切換功能" }).WaitForAsync();
        var close = page.GetByRole(AriaRole.Button, new() { Name = "關閉快速切換功能" });
        await close.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await page.GetByRole(AriaRole.Dialog, new() { Name = "快速切換功能" }).WaitForAsync(new() { State = WaitForSelectorState.Hidden });

        var theme = page.Locator(".theme-switcher-toggle");
        await command.FocusAsync();
        await page.Keyboard.PressAsync("Tab");
        Assert.True(await theme.EvaluateAsync<bool>("element => element===document.activeElement"));
        await page.Keyboard.PressAsync("Enter");
        var menu = page.GetByRole(AriaRole.Menu, new() { Name = "主題選單" });
        await menu.WaitForAsync();
        await AssertVisibleBoundsAsync(menu, "theme-menu", requireTarget: false);
        var options = menu.GetByRole(AriaRole.Menuitemradio);
        Assert.Equal(3, await options.CountAsync());
        for (var index = 0; index < 3; index++)
        {
            await page.Keyboard.PressAsync("Tab");
            Assert.True(await options.Nth(index).EvaluateAsync<bool>("element => element===document.activeElement"));
            await AssertVisibleBoundsAsync(options.Nth(index), $"theme-option-{index}");
        }
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForFunctionAsync("() => document.documentElement.dataset.theme==='light'");
        await theme.FocusAsync();
        await page.Keyboard.PressAsync("Tab");
        var logout = page.GetByRole(AriaRole.Button, new() { Name = "登出", Exact = true });
        Assert.True(await logout.EvaluateAsync<bool>("element => element===document.activeElement"));
        await AssertVisibleBoundsAsync(logout, "logout-focus");

        var content = page.Locator(".content");
        await content.EvaluateAsync("element => element.scrollTop=0");
        var contentBounds = await content.BoundingBoxAsync();
        Assert.NotNull(contentBounds);
        await page.Mouse.MoveAsync(contentBounds.X + 2, contentBounds.Y + contentBounds.Height / 2);
        await page.Mouse.WheelAsync(0, 500);
        await page.WaitForFunctionAsync("() => document.querySelector('.content').scrollTop>0");
        await AssertHeaderAsync(page, "after-content-wheel");
        await AssertVisibleBoundsAsync(page.Locator(".dashboard-footer"), "footer", requireTarget: false);
        await page.ScreenshotAsync(new() { Path = Path.Combine(fixture.ArtifactDirectory, $"header-{width}-{height}-{fontPercent}.png") });
    }

    [Fact]
    public async Task Background_graph_refresh_should_publish_completion_without_user_interaction()
    {
        await using var context = await fixture.CreateContextAsync(new("graph-poll-completion", 320, 844));
        var page = await LoginGraphAsync(context);
        await page.EvaluateAsync("percent => document.documentElement.style.fontSize=percent+'%'", 150);
        await page.Locator(".refresh-status-time time[data-local-iso]").WaitForAsync();
        await page.EvaluateAsync("""
            () => {
                const time = () => document.querySelector('.refresh-status-time time[data-local-iso]')?.dataset.localIso;
                const button = () => document.querySelector('.page-actions-primary button');
                const proof = window.__graphPollingProof = {initial: time(), completed: []};
                proof.observer = new MutationObserver(() => {
                    const value = time();
                    if (value && value !== proof.initial && button() && !button().disabled && !proof.completed.includes(value)) {
                        proof.completed.push(value);
                    }
                });
                proof.observer.observe(document.body, {subtree: true, childList: true, attributes: true, attributeFilter: ['disabled', 'data-local-iso']});
            }
            """);
        // The fixture polls every second. Only observe DOM; do not trigger a render or refresh.
        await page.WaitForFunctionAsync("() => window.__graphPollingProof.completed.length >= 2", null, new() { Timeout = 10000 });
        var completed = await page.EvaluateAsync<string[]>("() => { const proof=window.__graphPollingProof; proof.observer.disconnect(); return proof.completed; }");
        Assert.True(completed.Length >= 2);
        Assert.True(DateTimeOffset.Parse(completed[1]) > DateTimeOffset.Parse(completed[0]));
        output.WriteLine($"Passive polling completion timestamps: {string.Join(", ", completed)}");
    }

    private async Task<IPage> LoginGraphAsync(IBrowserContext context)
    {
        var page = await context.NewPageAsync();
        await page.GotoAsync(new Uri(fixture.BaseUri, "/login?returnUrl=%2Fgraph%3FuiProfile%3Ddense").ToString());
        // Authenticate through the real local endpoint and shared cookie jar; this test targets layout, not login hydration.
        var form = context.APIRequest.CreateFormData();
        form.Set("Username", "admin");
        form.Set("Password", "ContextHub!123");
        form.Set("ReturnUrl", "/graph?uiProfile=dense");
        form.Set("__RequestVerificationToken", await page.Locator("input[name='__RequestVerificationToken']").InputValueAsync());
        var loginResponse = await context.APIRequest.PostAsync(new Uri(fixture.BaseUri, "/account/login").ToString(), new() { Form = form, MaxRedirects = 0 });
        Assert.Equal(302, loginResponse.Status);
        Assert.Equal("/graph?uiProfile=dense", loginResponse.Headers["location"]);
        await loginResponse.DisposeAsync();
        await page.GotoAsync(new Uri(fixture.BaseUri, "/graph?uiProfile=dense").ToString());
        await page.Locator(".dashboard-shell[data-dashboard-interactive='true']").WaitForAsync();
        await page.Locator(".graph-view-node").First.WaitForAsync();
        return page;
    }

    private async Task AssertHeaderAsync(IPage page, string stage)
    {
        var metrics = await page.EvaluateAsync<JsonElement>("""
            () => {
                const shell=document.querySelector('.main-shell'), header=document.querySelector('.topbar'), content=document.querySelector('.content');
                return {shellX:shell.scrollLeft,windowX:scrollX,contentX:content.scrollLeft,contentWidth:content.clientWidth,contentScrollWidth:content.scrollWidth,
                    headerWidth:header.clientWidth,headerScrollWidth:header.scrollWidth,
                    headerLeft:header.getBoundingClientRect().left,headerRight:header.getBoundingClientRect().right,viewportWidth:innerWidth,
                    containers:[...document.querySelectorAll('*')].filter(e=>getComputedStyle(e).containerName!=='none').map(e=>({classes:e.getAttribute('class'),name:getComputedStyle(e).containerName,width:e.clientWidth})),
                    overflowElements:[...content.querySelectorAll('*')].map(e=>({tag:e.tagName,classes:e.getAttribute('class'),left:e.getBoundingClientRect().left,right:e.getBoundingClientRect().right,width:e.clientWidth,scrollWidth:e.scrollWidth,minWidth:getComputedStyle(e).minWidth})).filter(e=>e.right>innerWidth+1 && e.tag!=='svg' && e.tag!=='g' && e.tag!=='path' && e.tag!=='text' && e.tag!=='circle' && e.tag!=='line').slice(0,20)};
            }
            """);
        output.WriteLine($"{stage}: {metrics}");
        Assert.InRange(Math.Abs(metrics.GetProperty("shellX").GetDouble()), 0, 1);
        Assert.InRange(Math.Abs(metrics.GetProperty("windowX").GetDouble()), 0, 1);
        Assert.InRange(Math.Abs(metrics.GetProperty("contentX").GetDouble()), 0, 1);
        Assert.True(metrics.GetProperty("contentScrollWidth").GetDouble() <= metrics.GetProperty("contentWidth").GetDouble() + 1, metrics.ToString());
        Assert.True(metrics.GetProperty("headerScrollWidth").GetDouble() <= metrics.GetProperty("headerWidth").GetDouble() + 1, metrics.ToString());
        var controls = page.Locator(".topbar .command-button, .topbar .theme-switcher-toggle, .topbar form button");
        Assert.Equal(3, await controls.CountAsync());
        for (var index = 0; index < 3; index++) await AssertVisibleBoundsAsync(controls.Nth(index), $"{stage}-control-{index}");
        var mobileNavigation = page.Locator(".mobile-nav-button");
        if (await mobileNavigation.IsVisibleAsync()) await AssertVisibleBoundsAsync(mobileNavigation, "mobile-navigation");
    }

    private static async Task AssertVisibleBoundsAsync(ILocator element, string name, bool requireTarget = true)
    {
        var state = await element.EvaluateAsync<JsonElement>("""
            element => {
                const rect=element.getBoundingClientRect(), hit=document.elementFromPoint(rect.x+rect.width/2,rect.y+rect.height/2);
                return {left:rect.left,top:rect.top,right:rect.right,bottom:rect.bottom,width:rect.width,height:rect.height,
                    viewportWidth:innerWidth,viewportHeight:innerHeight,hit:!!hit && (hit===element || element.contains(hit))};
            }
            """);
        Assert.True(state.GetProperty("left").GetDouble() >= -1 && state.GetProperty("top").GetDouble() >= -1
            && state.GetProperty("right").GetDouble() <= state.GetProperty("viewportWidth").GetDouble() + 1
            && state.GetProperty("bottom").GetDouble() <= state.GetProperty("viewportHeight").GetDouble() + 1, $"{name}: {state}");
        Assert.True(state.GetProperty("hit").GetBoolean(), $"{name} is covered: {state}");
        if (requireTarget) Assert.True(state.GetProperty("width").GetDouble() >= 44 && state.GetProperty("height").GetDouble() >= 44, $"{name} target: {state}");
    }
}
