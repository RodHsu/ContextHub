using System.Text.Json;
using Microsoft.Playwright;

namespace Memory.DashboardTests;

// Real Chromium geometry and shipped JavaScript; data comes from local browser fixtures only.
public sealed class GraphViewportTests(DashboardBrowserFixture fixture) : IClassFixture<DashboardBrowserFixture>
{
    [Fact]
    public async Task Fit_should_contain_offset_node_label_and_stroke_without_fitting_blank_canvas()
    {
        await using var context = await fixture.CreateContextAsync(new("graph-offset", 800, 600));
        var page = await GeometryPageAsync(context,
            "<g class='graph-view-node'><circle cx='4000' cy='-700' r='24' stroke='black' stroke-width='18'/><text x='4040' y='-694' font-size='30'>Offset label outside the nominal canvas</text></g>");
        await AssertContainedAsync(page);
        var width = await page.Locator("circle").EvaluateAsync<double>("node => node.getBoundingClientRect().width");
        Assert.True(width >= 20, $"A single node should remain readable; actual circle width {width}.");
    }

    [Fact]
    public async Task Dense_fit_below_old_minimum_should_remain_contained_and_zoom_out_should_not_zoom_in()
    {
        await using var context = await fixture.CreateContextAsync(new("graph-dense-fit", 800, 600));
        var page = await GeometryPageAsync(context,
            "<line class='graph-edge' x1='-5000' y1='-1800' x2='5000' y2='1800' stroke='black' stroke-width='20'/><g class='graph-view-node'><circle cx='-5000' cy='-1800' r='30'/><text x='5000' y='1800' font-size='24'>Far end label</text></g>");
        await AssertContainedAsync(page);
        var before = await ScaleAsync(page);
        Assert.InRange(before, 0.00001, 0.2499);
        await page.EvaluateAsync("() => contextHubGraph.zoomOut(document.querySelector('.graph-scroll-shell'))");
        var after = await ScaleAsync(page);
        Assert.True(after > 0 && after < before, $"Zoom out did not decrease the scale: {before} -> {after}.");
        await AssertContainedAsync(page);
    }

    [Fact]
    public async Task Fit_should_follow_data_font_and_viewport_updates_but_preserve_user_pan()
    {
        await using var context = await fixture.CreateContextAsync(new("graph-refresh", 800, 600));
        var page = await GeometryPageAsync(context,
            "<g class='graph-view-node'><circle cx='50' cy='50' r='20'/><text x='80' y='56' font-size='12'>Short label</text></g>");
        await AssertContainedAsync(page);
        await page.EvaluateAsync("() => { const t=document.querySelector('text'); t.textContent='A much longer replacement label after graph data update'; t.setAttribute('font-size','48'); }");
        await AssertContainedAsync(page);
        await page.EvaluateAsync("() => { document.querySelector('.graph-scroll-shell').style.width='220px'; }");
        await AssertContainedAsync(page);
        await page.EvaluateAsync("() => contextHubGraph.panLeft(document.querySelector('.graph-scroll-shell'))");
        var pan = await page.Locator(".graph-scroll-shell").GetAttributeAsync("data-pan-x");
        await page.EvaluateAsync("() => { const v=document.querySelector('.graph-scroll-shell'); v.style.width='250px'; contextHubGraph.refresh(v); }");
        await page.WaitForTimeoutAsync(150);
        Assert.Equal(pan, await page.Locator(".graph-scroll-shell").GetAttributeAsync("data-pan-x"));
        await page.EvaluateAsync("() => contextHubGraph.fit(document.querySelector('.graph-scroll-shell'))");
        await AssertContainedAsync(page);
    }

