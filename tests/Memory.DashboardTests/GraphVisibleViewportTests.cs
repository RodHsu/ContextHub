using System.Text.Json;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Memory.DashboardTests;

// Shipped UI and real browser interactions, with the existing local API fixture.
public sealed class GraphVisibleViewportTests(DashboardBrowserFixture fixture, ITestOutputHelper output) : IClassFixture<DashboardBrowserFixture>
{
    [Theory]
    [InlineData(1366, 768, 150)]
    [InlineData(1024, 600, 150)]
    [InlineData(390, 844, 150)]
    [InlineData(320, 844, 100)]
    [InlineData(320, 844, 150)]
    public async Task Native_fit_and_expansion_should_expose_painted_graph_and_all_controls(int width, int height, int fontPercent)
    {
        await using var context = await fixture.CreateContextAsync(new($"graph-visible-{width}-{fontPercent}", width, height));
        var page = await LoginAsync(context);
        await page.EvaluateAsync("percent => document.documentElement.style.fontSize=percent+'%'", fontPercent);
        await page.WaitForFunctionAsync("() => Number(document.querySelector('.graph-scroll-shell')?.dataset.scale)>0");
        var ids = await NodeIdsAsync(page);
        Assert.True(ids.Length > 1, "The dense fixture must exercise multiple visible nodes.");
        var titles = await page.Locator(".graph-node-title").AllTextContentsAsync();
        var fit = page.GetByRole(AriaRole.Button, new() { Name = "適應視圖", Exact = true });
        await fit.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await AssertPhysicalAsync(page, "normal");
        var expand = page.GetByRole(AriaRole.Button, new() { Name = "全螢幕顯示", Exact = true });
        await expand.ClickAsync();
        await page.Locator(".graph-canvas-panel-expanded").WaitForAsync();
        await AssertPhysicalAsync(page, "expanded");
        await expand.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await page.Locator(".graph-canvas-panel-expanded").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await AssertPhysicalAsync(page, "collapsed");
        Assert.Equal(ids, await NodeIdsAsync(page));
        Assert.Equal(titles, await page.Locator(".graph-node-title").AllTextContentsAsync());
    }

    [Fact]
    public async Task Owner_and_controls_resize_should_preserve_pan_and_scroll_and_stop_after_unregister()
    {
        await using var context = await fixture.CreateContextAsync(new("graph-owner-lifecycle", 800, 600));
        var page = await context.NewPageAsync();
        // Controlled geometry isolates JavaScript lifecycle; the theory above covers the shipped CSS and UI.
        await page.SetContentAsync("""
            <style>
            .content{height:400px;width:400px;overflow:auto}.graph-viewport-frame{width:360px}
            .graph-scroll-shell{height:var(--graph-physical-height,500px);width:360px;overflow:hidden}
            .graph-viewport-controls{height:50px;margin-top:12px}.graph-pan-content{width:880px;height:600px;transform-origin:0 0}
            svg{width:880px;height:600px;overflow:visible}
            </style>
            <div class="content"><div style="height:600px"></div><div class="graph-canvas-panel">
            <div class="graph-viewport-frame"><div class="graph-scroll-shell"><div class="graph-pan-content">
            <svg class="graph-view-svg"><g class="graph-view-node"><circle cx="50" cy="50" r="20"/><text x="80" y="56">First</text></g>
            <g class="graph-view-node"><circle cx="200" cy="150" r="20"/><text x="230" y="156">Second</text></g></svg>
            </div></div><div class="graph-viewport-controls"></div></div></div></div>
            """);
        await page.AddScriptTagAsync(new() { Url = new Uri(fixture.BaseUri, "/dashboard-graph.js").ToString() });
        await page.EvaluateAsync("""
            () => {
                const shell=document.querySelector('.graph-scroll-shell');
                contextHubGraph.register(shell,document.querySelector('.graph-pan-content'));
                contextHubGraph.fit(shell);
                contextHubGraph.panRight(shell);
                document.querySelector('.content').scrollTop=200;
            }
            """);
        await page.WaitForTimeoutAsync(350);
        var before = await LifecycleStateAsync(page);
        await page.EvaluateAsync("""
            () => {
                window.__graphFrames=0;
                const request=window.requestAnimationFrame.bind(window);
                window.requestAnimationFrame=callback=>{window.__graphFrames++;return request(callback);};
                document.querySelector('.content').style.height='300px';
                document.querySelector('.graph-viewport-controls').style.height='80px';
            }
            """);
        await page.WaitForFunctionAsync("before=>document.querySelector('.graph-scroll-shell').getBoundingClientRect().height<before", before.GetProperty("height").GetDouble());
        await page.WaitForTimeoutAsync(350);
        var resized = await LifecycleStateAsync(page);
        Assert.Equal(before.GetProperty("panX").GetString(), resized.GetProperty("panX").GetString());
        Assert.Equal(before.GetProperty("panY").GetString(), resized.GetProperty("panY").GetString());
        Assert.Equal(200, resized.GetProperty("scrollTop").GetDouble());
        Assert.True(resized.GetProperty("frameHeight").GetDouble() <= resized.GetProperty("ownerHeight").GetDouble());
        var frames = await page.EvaluateAsync<int>("window.__graphFrames");
        await page.WaitForTimeoutAsync(350);
        Assert.Equal(frames, await page.EvaluateAsync<int>("window.__graphFrames"));
        await page.EvaluateAsync("() => contextHubGraph.unregister(document.querySelector('.graph-scroll-shell'))");
        var disposedStyle = await page.Locator(".graph-viewport-frame").GetAttributeAsync("style");
        Assert.Null(await page.Locator(".graph-viewport-frame").GetAttributeAsync("data-physical-height"));
        await page.EvaluateAsync("""
            () => {
                window.__graphFrames=0;
                document.querySelector('.content').style.height='250px';
                document.querySelector('.graph-viewport-controls').style.height='90px';
                document.querySelector('text').textContent='Changed after unregister';
                window.dispatchEvent(new Event('resize'));
            }
            """);
        await page.WaitForTimeoutAsync(350);
        Assert.Equal(0, await page.EvaluateAsync<int>("window.__graphFrames"));
        Assert.Equal(disposedStyle, await page.Locator(".graph-viewport-frame").GetAttributeAsync("style"));
        Assert.Equal(200, (await LifecycleStateAsync(page)).GetProperty("scrollTop").GetDouble());
    }

