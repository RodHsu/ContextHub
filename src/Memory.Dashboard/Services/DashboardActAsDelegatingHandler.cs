using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace Memory.Dashboard.Services;

public sealed record DashboardExportAccount
{
    internal static readonly HttpRequestOptionsKey<DashboardExportAccount> RequestOption = new("ContextHub.Dashboard.ExportAccount");

    private DashboardExportAccount(Guid tenantId, Guid userId)
    {
        TenantId = tenantId;
        UserId = userId;
    }

    internal Guid TenantId { get; }
    internal Guid UserId { get; }

    internal static async Task<DashboardExportAccount> RequireAsync(AuthenticationStateProvider provider)
        => Require((await provider.GetAuthenticationStateAsync()).User);

    internal async Task EnsureCurrentAsync(AuthenticationStateProvider provider)
    {
        if (await RequireAsync(provider) != this)
            throw new UnauthorizedAccessException("匯出期間登入帳號已變更，請重新發起匯出。");
    }

    internal static DashboardExportAccount Require(ClaimsPrincipal? user)
    {
        if (user?.Identity is not ClaimsIdentity { IsAuthenticated: true } identity ||
            !Guid.TryParse(identity.FindFirst("contexthub:tenant_id")?.Value, out var tenantId) || tenantId == Guid.Empty ||
            !Guid.TryParse(identity.FindFirst("contexthub:user_id")?.Value, out var userId) || userId == Guid.Empty)
            throw new UnauthorizedAccessException("請以具備帳號識別的使用者重新登入後再匯出。");

        return new(tenantId, userId);
    }
}

public sealed class DashboardActAsDelegatingHandler(IHttpContextAccessor httpContextAccessor) : DelegatingHandler
{
    private const string TenantIdHeader = "X-ContextHub-Act-As-TenantId";
    private const string UserIdHeader = "X-ContextHub-Act-As-UserId";
    private const string UsernameHeader = "X-ContextHub-Act-As-Username";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var httpContext = httpContextAccessor.HttpContext;
        var user = httpContext?.User;
        if (request.Options.TryGetValue(DashboardExportAccount.RequestOption, out var account))
        {
            if (httpContext is not null && DashboardExportAccount.Require(user) != account)
                throw new UnauthorizedAccessException("匯出期間登入帳號已變更，請重新發起匯出。");

            AddHeader(request, TenantIdHeader, account.TenantId.ToString("D"));
            AddHeader(request, UserIdHeader, account.UserId.ToString("D"));
            request.Headers.Remove(UsernameHeader);
            AddHeader(request, UsernameHeader, user?.Identity?.Name);
        }
        else if (user?.Identity?.IsAuthenticated == true)
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
