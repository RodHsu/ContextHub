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
        var completedFits = await CompletedFitsAsync(page);
        await fit.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await WaitForFitAndStableGeometryAsync(page, completedFits, expanded: false);
        await AssertPhysicalAsync(page, "normal");
        var expand = page.GetByRole(AriaRole.Button, new() { Name = "全螢幕顯示", Exact = true });
        completedFits = await CompletedFitsAsync(page);
        await expand.ClickAsync();
        await page.Locator(".graph-canvas-panel-expanded").WaitForAsync();
        await WaitForFitAndStableGeometryAsync(page, completedFits, expanded: true);
        await AssertPhysicalAsync(page, "expanded");
        completedFits = await CompletedFitsAsync(page);
        await expand.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await page.Locator(".graph-canvas-panel-expanded").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await WaitForFitAndStableGeometryAsync(page, completedFits, expanded: false);
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
        await ObserveFitCompletionAsync(page);
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

    private static Task ObserveFitCompletionAsync(IPage page) => page.AddInitScriptAsync("""
        (() => {
            let graphApi;
            window.__graphFitCompletion={completed:0,settling:null};
            // Observe the initial export, before Blazor can resolve and cache its fit function.
            Object.defineProperty(window,'contextHubGraph',{configurable:true,
                get:()=>graphApi,
                set:api=>{
                    const original=api.fit;
                    api.fit=function(...args) {
                        const result=original.apply(this,args);
                        // The shipped fit is synchronous; count after size/transform/reveal returns.
                        window.__graphFitCompletion.completed++;
                        return result;
                    };
                    graphApi=api;
                }});
        })();
        """);

    private static Task<int> CompletedFitsAsync(IPage page)
        => page.EvaluateAsync<int>("window.__graphFitCompletion.completed");

    private async Task WaitForFitAndStableGeometryAsync(IPage page, int previousFits, bool expanded)
    {
        try
        {
            await page.WaitForFunctionAsync("""
                expected => {
                    const observation=window.__graphFitCompletion;
                    const panel=document.querySelector('.graph-canvas-panel-expanded');
                    const shell=document.querySelector('.graph-scroll-shell');
                    const frame=shell?.closest('.graph-viewport-frame');
                    const content=shell?.querySelector('.graph-pan-content');
                    const owner=panel??document.querySelector('.content');
                    if (!observation || observation.completed<=expected.previousFits || !!panel!==expected.expanded ||
                        !shell || !frame || !content || !owner || document.fonts.status!=='loaded' ||
                        frame.dataset.physicalHeight!=='true') return false;
                    // The shipped pan-content has a CSS transform transition; a quiet easing tail is not completion.
                    if (content.getAnimations({subtree:true}).some(animation=>animation.pending || animation.playState==='running')) {
                        observation.settling=null;
                        return false;
                    }
                    const controls=[...document.querySelectorAll('.graph-viewport-controls button')];
                    if (controls.length!==8) return false;
                    const elements=[owner,frame,shell,...controls,...shell.querySelectorAll(
                        '.graph-edge,.graph-label-leader,.graph-view-node circle,.graph-view-node text')];
                    const values=[innerWidth,innerHeight,owner.scrollTop,owner.scrollLeft,
                        window.visualViewport?.offsetTop??0,window.visualViewport?.offsetLeft??0,
                        window.visualViewport?.height??innerHeight,window.visualViewport?.width??innerWidth,
                        ...elements.flatMap(e=>{const r=e.getBoundingClientRect();return [r.x,r.y,r.width,r.height];})];
                    if (!values.every(Number.isFinite) || shell.clientHeight<=0 || shell.clientWidth<=0) return false;
                    const transform=[content.style.transform,getComputedStyle(content).transform,
                        shell.dataset.scale,shell.dataset.panX,shell.dataset.panY,
                        frame.style.getPropertyValue('--graph-physical-height')].join('|');
                    const key=expected.previousFits+':'+expected.expanded;
                    const prior=observation.settling;
                    const stable=prior?.key===key && prior.completed===observation.completed &&
                        prior.transform===transform && prior.values.length===values.length &&
                        values.every((value,index)=>Math.abs(value-prior.values[index])<=0.25);
                    observation.settling={key,completed:observation.completed,transform,values,
                        frames:stable?prior.frames+1:1};
                    // RAF polling observes three consecutive stable frames; it never waits for the oracle to pass.
                    return observation.settling.frames>=3;
                }
                """, new { previousFits, expanded }, new() { Timeout = 10_000 });
        }
        catch (Exception exception) when (exception is PlaywrightException or System.TimeoutException)
        {
            var state = await page.EvaluateAsync<JsonElement>("""
                () => ({completedFits:window.__graphFitCompletion?.completed??0,
                    stableFrames:window.__graphFitCompletion?.settling?.frames??0,
                    expanded:!!document.querySelector('.graph-canvas-panel-expanded'),
                    fontLoaded:document.fonts.status==='loaded',
                    activeAnimations:document.querySelector('.graph-pan-content')?.getAnimations({subtree:true})
                        .filter(animation=>animation.pending || animation.playState==='running').length??0,
                    physicalHeightReady:document.querySelector('.graph-viewport-frame')?.dataset.physicalHeight==='true'})
                """);
            output.WriteLine($"fit/geometry completion timeout: {state}");
            throw;
        }
    }

    private async Task AssertPhysicalAsync(IPage page, string phase)
    {
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
