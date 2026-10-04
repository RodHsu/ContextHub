using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Dashboard.Services;

namespace Memory.DashboardTests;

public sealed class CacheTelemetryPresentationTests
{
    [Fact]
    public void Origin_Attempts_Should_Have_Independent_Labels_And_Not_Change_Interactive_Hit_Rate()
    {
        CacheMetricsSeriesResult[] series =
        [
            new("final-result", "interactive", 4, 1, 0, 0, 0, 0, 0),
            new(CacheMetricKinds.OriginSearch, "interactive", 0, 0, 0, 0, 0, 0, 0, 100)
        ];
        CacheTelemetryPresentation.InteractiveResults(series).HitRate.Should().Be(80);
        CacheTelemetryPresentation.Kind(CacheMetricKinds.OriginSearch).Should().Be("搜尋回源");
        CacheTelemetryPresentation.Kind(CacheMetricKinds.OriginContext).Should().Be("工作脈絡重建");
        CacheTelemetryPresentation.Kind(CacheMetricKinds.OriginSemantic).Should().Be("向量資料庫回源");
        CacheTelemetryPresentation.Kind(CacheMetricKinds.OriginEmbedding).Should().Be("向量服務回源");
    }

    [Fact]
    public void Interactive_Result_Should_Exclude_Background_And_Nested_Layers_And_Weight_Denominator()
    {
        CacheMetricsSeriesResult[] series =
        [
            new("final-result", "interactive", 1, 1, 0, 0, 0, 0, 5),
            new("final-result", "interactive", 0, 98, 0, 0, 0, 0, 20),
            new("final-result", "graph-background", 10000, 0, 0, 0, 0, 0, 5),
            new("search-final", "application", 10000, 0, 0, 0, 0, 0, 5)
        ];

        var result = CacheTelemetryPresentation.InteractiveResults(series);
        result.Samples.Should().Be(100);
        result.Hits.Should().Be(1);
        result.HitRate.Should().Be(1);
        CacheTelemetryPresentation.Rate(result).Should().Be("1%");
    }

    [Fact]
    public void Empty_Or_Unknown_Data_Should_Never_Appear_Healthy_Or_As_Zero_Percent()
    {
        CacheTelemetryPresentation.Rate(CacheTelemetryPresentation.InteractiveResults([])).Should().Be("無樣本");
        CacheTelemetryPresentation.Coverage(null).Should().Contain("未知");
        CacheTelemetryPresentation.Coverage("future-value").Should().Contain("未知");
        CacheTelemetryPresentation.Coverage("Observed").Should().Contain("未保證所有副本");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.NotImplemented)]
    public async Task Older_Api_Should_Return_Unsupported_Without_Fabricated_Metrics(HttpStatusCode status)
    {
        using var http = new HttpClient(new ResponseHandler(status)) { BaseAddress = new Uri("http://context-hub.example.com") };
        var client = new ContextHubApiClient(http);
        (await client.GetCacheMetricsAsync("24H", CancellationToken.None)).Should().BeNull();
        (await client.GetGraphRefreshStatusAsync(CancellationToken.None)).Should().BeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Authorization_And_Service_Errors_Should_Not_Be_Masked_As_Unsupported(HttpStatusCode status)
    {
        using var http = new HttpClient(new ResponseHandler(status)) { BaseAddress = new Uri("http://context-hub.example.com") };
        var client = new ContextHubApiClient(http);
        var action = () => client.GetCacheMetricsAsync("24H", CancellationToken.None);
        await action.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Client_Should_Preserve_Window_And_Boot_Metadata()
    {
        var now = DateTimeOffset.UtcNow;
        var boot = Guid.NewGuid();
        var data = new CacheMetricsWindowResult(true, "30D", now.AddDays(-30), now, now, "Partial", 17,
            [new("final-result", "interactive", 0, 0, 0, 0, 3, 1, 0)],
            [new("mcp-test", boot, now.AddDays(-1), now.AddMinutes(-2), 3, 17, true)]);
        using var http = new HttpClient(new ResponseHandler(HttpStatusCode.OK, data)) { BaseAddress = new Uri("http://context-hub.example.com") };
        var result = await new ContextHubApiClient(http).GetCacheMetricsAsync("30D", CancellationToken.None);
        result!.Period.Should().Be("30D");
        result.Instances.Single().BootId.Should().Be(boot);
        result.Instances.Single().UncleanShutdown.Should().BeTrue();
        result.Series.Single().HitRate.Should().BeNull();
    }

    private sealed class ResponseHandler(HttpStatusCode status, CacheMetricsWindowResult? payload = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status) { Content = payload is null ? new StringContent(string.Empty) : JsonContent.Create(payload) });
    }
}
