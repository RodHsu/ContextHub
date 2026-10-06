using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Memory.Application;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Memory.Infrastructure;

public sealed class RedisCacheVersionStore(
    IConnectionMultiplexer redis,
    IOptions<MemoryOptions> options,
    DurableCacheRevisionStore? durableRevisions = null) : ICacheVersionStore
{
    private readonly IDatabase _database = redis.GetDatabase();
    private readonly ISubscriber _subscriber = redis.GetSubscriber();
    private readonly string _prefix = $"memory:{options.Value.Namespace}:";
    private readonly Channel<Guid> _jobSignals = Channel.CreateUnbounded<Guid>();
    private readonly SemaphoreSlim _subscriptionGate = new(1, 1);
    private bool _jobSignalSubscribed;

    public Task<long> GetVersionAsync(CancellationToken cancellationToken)
        => durableRevisions is not null
            ? durableRevisions.ReadScopeAsync("global", cancellationToken)
            : GetOrCreateVersionAsync("version:global", cancellationToken);

    public async Task<CacheVersionStamp> GetVersionStampAsync(
        IReadOnlyList<string> projectIds,
        ContextHubRequestActor actor,
        bool includeShared,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (durableRevisions is not null)
        {
            var durable = await durableRevisions.ReadAsync(projectIds, actor, includeShared, cancellationToken);
            return await WithInvalidationSignalsAsync(durable, projectIds, actor, includeShared, cancellationToken);
        }
        var globalVersion = await GetVersionAsync(cancellationToken);
        var securityVersion = await GetOrCreateVersionAsync("version:security", cancellationToken);
        var sharedVersion = includeShared ? await GetOrCreateVersionAsync("version:shared", cancellationToken) : 0L;
        var normalizedProjects = ProjectContext.IdentityKeys((projectIds ?? [])
            .Select(x => ProjectContext.Normalize(x)));

        var projectVersions = new Dictionary<string, long>(ProjectContext.IdentityComparer);
        foreach (var projectId in normalizedProjects)
        {
            projectVersions[projectId] = await GetOrCreateVersionAsync(ScopedProjectVersionKey(projectId), cancellationToken);
        }

        var userVersion = actor.HasUser
            ? await GetOrCreateVersionAsync(UserVersionKey(actor), cancellationToken)
            : 0L;

        var value = string.Join(
            ";",
            [
                $"g={globalVersion}",
                $"s={securityVersion}",
                $"sh={sharedVersion}",
                $"u={userVersion}",
                $"p={string.Join(",", projectVersions.Select(x => $"{x.Key}:{x.Value}"))}"
            ]);

        return new CacheVersionStamp(value, globalVersion, securityVersion, sharedVersion, userVersion, projectVersions);
    }

    public async Task<long> IncrementAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (durableRevisions is not null) return await durableRevisions.IncrementAsync("global", cancellationToken);
        return await _database.StringIncrementAsync($"{_prefix}version:global");
    }

    public async Task<long> IncrementProjectAsync(string projectId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = ProjectContext.Normalize(projectId);
        if (ProjectContext.IsShared(normalized))
        {
            return await IncrementSharedAsync(cancellationToken);
        }

        if (ProjectContext.IsUser(normalized))
        {
            return await _database.StringIncrementAsync($"{_prefix}version:user-project");
        }

        return await _database.StringIncrementAsync($"{_prefix}{ProjectVersionKey(normalized)}");
    }

    public async Task<long> IncrementUserAsync(ContextHubRequestActor actor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return actor.HasUser
            ? await _database.StringIncrementAsync($"{_prefix}{UserVersionKey(actor)}")
            : await _database.StringIncrementAsync($"{_prefix}version:user-project");
    }

    public async Task<long> IncrementSharedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _database.StringIncrementAsync($"{_prefix}version:shared");
    }

    public async Task<long> IncrementSecurityAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _database.StringIncrementAsync($"{_prefix}version:security");
    }

    public Task<long> GetJobVersionAsync(CancellationToken cancellationToken)
        => GetOrCreateVersionAsync("version:jobs", cancellationToken);

    public async Task<long> IncrementJobsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _database.StringIncrementAsync($"{_prefix}version:jobs");
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = await _database.StringGetAsync($"{_prefix}{key}");
        return value.IsNullOrEmpty ? default : JsonSerializer.Deserialize<T>(value.ToString(), RedisObjectCache.SerializerOptions);
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = JsonSerializer.Serialize(value, RedisObjectCache.SerializerOptions);
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

    private async Task<long> GetOrCreateVersionAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var redisKey = $"{_prefix}{key}";
        var value = await _database.StringGetAsync(redisKey);
        if (!value.IsNullOrEmpty)
        {
            return (long)value!;
        }

        await _database.StringSetAsync(redisKey, 1L, when: When.NotExists);
        value = await _database.StringGetAsync(redisKey);
        return value.IsNullOrEmpty ? 1L : (long)value!;
    }

    private async Task<CacheVersionStamp> WithInvalidationSignalsAsync(
        CacheVersionStamp durable, IReadOnlyList<string> projects, ContextHubRequestActor actor,
        bool includeShared, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (RedisCacheOperationScope.IsBypassed) return UnknownSignals(durable);
        // Application invalidations can occur inside an outer business transaction. They remain
        // Redis signals: writing the DB revision on another connection would wait on our own trigger.
        // The committed DB revision is always part of the key, so early/lost/restored signals cannot
        // make a pre-commit result valid after commit. One MGET also preserves explicit invalidations.
        var keys = projects.Select(project => ProjectContext.IsShared(project) ? "version:shared" : ScopedProjectVersionKey(project))
            .Append("version:global").Append("version:security")
            .Append(actor.HasUser ? UserVersionKey(actor) : "version:user-project");
        if (includeShared) keys = keys.Append("version:shared");
        var redisKeys = keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(key => (RedisKey)($"{_prefix}{key}")).ToArray();
        try
        {
            var values = await _database.StringGetAsync(redisKeys).WaitAsync(cancellationToken);
            var signals = redisKeys.Select((key, index) => new { Key = key.ToString(), Value = values[index].ToString() }).ToArray();
            return durable with { Value = durable.Value + ";signals=" + JsonSerializer.Serialize(signals) };
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Unknown operational signals cannot address any existing object cache entry. Authority
            // remains the DB snapshot; origin reads may continue while Redis is unavailable.
            if (ex is RedisConnectionException or RedisTimeoutException) RedisCacheOperationScope.MarkTransportFailure();
            return UnknownSignals(durable);
        }
    }

    private static CacheVersionStamp UnknownSignals(CacheVersionStamp durable)
        => durable with { Value = durable.Value + ";signals-unavailable=" + Guid.NewGuid().ToString("N") };

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
                _jobSignals.Writer.TryWrite(Guid.TryParse(value.ToString(), out var jobId) ? jobId : Guid.Empty);
            });

            _jobSignalSubscribed = true;
        }
        finally
        {
            _subscriptionGate.Release();
        }
    }

    private static string ProjectVersionKey(string projectId)
        => $"version:project-identity-v1:{RedisCacheKeyBuilder.Hash(ProjectContext.IdentityKey(ProjectContext.Normalize(projectId)))}";

    private static string ScopedProjectVersionKey(string projectId)
        => ProjectContext.IsUser(projectId)
            ? "version:user-project"
            : ProjectVersionKey(projectId);

    private static string UserVersionKey(ContextHubRequestActor actor)
        => $"version:user:{actor.TenantId!.Value:N}:{actor.UserId!.Value:N}";
}

