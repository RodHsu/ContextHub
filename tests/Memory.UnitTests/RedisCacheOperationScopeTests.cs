using System.Reflection;
using Memory.Application;
using Memory.Infrastructure;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Memory.UnitTests;

public sealed class RedisCacheOperationScopeTests
{
    [Fact]
    public void Mark_without_scope_is_noop_and_nested_disposal_preserves_parent_failure()
    {
        Assert.False(RedisCacheOperationScope.IsBypassed);
        RedisCacheOperationScope.MarkTransportFailure();
        Assert.False(RedisCacheOperationScope.IsBypassed);
        using (RedisCacheOperationScope.BeginOrJoin())
        {
            Assert.False(RedisCacheOperationScope.IsBypassed);
            using (RedisCacheOperationScope.BeginOrJoin())
            {
                RedisCacheOperationScope.MarkTransportFailure();
                Assert.True(RedisCacheOperationScope.IsBypassed);
            }
            Assert.True(RedisCacheOperationScope.IsBypassed);
        }
        Assert.False(RedisCacheOperationScope.IsBypassed);
        using (RedisCacheOperationScope.BeginOrJoin())
            Assert.False(RedisCacheOperationScope.IsBypassed);
    }

    [Fact]
    public async Task Async_child_failure_propagates_to_parent_without_leaking_after_root_disposal()
    {
        using (RedisCacheOperationScope.BeginOrJoin())
        {
            await FailInNestedAsync();
            Assert.True(RedisCacheOperationScope.IsBypassed);
        }
        Assert.False(RedisCacheOperationScope.IsBypassed);

        static async Task FailInNestedAsync()
        {
            using var nested = RedisCacheOperationScope.BeginOrJoin();
            await Task.Yield();
            RedisCacheOperationScope.MarkTransportFailure();
        }
    }

    [Fact]
    public async Task Independent_concurrent_roots_do_not_share_failure()
    {
        Assert.False(RedisCacheOperationScope.IsBypassed);
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var healthyEntered = Signal();
        var failed = Signal();
        var healthyObserved = Signal();
        var failing = Task.Run(async () =>
        {
            using var root = RedisCacheOperationScope.BeginOrJoin();
            await healthyEntered.Task.WaitAsync(bound.Token);
            RedisCacheOperationScope.MarkTransportFailure();
            failed.SetResult(true);
            await healthyObserved.Task.WaitAsync(bound.Token);
            Assert.True(RedisCacheOperationScope.IsBypassed);
        });
        var healthy = Task.Run(async () =>
        {
            using var root = RedisCacheOperationScope.BeginOrJoin();
            healthyEntered.SetResult(true);
            await failed.Task.WaitAsync(bound.Token);
            Assert.False(RedisCacheOperationScope.IsBypassed);
            healthyObserved.SetResult(true);
        });
        await Task.WhenAll(failing, healthy).WaitAsync(bound.Token);
        Assert.False(RedisCacheOperationScope.IsBypassed);
    }