    [Fact]
    public async Task Narrow_titles_should_not_overlap_and_should_restore_when_widened_or_unregistered()
    {
        await using var context = await fixture.CreateContextAsync(new("graph-label-column", 800, 600));
        var page = await GeometryPageAsync(context,
            "<g class='graph-view-node' transform='translate(70 50)'><circle r='12'/><text class='graph-node-title' x='17' y='4' font-size='24'>First full title</text></g>" +
            "<g class='graph-view-node' transform='translate(90 130)'><circle r='12'/><text class='graph-node-title' x='17' y='4' font-size='24'>Second full title</text></g>" +
            "<g class='graph-view-node' transform='translate(80 210)'><circle r='12'/><text class='graph-node-title' x='17' y='4' font-size='24'>Third full title</text></g>");
        await page.EvaluateAsync("() => { document.querySelector('.graph-scroll-shell').style.width='390px'; }");
        await page.WaitForFunctionAsync("() => document.querySelectorAll('.graph-label-leader').length===3");
        await AssertContainedAsync(page);
        Assert.True(await page.EvaluateAsync<bool>("""
            () => {
                const boxes=[...document.querySelectorAll('.graph-node-title')].map(t=>t.getBoundingClientRect());
                return boxes.every((a,i)=>boxes.every((b,j)=>i===j || a.bottom<=b.top || b.bottom<=a.top || a.right<=b.left || b.right<=a.left));
            }
            """));
        Assert.Equal("First full title", await page.Locator(".graph-node-title").First.TextContentAsync());
        Assert.Equal("translate(70 50)", await page.Locator(".graph-view-node").First.GetAttributeAsync("transform"));
        await page.EvaluateAsync("() => contextHubGraph.panLeft(document.querySelector('.graph-scroll-shell'))");
        var pan = await page.Locator(".graph-scroll-shell").GetAttributeAsync("data-pan-x");
        await page.EvaluateAsync("() => { document.querySelector('.graph-node-title').textContent='First complete replacement title'; }");
        await page.WaitForTimeoutAsync(300);
        Assert.Equal(pan, await page.Locator(".graph-scroll-shell").GetAttributeAsync("data-pan-x"));
        await page.EvaluateAsync("() => contextHubGraph.fit(document.querySelector('.graph-scroll-shell'))");
        await AssertContainedAsync(page);
        await page.EvaluateAsync("() => { document.querySelector('.graph-scroll-shell').style.width='800px'; }");
        await page.WaitForFunctionAsync("() => document.querySelectorAll('.graph-label-leader').length===0");
        Assert.Null(await page.Locator(".graph-node-title").First.GetAttributeAsync("transform"));
        await page.EvaluateAsync("() => { document.querySelector('.graph-scroll-shell').style.width='390px'; }");
        await page.WaitForFunctionAsync("() => document.querySelectorAll('.graph-label-leader').length===3");
        await page.EvaluateAsync("() => contextHubGraph.unregister(document.querySelector('.graph-scroll-shell'))");
        Assert.Equal(0, await page.Locator(".graph-label-leader").CountAsync());
        Assert.Null(await page.Locator(".graph-node-title").First.GetAttributeAsync("transform"));
    }

    [Fact]
    public async Task Wide_inline_title_collisions_should_be_separated_and_restore_when_data_no_longer_collides()
    {
        await using var context = await fixture.CreateContextAsync(new("graph-wide-collision", 1000, 600));
        var page = await GeometryPageAsync(context,
            "<g class='graph-view-node' transform='translate(70 50)'><circle r='12'/><text class='graph-node-title' x='17' y='4' font-size='24'>First complete title</text></g>" +
            "<g class='graph-view-node' transform='translate(90 60)'><circle r='12'/><text class='graph-node-title' x='17' y='4' font-size='24'>Second complete title</text></g>");
        await page.EvaluateAsync("() => { document.querySelector('.graph-scroll-shell').style.width='800px'; }");
        await AssertContainedAsync(page);
        Assert.Equal(2, await page.Locator(".graph-label-leader").CountAsync());
        Assert.True(await page.EvaluateAsync<bool>("""
            () => {
                const [a,b]=[...document.querySelectorAll('.graph-node-title')].map(t=>t.getBoundingClientRect());
                return a.bottom<=b.top || b.bottom<=a.top || a.right<=b.left || b.right<=a.left;
            }
            """));
        Assert.Equal("translate(70 50)", await page.Locator(".graph-view-node").First.GetAttributeAsync("transform"));
        await page.EvaluateAsync("() => document.querySelectorAll('.graph-view-node')[1].setAttribute('transform','translate(90 160)')");
        await page.WaitForFunctionAsync("() => document.querySelectorAll('.graph-label-leader').length===0");
        Assert.Null(await page.Locator(".graph-node-title").First.GetAttributeAsync("transform"));
        await AssertContainedAsync(page);
    }

