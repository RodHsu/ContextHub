namespace Memory.Dashboard.Services;

public sealed class DashboardActAsDelegatingHandler(IHttpContextAccessor httpContextAccessor) : DelegatingHandler
{
    private const string TenantIdHeader = "X-ContextHub-Act-As-TenantId";
    private const string UserIdHeader = "X-ContextHub-Act-As-UserId";
    private const string UsernameHeader = "X-ContextHub-Act-As-Username";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var user = httpContextAccessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated == true)
        {
            AddHeader(request, TenantIdHeader, user.FindFirst("contexthub:tenant_id")?.Value);
            AddHeader(request, UserIdHeader, user.FindFirst("contexthub:user_id")?.Value);
            AddHeader(request, UsernameHeader, user.Identity.Name);
        }

        return base.SendAsync(request, cancellationToken);
    }

    private static void AddHeader(HttpRequestMessage request, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            request.Headers.Remove(name);
            request.Headers.TryAddWithoutValidation(name, value);
        }
    }
}
