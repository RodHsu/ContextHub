using System.Collections.Concurrent;
using System.Globalization;
using Memory.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Memory.IntegrationTests;

public sealed class RequestArrivalCaptureStartupTests
{
    [Theory]
    [InlineData(false, 5, false)]
    [InlineData(true, -5, false)]
    [InlineData(true, 1500, false)]
    [InlineData(true, 5, true)]
    public async Task Disabled_or_invalid_capture_does_not_emit_startup_or_access_database(bool enabled, int minutes, bool malformed)
    {
        var buffer = Buffer(enabled, malformed ? "PRIVATE_INVALID_CONFIG_SENTINEL" : DateTimeOffset.UtcNow.AddMinutes(minutes).ToString("O"));
        var logger = new RecordingLogger();
        using var flush = await Service(buffer, logger);
        await flush.StartAsync(default);
        await flush.StopAsync(default);
        Assert.False(buffer.Enabled);
        Assert.Empty(logger.Events);
        Assert.Null(flush.ExecuteTask?.Exception);
    }

    [Fact]
    public async Task Startup_emits_one_fixed_structured_record_with_only_safe_boot_metadata()
    {
        var buffer = Buffer(true, DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"));
        buffer.Begin("http", "PRIVATE_OPERATION_SENTINEL")!.Complete("success");
        var logger = new RecordingLogger();
        using var flush = await Service(buffer, logger);
        await flush.StartAsync(default);
        await logger.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await flush.StopAsync(default);

        var entry = Assert.Single(logger.Events, x => x.Event.Id == 5701);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("RequestArrivalCaptureStarted", entry.Event.Name);
        Assert.Null(entry.Exception);
        Assert.Equal(new[] { "ObservationBootId", "ProcessId", "SchemaVersion", "StartedAtUtc", "TimestampFrequency", "UntilUtc", "{OriginalFormat}" }, entry.State.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(1, entry.State["SchemaVersion"]);
        Assert.Equal(buffer.BootId.ToString("D"), entry.State["ObservationBootId"]);
        Assert.Equal(buffer.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture), entry.State["StartedAtUtc"]);
        Assert.Equal(buffer.UntilUtc.ToString("O", CultureInfo.InvariantCulture), entry.State["UntilUtc"]);
        Assert.Equal(buffer.Snapshot().Coverage.TimestampFrequency, entry.State["TimestampFrequency"]);
        Assert.Equal(Environment.ProcessId, entry.State["ProcessId"]);
        Assert.DoesNotContain("PRIVATE_", string.Join(" ", entry.State.Values));
        Assert.Null(flush.ExecuteTask?.Exception);
    }

    [Fact]
    public async Task Startup_logger_failure_does_not_disable_capture_or_fault_worker()
    {
        var buffer = Buffer(true, DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"));
        var logger = new RecordingLogger { FailStartup = true };
        using var flush = await Service(buffer, logger);
        await flush.StartAsync(default);
        await logger.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await flush.StopAsync(default);
        Assert.True(buffer.Enabled);
        buffer.Begin("http", "http-mcp")!.Complete("success");
        Assert.Single(buffer.Snapshot().Samples);
        Assert.Null(flush.ExecuteTask?.Exception);
        Assert.DoesNotContain(logger.Events, x => x.Exception is not null);
        Assert.DoesNotContain(logger.Events.SelectMany(x => x.State.Values), x => x?.ToString()?.Contains("PRIVATE_LOGGER_FAILURE", StringComparison.Ordinal) == true);
    }

    private static RequestArrivalObservations Buffer(bool enabled, string until)
        => new(TimeProvider.System, Options.Create(new RequestArrivalObservationOptions { Enabled = enabled, UntilUtc = until }));

    private static async Task<RequestArrivalObservationFlushService> Service(RequestArrivalObservations buffer, RecordingLogger logger)
    {
        var source = NpgsqlDataSource.Create("Host=127.0.0.1;Database=owned-unused;Username=owned-unused");
        await source.DisposeAsync();
        return new(buffer, new RequestArrivalObservationStore(source), TimeProvider.System, logger);
    }

    private sealed record LogEntry(LogLevel Level, EventId Event, IReadOnlyDictionary<string, object?> State, Exception? Exception);

    private sealed class RecordingLogger : ILogger<RequestArrivalObservationFlushService>
    {
        public bool FailStartup { get; init; }
        public ConcurrentQueue<LogEntry> Events { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == 5701)
            {
                Started.TrySetResult();
                if (FailStartup) throw new InvalidOperationException("PRIVATE_LOGGER_FAILURE");
            }
            Events.Enqueue(new(logLevel, eventId, ((IEnumerable<KeyValuePair<string, object?>>)(object)state!).ToDictionary(x => x.Key, x => x.Value), exception));
        }
    }
}
