using System.Reflection;
using FluentAssertions;
using Memory.Infrastructure;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Memory.UnitTests;

public sealed class RedisCacheCancellationTests
{
    [Fact]
    public async Task In_flight_read_cancellation_should_propagate_without_recording_a_cache_error_or_hit()
    {
        var pending = new TaskCompletionSource<RedisValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (cache, telemetry) = Create((method, _) => method.Name == "StringGetAsync" ? pending.Task : throw new NotSupportedException(method.Name));
        using var cancellation = new CancellationTokenSource();
        var read = cache.GetAsync<string[]>("key", "search-final", cancellation.Token);
        read.IsCompleted.Should().BeFalse();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        pending.SetResult("[]");
        var snapshot = telemetry.GetSnapshot();
        snapshot.Hits.Should().Be(0);
        snapshot.Misses.Should().Be(0);
        snapshot.Errors.Should().Be(0);
    }

    [Fact]
    public async Task In_flight_write_cancellation_should_propagate_without_claiming_a_completed_set()
    {
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (cache, telemetry) = Create((method, _) => method.Name == "StringSetAsync" ? pending.Task : throw new NotSupportedException(method.Name));
        using var cancellation = new CancellationTokenSource();
        var write = cache.SetAsync("key", "search-final", new[] { "value" }, TimeSpan.FromMinutes(1), cancellation.Token);
        write.IsCompleted.Should().BeFalse();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        pending.SetResult(true);
        telemetry.GetSnapshot().Sets.Should().Be(0);
        telemetry.GetSnapshot().Errors.Should().Be(0);
    }

    [Fact]
    public async Task Redis_transport_failure_should_be_an_error_and_allow_origin_fallback()
    {
        var (cache, telemetry) = Create((method, _) => method.Name == "StringGetAsync"
            ? Task.FromException<RedisValue>(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Test transport unavailable."))
            : throw new NotSupportedException(method.Name));
        var result = await cache.GetAsync<string[]>("key", "search-final", CancellationToken.None);
        result.Hit.Should().BeFalse();
        telemetry.GetSnapshot().Errors.Should().Be(1);
        telemetry.GetSnapshot().Hits.Should().Be(0);
        telemetry.GetSnapshot().InvalidPayloads.Should().Be(0);
    }

    private static (RedisObjectCache Cache, RedisCacheTelemetry Telemetry) Create(Func<MethodInfo, object?[]?, object?> call)
    {
        var database = DispatchProxy.Create<IDatabase, InterfaceProxy>();
        ((InterfaceProxy)database).Call = call;
        var connection = DispatchProxy.Create<IConnectionMultiplexer, InterfaceProxy>();
        ((InterfaceProxy)connection).Call = (method, _) => method.Name == "GetDatabase" ? database : throw new NotSupportedException(method.Name);
        var telemetry = new RedisCacheTelemetry();
        return (new RedisObjectCache(connection, Options.Create(new MemoryOptions()), telemetry), telemetry);
    }

    public class InterfaceProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Call { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Call(targetMethod!, args);
    }
}
