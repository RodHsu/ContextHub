using System.Text.Json;
using System.Threading.Channels;
using Memory.Application;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Memory.Infrastructure;

public sealed class RedisCacheVersionStore(IConnectionMultiplexer redis, IOptions<MemoryOptions> options) : ICacheVersionStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly IDatabase _database = redis.GetDatabase();
    private readonly ISubscriber _subscriber = redis.GetSubscriber();
    private readonly string _prefix = $"memory:{options.Value.Namespace}:";
    private readonly Channel<Guid> _jobSignals = Channel.CreateUnbounded<Guid>();
    private readonly SemaphoreSlim _subscriptionGate = new(1, 1);
    private bool _jobSignalSubscribed;

    public async Task<long> GetVersionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = await _database.StringGetAsync($"{_prefix}cache-version");
        if (value.IsNullOrEmpty)
        {
            await _database.StringSetAsync($"{_prefix}cache-version", 1L);
            return 1L;
        }

        return (long)value!;
    }

    public async Task<long> IncrementAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _database.StringIncrementAsync($"{_prefix}cache-version");
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = await _database.StringGetAsync($"{_prefix}{key}");
        return value.IsNullOrEmpty ? default : JsonSerializer.Deserialize<T>(value.ToString(), SerializerOptions);
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = JsonSerializer.Serialize(value, SerializerOptions);
        await _database.StringSetAsync($"{_prefix}{key}", payload, ttl);
    }

    public async Task PublishJobSignalAsync(Guid jobId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _subscriber.PublishAsync(RedisChannel.Literal($"{_prefix}jobs"), jobId.ToString("N"));
    }

    public async Task<bool> WaitForJobSignalAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureJobSignalSubscriptionAsync(cancellationToken);

        if (_jobSignals.Reader.TryRead(out _))
        {
            return true;
        }

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await _jobSignals.Reader.ReadAsync(linkedCts.Token);
            return true;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task EnsureJobSignalSubscriptionAsync(CancellationToken cancellationToken)
    {
        if (_jobSignalSubscribed)
        {
            return;
        }

        await _subscriptionGate.WaitAsync(cancellationToken);
        try
        {
            if (_jobSignalSubscribed)
            {
                return;
            }

            await _subscriber.SubscribeAsync(RedisChannel.Literal($"{_prefix}jobs"), (_, value) =>
            {
                if (Guid.TryParse(value.ToString(), out var jobId))
                {
                    _jobSignals.Writer.TryWrite(jobId);
                }
                else
                {
                    _jobSignals.Writer.TryWrite(Guid.Empty);
                }
            });

            _jobSignalSubscribed = true;
        }
        finally
        {
            _subscriptionGate.Release();
        }
    }
}
