using Memory.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Memory.ChatGptGateway;

namespace Memory.ChatGptGatewayTests;

public sealed class GatewayArrivalCaptureFailureTests
{
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public async Task Capture_failure_preserves_application_result_and_original_exception(
        bool failBegin, bool failComplete, bool applicationThrows)
    {
        var clock = new FailingClock();
        var observations = new RequestArrivalObservations(clock, Options.Create(new RequestArrivalObservationOptions
        {
            Enabled = true,
            UntilUtc = clock.GetUtcNow().AddMinutes(5).ToString("O")
        }));
        using var services = new ServiceCollection().AddSingleton(observations).BuildServiceProvider();
        var builder = new ApplicationBuilder(services);
        builder.UseMiddleware(typeof(ChatGptGatewayTools).Assembly.GetType(
            "Memory.ChatGptGateway.GatewayRequestArrivalObservationMiddleware", throwOnError: true)!);
        var original = new InvalidOperationException("application-only sentinel");
        var called = false;
        builder.Run(context =>
        {
            called = true;
            clock.FailReads = failComplete;
            if (applicationThrows) throw original;
            context.Response.StatusCode = 202;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = "/mcp";
        context.Request.Method = "POST";
        context.Request.Body = new UnreadableBody();
        clock.FailReads = failBegin;
        if (applicationThrows)
            Assert.Same(original, await Assert.ThrowsAsync<InvalidOperationException>(() => builder.Build()(context)));
        else
        {
            await builder.Build()(context);
            Assert.Equal(202, context.Response.StatusCode);
        }
        Assert.True(called);
    }

    [Fact]
    public async Task Disabled_capture_preserves_dispatch_when_time_source_fails()
    {
        var clock = new FailingClock();
        var observations = new RequestArrivalObservations(clock, Options.Create(new RequestArrivalObservationOptions()));
        using var services = new ServiceCollection().AddSingleton(observations).BuildServiceProvider();
        var builder = new ApplicationBuilder(services);
        builder.UseMiddleware(typeof(ChatGptGatewayTools).Assembly.GetType(
            "Memory.ChatGptGateway.GatewayRequestArrivalObservationMiddleware", throwOnError: true)!);
        builder.Run(context => { context.Response.StatusCode = 204; return Task.CompletedTask; });
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = "/mcp";
        context.Request.Method = "POST";
        context.Request.Body = new UnreadableBody();
        clock.FailReads = true;
        await builder.Build()(context);
        Assert.Equal(204, context.Response.StatusCode);
        clock.FailReads = false;
        Assert.Empty(observations.Snapshot().Samples);
    }

    [Theory]
    [InlineData("/mcp", true)]
    [InlineData("/mcp/", true)]
    [InlineData("/mcp/private-sentinel", false)]
    [InlineData("/health/live", false)]
    public async Task V2_gateway_classifies_only_exact_envelope_without_reading_body(string path, bool captured)
    {
        var observations = new RequestArrivalObservations(TimeProvider.System, Options.Create(new RequestArrivalObservationOptions
        {
            Enabled = true,
            CaptureSchemaVersion = 2,
            UntilUtc = DateTimeOffset.UtcNow.AddMinutes(5).ToString("O")
        }), new TestClock());
        using var services = new ServiceCollection().AddSingleton(observations).BuildServiceProvider();
        var builder = new ApplicationBuilder(services);
        builder.UseMiddleware(typeof(ChatGptGatewayTools).Assembly.GetType(
            "Memory.ChatGptGateway.GatewayRequestArrivalObservationMiddleware", throwOnError: true)!);
        builder.Run(context => { context.Response.StatusCode = 202; return Task.CompletedTask; });
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = path;
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString("?private=SECRET_QUERY");
        context.Request.Headers["X-Canary"] = "SECRET_HEADER";
        context.Request.ContentLength = 16_385;
        context.Request.Body = new UnreadableBody();
        await builder.Build()(context);
        Assert.Equal(202, context.Response.StatusCode);
        var samples = observations.Snapshot().Samples;
        if (!captured) { Assert.Empty(samples); return; }
        var row = Assert.Single(samples);
        Assert.Equal("http-mcp", row.RequestFamily);
        Assert.Equal("POST", row.RequestMethod);
        Assert.Equal("le256k", row.PayloadSizeBucket);
        Assert.DoesNotContain("SECRET", System.Text.Json.JsonSerializer.Serialize(row));
    }

    private sealed class TestClock : IRequestArrivalClock
    {
        private long _tick = 1_000_000_000;
        public bool TryReadDomain(out RequestArrivalClockDomain? domain)
        {
            domain = new(Guid.Parse("cd05aad2-d32c-4fb8-8f96-b52722987b0b"), "time:[123]", 0, 0, "tsc");
            return true;
        }
        public bool TryRead(out RequestArrivalClockReading? reading)
        {
            var stamp = Interlocked.Add(ref _tick, 100_000);
            reading = new(stamp, stamp + 1_000, stamp, stamp, stamp);
            return true;
        }
    }

    private sealed class FailingClock : TimeProvider
    {
        public bool FailReads { get; set; }
        public override DateTimeOffset GetUtcNow()
            => FailReads ? throw new InvalidOperationException("capture-only sentinel") : new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class UnreadableBody : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("body must not be read");
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
