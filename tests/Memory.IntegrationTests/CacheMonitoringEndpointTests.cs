using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Memory.IntegrationTests;

public sealed class CacheMonitoringEndpointTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Restricted_token_should_keep_project_grants_on_cold_and_warm_catalog_and_detail_reads()
    {
        var factory = environment.GetFactory();
        var marker = $"catalog-scope-{Guid.NewGuid():N}";
        var projectA = marker + "-i";
        var projectB = marker + "-İ";
        StringComparer.OrdinalIgnoreCase.Equals(projectA, projectB).Should().BeFalse("PostgreSQL lower() must not determine authorization for dotted-I project identifiers");
        var restrictedPlainToken = Guid.NewGuid().ToString("N");
        var broadPlainToken = Guid.NewGuid().ToString("N");
        Guid restrictedTokenId;
        Guid itemAId;
        Guid itemBId;
        Guid sharedId;
        Guid userId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var administrator = await db.TenantUsers.AsNoTracking().SingleAsync(x => x.Username == "contract-test-admin");
            var now = DateTimeOffset.UtcNow;
            var member = new TenantUser
            {
                TenantId = administrator.TenantId,
                Username = marker,
                DisplayName = marker,
                Role = TenantUserRole.Member,
                Status = TenantUserStatus.Active,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.TenantUsers.Add(member);
            await db.SaveChangesAsync();
            var restrictedToken = NewToken(restrictedPlainToken, [projectA.ToUpperInvariant()]);
            restrictedTokenId = restrictedToken.Id;
            db.ApiTokens.AddRange(restrictedToken, NewToken(broadPlainToken, []));
            var itemA = NewItem(projectA, member.Id);
            var itemB = NewItem(projectB, member.Id);
            var shared = NewItem(ProjectContext.SharedProjectId, member.Id);
            var user = NewItem(ProjectContext.UserProjectId, member.Id);
            itemAId = itemA.Id;
            itemBId = itemB.Id;
            sharedId = shared.Id;
            userId = user.Id;
            db.MemoryItems.AddRange(itemA, itemB, shared, user, NewItem(projectA, administrator.Id));
            await db.SaveChangesAsync();

            ApiToken NewToken(string plainToken, string[] projects) => new()
            {
                TenantId = member.TenantId,
                OwnerUserId = member.Id,
                Name = marker,
                TokenHash = TenantSecurityService.HashToken(plainToken),
                TokenPrefix = "fixture",
                TokenLastFour = plainToken[^4..],
                Scopes = [SecurityScopes.MemoryRead],
                AllowedProjectIds = projects,
                ExpiresAt = now.AddMinutes(10),
                CreatedAt = now,
                UpdatedAt = now
            };

            MemoryItem NewItem(string projectId, Guid ownerId) => new()
            {
                TenantId = member.TenantId,
                OwnerUserId = ownerId,
                ProjectId = projectId,
                ExternalKey = Guid.NewGuid().ToString("N"),
                Title = marker,
                Summary = marker,
                Content = marker,
                Scope = MemoryScope.Project,
                MemoryType = MemoryType.Fact,
                Status = MemoryStatus.Active,
                CreatedAt = now,
                UpdatedAt = now
            };
        }

        using var broadClient = factory.CreateClient();
        broadClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", broadPlainToken);
        var catalogUrl = $"/api/memories?query={marker}&pageSize=100";
        var broad = await broadClient.GetFromJsonAsync<PagedResult<MemoryListItemResult>>(catalogUrl);
        broad!.Items.Select(x => x.Id).Should().BeEquivalentTo([itemAId, itemBId, sharedId, userId]);
        using var broadDetail = await broadClient.GetAsync($"/api/memories/{itemBId}/details");
        broadDetail.StatusCode.Should().Be(HttpStatusCode.OK);

        using var restrictedClient = factory.CreateClient();
        restrictedClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", restrictedPlainToken);
        var cacheTelemetry = factory.Services.GetRequiredService<IRedisCacheTelemetry>();
        var catalogHitsBefore = cacheTelemetry.GetSnapshot().Kinds.GetValueOrDefault("dashboard-memories")?.Hits ?? 0;
        var detailHitsBefore = cacheTelemetry.GetSnapshot().Kinds.GetValueOrDefault("dashboard-memory-details")?.Hits ?? 0;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var catalog = await restrictedClient.GetFromJsonAsync<PagedResult<MemoryListItemResult>>(catalogUrl);
            catalog!.Items.Select(x => x.Id).Should().BeEquivalentTo([itemAId, sharedId, userId]);
            using var allowedDetail = await restrictedClient.GetAsync($"/api/memories/{itemAId}/details");
            allowedDetail.StatusCode.Should().Be(HttpStatusCode.OK);
            foreach (var deniedUrl in new[]
            {
                $"/api/memories?projectId={projectB}",
                $"/api/memories?projectId={projectA}&includedProjectIds={projectB}&queryMode=CurrentPlusReferencedProjects",
                $"/api/memories/{itemBId}/details"
            })
            {
                using var denied = await restrictedClient.GetAsync(deniedUrl);
                denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            }
        }
        (cacheTelemetry.GetSnapshot().Kinds["dashboard-memories"].Hits - catalogHitsBefore).Should().BeGreaterThanOrEqualTo(1);
        (cacheTelemetry.GetSnapshot().Kinds["dashboard-memory-details"].Hits - detailHitsBefore).Should().BeGreaterThanOrEqualTo(1);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var token = await db.ApiTokens.SingleAsync(x => x.Id == restrictedTokenId);
            token.AllowedProjectIds = [projectB];
            token.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        using var revokedDetail = await restrictedClient.GetAsync($"/api/memories/{itemAId}/details");
        revokedDetail.StatusCode.Should().Be(HttpStatusCode.Forbidden, "the previously warm detail must re-check current project grants");
        var afterGrantChange = await restrictedClient.GetFromJsonAsync<PagedResult<MemoryListItemResult>>(catalogUrl);
        afterGrantChange!.Items.Select(x => x.Id).Should().BeEquivalentTo([itemBId, sharedId, userId]);
    }

    [DockerRequiredFact]
    public async Task Detail_query_should_not_return_or_cache_an_item_moved_after_the_project_precheck()
    {
        var factory = environment.GetFactory();
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<MemoryDbContext>();
        var owner = await db.TenantUsers.AsNoTracking().SingleAsync(x => x.Username == "contract-test-admin");
        var marker = $"detail-move-{Guid.NewGuid():N}";
        var projectA = marker + "-a";
        var projectB = marker + "-b";
        var item = new MemoryItem
        {
            TenantId = owner.TenantId,
            OwnerUserId = owner.Id,
            ProjectId = projectA,
            ExternalKey = marker,
            Title = marker,
            Summary = marker,
            Content = marker,
            Scope = MemoryScope.Project,
            MemoryType = MemoryType.Fact,
            Status = MemoryStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.MemoryItems.Add(item);
        await db.SaveChangesAsync();
        var actorAccessor = services.GetRequiredService<IRequestActorAccessor>();
        actorAccessor.Current = new(owner.TenantId, owner.Id, owner.Username, TenantUserRole.Member,
            [SecurityScopes.MemoryRead], [projectA], true);
        var cache = new MoveBeforeLookupCache(async () =>
        {
            using var movingScope = factory.Services.CreateScope();
            var movingDb = movingScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            await movingDb.Database.ExecuteSqlInterpolatedAsync($"UPDATE memory_items SET project_id = {projectB}, content = 'moved-project-content' WHERE id = {item.Id}");
        });
        var dashboard = new DashboardQueryService(db, services.GetRequiredService<IStorageExplorerStore>(),
            services.GetRequiredService<IDashboardSnapshotStore>(), services.GetRequiredService<IMemoryService>(),
            services.GetRequiredService<ICacheVersionStore>(), cache, services.GetRequiredService<TimeProvider>(), actorAccessor);

        (await dashboard.GetMemoryDetailsAsync(item.Id, CancellationToken.None)).Should().BeNull();
        cache.SetCalls.Should().Be(0, "the denied destination must not be published under the original project revision");
        var retry = () => dashboard.GetMemoryDetailsAsync(item.Id, CancellationToken.None);
        await retry.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [DockerRequiredFact]
    public async Task Administrator_should_receive_typed_windows_and_graph_status_and_reject_unsupported_periods()
    {
        using var client = environment.GetFactory().CreateClient();
        foreach (var period in new[] { "24H", "3D", "7D", "14D", "30D" })
        {
            using var response = await client.GetAsync($"/api/dashboard/cache-metrics?period={period}");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            payload.RootElement.GetProperty("supported").GetBoolean().Should().BeTrue();
            payload.RootElement.GetProperty("period").GetString().Should().Be(period);
            payload.RootElement.GetProperty("series").ValueKind.Should().Be(JsonValueKind.Array);
            payload.RootElement.GetProperty("instances").ValueKind.Should().Be(JsonValueKind.Array);
            var start = payload.RootElement.GetProperty("startedAtUtc").GetDateTimeOffset();
            var end = payload.RootElement.GetProperty("endedAtUtc").GetDateTimeOffset();
            start.Should().BeBefore(end);
            end.Second.Should().Be(0);
            payload.RootElement.GetProperty("coverageStatus").GetString().Should().NotBeNullOrWhiteSpace();
        }

        using var invalid = await client.GetAsync("/api/dashboard/cache-metrics?period=2H");
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var invalidPayload = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync());
        invalidPayload.RootElement.GetProperty("errors").TryGetProperty("period", out _).Should().BeTrue();

        using var graphResponse = await client.GetAsync("/api/dashboard/graph-refresh");
        graphResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var graph = await graphResponse.Content.ReadFromJsonAsync<DashboardGraphRefreshStatus>();
        graph.Should().NotBeNull();
        graph!.Scope.Should().Be("global");
        graph.Generation.Should().BeGreaterThanOrEqualTo(0);
    }

    [DockerRequiredFact]
    public async Task Anonymous_requests_should_be_rejected_on_both_monitoring_endpoints()
    {
        using var client = environment.GetFactory().CreateClient();
        client.DefaultRequestHeaders.Authorization = null;
        foreach (var path in new[] { "/api/dashboard/cache-metrics?period=24H", "/api/dashboard/graph-refresh" })
        {
            using var response = await client.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
    }

    [DockerRequiredFact]
    public async Task Authenticated_member_token_should_be_forbidden_without_changing_bootstrap_administrator()
    {
        var factory = environment.GetFactory();
        var plainToken = $"cache-monitoring-fixture-{Guid.NewGuid():N}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var administrator = await db.TenantUsers.AsNoTracking().SingleAsync(x => x.Username == "contract-test-admin");
            var now = DateTimeOffset.UtcNow;
            var member = new TenantUser
            {
                TenantId = administrator.TenantId,
                Username = $"cache-member-{Guid.NewGuid():N}",
                DisplayName = "Cache monitoring contract fixture",
                Role = TenantUserRole.Member,
                Status = TenantUserStatus.Active,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.TenantUsers.Add(member);
            await db.SaveChangesAsync();
            db.ApiTokens.Add(new ApiToken
            {
                TenantId = member.TenantId,
                OwnerUserId = member.Id,
                Name = "Cache monitoring contract fixture",
                TokenHash = TenantSecurityService.HashToken(plainToken),
                TokenPrefix = "fixture",
                TokenLastFour = plainToken[^4..],
                Scopes = [SecurityScopes.MemoryRead],
                AllowedProjectIds = ["ContextHub"],
                ExpiresAt = now.AddMinutes(10),
                CreatedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", plainToken);
        using var identity = await client.GetAsync("/api/me");
        identity.StatusCode.Should().Be(HttpStatusCode.OK, "the Member token must authenticate before the administrator policy is tested");
        foreach (var path in new[] { "/api/dashboard/cache-metrics?period=24H", "/api/dashboard/graph-refresh" })
        {
            using var response = await client.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        using var adminClient = factory.CreateClient();
        using var adminResponse = await adminClient.GetAsync("/api/dashboard/cache-metrics?period=24H");
        adminResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed class MoveBeforeLookupCache(Func<Task> move) : IRedisObjectCache
    {
        public int SetCalls { get; private set; }

        public async Task<RedisCacheLookup<T>> GetAsync<T>(string key, string kind, CancellationToken cancellationToken)
        {
            await move();
            return new(false, default);
        }

        public Task SetAsync<T>(string key, string kind, T value, TimeSpan ttl, CancellationToken cancellationToken)
        {
            SetCalls++;
            return Task.CompletedTask;
        }
    }
}