    [Fact]
    public async Task Dense_painted_titles_should_select_exact_nodes_with_full_titles_and_inert_leaders()
    {
        await using var context = await fixture.CreateContextAsync(new("graph-dense-mouse", 390, 844));
        var shapes = string.Concat(Enumerable.Range(1, 36).Select(index =>
            $"<a class='graph-view-node' href='#' data-graph-node-id='{index}' transform='translate({40 + index % 6 * 25} {40 + index / 6 * 25})'><circle r='10'/><text class='graph-node-title' x='15' y='4' font-size='24'>Node {index} 完整標題</text></a>"));
        var page = await GeometryPageAsync(context, shapes);
        await page.EvaluateAsync("""
            () => document.addEventListener('click', event => {
                const node=event.target.closest('.graph-view-node');
                if(node) { event.preventDefault(); window.__selectedGraphNode=node.dataset.graphNodeId; }
            })
            """);
        await AssertContainedAsync(page);
        Assert.Equal(36, await page.Locator(".graph-label-leader").CountAsync());
        Assert.True(await page.Locator(".graph-label-leader").EvaluateAllAsync<bool>(
            "lines => lines.every(line=>getComputedStyle(line).pointerEvents==='none')"));
        foreach (var id in new[] { "1", "10", "36" })
        {
            var title = page.Locator($"[data-graph-node-id='{id}'] .graph-node-title");
            Assert.Equal($"Node {id} 完整標題", await title.TextContentAsync());
            await title.ClickAsync();
            Assert.Equal(id, await page.EvaluateAsync<string>("() => window.__selectedGraphNode"));
        }
    }

    [Fact]
    public async Task Empty_graph_fallback_should_publish_finite_transform_and_allow_later_data()
    {
        await using var context = await fixture.CreateContextAsync(new("graph-empty", 800, 600));
        var page = await GeometryPageAsync(context, "");
        var state = await page.Locator(".graph-scroll-shell").EvaluateAsync<bool>("v => ['scale','panX','panY'].every(k => Number.isFinite(Number(v.dataset[k]))) && Number(v.dataset.scale)>0");
        Assert.True(state);
        await page.EvaluateAsync("() => { document.querySelector('svg').innerHTML='<g class=\"graph-view-node\"><circle cx=\"-200\" cy=\"80\" r=\"25\"/><text x=\"-160\" y=\"86\">First arriving node</text></g>'; contextHubGraph.refresh(document.querySelector('.graph-scroll-shell')); }");
        await AssertContainedAsync(page);
    }

    [Fact]
    public async Task Unregister_should_stop_observation_and_animation_after_later_dom_changes()
    {
        await using var context = await fixture.CreateContextAsync(new("graph-disposal", 800, 600));
        var page = await GeometryPageAsync(context,
            "<g class='graph-view-node'><circle cx='50' cy='50' r='20'/><text x='80' y='56'>Original label</text></g>");
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        await AssertContainedAsync(page);
        var previous = await page.Locator(".graph-pan-content").GetAttributeAsync("style");
        await page.EvaluateAsync("""
            () => {
                const viewport=document.querySelector('.graph-scroll-shell');
                contextHubGraph.unregister(viewport);
                window.__graphFrameRequests=0;
                const request=window.requestAnimationFrame.bind(window);
                window.requestAnimationFrame=callback=>{ window.__graphFrameRequests++; return request(callback); };
                document.querySelector('text').textContent='A longer replacement after disposal';
                viewport.style.width='230px';
                window.dispatchEvent(new Event('resize'));
            }
            """);
        await page.WaitForTimeoutAsync(350);
        Assert.Equal(previous, await page.Locator(".graph-pan-content").GetAttributeAsync("style"));
        Assert.Equal(0, await page.EvaluateAsync<int>("() => window.__graphFrameRequests"));
        Assert.Empty(errors);
    }