public sealed class RedisObjectCache(
    IConnectionMultiplexer redis,
    IOptions<MemoryOptions> options,
    RedisCacheTelemetry telemetry) : IRedisObjectCache
{
    internal static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };
    private readonly IDatabase _database = redis.GetDatabase();
    private readonly MemoryOptions _options = options.Value;
    private readonly string _prefix = $"memory:{options.Value.Namespace}:";

    public async Task<RedisCacheLookup<T>> GetAsync<T>(string key, string kind, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.RedisCache.Enabled || RedisCacheOperationScope.IsBypassed)
        {
            telemetry.RecordBypass(kind);
            return new RedisCacheLookup<T>(false, default);
        }

        try
        {
            var value = await _database.StringGetAsync($"{_prefix}{key}").WaitAsync(cancellationToken);
            if (value.IsNullOrEmpty)
            {
                telemetry.RecordMiss(kind);
                return new RedisCacheLookup<T>(false, default);
            }

            var decoded = JsonSerializer.Deserialize<T>(value.ToString(), SerializerOptions);
            if (decoded is null || !IsValidPayload(decoded))
            {
                telemetry.RecordInvalidPayload(kind);
                return new RedisCacheLookup<T>(false, default);
            }

            telemetry.RecordHit(kind);
            return new RedisCacheLookup<T>(true, decoded);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JsonException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            telemetry.RecordInvalidPayload(kind);
            return new RedisCacheLookup<T>(false, default);
        }
        catch (Exception ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ex is RedisConnectionException or RedisTimeoutException) RedisCacheOperationScope.MarkTransportFailure();
            telemetry.RecordError(kind);
            return new RedisCacheLookup<T>(false, default);
        }
    }

    private static bool IsValidPayload<T>(T value)
    {
        // Nullable annotations do not validate collection elements. Validate bounded known cache
        // containers explicitly so a syntactically valid [null] cannot become a hit and crash callers.
        static bool Valid(IEnumerable<object?>? values) => values is not null && values.All(x => x is not null);
        return value switch
        {
            WorkingContextResult context => Valid(context.Facts) && Valid(context.Decisions) && Valid(context.Episodes)
                && Valid(context.Artifacts) && Valid(context.RecentLogs) && Valid(context.UserPreferences)
                && Valid(context.SuggestedTests) && Valid(context.Citations),
            PagedResult<MemoryListItemResult> page => Valid(page.Items),
            MemoryDetailsResult details => details.Document is not null && Valid(details.Revisions) && Valid(details.Chunks),
            IEnumerable<object?> values => Valid(values),
            _ => true
        };
    }

    public async Task SetAsync<T>(string key, string kind, T value, TimeSpan ttl, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.RedisCache.Enabled || RedisCacheOperationScope.IsBypassed)
        {
            telemetry.RecordBypass(kind);
            return;
        }

        try
        {
            var payload = JsonSerializer.Serialize(value, SerializerOptions);
            await _database.StringSetAsync($"{_prefix}{key}", payload, ttl).WaitAsync(cancellationToken);
            telemetry.RecordSet(kind);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ex is RedisConnectionException or RedisTimeoutException) RedisCacheOperationScope.MarkTransportFailure();
            telemetry.RecordError(kind);
        }
    }
}

