using Memory.Application;

namespace Memory.McpServer;

public static class DashboardCacheTelemetryEndpoints
{
    // Register on the existing dashboard group so its administrator policy is inherited.
    public static RouteGroupBuilder MapCacheTelemetryEndpoints(this RouteGroupBuilder dashboard)
    {
        dashboard.MapGet("/cache-metrics", async (string? period, ICacheMetricsQueryService service, CancellationToken cancellationToken) =>
        {
            var selectedPeriod = (period ?? "24H").ToUpperInvariant();
            if (selectedPeriod is not ("24H" or "3D" or "7D" or "14D" or "30D"))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["period"] = ["Supported periods: 24H, 3D, 7D, 14D, 30D."] });
            }

            return Results.Ok(await service.GetAsync(selectedPeriod, cancellationToken));
        });
        dashboard.MapGet("/graph-refresh", async (IDashboardGraphRefreshCoordinator service, CancellationToken cancellationToken)
            => Results.Ok(await service.GetStatusAsync(cancellationToken)));
        return dashboard;
    }
}
