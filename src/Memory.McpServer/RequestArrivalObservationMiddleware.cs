using Memory.Infrastructure;
using Microsoft.AspNetCore.Routing;

namespace Memory.McpServer;

public static class RequestArrivalObservationMiddleware
{
    public static IApplicationBuilder UseRequestArrivalObservations(this IApplicationBuilder app)
        => app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            if (!path.StartsWithSegments("/api") && !path.StartsWithSegments("/mcp"))
            {
                await next();
                return;
            }
            var operation = path == "/api/memories/search" ? "rest-memory-search"
                : path == "/api/context/build" ? "rest-working-context"
                : path.StartsWithSegments("/mcp") ? "http-mcp" : "other";
            RequestArrivalLease? observation = null;
            try
            {
                var buffer = context.RequestServices.GetRequiredService<RequestArrivalObservations>();
                if (buffer.Enabled)
                    observation = RequestArrivalCapture.TryBegin(buffer, "http", operation, classification:
                        ArrivalRequestClassification.ForHttp(context.Request.Method,
                            (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText,
                            context.Request.ContentLength, path == "/mcp" || path == "/mcp/"));
            }
            catch (Exception)
            {
                // Capture and classification are best-effort; do not change HTTP behavior or log request data.
            }
            if (observation is not null) context.Items[typeof(RequestArrivalLease)] = observation.Sequence;
            var completed = false;
            try { await next(); completed = true; }
            finally
            {
                RequestArrivalCapture.TryComplete(observation, context.RequestAborted.IsCancellationRequested ? "cancelled"
                    : completed && context.Response.StatusCode < 400 ? "success" : "error");
            }
        });
}
