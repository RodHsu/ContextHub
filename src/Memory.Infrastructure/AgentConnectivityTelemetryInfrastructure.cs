using Memory.Application;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Memory.Infrastructure;

public sealed class AgentConnectivityService(
    MemoryDbContext dbContext,
    IRequestActorAccessor actorAccessor,
    IOptions<AgentConnectivityTelemetryOptions> options,
    TimeProvider timeProvider) : IAgentConnectivityService
{
    private const int BucketMinutes = 1;
    private readonly AgentConnectivityTelemetryOptions options = options.Value;

    public AgentConnectivitySettingsResult GetSettings()
        => new(
            options.Enabled && options.ResolvedProfile != AgentConnectivityTelemetryProfile.Off,
            options.ResolvedProfile,
            options.NormalizedSuccessSampleRate,
            options.NormalizedFailureSampleRate,
            options.NormalizedProbeIntervalSeconds,
            options.NormalizedUploadIntervalSeconds,
            options.NormalizedMaxBatchSize,
            options.NormalizedMaxSamplesPerAgentMethodPerMinute,
            options.NormalizedRawRetentionDays,
            options.NormalizedSummaryRetentionDays);

    public async Task<AgentConnectivityIngestResult> IngestAsync(
        AgentConnectivityObservationBatchRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var settings = GetSettings();
        if (!settings.Enabled)
        {
            return new AgentConnectivityIngestResult(0, request.Observations?.Count ?? 0, now);
        }

        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.AgentConnectivityWrite);
        var projectId = ProjectContext.Normalize(request.ProjectId);
        ActorAuthorization.EnsureProjectAllowed(actor, projectId, write: true);

        var observations = request.Observations ?? [];
        var maxBatchSize = settings.MaxBatchSize;
        var accepted = new List<AgentConnectivityObservation>(Math.Min(observations.Count, maxBatchSize));
        foreach (var observation in observations.Take(maxBatchSize))
        {
            if (!TryCreateObservation(projectId, observation, now, out var entity))
            {
                continue;
            }

            accepted.Add(entity);
        }

        if (accepted.Count > 0)
        {
            await dbContext.AgentConnectivityObservations.AddRangeAsync(accepted, cancellationToken);
            await UpsertSummariesAsync(accepted, now, cancellationToken);
            await ApplyRetentionAsync(now, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return new AgentConnectivityIngestResult(
            accepted.Count,
            Math.Max(0, observations.Count - accepted.Count),
            now);
    }

    public async Task<AgentConnectivityStatusResult> GetStatusAsync(string? projectId, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        var effectiveProjectId = ProjectContext.Normalize(projectId);
        ActorAuthorization.EnsureProjectAllowed(actor, effectiveProjectId, write: false);

        var now = timeProvider.GetUtcNow();
        var recentSince = now.AddMinutes(-10);
        var recent = await dbContext.AgentConnectivityObservations
            .AsNoTracking()
            .Where(x => x.ProjectId == effectiveProjectId && x.ObservedAtUtc >= recentSince)
            .OrderByDescending(x => x.ObservedAtUtc)
            .Take(500)
            .ToListAsync(cancellationToken);

        if (recent.Count == 0)
        {
            var lastSeen = await dbContext.AgentConnectivityObservations
                .AsNoTracking()
                .Where(x => x.ProjectId == effectiveProjectId)
                .OrderByDescending(x => x.ObservedAtUtc)
                .Select(x => (DateTimeOffset?)x.ObservedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

            return new AgentConnectivityStatusResult(
                effectiveProjectId,
                AgentConnectivityStatus.Unknown,
                lastSeen,
                0,
                0,
                null,
                null,
                lastSeen is null ? "No agent connectivity telemetry has been received." : "No recent telemetry has been received.");
        }

        var failures = recent.Count(x => !x.Success);
        var failureRate = failures / (double)recent.Count;
        var p95 = Percentile(recent.Select(x => x.ClientElapsedMs), 0.95);
        var status = failureRate >= 0.5 || p95 >= 15_000
            ? AgentConnectivityStatus.Unavailable
            : failureRate >= 0.1 || p95 >= 5_000
                ? AgentConnectivityStatus.Degraded
                : AgentConnectivityStatus.Healthy;

        return new AgentConnectivityStatusResult(
            effectiveProjectId,
            status,
            recent.Max(x => x.ObservedAtUtc),
            recent.Count,
            failures,
            failureRate,
            p95,
            status switch
            {
                AgentConnectivityStatus.Healthy => "Recent agent connectivity telemetry is healthy.",
                AgentConnectivityStatus.Degraded => "Recent agent connectivity telemetry shows elevated latency or failures.",
                _ => "Recent agent connectivity telemetry indicates unavailable or unstable connectivity."
            });
    }

    public async Task<IReadOnlyList<AgentConnectivitySummaryResult>> GetSummariesAsync(
        AgentConnectivitySummaryQuery query,
        CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        var projectId = ProjectContext.Normalize(query.ProjectId);
        ActorAuthorization.EnsureProjectAllowed(actor, projectId, write: false);
        var from = query.FromUtc ?? timeProvider.GetUtcNow().AddHours(-24);
        var to = query.ToUtc ?? timeProvider.GetUtcNow();
        var limit = Math.Clamp(query.Limit, 1, 1_000);

        var rows = dbContext.AgentConnectivitySummaries
            .AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.BucketStartUtc >= from && x.BucketStartUtc <= to);

        if (!string.IsNullOrWhiteSpace(query.AgentId))
        {
            var agentId = Normalize(query.AgentId, 128);
            rows = rows.Where(x => x.AgentId == agentId);
        }

        if (!string.IsNullOrWhiteSpace(query.McpMethod))
        {
            var method = Normalize(query.McpMethod, 128);
            rows = rows.Where(x => x.McpMethod == method);
        }

        return await rows
            .OrderByDescending(x => x.BucketStartUtc)
            .ThenBy(x => x.AgentId)
            .Take(limit)
            .Select(x => new AgentConnectivitySummaryResult(
                x.BucketStartUtc,
                x.BucketMinutes,
                x.ProjectId,
                x.AgentId,
                x.EndpointHost,
                x.Transport,
                x.McpMethod,
                x.ToolName,
                x.SampleCount,
                x.SuccessCount,
                x.FailureCount,
                x.TimeoutCount,
                x.AuthFailureCount,
                x.ReconnectCount,
                x.AvgClientElapsedMs,
                x.P95ClientElapsedMs,
                x.MaxClientElapsedMs,
                x.LastObservedAtUtc,
                x.Status))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AgentConnectivityRecentObservationResult>> GetRecentAsync(
        string? projectId,
        string? agentId,
        int limit,
        CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        var effectiveProjectId = ProjectContext.Normalize(projectId);
        ActorAuthorization.EnsureProjectAllowed(actor, effectiveProjectId, write: false);
        var rows = dbContext.AgentConnectivityObservations
            .AsNoTracking()
            .Where(x => x.ProjectId == effectiveProjectId);

        if (!string.IsNullOrWhiteSpace(agentId))
        {
            var normalizedAgentId = Normalize(agentId, 128);
            rows = rows.Where(x => x.AgentId == normalizedAgentId);
        }

        return await rows
            .OrderByDescending(x => x.ObservedAtUtc)
            .Take(Math.Clamp(limit, 1, 500))
            .Select(x => new AgentConnectivityRecentObservationResult(
                x.Id,
                x.ProjectId,
                x.AgentId,
                x.EndpointHost,
                x.McpMethod,
                x.ToolName,
                x.Success,
                x.StatusCode,
                x.ErrorKind,
                x.ClientElapsedMs,
                x.ReconnectAttempted,
                x.CorrelationId,
                x.ObservedAtUtc))
            .ToListAsync(cancellationToken);
    }

    private async Task UpsertSummariesAsync(
        IReadOnlyList<AgentConnectivityObservation> observations,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var group in observations.GroupBy(x => new
        {
            BucketStartUtc = TruncateToMinute(x.ObservedAtUtc),
            x.ProjectId,
            x.AgentId,
            x.EndpointHost,
            x.Transport,
            x.McpMethod,
            x.ToolName
        }))
        {
            var rows = group.ToArray();
            var summary = await dbContext.AgentConnectivitySummaries.FirstOrDefaultAsync(
                x => x.BucketStartUtc == group.Key.BucketStartUtc &&
                     x.BucketMinutes == BucketMinutes &&
                     x.ProjectId == group.Key.ProjectId &&
                     x.AgentId == group.Key.AgentId &&
                     x.EndpointHost == group.Key.EndpointHost &&
                     x.Transport == group.Key.Transport &&
                     x.McpMethod == group.Key.McpMethod &&
                     x.ToolName == group.Key.ToolName,
                cancellationToken);

            var batchCount = rows.Length;
            var batchSuccess = rows.Count(x => x.Success);
            var batchFailure = batchCount - batchSuccess;
            var batchTimeout = rows.Count(x => string.Equals(x.ErrorKind, "timeout", StringComparison.OrdinalIgnoreCase));
            var batchAuthFailure = rows.Count(x => x.StatusCode is 401 or 403);
            var batchReconnect = rows.Count(x => x.ReconnectAttempted);
            var batchAvg = rows.Average(x => x.ClientElapsedMs);
            var batchP95 = Percentile(rows.Select(x => x.ClientElapsedMs), 0.95);
            var batchMax = rows.Max(x => x.ClientElapsedMs);
            var lastObserved = rows.Max(x => x.ObservedAtUtc);
            var status = ResolveSummaryStatus(batchCount, batchFailure, batchP95);

            if (summary is null)
            {
                summary = new AgentConnectivitySummary
                {
                    BucketStartUtc = group.Key.BucketStartUtc,
                    BucketMinutes = BucketMinutes,
                    ProjectId = group.Key.ProjectId,
                    AgentId = group.Key.AgentId,
                    EndpointHost = group.Key.EndpointHost,
                    Transport = group.Key.Transport,
                    McpMethod = group.Key.McpMethod,
                    ToolName = group.Key.ToolName,
                    SampleCount = batchCount,
                    SuccessCount = batchSuccess,
                    FailureCount = batchFailure,
                    TimeoutCount = batchTimeout,
                    AuthFailureCount = batchAuthFailure,
                    ReconnectCount = batchReconnect,
                    AvgClientElapsedMs = batchAvg,
                    P95ClientElapsedMs = batchP95,
                    MaxClientElapsedMs = batchMax,
                    LastObservedAtUtc = lastObserved,
                    Status = status,
                    UpdatedAtUtc = now
                };
                await dbContext.AgentConnectivitySummaries.AddAsync(summary, cancellationToken);
                continue;
            }

            var totalCount = summary.SampleCount + batchCount;
            summary.AvgClientElapsedMs = ((summary.AvgClientElapsedMs * summary.SampleCount) + (batchAvg * batchCount)) / totalCount;
            summary.SampleCount = totalCount;
            summary.SuccessCount += batchSuccess;
            summary.FailureCount += batchFailure;
            summary.TimeoutCount += batchTimeout;
            summary.AuthFailureCount += batchAuthFailure;
            summary.ReconnectCount += batchReconnect;
            summary.P95ClientElapsedMs = Math.Max(summary.P95ClientElapsedMs, batchP95);
            summary.MaxClientElapsedMs = Math.Max(summary.MaxClientElapsedMs, batchMax);
            summary.LastObservedAtUtc = lastObserved > summary.LastObservedAtUtc ? lastObserved : summary.LastObservedAtUtc;
            summary.Status = ResolveSummaryStatus(summary.SampleCount, summary.FailureCount, summary.P95ClientElapsedMs);
            summary.UpdatedAtUtc = now;
        }
    }

    private async Task ApplyRetentionAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var rawCutoff = now.AddDays(-options.NormalizedRawRetentionDays);
        var summaryCutoff = now.AddDays(-options.NormalizedSummaryRetentionDays);
        await dbContext.AgentConnectivityObservations
            .Where(x => x.ObservedAtUtc < rawCutoff)
            .ExecuteDeleteAsync(cancellationToken);
        await dbContext.AgentConnectivitySummaries
            .Where(x => x.BucketStartUtc < summaryCutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private static bool TryCreateObservation(
        string projectId,
        AgentConnectivityObservationWriteRequest request,
        DateTimeOffset now,
        out AgentConnectivityObservation observation)
    {
        observation = new AgentConnectivityObservation();
        var agentId = Normalize(request.AgentId, 128);
        var endpointHost = Normalize(request.EndpointHost, 255);
        var method = Normalize(request.McpMethod, 128);
        if (string.IsNullOrWhiteSpace(agentId) ||
            string.IsNullOrWhiteSpace(endpointHost) ||
            string.IsNullOrWhiteSpace(method) ||
            request.ClientElapsedMs < 0 ||
            double.IsNaN(request.ClientElapsedMs) ||
            double.IsInfinity(request.ClientElapsedMs))
        {
            return false;
        }

        var observedAt = request.ObservedAtUtc == default ? now : request.ObservedAtUtc.ToUniversalTime();
        if (observedAt < now.AddDays(-31) || observedAt > now.AddMinutes(5))
        {
            observedAt = now;
        }

        var serverElapsedMs = NormalizeNullableDuration(request.ServerElapsedMs);
        observation = new AgentConnectivityObservation
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            AgentId = agentId,
            AgentName = Normalize(request.AgentName, 128),
            AgentVersion = Normalize(request.AgentVersion, 64),
            BridgeVersion = Normalize(request.BridgeVersion, 64),
            EndpointHost = endpointHost,
            Transport = Normalize(request.Transport, 64, "mcp-streamable-http"),
            McpMethod = method,
            ToolName = Normalize(request.ToolName, 128),
            Attempt = Math.Clamp(request.Attempt, 1, 10),
            Success = request.Success,
            StatusCode = request.StatusCode is >= 100 and <= 599 ? request.StatusCode : null,
            ErrorKind = request.Success ? string.Empty : Normalize(request.ErrorKind, 64, "unknown"),
            ClientElapsedMs = Math.Min(request.ClientElapsedMs, 3_600_000),
            ServerElapsedMs = serverElapsedMs,
            NetworkOverheadMs = serverElapsedMs.HasValue ? Math.Max(0, request.ClientElapsedMs - serverElapsedMs.Value) : null,
            SessionWasInitialized = request.SessionWasInitialized,
            ReconnectAttempted = request.ReconnectAttempted,
            CorrelationId = Normalize(request.CorrelationId, 128),
            Source = Normalize(request.Source, 64, "stdio-bridge"),
            ObservedAtUtc = observedAt,
            CreatedAtUtc = now
        };
        return true;
    }

    private static double? NormalizeNullableDuration(double? value)
    {
        if (!value.HasValue || value.Value < 0 || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return null;
        }

        return Math.Min(value.Value, 3_600_000);
    }

    private static AgentConnectivityStatus ResolveSummaryStatus(int sampleCount, int failureCount, double p95)
    {
        if (sampleCount <= 0)
        {
            return AgentConnectivityStatus.Unknown;
        }

        var failureRate = failureCount / (double)sampleCount;
        if (failureRate >= 0.5 || p95 >= 15_000)
        {
            return AgentConnectivityStatus.Unavailable;
        }

        return failureRate >= 0.1 || p95 >= 5_000
            ? AgentConnectivityStatus.Degraded
            : AgentConnectivityStatus.Healthy;
    }

    private static DateTimeOffset TruncateToMinute(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, TimeSpan.Zero);
    }

    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        var ordered = values.Order().ToArray();
        if (ordered.Length == 0)
        {
            return 0;
        }

        var index = (int)Math.Ceiling(percentile * ordered.Length) - 1;
        return ordered[Math.Clamp(index, 0, ordered.Length - 1)];
    }

    private static string Normalize(string? value, int maxLength, string fallback = "")
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            trimmed = fallback;
        }

        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
