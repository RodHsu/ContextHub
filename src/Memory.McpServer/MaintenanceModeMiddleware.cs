using Memory.Application;
using Microsoft.AspNetCore.Mvc;

namespace Memory.McpServer;

internal sealed class MaintenanceModeMiddleware(
    RequestDelegate next,
    ILogger<MaintenanceModeMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, IMaintenanceModeStore maintenanceModeStore)
    {
        if (IsAllowedDuringMaintenance(context.Request))
        {
            await next(context);
            return;
        }

        MaintenanceModeStateResult state;
        try
        {
            state = await maintenanceModeStore.GetAsync(context.RequestAborted);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read maintenance mode state; allowing request to continue.");
            await next(context);
            return;
        }

        if (!state.Active)
        {
            await next(context);
            return;
        }

        var retryAfterSeconds = ComputeRetryAfterSeconds(state.EstimatedEndsAtUtc);
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers["Retry-After"] = retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        context.Response.Headers["X-ContextHub-Maintenance"] = "true";

        var problem = new ProblemDetails
        {
            Title = "ContextHub is under maintenance.",
            Detail = string.IsNullOrWhiteSpace(state.Message) ? "ContextHub is temporarily unavailable due to maintenance." : state.Message,
            Status = StatusCodes.Status503ServiceUnavailable,
            Type = "https://httpstatuses.com/503"
        };
        problem.Extensions["reason"] = state.Reason;
        problem.Extensions["runId"] = state.RunId;
        problem.Extensions["startedAtUtc"] = state.StartedAtUtc;
        problem.Extensions["estimatedEndsAtUtc"] = state.EstimatedEndsAtUtc;

        await context.Response.WriteAsJsonAsync(problem, context.RequestAborted);
    }

    private static bool IsAllowedDuringMaintenance(HttpRequest request)
    {
        var path = request.Path;
        if (path.StartsWithSegments("/health/live", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/health/ready", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (HttpMethods.IsGet(request.Method) &&
            (path.StartsWithSegments("/api/status", StringComparison.OrdinalIgnoreCase) ||
             path.StartsWithSegments("/api/maintenance/status", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (HttpMethods.IsDelete(request.Method) &&
            path.StartsWithSegments("/api/maintenance/mode", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return HttpMethods.IsPost(request.Method) &&
               (path.StartsWithSegments("/api/maintenance/vacuum-full-reclaim/run", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWithSegments("/api/maintenance/domain-owner-repair", StringComparison.OrdinalIgnoreCase));
    }

    private static int ComputeRetryAfterSeconds(DateTimeOffset? estimatedEndsAtUtc)
    {
        if (!estimatedEndsAtUtc.HasValue)
        {
            return 300;
        }

        var seconds = (int)Math.Ceiling((estimatedEndsAtUtc.Value - DateTimeOffset.UtcNow).TotalSeconds);
        return Math.Clamp(seconds, 1, 24 * 60 * 60);
    }
}
