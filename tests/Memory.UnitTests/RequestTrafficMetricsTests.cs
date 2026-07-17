using System.Net;
using System.Net.Http;
using Memory.Infrastructure;

namespace Memory.UnitTests;

public sealed class RequestTrafficMetricsTests
{
    [Fact]
    public async Task DelegatingHandler_Should_Record_Outbound_When_Not_Suppressed()
    {
        var collector = new RequestTrafficMetricsCollector();
        using var handler = new RequestTrafficDelegatingHandler(collector)
        {
            InnerHandler = new StubHandler()
        };
        using var client = new HttpClient(handler);

        using var response = await client.GetAsync("http://localhost/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, collector.GetRecentSamples(1).Single().OutboundRequests);
    }

    [Fact]
    public async Task DelegatingHandler_Should_Skip_Outbound_When_Suppressed()
    {
        var collector = new RequestTrafficMetricsCollector();
        using var handler = new RequestTrafficDelegatingHandler(collector)
        {
            InnerHandler = new StubHandler()
        };
        using var client = new HttpClient(handler);

        using (RequestTrafficSuppressionScope.Suppress())
        {
            using var response = await client.GetAsync("http://localhost/health/ready");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(0, collector.GetRecentSamples(1).Single().OutboundRequests);
    }

    [Fact]
    public void GetRecentSampleTotal_Should_Sum_The_Requested_Window()
    {
        var collector = new RequestTrafficMetricsCollector();
        collector.RecordInbound();
        collector.RecordInbound();
        collector.RecordOutbound();

        var total = collector.GetRecentSampleTotal(5);

        Assert.Equal(2, total.InboundRequests);
        Assert.Equal(1, total.OutboundRequests);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
