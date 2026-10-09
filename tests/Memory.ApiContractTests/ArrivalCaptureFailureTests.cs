using Memory.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Memory.McpServer;

namespace Memory.ApiContractTests;

public sealed class ArrivalCaptureFailureTests
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
        builder.UseRequestArrivalObservations();
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
        builder.UseRequestArrivalObservations();
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
