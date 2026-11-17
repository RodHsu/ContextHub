using System.Text.Json;
using FluentAssertions;
using Microsoft.Playwright;

namespace Memory.DashboardTests;

public sealed partial class DashboardBrowserUiTests
{
    [Theory]
    [InlineData(1366, 768)]
    [InlineData(390, 844)]
    [InlineData(999, 700)]
    public async Task Operations_Desk_Should_Keep_Full_Content_In_Scroll_Flow_And_Reach_Footer_With_Wheel(int width, int height)
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(new("operations-desk-scroll", width, height));
        var page = await context.NewPageAsync();
        await LoginAndOpenAsync(page, "/?uiProfile=dense");
        var containment = await page.EvaluateAsync<double[]>("""
            () => [document.querySelector('.page-viewport').getBoundingClientRect().bottom,
                   document.querySelector('.overview-page-body').getBoundingClientRect().bottom]
            """);
        containment[0].Should().BeGreaterThanOrEqualTo(containment[1] - 1, "the page must grow with all overview content instead of leaving it outside a shrunken flex item");

        var content = page.Locator("main.content");
        await page.GetByRole(AriaRole.Link, new() { Name = "跳到主要內容", Exact = true }).FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(content).ToBeFocusedAsync();
        await content.EvaluateAsync("element => { element.dataset.qaScrollFinished = 'false'; element.addEventListener('scrollend', () => element.dataset.qaScrollFinished = 'true', { once: true }); }");
        await page.Keyboard.PressAsync("PageDown");
        await page.WaitForFunctionAsync("() => document.querySelector('main.content').dataset.qaScrollFinished === 'true' && document.querySelector('main.content').scrollTop > 0");
        await content.EvaluateAsync("element => { element.dataset.qaScrollFinished = 'false'; element.addEventListener('scrollend', () => element.dataset.qaScrollFinished = 'true', { once: true }); element.scrollTop = 0; }");
        await page.WaitForFunctionAsync("() => document.querySelector('main.content').dataset.qaScrollFinished === 'true' && document.querySelector('main.content').scrollTop === 0");
        var bounds = (await content.BoundingBoxAsync())!;
        await page.Mouse.MoveAsync(bounds.X + bounds.Width - 12, bounds.Y + bounds.Height / 2);
        await page.Mouse.WheelAsync(0, 4000);
        await page.WaitForFunctionAsync("""
            () => {
                const content=document.querySelector('main.content'), footer=document.querySelector('.operations-desk-release');
                return content.scrollTop > 0 && footer.getBoundingClientRect().bottom <= content.getBoundingClientRect().bottom + 1;
            }
            """);
        await Assertions.Expect(page.Locator(".operations-desk-release")).ToBeInViewportAsync();
        await CaptureScreenshotAsync(page, "operations-desk-scroll-footer", DashboardUiProfile.Dense, new("operations-desk-scroll", width, height), DashboardTheme.Dark);
    }

    [Theory]
    [InlineData(1366, 768)]
    [InlineData(390, 844)]
    public async Task Operations_Desk_Should_Keep_Original_Entrances_And_Real_Risk_Messages_Keyboard_Reachable(int width, int height)
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(new("operations-desk", width, height));
        var page = await context.NewPageAsync();
        await LoginAndOpenAsync(page, "/?uiProfile=dense");
        await AssertOperationsDeskLayoutAsync(page, width);
        (await page.Locator("main h1").AllTextContentsAsync()).Should().Equal("工作總覽");
        var primary = page.GetByRole(AriaRole.Link, new() { Name = "檢視待辦", Exact = true });
        (await primary.GetAttributeAsync("href")).Should().Be("/project-work-items");

        var risks = page.Locator(".operations-desk-risk-item");
        (await page.Locator(".overview-risk-panel").GetByRole(AriaRole.Columnheader).AllTextContentsAsync())
            .Select(label => label.Trim()).Should().Equal("項目", "等級", "發生時間");
        (await risks.CountAsync()).Should().Be(3, "the overview previews three real log records");
        (await risks.Locator(".status-badge").AllTextContentsAsync()).Select(level => level.Trim())
            .Should().Equal("Error", "Warning", "Error");
        var message = risks.First.Locator("details");
        var disclosure = message.Locator("summary");
        await disclosure.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForFunctionAsync("() => document.querySelector('.operations-desk-risk-item details').open");
        var fullMessage = message.Locator("p");
        await Assertions.Expect(fullMessage).ToBeVisibleAsync();
        (await fullMessage.InnerTextAsync()).Should().Be("示範 log 1: shared summary layer refresh, project isolation, and RWD validation payload with long trace labels.");
        (await fullMessage.EvaluateAsync<string>("element => getComputedStyle(element).userSelect")).Should().NotBe("none");
        (await risks.First.InnerTextAsync()).Should().Contain("mcp-server");
        await Assertions.Expect(risks.First.Locator("time")).ToBeVisibleAsync();
        await AssertOperationsDeskLayoutAsync(page, width);
        await disclosure.FocusAsync();
        await page.Keyboard.PressAsync("Space");
        await page.WaitForFunctionAsync("() => !document.querySelector('.operations-desk-risk-item details').open");

        string[] destinations = ["/project-information", "/project-work-items", "/memories", "/monitoring", "/operations", "/logs", "/inbox", "/chatgpt-proposals", "/governance", "/jobs"];
        foreach (var destination in destinations)
        {
            if (destination is "/inbox" or "/chatgpt-proposals" or "/governance")
            {
                var reviewSummary = page.Locator(".operations-desk-review-section > summary");
                await reviewSummary.FocusAsync();
                await page.Keyboard.PressAsync("Enter");
                await Assertions.Expect(page.Locator(".operations-desk-review-section")).ToHaveAttributeAsync("open", "");
            }
            var link = page.Locator($"main a[href='{destination}']").First;
            await link.ScrollIntoViewIfNeededAsync();
            await link.FocusAsync();
            (await link.EvaluateAsync<bool>("element => element === document.activeElement")).Should().BeTrue(destination);
            await page.Keyboard.PressAsync("Enter");
            await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == destination);
            await LoginAndOpenAsync(page, "/?uiProfile=dense");
        }
    }

    [Theory]
    [InlineData(1366, 768)]
    [InlineData(390, 844)]
    public async Task Operations_Desk_Should_Keep_Healthy_And_Empty_States_Compact_Without_Losing_Freshness(int width, int height)
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(new("operations-desk-empty", width, height));
        var page = await context.NewPageAsync();
        await LoginAndOpenAsync(page, "/?uiProfile=empty");
        await AssertOperationsDeskLayoutAsync(page, width);
        var health = page.Locator(".overview-health-panel");
        (await health.InnerTextAsync()).Should().Contain("正常");
        (await page.Locator(".overview-risk-panel").InnerTextAsync()).Should().Contain("目前沒有待檢視異常");
        (await page.Locator(".overview-work-panel").InnerTextAsync()).Should().Contain("目前沒有等待中或執行中的背景工作");
        var dimensions = await page.EvaluateAsync<double[]>("""
            () => [document.querySelector('.overview-health-panel').getBoundingClientRect().height,
                   document.querySelector('.operations-desk-workspace').getBoundingClientRect().height]
            """);
        dimensions[0].Should().BeLessThan(dimensions[1] / 2, "healthy status should be a compact summary rather than a large empty workspace");
        var warning = page.GetByLabel("顯示資料延遲說明");
        await Assertions.Expect(warning).ToBeVisibleAsync();
        var updated = page.Locator(".operations-desk-summary .refresh-status-time time[data-local-iso]");
        await updated.WaitForAsync();
        var before = await updated.GetAttributeAsync("data-local-iso");
        var refresh = page.GetByRole(AriaRole.Button, new() { Name = "刷新", Exact = true });
        await refresh.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForFunctionAsync("before => document.querySelector('.operations-desk-summary .refresh-status-time time[data-local-iso]')?.dataset.localIso !== before", before);
        await Assertions.Expect(refresh).ToBeEnabledAsync();
        await Assertions.Expect(page.Locator(".operations-desk-summary .refresh-status-group")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".overview-work-panel a[href='/jobs']")).ToBeVisibleAsync();
    }

    [Theory]
    [InlineData(1366, 768, "dark")]
    [InlineData(390, 844, "dark")]
    [InlineData(1366, 768, "light")]
    [InlineData(390, 844, "black")]
    public async Task Operations_Desk_Should_Preserve_Readable_Text_And_Reflow_At_Double_Font_Size(int width, int height, string theme)
    {
        await _fixture.EnsureDashboardRunningAsync();
        await using var context = await _fixture.CreateContextAsync(new("operations-desk-text", width, height));
        var page = await context.NewPageAsync();
        await LoginAndOpenAsync(page, "/?uiProfile=dense");
        await page.Locator(".theme-switcher-toggle").SelectOptionAsync(theme);
        await page.WaitForFunctionAsync("theme => document.documentElement.dataset.theme === theme", theme);
        await AssertOperationsDeskTypographyAndContrastAsync(page);
        var original = await page.Locator("main h1").EvaluateAsync<double>("element => parseFloat(getComputedStyle(element).fontSize)");
        await page.EvaluateAsync("document.documentElement.style.fontSize = '200%'");
        var enlarged = await page.Locator("main h1").EvaluateAsync<double>("element => parseFloat(getComputedStyle(element).fontSize)");
        enlarged.Should().BeGreaterThanOrEqualTo(original * 1.9, "text must honor the user's font-size preference");
        await page.AddStyleTagAsync(new() { Content = "main, main * { line-height: 1.5 !important; letter-spacing: .12em !important; word-spacing: .16em !important; } main p { margin-block-end: 2em !important; }" });
        await AssertOperationsDeskLayoutAsync(page, width, allowReflow: true);
        var primary = page.GetByRole(AriaRole.Link, new() { Name = "檢視待辦", Exact = true });
        await primary.ScrollIntoViewIfNeededAsync();
        var textContained = await primary.EvaluateAsync<bool>("element => element.scrollWidth <= element.clientWidth + 1 && element.scrollHeight <= element.clientHeight + 1");
        textContained.Should().BeTrue("enlarged primary action text must remain inside its hit target");
    }

    private static async Task AssertOperationsDeskLayoutAsync(IPage page, int width, bool allowReflow = false)
    {
        await page.Locator(".operations-desk-workspace").WaitForAsync();
        var layout = await page.EvaluateAsync<JsonElement>("""
            () => {
                const box = selector => { const r=document.querySelector(selector).getBoundingClientRect(); return {left:r.left,top:r.top,right:r.right,bottom:r.bottom}; };
                return {summary:box('.operations-desk-summary'),workspace:box('.operations-desk-workspace'),risk:box('.overview-risk-panel'),side:box('.operations-desk-secondary'),
                    documentWidth:document.documentElement.scrollWidth,bodyWidth:document.body.scrollWidth,
                    contentWidth:document.querySelector('.content').clientWidth,contentScrollWidth:document.querySelector('.content').scrollWidth};
            }
            """);
        double Edge(string panel, string edge) => layout.GetProperty(panel).GetProperty(edge).GetDouble();
        Edge("summary", "bottom").Should().BeLessThanOrEqualTo(Edge("workspace", "top") + 1, layout.ToString());
        if (width >= 1366 && !allowReflow)
            Edge("risk", "right").Should().BeLessThanOrEqualTo(Edge("side", "left") + 1, layout.ToString());
        else if (width <= 999)
            Edge("risk", "bottom").Should().BeLessThanOrEqualTo(Edge("side", "top") + 1, layout.ToString());
        else
            (Edge("risk", "right") <= Edge("side", "left") + 1 || Edge("risk", "bottom") <= Edge("side", "top") + 1)
                .Should().BeTrue("enlarged text may reflow columns but panels must not overlap: {0}", layout);
        layout.GetProperty("documentWidth").GetDouble().Should().BeLessThanOrEqualTo(width + 1, layout.ToString());
        layout.GetProperty("bodyWidth").GetDouble().Should().BeLessThanOrEqualTo(width + 1, layout.ToString());
        layout.GetProperty("contentScrollWidth").GetDouble().Should().BeLessThanOrEqualTo(layout.GetProperty("contentWidth").GetDouble() + 1, layout.ToString());
        var summaryBounds = await page.EvaluateAsync<JsonElement>("""
            () => {
                const summary=document.querySelector('.operations-desk-summary'), outer=summary.getBoundingClientRect();
                const issues=[];
                for(const element of summary.querySelectorAll('.operations-desk-health, .operations-desk-metrics, .operations-desk-freshness, .operations-desk-metric > span, .operations-desk-metric > strong, .operations-desk-refresh, .refresh-status-primary-copy')) {
                    const rect=element.getBoundingClientRect();
                    if(rect.left < outer.left-1 || rect.right > outer.right+1 || rect.top < outer.top-1 || rect.bottom > outer.bottom+1 || element.scrollWidth > element.clientWidth+1)
                        issues.push({element:element.className,text:element.textContent.trim(),left:rect.left,right:rect.right,scroll:element.scrollWidth,client:element.clientWidth});
                }
                return {height:outer.height,issues};
            }
            """);
        summaryBounds.GetProperty("issues").EnumerateArray().Should().BeEmpty("summary contents must remain readable inside their container: {0}", summaryBounds);
        if (width >= 1487 && !allowReflow)
            summaryBounds.GetProperty("height").GetDouble().Should().BeLessThan(100, "desktop summary should fit one compact band");
    }

    private static async Task AssertOperationsDeskTypographyAndContrastAsync(IPage page)
    {
        await page.EvaluateAsync("""
            async () => {
                getComputedStyle(document.body).color;
                await Promise.all(document.getAnimations().filter(animation =>
                    animation.effect?.getComputedTiming().iterations !== Infinity)
                    .map(animation => animation.finished.catch(() => {})));
            }
            """);
        var typography = await page.EvaluateAsync<JsonElement>("""
            () => {
                const canvas=document.createElement('canvas'); canvas.width=canvas.height=1;
                const context=canvas.getContext('2d',{willReadFrequently:true});
                const rgba = value => { context.clearRect(0,0,1,1); context.fillStyle=value; context.fillRect(0,0,1,1); return [...context.getImageData(0,0,1,1).data].map(channel=>channel/255); };
                const blend = (front,back) => front.slice(0,3).map((c,i)=>c*front[3]+back[i]*(1-front[3]));
                const luminance = color => color.map(c=>c<=.04045?c/12.92:((c+.055)/1.055)**2.4).reduce((sum,c,i)=>sum+c*[.2126,.7152,.0722][i],0);
                const issues=[];
                const roots=document.querySelectorAll('main h1, .operations-desk-summary, .operations-desk-workspace');
                for(const root of roots) for(const element of [root,...root.querySelectorAll('*')]) {
                    if(element.children.length || !element.textContent.trim() || !element.getClientRects().length || getComputedStyle(element).visibility!=='visible') continue;
                    const chain=[]; for(let ancestor=element;ancestor;ancestor=ancestor.parentElement) chain.push(ancestor);
                    let background=[1,1,1]; for(const ancestor of chain.reverse()) background=blend(rgba(getComputedStyle(ancestor).backgroundColor),background);
                    const style=getComputedStyle(element),foreground=blend(rgba(style.color),background);
                    const values=[luminance(foreground),luminance(background)].sort((a,b)=>b-a),ratio=(values[0]+.05)/(values[1]+.05);
                    const large=parseFloat(style.fontSize)>=24 || (parseFloat(style.fontSize)>=18.66 && Number(style.fontWeight)>=700);
                    if(ratio+.01<(large?3:4.5)) issues.push({text:element.textContent.trim().slice(0,60),ratio,foreground:style.color});
                }
                return {bodyFont:parseFloat(getComputedStyle(document.body).fontSize),headingFont:parseFloat(getComputedStyle(document.querySelector('main h1')).fontSize),issues};
            }
            """);
        typography.GetProperty("bodyFont").GetDouble().Should().BeGreaterThanOrEqualTo(16, typography.ToString());
        typography.GetProperty("headingFont").GetDouble().Should().BeGreaterThan(typography.GetProperty("bodyFont").GetDouble(), typography.ToString());
        typography.GetProperty("issues").EnumerateArray().Should().BeEmpty("visible operational text needs readable foreground/background contrast: {0}", typography);
    }
}