    private static Task<JsonElement> LifecycleStateAsync(IPage page) => page.EvaluateAsync<JsonElement>("""
        () => {const shell=document.querySelector('.graph-scroll-shell'),owner=document.querySelector('.content');
            return {panX:shell.dataset.panX,panY:shell.dataset.panY,scrollTop:owner.scrollTop,height:shell.getBoundingClientRect().height,
                frameHeight:document.querySelector('.graph-viewport-frame').getBoundingClientRect().height,ownerHeight:owner.clientHeight};}
        """);

    private async Task<IPage> LoginAsync(IBrowserContext context)
    {
        var page = await context.NewPageAsync();
        await page.GotoAsync(new Uri(fixture.BaseUri, "/login?returnUrl=%2Fgraph%3FuiProfile%3Ddense").ToString());
        var form = context.APIRequest.CreateFormData();
        form.Set("Username", "admin");
        form.Set("Password", "ContextHub!123");
        form.Set("ReturnUrl", "/graph?uiProfile=dense");
        form.Set("__RequestVerificationToken", await page.Locator("input[name='__RequestVerificationToken']").InputValueAsync());
        var response = await context.APIRequest.PostAsync(new Uri(fixture.BaseUri, "/account/login").ToString(), new() { Form = form, MaxRedirects = 0 });
        Assert.Equal(302, response.Status);
        await response.DisposeAsync();
        await page.GotoAsync(new Uri(fixture.BaseUri, "/graph?uiProfile=dense").ToString());
        await page.Locator(".dashboard-shell[data-dashboard-interactive='true']").WaitForAsync();
        await page.Locator(".graph-view-node").First.WaitForAsync();
        return page;
    }

    private static Task<string[]> NodeIdsAsync(IPage page)
        => page.Locator("[data-graph-node-id]").EvaluateAllAsync<string[]>("nodes=>nodes.map(n=>n.dataset.graphNodeId).sort()");