    [Fact]
    public async Task Concurrent_children_in_one_root_share_failure()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var root = RedisCacheOperationScope.BeginOrJoin();
        var observing = Signal();
        var failed = Signal();
        var first = Task.Run(async () =>
        {
            using var nested = RedisCacheOperationScope.BeginOrJoin();
            await observing.Task.WaitAsync(bound.Token);
            RedisCacheOperationScope.MarkTransportFailure();
            failed.SetResult(true);
        });
        var second = Task.Run(async () =>
        {
            using var nested = RedisCacheOperationScope.BeginOrJoin();
            observing.SetResult(true);
            await failed.Task.WaitAsync(bound.Token);
            Assert.True(RedisCacheOperationScope.IsBypassed);
        });
        await Task.WhenAll(first, second).WaitAsync(bound.Token);
        Assert.True(RedisCacheOperationScope.IsBypassed);
    }

    [Fact]
    public async Task Exceptional_root_exit_does_not_poison_next_operation()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var root = RedisCacheOperationScope.BeginOrJoin();
            await Task.Yield();
            RedisCacheOperationScope.MarkTransportFailure();
            throw new InvalidOperationException("Synthetic operation failure.");
        });
        Assert.False(RedisCacheOperationScope.IsBypassed);
        using var next = RedisCacheOperationScope.BeginOrJoin();
        Assert.False(RedisCacheOperationScope.IsBypassed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Transport_failure_records_one_error_then_bypasses_reads_and_writes_and_next_root_recovers(bool writeFirst, bool timeout)
    {
        var calls = 0;
        var available = false;
        var (cache, telemetry) = Create((method, _) =>
        {
            Interlocked.Increment(ref calls);
            Exception failure = timeout
                ? new RedisTimeoutException("Synthetic transport timeout.", CommandStatus.Unknown)
                : new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Synthetic transport unavailable.");
            return method.Name switch
            {
                "StringGetAsync" => available ? Task.FromResult<RedisValue>("\"healthy\"") : Task.FromException<RedisValue>(failure),
                "StringSetAsync" => available ? Task.FromResult(true) : Task.FromException<bool>(failure),
                _ => throw new NotSupportedException(method.Name)
            };
        });
        using (RedisCacheOperationScope.BeginOrJoin())
        {
            if (writeFirst)
                await cache.SetAsync("key", "search-final", "value", TimeSpan.FromMinutes(1), CancellationToken.None);
            else
                Assert.False((await cache.GetAsync<string>("key", "search-final", CancellationToken.None)).Hit);
            Assert.True(RedisCacheOperationScope.IsBypassed);
            available = true;
            Assert.False((await cache.GetAsync<string>("key", "search-final", CancellationToken.None)).Hit);
            await cache.SetAsync("key", "search-final", "value", TimeSpan.FromMinutes(1), CancellationToken.None);
            Assert.Equal(1, calls);
            AssertCounts(telemetry, errors: 1, bypasses: 2);
        }
        using (RedisCacheOperationScope.BeginOrJoin())
        {
            Assert.False(RedisCacheOperationScope.IsBypassed);
            var result = await cache.GetAsync<string>("key", "search-final", CancellationToken.None);
            Assert.True(result.Hit);
            Assert.Equal("healthy", result.Value);
            await cache.SetAsync("key", "search-final", "value", TimeSpan.FromMinutes(1), CancellationToken.None);
        }
        Assert.Equal(3, calls);
        AssertCounts(telemetry, hits: 1, sets: 1, errors: 1, bypasses: 2);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("null")]
    [InlineData("server")]
    public async Task Nontransport_read_failure_does_not_poison_scope(string failure)
    {
        var calls = 0;
        var (cache, telemetry) = Create((method, _) =>
        {
            Assert.Equal("StringGetAsync", method.Name);
            calls++;
            if (calls > 1) return Task.FromResult<RedisValue>("\"healthy\"");
            return failure == "server"
                ? Task.FromException<RedisValue>(new RedisServerException("Synthetic WRONGTYPE failure."))
                : Task.FromResult<RedisValue>(failure == "null" ? "null" : "{broken");
        });
        using var root = RedisCacheOperationScope.BeginOrJoin();
        Assert.False((await cache.GetAsync<string>("key", "search-final", CancellationToken.None)).Hit);
        Assert.False(RedisCacheOperationScope.IsBypassed);
        Assert.True((await cache.GetAsync<string>("key", "search-final", CancellationToken.None)).Hit);
        Assert.Equal(2, calls);
        AssertCounts(telemetry, hits: 1, errors: failure == "server" ? 1 : 0, invalid: failure == "server" ? 0 : 1);
    }

    [Fact]
    public async Task Server_write_failure_does_not_poison_scope()
    {
        var calls = 0;
        var (cache, telemetry) = Create((method, _) =>
        {
            Assert.Equal("StringSetAsync", method.Name);
            return ++calls == 1 ? Task.FromException<bool>(new RedisServerException("Synthetic write rejection.")) : Task.FromResult(true);
        });
        using var root = RedisCacheOperationScope.BeginOrJoin();
        await cache.SetAsync("key", "search-final", "value", TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.False(RedisCacheOperationScope.IsBypassed);
        await cache.SetAsync("key", "search-final", "value", TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Equal(2, calls);
        AssertCounts(telemetry, sets: 1, errors: 1);
    }

    [Fact]
    public async Task Serialization_failure_does_not_poison_scope_or_send_bad_payload()
    {
        var calls = 0;
        var (cache, telemetry) = Create((method, _) =>
        {
            Assert.Equal("StringSetAsync", method.Name);
            calls++;
            return Task.FromResult(true);
        });
        using var root = RedisCacheOperationScope.BeginOrJoin();
        var cyclic = new CyclicPayload();
        cyclic.Self = cyclic;
        await cache.SetAsync("key", "search-final", cyclic, TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.False(RedisCacheOperationScope.IsBypassed);
        Assert.Equal(0, calls);
        await cache.SetAsync("key", "search-final", "value", TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Equal(1, calls);
        AssertCounts(telemetry, sets: 1, errors: 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Precancelled_calls_throw_without_commands_or_counts_even_when_already_bypassed(bool bypassed)
    {
        var calls = 0;
        var (cache, telemetry) = Create((method, _) =>
        {
            calls++;
            throw new NotSupportedException(method.Name);
        });
        using var root = RedisCacheOperationScope.BeginOrJoin();
        if (bypassed) RedisCacheOperationScope.MarkTransportFailure();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetAsync<string>("key", "search-final", cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.SetAsync("key", "search-final", "value", TimeSpan.FromMinutes(1), cancellation.Token));
        Assert.Equal(bypassed, RedisCacheOperationScope.IsBypassed);
        Assert.Equal(0, calls);
        AssertCounts(telemetry);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Inflight_cancellation_has_no_counts_even_if_another_child_marks_bypass(bool write, bool bypassWhilePending)
    {
        var pendingRead = new TaskCompletionSource<RedisValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingWrite = Signal();
        var calls = 0;
        var (cache, telemetry) = Create((method, _) =>
        {
            calls++;
            return method.Name switch
            {
                "StringGetAsync" => pendingRead.Task,
                "StringSetAsync" => pendingWrite.Task,
                _ => throw new NotSupportedException(method.Name)
            };
        });
        using var root = RedisCacheOperationScope.BeginOrJoin();
        using var cancellation = new CancellationTokenSource();
        Task operation = write
            ? cache.SetAsync("key", "search-final", "value", TimeSpan.FromMinutes(1), cancellation.Token)
            : cache.GetAsync<string>("key", "search-final", cancellation.Token);
        Assert.False(operation.IsCompleted);
        if (bypassWhilePending)
            await Task.Run(() => RedisCacheOperationScope.MarkTransportFailure());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(10)));
        pendingRead.SetResult("\"late result\"");
        pendingWrite.SetResult(true);
        Assert.Equal(bypassWhilePending, RedisCacheOperationScope.IsBypassed);
        Assert.Equal(1, calls);
        AssertCounts(telemetry);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Synchronous_cancellation_wins_over_already_faulted_transport_task(bool write, bool timeout)
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var (cache, telemetry) = Create((method, _) =>
        {
            Assert.Equal(write ? "StringSetAsync" : "StringGetAsync", method.Name);
            if (++calls > 1)
                return write ? Task.FromResult(true) : Task.FromResult<RedisValue>("\"healthy\"");
            cancellation.Cancel();
            Exception failure = timeout
                ? new RedisTimeoutException("Synthetic completed transport timeout.", CommandStatus.Unknown)
                : new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Synthetic completed transport failure.");
            return write ? Task.FromException<bool>(failure) : Task.FromException<RedisValue>(failure);
        });
        using var root = RedisCacheOperationScope.BeginOrJoin();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write
            ? cache.SetAsync("key", "search-final", "value", TimeSpan.FromMinutes(1), cancellation.Token)
            : cache.GetAsync<string>("key", "search-final", cancellation.Token));
        Assert.Equal(1, calls);
        Assert.False(RedisCacheOperationScope.IsBypassed);
        AssertCounts(telemetry);

        if (write)
            await cache.SetAsync("key", "search-final", "value", TimeSpan.FromMinutes(1), CancellationToken.None);
        else
        {
            var recovered = await cache.GetAsync<string>("key", "search-final", CancellationToken.None);
            Assert.True(recovered.Hit);
            Assert.Equal("healthy", recovered.Value);
        }
        Assert.Equal(2, calls);
        Assert.False(RedisCacheOperationScope.IsBypassed);
        AssertCounts(telemetry, hits: write ? 0 : 1, sets: write ? 1 : 0);
    }

    [Fact]
    public async Task Synchronous_cancellation_wins_over_completed_malformed_json_without_recording_invalid_payload()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var (cache, telemetry) = Create((method, _) =>
        {
            Assert.Equal("StringGetAsync", method.Name);
            if (++calls > 1) return Task.FromResult<RedisValue>("\"healthy\"");
            cancellation.Cancel();
            return Task.FromResult<RedisValue>("{broken");
        });
        using var root = RedisCacheOperationScope.BeginOrJoin();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetAsync<string>("key", "search-final", cancellation.Token));
        Assert.Equal(1, calls);
        Assert.False(RedisCacheOperationScope.IsBypassed);
        AssertCounts(telemetry);

        var recovered = await cache.GetAsync<string>("key", "search-final", CancellationToken.None);
        Assert.True(recovered.Hit);
        Assert.Equal("healthy", recovered.Value);
        Assert.Equal(2, calls);
        Assert.False(RedisCacheOperationScope.IsBypassed);
        AssertCounts(telemetry, hits: 1);
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void AssertCounts(RedisCacheTelemetry telemetry, long hits = 0, long sets = 0, long errors = 0, long bypasses = 0, long invalid = 0)
    {
        var actual = telemetry.GetSnapshot();
        Assert.Equal(hits, actual.Hits);
        Assert.Equal(0, actual.Misses);
        Assert.Equal(sets, actual.Sets);
        Assert.Equal(errors, actual.Errors);
        Assert.Equal(bypasses, actual.Bypasses);
        Assert.Equal(invalid, actual.InvalidPayloads);
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

    public sealed class CyclicPayload
    {
        public CyclicPayload? Self { get; set; }
    }

    public class InterfaceProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Call { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Call(targetMethod!, args);
    }
}
