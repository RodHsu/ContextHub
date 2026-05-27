using Memory.Application;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Memory.Infrastructure;

public sealed class SecurityBootstrapHostedService(
    IDbContextFactory<MemoryDbContext> dbContextFactory,
    IOptions<ContextHubOptions> options,
    TimeProvider timeProvider,
    ILogger<SecurityBootstrapHostedService> logger) : IHostedService
{
    private static readonly string[] BootstrapScopes =
    [
        SecurityScopes.MemoryRead,
        SecurityScopes.MemoryWrite,
        SecurityScopes.PreferencesRead,
        SecurityScopes.PreferencesWrite,
        SecurityScopes.TokenManage,
        SecurityScopes.SecurityManage,
        SecurityScopes.DashboardActAs
    ];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var security = options.Value.Security;
        var plainToken = security.BootstrapToken.Trim();
        if (string.IsNullOrWhiteSpace(plainToken))
        {
            return;
        }

        if (plainToken.Length < 16)
        {
            throw new InvalidOperationException("ContextHub:Security:BootstrapToken must be at least 16 characters.");
        }

        var tenantSlug = NormalizeSlug(security.BootstrapTenantSlug);
        var username = NormalizeUsername(security.BootstrapUsername);
        var projectIds = ParseProjectIds(security.BootstrapAllowedProjectIds);
        var now = timeProvider.GetUtcNow();
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var changed = false;

        var tenant = await dbContext.Tenants.FirstOrDefaultAsync(x => x.Slug == tenantSlug, cancellationToken);
        if (tenant is null)
        {
            tenant = new Tenant
            {
                Slug = tenantSlug,
                DisplayName = tenantSlug,
                Status = TenantStatus.Active,
                CreatedAt = now,
                UpdatedAt = now
            };
            await dbContext.Tenants.AddAsync(tenant, cancellationToken);
            changed = true;
        }
        else if (tenant.Status != TenantStatus.Active)
        {
            tenant.Status = TenantStatus.Active;
            tenant.UpdatedAt = now;
            changed = true;
        }

        var user = await dbContext.TenantUsers.FirstOrDefaultAsync(x => x.TenantId == tenant.Id && x.Username == username, cancellationToken);
        if (user is null)
        {
            user = new TenantUser
            {
                TenantId = tenant.Id,
                Username = username,
                DisplayName = username,
                Role = TenantUserRole.Owner,
                Status = TenantUserStatus.Active,
                CreatedAt = now,
                UpdatedAt = now
            };
            await dbContext.TenantUsers.AddAsync(user, cancellationToken);
            changed = true;
        }
        else if (user.Status != TenantUserStatus.Active || user.Role != TenantUserRole.Owner)
        {
            user.Status = TenantUserStatus.Active;
            user.Role = TenantUserRole.Owner;
            user.UpdatedAt = now;
            changed = true;
        }

        foreach (var projectId in projectIds)
        {
            var grant = await dbContext.TenantProjectGrants.FirstOrDefaultAsync(
                x => x.TenantId == tenant.Id && x.ProjectId == projectId,
                cancellationToken);
            if (grant is null)
            {
                grant = new TenantProjectGrant
                {
                    TenantId = tenant.Id,
                    ProjectId = projectId,
                    CreatedAt = now
                };
                await dbContext.TenantProjectGrants.AddAsync(grant, cancellationToken);
                changed = true;
            }

            if (!grant.CanRead || !grant.CanWrite || !grant.CanManageTokens)
            {
                grant.CanRead = true;
                grant.CanWrite = true;
                grant.CanManageTokens = true;
                grant.UpdatedAt = now;
                changed = true;
            }
        }

        var tokenHash = TenantSecurityService.HashToken(plainToken);
        var token = await dbContext.ApiTokens.FirstOrDefaultAsync(x => x.TokenHash == tokenHash, cancellationToken);
        if (token is null)
        {
            token = new ApiToken
            {
                TenantId = tenant.Id,
                OwnerUserId = user.Id,
                Name = "Bootstrap service token",
                Notes = "Provisioned from ContextHub security bootstrap settings.",
                TokenPrefix = plainToken[..Math.Min(12, plainToken.Length)],
                TokenHash = tokenHash,
                TokenLastFour = plainToken[^Math.Min(4, plainToken.Length)..],
                Scopes = BootstrapScopes,
                AllowedProjectIds = projectIds,
                CreatedAt = now,
                UpdatedAt = now
            };
            await dbContext.ApiTokens.AddAsync(token, cancellationToken);
            changed = true;
        }
        else
        {
            var tokenChanged = false;
            if (token.TenantId != tenant.Id)
            {
                token.TenantId = tenant.Id;
                tokenChanged = true;
            }

            if (token.OwnerUserId != user.Id)
            {
                token.OwnerUserId = user.Id;
                tokenChanged = true;
            }

            if (string.IsNullOrWhiteSpace(token.Name))
            {
                token.Name = "Bootstrap service token";
                tokenChanged = true;
            }

            var tokenPrefix = plainToken[..Math.Min(12, plainToken.Length)];
            if (!string.Equals(token.TokenPrefix, tokenPrefix, StringComparison.Ordinal))
            {
                token.TokenPrefix = tokenPrefix;
                tokenChanged = true;
            }

            var tokenLastFour = plainToken[^Math.Min(4, plainToken.Length)..];
            if (!string.Equals(token.TokenLastFour, tokenLastFour, StringComparison.Ordinal))
            {
                token.TokenLastFour = tokenLastFour;
                tokenChanged = true;
            }

            if (!token.Scopes.SequenceEqual(BootstrapScopes, StringComparer.OrdinalIgnoreCase))
            {
                token.Scopes = BootstrapScopes;
                tokenChanged = true;
            }

            if (!token.AllowedProjectIds.SequenceEqual(projectIds, StringComparer.OrdinalIgnoreCase))
            {
                token.AllowedProjectIds = projectIds;
                tokenChanged = true;
            }

            if (token.RevokedAt.HasValue || token.ExpiresAt.HasValue)
            {
                token.RevokedAt = null;
                token.ExpiresAt = null;
                tokenChanged = true;
            }

            if (tokenChanged)
            {
                token.UpdatedAt = now;
                changed = true;
            }
        }

        if (changed)
        {
            await dbContext.SecurityAuditEvents.AddAsync(new SecurityAuditEvent
            {
                TenantId = tenant.Id,
                ActorUserId = user.Id,
                ApiTokenId = token.Id,
                EventType = SecurityAuditEventType.ApiTokenCreated,
                Outcome = "BootstrapEnsured",
                DetailsJson = $$"""{"tenantSlug":"{{tenantSlug}}","username":"{{username}}"}""",
                CreatedAt = now
            }, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("ContextHub bootstrap service token ensured for tenant {TenantSlug} and user {Username}.", tenantSlug, username);
        }
        else
        {
            await transaction.RollbackAsync(cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    private static string NormalizeSlug(string value)
        => NormalizeRequired(value, nameof(ContextHubSecurityOptions.BootstrapTenantSlug)).ToLowerInvariant();

    private static string NormalizeUsername(string value)
        => NormalizeRequired(value, nameof(ContextHubSecurityOptions.BootstrapUsername)).ToLowerInvariant();

    private static string NormalizeRequired(string? value, string name)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException($"{name} is required when ContextHub:Security:BootstrapToken is configured.");
        }

        return normalized;
    }

    private static string[] ParseProjectIds(string value)
    {
        var projectIds = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(projectId => ProjectContext.Normalize(projectId))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return projectIds.Length > 0 ? projectIds : [ProjectContext.DefaultProjectId];
    }
}
