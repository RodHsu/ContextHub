using System.Security.Claims;
using Memory.Application;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Memory.McpServer;

internal static class ContextHubActAsHeaders
{
    public const string TenantId = "X-ContextHub-Act-As-TenantId";
    public const string UserId = "X-ContextHub-Act-As-UserId";
    public const string Username = "X-ContextHub-Act-As-Username";
}

internal sealed class RequestActorMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IRequestActorAccessor actorAccessor, IApplicationDbContext dbContext)
    {
        if (IsHealthCheck(context.Request.Path))
        {
            await next(context);
            return;
        }

        var previousActor = actorAccessor.Current;
        actorAccessor.Current = await ResolveActorAsync(context, dbContext);
        try
        {
            if (context.Response.StatusCode is StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden)
            {
                return;
            }

            await next(context);
        }
        finally
        {
            actorAccessor.Current = previousActor;
        }
    }

    private static async Task<ContextHubRequestActor> ResolveActorAsync(HttpContext context, IApplicationDbContext dbContext)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return ContextHubRequestActor.Unrestricted;
        }

        var tokenActor = BuildTokenActor(context.User);
        if (!HasActAsHeaders(context))
        {
            return tokenActor;
        }

        if (!tokenActor.HasScope(SecurityScopes.DashboardActAs) ||
            !Guid.TryParse(context.Request.Headers[ContextHubActAsHeaders.TenantId].FirstOrDefault(), out var tenantId) ||
            !Guid.TryParse(context.Request.Headers[ContextHubActAsHeaders.UserId].FirstOrDefault(), out var userId))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return tokenActor;
        }

        var user = await dbContext.TenantUsers
            .AsNoTracking()
            .Include(x => x.Tenant)
            .FirstOrDefaultAsync(
                x => x.Id == userId &&
                     x.TenantId == tenantId &&
                     x.Status == TenantUserStatus.Active &&
                     x.Tenant!.Status == TenantStatus.Active,
                context.RequestAborted);

        if (user is null)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return tokenActor;
        }

        return new ContextHubRequestActor(
            user.TenantId,
            user.Id,
            user.Username,
            user.Role,
            tokenActor.Scopes,
            tokenActor.AllowedProjectIds,
            true,
            IsServiceActor: false);
    }

    private static ContextHubRequestActor BuildTokenActor(ClaimsPrincipal user)
    {
        Guid.TryParse(user.FindFirstValue(ContextHubAuthentication.TenantIdClaim), out var tenantId);
        Guid.TryParse(user.FindFirstValue(ContextHubAuthentication.UserIdClaim), out var userId);
        Enum.TryParse<TenantUserRole>(user.FindFirstValue(ClaimTypes.Role), out var role);
        return new ContextHubRequestActor(
            tenantId == Guid.Empty ? null : tenantId,
            userId == Guid.Empty ? null : userId,
            user.FindFirstValue(ContextHubAuthentication.UsernameClaim) ?? string.Empty,
            role,
            user.FindAll(ContextHubAuthentication.ScopeClaim).Select(x => x.Value).ToArray(),
            user.FindAll(ContextHubAuthentication.ProjectClaim).Select(x => x.Value).ToArray(),
            true);
    }

    private static bool HasActAsHeaders(HttpContext context)
        => context.Request.Headers.ContainsKey(ContextHubActAsHeaders.TenantId) ||
           context.Request.Headers.ContainsKey(ContextHubActAsHeaders.UserId) ||
           context.Request.Headers.ContainsKey(ContextHubActAsHeaders.Username);

    private static bool IsHealthCheck(PathString path)
        => path.StartsWithSegments("/health/live", StringComparison.OrdinalIgnoreCase) ||
           path.StartsWithSegments("/health/ready", StringComparison.OrdinalIgnoreCase);
}