    private async Task AssertPhysicalAsync(IPage page, string phase)
    {
        // Allow the UI's scheduled fit to settle, without scrolling or taking an element screenshot.
        await page.WaitForTimeoutAsync(350);
        var state = await page.EvaluateAsync<JsonElement>("""
            () => {
                const shell=document.querySelector('.graph-scroll-shell'), expanded=document.querySelector('.graph-canvas-panel-expanded');
                const owner=expanded??document.querySelector('.content'), r=owner.getBoundingClientRect(), s=shell.getBoundingClientRect();
                const clip={left:Math.max(0,r.left+owner.clientLeft,s.left+shell.clientLeft),top:Math.max(0,r.top+owner.clientTop,s.top+shell.clientTop),
                    right:Math.min(innerWidth,r.left+owner.clientLeft+owner.clientWidth,s.left+shell.clientLeft+shell.clientWidth),
                    bottom:Math.min(innerHeight,r.top+owner.clientTop+owner.clientHeight,s.top+shell.clientTop+shell.clientHeight)};
                const inside=(r,c)=>r.left>=c.left-1&&r.right<=c.right+1&&r.top>=c.top-1&&r.bottom<=c.bottom+1;
                const shapes=[...shell.querySelectorAll('.graph-edge,.graph-label-leader,.graph-view-node circle,.graph-view-node text')]
                    .filter(e=>getComputedStyle(e).display!=='none'&&getComputedStyle(e).visibility!=='hidden');
                const outside=shapes.filter(e=>{const b=e.getBoundingClientRect(),m=e.getScreenCTM(),st=getComputedStyle(e),stroke=st.stroke==='none'?0:(parseFloat(st.strokeWidth)||0)*Math.hypot(m.a,m.b)/2;
                    return !inside({left:b.left-stroke,right:b.right+stroke,top:b.top-stroke,bottom:b.bottom+stroke},clip);});
                const hit=e=>{const b=e.getBoundingClientRect(),h=document.elementFromPoint(b.left+b.width/2,b.top+b.height/2);return !!h&&(h===e||e.contains(h));};
                const titles=[...shell.querySelectorAll('.graph-node-title')];
                const nodes=[...shell.querySelectorAll('[data-graph-node-id]')];
                const nodeCentersHit=nodes.every(e=>{const b=e.querySelector('circle').getBoundingClientRect(),h=document.elementFromPoint(b.left+b.width/2,b.top+b.height/2);
                    return h?.closest('[data-graph-node-id]')?.dataset.graphNodeId===e.dataset.graphNodeId;});
                const ownerClip={left:Math.max(0,r.left+owner.clientLeft),top:Math.max(0,r.top+owner.clientTop),right:Math.min(innerWidth,r.left+owner.clientLeft+owner.clientWidth),bottom:Math.min(innerHeight,r.top+owner.clientTop+owner.clientHeight)};
                const controls=[...document.querySelectorAll('.graph-viewport-controls button')].map(e=>{const b=e.getBoundingClientRect();
                    const points=[[b.left+b.width/2,b.top+b.height/2],[b.left+4,b.top+4],[b.right-4,b.top+4],[b.left+4,b.bottom-4],[b.right-4,b.bottom-4]];
                    return {label:e.getAttribute('aria-label'),visible:inside(b,ownerClip),width:b.width,height:b.height,hit:points.every(([x,y])=>{const h=document.elementFromPoint(x,y);return !!h&&(h===e||e.contains(h));})};});
                return {clip,ownerClip,shell:s.toJSON(),scrollTop:owner.scrollTop,shapeCount:shapes.length,outside:outside.length,
                    titleCount:titles.length,titlesHit:titles.every(hit),nodeCentersHit,controls};
            }
            """);
        output.WriteLine($"{phase}: {state}");
        Assert.True(state.GetProperty("shapeCount").GetInt32() > 0);
        Assert.Equal(0, state.GetProperty("outside").GetInt32());
        Assert.True(state.GetProperty("titleCount").GetInt32() > 0);
        Assert.True(state.GetProperty("titlesHit").GetBoolean(), state.ToString());
        Assert.True(state.GetProperty("nodeCentersHit").GetBoolean(), state.ToString());
        var controls = state.GetProperty("controls").EnumerateArray().ToArray();
        Assert.Equal(8, controls.Length);
        foreach (var control in controls)
        {
            Assert.True(control.GetProperty("visible").GetBoolean() && control.GetProperty("hit").GetBoolean(), control.ToString());
            Assert.True(control.GetProperty("width").GetDouble() >= 44 && control.GetProperty("height").GetDouble() >= 44, control.ToString());
        }
    }
}