public sealed class RedisCacheTelemetry(ICacheMetricsRecorder? metrics = null) : IRedisCacheTelemetry
{
    private readonly ConcurrentDictionary<string, CacheCounters> _byKind = new(StringComparer.OrdinalIgnoreCase);
    private long _hits;
    private long _misses;
    private long _sets;
    private long _bypasses;
    private long _errors;
    private long _invalidPayloads;

    public void RecordHit(string kind)
    {
        Interlocked.Increment(ref _hits);
        Kind(kind).IncrementHit();
        metrics?.Record(kind, CacheMetricOutcome.Hit);
    }

    public void RecordMiss(string kind)
    {
        Interlocked.Increment(ref _misses);
        Kind(kind).IncrementMiss();
        metrics?.Record(kind, CacheMetricOutcome.Miss);
    }

    public void RecordSet(string kind)
    {
        Interlocked.Increment(ref _sets);
        Kind(kind).IncrementSet();
        metrics?.Record(kind, CacheMetricOutcome.Set);
    }

    public void RecordBypass(string kind)
    {
        Interlocked.Increment(ref _bypasses);
        Kind(kind).IncrementBypass();
        metrics?.Record(kind, CacheMetricOutcome.Bypass);
    }

    public void RecordError(string kind)
    {
        Interlocked.Increment(ref _errors);
        Kind(kind).IncrementError();
        metrics?.Record(kind, CacheMetricOutcome.Error);
    }

    public void RecordInvalidPayload(string kind)
    {
        Interlocked.Increment(ref _invalidPayloads);
        Kind(kind).IncrementInvalidPayload();
        metrics?.Record(kind, CacheMetricOutcome.InvalidPayload);
    }

    public RedisCacheTelemetrySnapshot GetSnapshot()
        => new(
            Interlocked.Read(ref _hits),
            Interlocked.Read(ref _misses),
            Interlocked.Read(ref _sets),
            Interlocked.Read(ref _bypasses),
            Interlocked.Read(ref _errors),
            _byKind.ToDictionary(x => x.Key, x => x.Value.Snapshot(), StringComparer.OrdinalIgnoreCase),
            Interlocked.Read(ref _invalidPayloads));

    private CacheCounters Kind(string kind)
        => _byKind.GetOrAdd(string.IsNullOrWhiteSpace(kind) ? "unknown" : kind.Trim(), _ => new CacheCounters());

    private sealed class CacheCounters
    {
        private long _hits;
        private long _misses;
        private long _sets;
        private long _bypasses;
        private long _errors;
        private long _invalidPayloads;

        public void IncrementHit() => Interlocked.Increment(ref _hits);
        public void IncrementMiss() => Interlocked.Increment(ref _misses);
        public void IncrementSet() => Interlocked.Increment(ref _sets);
        public void IncrementBypass() => Interlocked.Increment(ref _bypasses);
        public void IncrementError() => Interlocked.Increment(ref _errors);
        public void IncrementInvalidPayload() => Interlocked.Increment(ref _invalidPayloads);

        public RedisCacheKindTelemetry Snapshot()
            => new(
                Interlocked.Read(ref _hits),
                Interlocked.Read(ref _misses),
                Interlocked.Read(ref _sets),
                Interlocked.Read(ref _bypasses),
                Interlocked.Read(ref _errors),
                Interlocked.Read(ref _invalidPayloads));
    }
}
