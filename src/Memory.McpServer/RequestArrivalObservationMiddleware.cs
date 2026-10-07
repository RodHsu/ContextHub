using Memory.Infrastructure;

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
            var observation = context.RequestServices.GetRequiredService<RequestArrivalObservations>().Begin("http", operation);
            if (observation is not null) context.Items[typeof(RequestArrivalLease)] = observation.Sequence;
            var completed = false;
            try { await next(); completed = true; }
            finally
            {
                observation?.Complete(context.RequestAborted.IsCancellationRequested ? "cancelled"
                    : completed && context.Response.StatusCode < 400 ? "success" : "error");
            }
        });
}