    [Fact]
    public async Task Narrow_real_dashboard_toolbar_should_have_44px_targets_and_keyboard_zoom_fit()
    {
        await using var context = await fixture.CreateContextAsync(new("graph-toolbar-mobile", 390, 844));
        var page = await context.NewPageAsync();
        await page.GotoAsync(new Uri(fixture.BaseUri, "/login?returnUrl=%2Fgraph%3FuiProfile%3Dnormal").ToString());
        await page.Locator("input[name='Username']").FillAsync("admin");
        await page.Locator("input[name='Password']").FillAsync("ContextHub!123");
        await page.Locator("form").EvaluateAsync("form => form.requestSubmit()");
        await page.WaitForURLAsync(url => !url.Contains("/login", StringComparison.OrdinalIgnoreCase));
        await page.Locator(".dashboard-shell[data-dashboard-interactive='true']").WaitForAsync();
        await page.Locator(".graph-view-node").First.WaitForAsync();
        await page.WaitForFunctionAsync("() => Number(document.querySelector('.graph-scroll-shell')?.dataset.scale)>0");
        var toolbar = page.Locator(".graph-viewport-controls");
        var buttons = toolbar.Locator("button");
        Assert.Equal(8, await buttons.CountAsync());
        foreach (var fontPercent in new[] { 100, 150 })
        {
            await page.AddStyleTagAsync(new() { Content = $"html {{ font-size:{fontPercent}% !important; }}" });
            await page.WaitForTimeoutAsync(150);
            for (var index = 0; index < await buttons.CountAsync(); index++)
            {
                var bounds = await buttons.Nth(index).BoundingBoxAsync();
                Assert.NotNull(bounds);
                Assert.True(bounds.Width >= 44 && bounds.Height >= 44, $"Target {index} at {fontPercent}% font: {bounds.Width}x{bounds.Height}");
            }
            Assert.False(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > innerWidth+1"));
            var overlapJson = await page.EvaluateAsync<string>("""
                () => {
                    const toolbar=document.querySelector('.graph-viewport-controls').getBoundingClientRect();
                    const canvas=document.querySelector('.graph-scroll-shell').getBoundingClientRect();
                    const overlap=Math.min(toolbar.right,canvas.right)-Math.max(toolbar.left,canvas.left)>1
                        && Math.min(toolbar.bottom,canvas.bottom)-Math.max(toolbar.top,canvas.top)>1;
                    return JSON.stringify({overlap,toolbar:toolbar.toJSON(),canvas:canvas.toJSON()});
                }
                """);
            await page.ScreenshotAsync(new() { Path = Path.Combine(fixture.ArtifactDirectory, $"graph-toolbar-mobile-{fontPercent}-geometry.png"), FullPage = true });
            using var overlapDocument = JsonDocument.Parse(overlapJson);
            Assert.False(overlapDocument.RootElement.GetProperty("overlap").GetBoolean(), $"Toolbar covers drawable viewport at {fontPercent}% font: {overlapJson}");
        }
        var zoom = toolbar.GetByRole(AriaRole.Button, new() { Name = "放大", Exact = true });
        var initialScale = await ScaleAsync(page);
        await toolbar.GetByRole(AriaRole.Button, new() { Name = "縮小", Exact = true }).FocusAsync();
        await page.Keyboard.PressAsync("Space");
        await page.WaitForFunctionAsync("before => Number(document.querySelector('.graph-scroll-shell').dataset.scale)<before", initialScale);
        var before = await ScaleAsync(page);
        await zoom.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForFunctionAsync("before => Number(document.querySelector('.graph-scroll-shell').dataset.scale)>before", before);
        await page.Keyboard.PressAsync("Tab");
        Assert.Equal("縮小", await page.EvaluateAsync<string>("() => document.activeElement.getAttribute('aria-label')"));
        var fit = toolbar.GetByRole(AriaRole.Button, new() { Name = "適應視圖", Exact = true });
        await fit.FocusAsync();
        await page.Keyboard.PressAsync("Space");
        await page.WaitForTimeoutAsync(300);
        Assert.True(await ScaleAsync(page) > 0);
        await page.ScreenshotAsync(new() { Path = Path.Combine(fixture.ArtifactDirectory, "graph-toolbar-mobile-150.png"), FullPage = true });
    }

    private async Task<IPage> GeometryPageAsync(IBrowserContext context, string shapes)
    {
        var page = await context.NewPageAsync();
        await page.SetContentAsync("<style>body{margin:20px}.graph-scroll-shell{position:relative;width:400px;height:300px;overflow:hidden}.graph-pan-content{width:1200px;height:800px;transform-origin:0 0}svg{width:1200px;height:800px;overflow:visible}</style><div class='graph-canvas-panel'><span data-graph-zoom-chip></span><div class='graph-scroll-shell'><div class='graph-pan-content'><svg class='graph-view-svg' viewBox='0 0 1200 800'>" + shapes + "</svg></div></div></div>");
        await page.AddScriptTagAsync(new() { Url = new Uri(fixture.BaseUri, "/dashboard-graph.js").ToString() });
        await page.EvaluateAsync("() => { const v=document.querySelector('.graph-scroll-shell'); contextHubGraph.register(v,document.querySelector('.graph-pan-content')); contextHubGraph.fit(v); }");
        return page;
    }

    private static async Task<double> ScaleAsync(IPage page) => await page.Locator(".graph-scroll-shell").EvaluateAsync<double>("v => Number(v.dataset.scale)");

    private static async Task AssertContainedAsync(IPage page)
    {
        await page.WaitForTimeoutAsync(300);
        await page.WaitForFunctionAsync("""
            () => {
                const v=document.querySelector('.graph-scroll-shell').getBoundingClientRect();
                return [...document.querySelectorAll('svg circle,svg text,svg line')].every(shape=>{
                    const r=shape.getBoundingClientRect(), m=shape.getScreenCTM(), css=getComputedStyle(shape);
                    const stroke=css.stroke==='none'?0:(parseFloat(css.strokeWidth)||0)*Math.hypot(m.a,m.b)/2;
                    return r.left-stroke>=v.left-1 && r.top-stroke>=v.top-1 && r.right+stroke<=v.right+1 && r.bottom+stroke<=v.bottom+1;
                });
            }
            """, options: new() { Timeout = 3000 });
        var json = await page.EvaluateAsync<string>("""
            () => {
                const v=document.querySelector('.graph-scroll-shell').getBoundingClientRect();
                const shapes=[...document.querySelectorAll('svg circle,svg text,svg line')];
                return JSON.stringify(shapes.map(shape=>{
                    const r=shape.getBoundingClientRect(), m=shape.getScreenCTM(), css=getComputedStyle(shape);
                    const stroke=css.stroke==='none'?0:(parseFloat(css.strokeWidth)||0)*Math.hypot(m.a,m.b)/2;
                    return {tag:shape.tagName,left:r.left-stroke-v.left,top:r.top-stroke-v.top,right:v.right-r.right-stroke,bottom:v.bottom-r.bottom-stroke};
                }));
            }
            """);
        using var result = JsonDocument.Parse(json);
        Assert.NotEmpty(result.RootElement.EnumerateArray());
        foreach (var shape in result.RootElement.EnumerateArray())
            foreach (var side in new[] { "left", "top", "right", "bottom" })
                Assert.True(shape.GetProperty(side).GetDouble() >= -1, $"Rendered painted extent clipped: {json}");
    }
}
