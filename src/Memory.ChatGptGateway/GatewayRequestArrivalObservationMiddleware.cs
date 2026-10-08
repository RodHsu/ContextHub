using Memory.Infrastructure;
using Microsoft.AspNetCore.Http;

namespace Memory.ChatGptGateway;

internal sealed class GatewayRequestArrivalObservationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, RequestArrivalObservations observations)
    {
        if (!IsMcpEnvelope(context.Request.Path))
        {
            await next(context);
            return;
        }

        RequestArrivalLease? observation = null;
        try
        {
            observation = observations.Begin("http", "http-mcp");
        }
        catch (Exception)
        {
            // Arrival capture is best-effort and must not change the gateway response.
        }

        if (observation is not null)
        {
            context.Items[typeof(RequestArrivalLease)] = observation.Sequence;
        }

        var completed = false;
        try
        {
            await next(context);
            completed = true;
        }
        finally
        {
            try
            {
                observation?.Complete(context.RequestAborted.IsCancellationRequested ? "cancelled"
                    : completed && context.Response.StatusCode < StatusCodes.Status400BadRequest ? "success" : "error");
            }
            catch (Exception)
            {
                // Arrival capture is best-effort and must not change the gateway response.
            }
        }
    }

    private static bool IsMcpEnvelope(PathString path)
        => string.Equals(path.Value, "/mcp", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(path.Value, "/mcp/", StringComparison.OrdinalIgnoreCase);
}
