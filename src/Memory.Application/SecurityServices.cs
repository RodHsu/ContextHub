using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Memory.Application;

public sealed class TenantSecurityService(
    IApplicationDbContext dbContext,
    IClock clock,
    ICacheVersionStore cacheStore,
    IRequestActorAccessor actorAccessor) : ITenantSecurityService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] DefaultTokenScopes = ["memory:read"];

    public async Task<TenantResult> CreateTenantAsync(TenantCreateRequest request, CancellationToken cancellationToken)
    {
        var slug = NormalizeSlug(request.Slug);
        var displayName = NormalizeRequired(request.DisplayName, nameof(request.DisplayName));
        if (await dbContext.Tenants.AnyAsync(x => x.Slug == slug, cancellationToken))
        {
            throw new InvalidOperationException($"Tenant '{slug}' already exists.");
        }

        var tenant = new Tenant
        {
            Slug = slug,
            DisplayName = displayName,
            Status = TenantStatus.Active,
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow
        };
        await dbContext.Tenants.AddAsync(tenant, cancellationToken);
        await AddAuditAsync(SecurityAuditEventType.TenantCreated, "Succeeded", tenant.Id, null, null, new { slug }, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToResult(tenant);
    }

    public async Task<IReadOnlyList<TenantResult>> ListTenantsAsync(bool includeArchived, int limit, CancellationToken cancellationToken)
    {
        var normalizedLimit = Math.Clamp(limit, 1, 200);
        var query = dbContext.Tenants.AsQueryable();
        if (!includeArchived)
        {
            query = query.Where(x => x.Status != TenantStatus.Archived);
        }

        var tenants = await query
            .OrderBy(x => x.Slug)
            .Take(normalizedLimit)
            .ToListAsync(cancellationToken);
        return tenants.Select(ToResult).ToList();
    }

    public async Task<TenantUserResult> CreateUserAsync(TenantUserCreateRequest request, CancellationToken cancellationToken)
    {
        var tenant = await dbContext.Tenants.FirstOrDefaultAsync(x => x.Id == request.TenantId, cancellationToken)
            ?? throw new InvalidOperationException("Tenant not found.");
        if (tenant.Status != TenantStatus.Active)
        {
            throw new InvalidOperationException("Tenant is not active.");
        }

        var username = NormalizeUsername(request.Username);
        if (await dbContext.TenantUsers.AnyAsync(x => x.TenantId == request.TenantId && x.Username == username, cancellationToken))
        {
            throw new InvalidOperationException($"User '{username}' already exists in this tenant.");
        }

        var user = new TenantUser
        {
            TenantId = request.TenantId,
            Username = username,
            DisplayName = NormalizeRequired(request.DisplayName, nameof(request.DisplayName)),
            Email = NormalizeOptional(request.Email),
            PasswordHash = NormalizeOptional(request.PasswordHash),
            Role = request.Role,
            Status = TenantUserStatus.Active,
            PasswordUpdatedAt = string.IsNullOrWhiteSpace(request.PasswordHash) ? null : clock.UtcNow,
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow
        };
        await dbContext.TenantUsers.AddAsync(user, cancellationToken);
        await AddAuditAsync(SecurityAuditEventType.TenantUserCreated, "Succeeded", request.TenantId, user.Id, null, new { username }, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToResult(user);
    }

    public async Task<TenantUserResult> UpdateUserAsync(Guid userId, TenantUserUpdateRequest request, CancellationToken cancellationToken)
    {
        var user = await dbContext.TenantUsers.FirstOrDefaultAsync(x => x.Id == userId, cancellationToken)
            ?? throw new InvalidOperationException("Tenant user not found.");

        if (request.DisplayName is not null)
        {
            user.DisplayName = NormalizeRequired(request.DisplayName, nameof(request.DisplayName));
        }

        if (request.Email is not null)
        {
            user.Email = NormalizeOptional(request.Email);
        }

        if (request.Role.HasValue)
        {
            user.Role = request.Role.Value;
        }

        if (request.Status.HasValue)
        {
            user.Status = request.Status.Value;
        }

        if (request.PasswordHash is not null)
        {
            user.PasswordHash = NormalizeOptional(request.PasswordHash);
            user.PasswordUpdatedAt = string.IsNullOrWhiteSpace(user.PasswordHash) ? null : clock.UtcNow;
        }

        user.UpdatedAt = clock.UtcNow;
        await AddAuditAsync(SecurityAuditEventType.TenantUserCreated, "Updated", user.TenantId, user.Id, null, new { user.Username }, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await cacheStore.IncrementSecurityAsync(cancellationToken);
        return ToResult(user);
    }

    public async Task<IReadOnlyList<TenantUserResult>> ListUsersAsync(Guid tenantId, bool includeArchived, CancellationToken cancellationToken)
    {
        var query = dbContext.TenantUsers.Where(x => x.TenantId == tenantId);
        if (!includeArchived)
        {
            query = query.Where(x => x.Status != TenantUserStatus.Archived);
        }

        var users = await query
            .OrderBy(x => x.Username)
            .ToListAsync(cancellationToken);
        return users.Select(ToResult).ToList();
    }

    public async Task<TenantProjectGrantResult> UpsertProjectGrantAsync(TenantProjectGrantUpsertRequest request, CancellationToken cancellationToken)
    {
        var projectId = ProjectContext.Normalize(request.ProjectId);
        var tenant = await dbContext.Tenants.FirstOrDefaultAsync(x => x.Id == request.TenantId, cancellationToken)
            ?? throw new InvalidOperationException("Tenant not found.");
        if (tenant.Status != TenantStatus.Active)
        {
            throw new InvalidOperationException("Tenant is not active.");
        }

        var grant = await dbContext.TenantProjectGrants
            .FirstOrDefaultAsync(x => x.TenantId == request.TenantId && x.ProjectId == projectId, cancellationToken);

        if (grant is null)
        {
            grant = new TenantProjectGrant
            {
                TenantId = request.TenantId,
                ProjectId = projectId,
                CreatedAt = clock.UtcNow
            };
            await dbContext.TenantProjectGrants.AddAsync(grant, cancellationToken);
        }

        grant.CanRead = request.CanRead;
        grant.CanWrite = request.CanWrite;
        grant.CanManageTokens = request.CanManageTokens;
        grant.UpdatedAt = clock.UtcNow;
        await AddAuditAsync(SecurityAuditEventType.ProjectGrantUpserted, "Succeeded", request.TenantId, null, null, new { projectId }, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await cacheStore.IncrementSecurityAsync(cancellationToken);
        return ToResult(grant);
    }

    public async Task<IReadOnlyList<TenantProjectGrantResult>> ListProjectGrantsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var grants = await dbContext.TenantProjectGrants
            .Where(x => x.TenantId == tenantId)
            .OrderBy(x => x.ProjectId)
            .ToListAsync(cancellationToken);
        return grants.Select(ToResult).ToList();
    }

    public async Task<ApiTokenCreatedResult> CreateTokenAsync(ApiTokenCreateRequest request, CancellationToken cancellationToken)
    {
        var user = await dbContext.TenantUsers
            .Include(x => x.Tenant)
            .FirstOrDefaultAsync(x => x.Id == request.OwnerUserId && x.TenantId == request.TenantId, cancellationToken)
            ?? throw new InvalidOperationException("Tenant user not found.");
        if (user.Status != TenantUserStatus.Active || user.Tenant?.Status != TenantStatus.Active)
        {
            throw new InvalidOperationException("Tenant user is not active.");
        }

        var plainToken = GenerateToken();
        var token = new ApiToken
        {
            TenantId = request.TenantId,
            OwnerUserId = request.OwnerUserId,
            Name = NormalizeRequired(request.Name, nameof(request.Name)),
            Notes = NormalizeOptional(request.Notes),
            TokenPrefix = plainToken[..Math.Min(12, plainToken.Length)],
            TokenHash = HashToken(plainToken),
            TokenLastFour = plainToken[^4..],
            Scopes = NormalizeScopes(request.Scopes),
            AllowedProjectIds = await NormalizeAllowedProjectIdsAsync(request.TenantId, request.AllowedProjectIds, cancellationToken),
            ExpiresAt = request.ExpiresAt?.ToUniversalTime(),
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow
        };

        await dbContext.ApiTokens.AddAsync(token, cancellationToken);
        await AddAuditAsync(SecurityAuditEventType.ApiTokenCreated, "Succeeded", request.TenantId, request.OwnerUserId, token.Id, new { token.Name }, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new ApiTokenCreatedResult(ToResult(token), plainToken);
    }

    public async Task<ApiTokenResult> UpdateTokenAsync(Guid tokenId, ApiTokenUpdateRequest request, CancellationToken cancellationToken)
    {
        var token = await dbContext.ApiTokens.FirstOrDefaultAsync(x => x.Id == tokenId, cancellationToken)
            ?? throw new InvalidOperationException("API token not found.");
        if (token.RevokedAt.HasValue)
        {
            throw new InvalidOperationException("Revoked API tokens cannot be updated.");
        }

        if (request.Name is not null)
        {
            token.Name = NormalizeRequired(request.Name, nameof(request.Name));
        }

        if (request.Notes is not null)
        {
            token.Notes = NormalizeOptional(request.Notes);
        }

        if (request.Scopes is not null)
        {
            token.Scopes = NormalizeScopes(request.Scopes);
        }

        if (request.AllowedProjectIds is not null)
        {
            token.AllowedProjectIds = await NormalizeAllowedProjectIdsAsync(token.TenantId, request.AllowedProjectIds, cancellationToken);
        }

        token.ExpiresAt = request.ExpiresAt?.ToUniversalTime() ?? token.ExpiresAt;
        token.UpdatedAt = clock.UtcNow;
        await AddAuditAsync(SecurityAuditEventType.ApiTokenUpdated, "Succeeded", token.TenantId, token.OwnerUserId, token.Id, new { token.Name }, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await cacheStore.IncrementSecurityAsync(cancellationToken);
        return ToResult(token);
    }

    public async Task<ApiTokenResult> RevokeTokenAsync(Guid tokenId, CancellationToken cancellationToken)
    {
        var token = await dbContext.ApiTokens.FirstOrDefaultAsync(x => x.Id == tokenId, cancellationToken)
            ?? throw new InvalidOperationException("API token not found.");
        token.RevokedAt ??= clock.UtcNow;
        token.UpdatedAt = clock.UtcNow;
        await AddAuditAsync(SecurityAuditEventType.ApiTokenRevoked, "Succeeded", token.TenantId, token.OwnerUserId, token.Id, new { token.Name }, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await cacheStore.IncrementSecurityAsync(cancellationToken);
        return ToResult(token);
    }

    public async Task<ApiTokenCreatedResult> RegenerateTokenAsync(Guid tokenId, CancellationToken cancellationToken)
    {
        var token = await dbContext.ApiTokens.FirstOrDefaultAsync(x => x.Id == tokenId, cancellationToken)
            ?? throw new InvalidOperationException("API token not found.");
        if (token.RevokedAt.HasValue)
        {
            throw new InvalidOperationException("Revoked API tokens cannot be regenerated.");
        }

        var plainToken = GenerateToken();
        token.TokenPrefix = plainToken[..Math.Min(12, plainToken.Length)];
        token.TokenHash = HashToken(plainToken);
        token.TokenLastFour = plainToken[^4..];
        token.LastUsedAt = null;
        token.LastUsedIp = string.Empty;
        token.LastUsedUserAgent = string.Empty;
        token.UpdatedAt = clock.UtcNow;

        await AddAuditAsync(SecurityAuditEventType.ApiTokenUpdated, "Succeeded", token.TenantId, token.OwnerUserId, token.Id, new { token.Name, Action = "Regenerated" }, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await cacheStore.IncrementSecurityAsync(cancellationToken);
        return new ApiTokenCreatedResult(ToResult(token), plainToken);
    }

    public async Task<IReadOnlyList<ApiTokenResult>> ListTokensAsync(Guid tenantId, bool includeRevoked, CancellationToken cancellationToken)
    {
        var query = dbContext.ApiTokens.Where(x => x.TenantId == tenantId);
        if (!includeRevoked)
        {
            query = query.Where(x => !x.RevokedAt.HasValue);
        }

        var tokens = await query
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken);
        return tokens.Select(ToResult).ToList();
    }

    public async Task<IReadOnlyList<ApiTokenResult>> ListMyTokensAsync(bool includeRevoked, CancellationToken cancellationToken)
    {
        var actor = RequireUserActor();
        var query = dbContext.ApiTokens.Where(x => x.TenantId == actor.TenantId!.Value && x.OwnerUserId == actor.UserId!.Value);
        if (!includeRevoked)
        {
            query = query.Where(x => !x.RevokedAt.HasValue);
        }

        var tokens = await query.OrderByDescending(x => x.CreatedAt).ToListAsync(cancellationToken);
        return tokens.Select(ToResult).ToList();
    }

    public async Task<ApiTokenCreatedResult> CreateMyTokenAsync(ApiTokenCreateRequest request, CancellationToken cancellationToken)
    {
        var actor = RequireUserActor();
        return await CreateTokenAsync(request with
        {
            TenantId = actor.TenantId!.Value,
            OwnerUserId = actor.UserId!.Value
        }, cancellationToken);
    }

    public async Task<ApiTokenResult> UpdateMyTokenAsync(Guid tokenId, ApiTokenUpdateRequest request, CancellationToken cancellationToken)
    {
        var actor = RequireUserActor();
        var token = await dbContext.ApiTokens.FirstOrDefaultAsync(
            x => x.Id == tokenId && x.TenantId == actor.TenantId!.Value && x.OwnerUserId == actor.UserId!.Value,
            cancellationToken)
            ?? throw new InvalidOperationException("API token not found.");

        return await UpdateTokenAsync(token.Id, request, cancellationToken);
    }

    public async Task<ApiTokenCreatedResult> RegenerateMyTokenAsync(Guid tokenId, CancellationToken cancellationToken)
    {
        var actor = RequireUserActor();
        var token = await dbContext.ApiTokens.FirstOrDefaultAsync(
            x => x.Id == tokenId && x.TenantId == actor.TenantId!.Value && x.OwnerUserId == actor.UserId!.Value,
            cancellationToken)
            ?? throw new InvalidOperationException("API token not found.");

        return await RegenerateTokenAsync(token.Id, cancellationToken);
    }

    public async Task<ApiTokenResult> RevokeMyTokenAsync(Guid tokenId, CancellationToken cancellationToken)
    {
        var actor = RequireUserActor();
        var token = await dbContext.ApiTokens.FirstOrDefaultAsync(
            x => x.Id == tokenId && x.TenantId == actor.TenantId!.Value && x.OwnerUserId == actor.UserId!.Value,
            cancellationToken)
            ?? throw new InvalidOperationException("API token not found.");

        return await RevokeTokenAsync(token.Id, cancellationToken);
    }

    public async Task<ApiTokenAuthenticationResult> AuthenticateTokenAsync(string token, string ipAddress, string userAgent, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            await AddAuditAsync(SecurityAuditEventType.ApiTokenAuthenticationFailed, "MissingToken", null, null, null, null, cancellationToken, ipAddress, userAgent);
            await dbContext.SaveChangesAsync(cancellationToken);
            return new ApiTokenAuthenticationResult(false, "Token is missing.");
        }

        var tokenHash = HashToken(token.Trim());
        var entity = await dbContext.ApiTokens
            .Include(x => x.Tenant)
            .Include(x => x.OwnerUser)
            .FirstOrDefaultAsync(x => x.TokenHash == tokenHash, cancellationToken);

        if (entity is null)
        {
            await AddAuditAsync(SecurityAuditEventType.ApiTokenAuthenticationFailed, "InvalidToken", null, null, null, null, cancellationToken, ipAddress, userAgent);
            await dbContext.SaveChangesAsync(cancellationToken);
            return new ApiTokenAuthenticationResult(false, "Token is invalid.");
        }

        var failure = ValidateActiveToken(entity);
        if (failure is not null)
        {
            await AddAuditAsync(SecurityAuditEventType.ApiTokenAuthenticationFailed, failure, entity.TenantId, entity.OwnerUserId, entity.Id, new { entity.Name }, cancellationToken, ipAddress, userAgent);
            await dbContext.SaveChangesAsync(cancellationToken);
            return new ApiTokenAuthenticationResult(false, failure);
        }

        entity.LastUsedAt = clock.UtcNow;
        entity.LastUsedIp = Truncate(ipAddress, 128);
        entity.LastUsedUserAgent = Truncate(userAgent, 512);
        entity.UpdatedAt = clock.UtcNow;
        await AddAuditAsync(SecurityAuditEventType.ApiTokenAuthenticated, "Succeeded", entity.TenantId, entity.OwnerUserId, entity.Id, new { entity.Name }, cancellationToken, ipAddress, userAgent);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new ApiTokenAuthenticationResult(
            true,
            string.Empty,
            entity.TenantId,
            entity.OwnerUserId,
            entity.Id,
            entity.Tenant?.Slug,
            entity.OwnerUser?.Username,
            entity.OwnerUser?.Role,
            entity.Scopes,
            entity.AllowedProjectIds);
    }

    public async Task<IReadOnlyList<SecurityAuditEventResult>> ListAuditEventsAsync(Guid? tenantId, int limit, CancellationToken cancellationToken)
    {
        var query = dbContext.SecurityAuditEvents.AsQueryable();
        if (tenantId.HasValue)
        {
            query = query.Where(x => x.TenantId == tenantId.Value);
        }

        var auditEvents = await query
            .OrderByDescending(x => x.CreatedAt)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(cancellationToken);
        return auditEvents.Select(ToResult).ToList();
    }

    private async Task<string[]> NormalizeAllowedProjectIdsAsync(Guid tenantId, IReadOnlyList<string>? requested, CancellationToken cancellationToken)
    {
        if (requested is { Count: > 0 })
        {
            if (requested.Any(x => string.Equals(x?.Trim(), ProjectContext.AllProjectIdsSentinel, StringComparison.OrdinalIgnoreCase)))
            {
                return [];
            }

            return requested
                .Select(x => ProjectContext.Normalize(x))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return await dbContext.TenantProjectGrants
            .Where(x => x.TenantId == tenantId && x.CanRead)
            .OrderBy(x => x.ProjectId)
            .Select(x => x.ProjectId)
            .ToArrayAsync(cancellationToken);
    }

    private static string GenerateToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return "chub_" + Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    public static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string? ValidateActiveToken(ApiToken token)
    {
        if (token.Tenant?.Status != TenantStatus.Active)
        {
            return "TenantInactive";
        }

        if (token.OwnerUser?.Status != TenantUserStatus.Active)
        {
            return "UserInactive";
        }

        if (token.RevokedAt.HasValue)
        {
            return "TokenRevoked";
        }

        if (token.ExpiresAt.HasValue && token.ExpiresAt.Value <= DateTimeOffset.UtcNow)
        {
            return "TokenExpired";
        }

        return null;
    }

    private async ValueTask AddAuditAsync(
        SecurityAuditEventType eventType,
        string outcome,
        Guid? tenantId,
        Guid? actorUserId,
        Guid? apiTokenId,
        object? details,
        CancellationToken cancellationToken,
        string ipAddress = "",
        string userAgent = "")
        => await dbContext.SecurityAuditEvents.AddAsync(new SecurityAuditEvent
        {
            TenantId = tenantId,
            ActorUserId = actorUserId,
            ApiTokenId = apiTokenId,
            EventType = eventType,
            Outcome = outcome,
            IpAddress = Truncate(ipAddress, 128),
            UserAgent = Truncate(userAgent, 512),
            DetailsJson = details is null ? "{}" : JsonSerializer.Serialize(details, JsonOptions),
            CreatedAt = clock.UtcNow
        }, cancellationToken);

    private ContextHubRequestActor RequireUserActor()
    {
        var actor = actorAccessor.Current;
        if (!actor.HasUser)
        {
            throw new InvalidOperationException("Authenticated user context is required.");
        }

        return actor;
    }

    private static string NormalizeSlug(string value)
    {
        var normalized = NormalizeRequired(value, nameof(value)).ToLowerInvariant();
        if (normalized.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
        {
            throw new InvalidOperationException("Tenant slug may only contain ASCII letters, digits, and hyphens.");
        }

        return normalized;
    }

    private static string NormalizeUsername(string value)
    {
        var normalized = NormalizeRequired(value, nameof(value)).ToLowerInvariant();
        if (normalized.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
        {
            throw new InvalidOperationException("Username may only contain ASCII letters, digits, dash, underscore, and dot.");
        }

        return normalized;
    }

    private static string NormalizeRequired(string? value, string name)
    {
        var normalized = NormalizeOptional(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException($"{name} is required.");
        }

        return normalized;
    }

    private static string NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string[] NormalizeScopes(IReadOnlyList<string>? scopes)
        => (scopes is { Count: > 0 } ? scopes : DefaultTokenScopes)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string Truncate(string? value, int maxLength)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Length <= maxLength ? value : value[..maxLength];

    private static TenantResult ToResult(Tenant tenant)
        => new(tenant.Id, tenant.Slug, tenant.DisplayName, tenant.Status, tenant.CreatedAt, tenant.UpdatedAt);

    private static TenantUserResult ToResult(TenantUser user)
        => new(
            user.Id,
            user.TenantId,
            user.Username,
            user.DisplayName,
            user.Email,
            user.Role,
            user.Status,
            user.LastLoginAt,
            user.PasswordUpdatedAt,
            user.CreatedAt,
            user.UpdatedAt);

    private static TenantProjectGrantResult ToResult(TenantProjectGrant grant)
        => new(grant.Id, grant.TenantId, grant.ProjectId, grant.CanRead, grant.CanWrite, grant.CanManageTokens, grant.CreatedAt, grant.UpdatedAt);

    private static ApiTokenResult ToResult(ApiToken token)
        => new(
            token.Id,
            token.TenantId,
            token.OwnerUserId,
            token.Name,
            token.Notes,
            token.TokenPrefix,
            token.TokenLastFour,
            token.Scopes,
            token.AllowedProjectIds,
            token.ExpiresAt,
            token.RevokedAt,
            token.LastUsedAt,
            token.LastUsedIp,
            token.LastUsedUserAgent,
            token.CreatedAt,
            token.UpdatedAt);

    private static SecurityAuditEventResult ToResult(SecurityAuditEvent auditEvent)
        => new(
            auditEvent.Id,
            auditEvent.TenantId,
            auditEvent.ActorUserId,
            auditEvent.ApiTokenId,
            auditEvent.EventType,
            auditEvent.Outcome,
            auditEvent.IpAddress,
            auditEvent.UserAgent,
            auditEvent.DetailsJson,
            auditEvent.CreatedAt);
}
