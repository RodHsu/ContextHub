using Memory.Application;
using Microsoft.AspNetCore.Mvc;

namespace Memory.McpServer;

internal sealed class MaintenanceModeMiddleware(
    RequestDelegate next,
    ILogger<MaintenanceModeMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, IMaintenanceCoordinator maintenanceCoordinator)
    {
        MaintenanceStatusResult state;
        try
        {
            state = await maintenanceCoordinator.GetStatusAsync(context.RequestAborted);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read maintenance state; allowing request to continue.");
            await next(context);
            return;
        }

        if (state.Phase is MaintenancePhase.Inactive or MaintenancePhase.Scheduled or MaintenancePhase.Completed or MaintenancePhase.Failed or MaintenancePhase.Cancelled)
        {
            await next(context);
            return;
        }

        if (state.Phase == MaintenancePhase.Draining && IsAllowedDuringDrain(context.Request))
        {
            context.Response.Headers["X-ContextHub-Maintenance"] = "draining";
            context.Response.Headers["X-ContextHub-Maintenance-Phase"] = state.Phase.ToString();
            await next(context);
            return;
        }

        if (IsAllowedDuringMaintenance(context.Request))
        {
            context.Response.Headers["X-ContextHub-Maintenance"] = state.Phase.ToString().ToLowerInvariant();
            context.Response.Headers["X-ContextHub-Maintenance-Phase"] = state.Phase.ToString();
            await next(context);
            return;
        }

        var retryAfterSeconds = ComputeRetryAfterSeconds(state.EstimatedEndsAtUtc);
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers["Retry-After"] = retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        context.Response.Headers["X-ContextHub-Maintenance"] = state.Phase.ToString().ToLowerInvariant();
        context.Response.Headers["X-ContextHub-Maintenance-Phase"] = state.Phase.ToString();

        var problem = new ProblemDetails
        {
            Title = state.Phase == MaintenancePhase.Draining
                ? "ContextHub is preparing for maintenance."
                : "ContextHub is under maintenance.",
            Detail = string.IsNullOrWhiteSpace(state.Message) ? "ContextHub is temporarily unavailable due to maintenance." : state.Message,
            Status = StatusCodes.Status503ServiceUnavailable,
            Type = "https://httpstatuses.com/503"
        };
        problem.Extensions["phase"] = state.Phase.ToString();
        problem.Extensions["reason"] = state.Reason;
        problem.Extensions["runId"] = state.RunId;
        problem.Extensions["startedAtUtc"] = state.StartedAtUtc;
        problem.Extensions["estimatedEndsAtUtc"] = state.EstimatedEndsAtUtc;
        problem.Extensions["activeLeaseCount"] = state.ActiveLeaseCount;

        await context.Response.WriteAsJsonAsync(problem, context.RequestAborted);
    }

    private static bool IsAllowedDuringDrain(HttpRequest request)
    {
        if (HttpMethods.IsGet(request.Method) ||
            HttpMethods.IsHead(request.Method) ||
            HttpMethods.IsOptions(request.Method))
        {
            return true;
        }

        var path = request.Path;
        return path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWithSegments("/api/maintenance", StringComparison.OrdinalIgnoreCase);
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
             path.StartsWithSegments("/api/maintenance/status", StringComparison.OrdinalIgnoreCase) ||
             path.StartsWithSegments("/api/maintenance/runs", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (path.StartsWithSegments("/api/maintenance", StringComparison.OrdinalIgnoreCase) &&
            (HttpMethods.IsDelete(request.Method) ||
             HttpMethods.IsPost(request.Method)))
        {
            return true;
        }

        return false;
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
