using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Memory.IntegrationTests;

public sealed class AuthorityAwarePreferenceCacheIntegrationTests(ContainerTestEnvironment environment)
    : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Working_context_preferences_should_cache_per_actor_and_invalidate_after_archiving_stale_authority()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var services = scope.ServiceProvider;
        var bootstrapActor = UseBootstrapActor(services);
        var actorAccessor = services.GetRequiredService<IRequestActorAccessor>();
        var db = services.GetRequiredService<MemoryDbContext>();
        var memoryService = services.GetRequiredService<IMemoryService>();
        var cacheStore = services.GetRequiredService<ICacheVersionStore>();
        var cacheTelemetry = services.GetRequiredService<IRedisCacheTelemetry>();

        var owner = new TenantUser
        {
            TenantId = bootstrapActor.TenantId!.Value,
            Username = $"preference-cache-{Guid.NewGuid():N}"[..28],
            DisplayName = "Preference cache owner",
            Role = TenantUserRole.Member,
            Status = TenantUserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.TenantUsers.Add(owner);
        await db.SaveChangesAsync(CancellationToken.None);

        actorAccessor.Current = bootstrapActor with
        {
            UserId = owner.Id,
            Username = owner.Username,
            Role = owner.Role
        };
        var actor = actorAccessor.Current;

        var authorityKey = $"preference-cache-authority-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var superseded = CreatePreference(
            owner,
            $"superseded-{Guid.NewGuid():N}",
            authorityKey,
            importance: 0.99m,
            updatedAt: now,
            authorityState: MemoryAuthorityState.Superseded);
        var current = CreatePreference(
            owner,
            $"current-{Guid.NewGuid():N}",
            authorityKey,
            importance: 0.10m,
            updatedAt: now.AddMinutes(-1),
            authorityState: MemoryAuthorityState.Current);
        db.MemoryItems.AddRange(superseded, current);
        await db.SaveChangesAsync(CancellationToken.None);

        superseded.SupersededById = current.Id;
        current.SupersedesId = superseded.Id;
        await db.SaveChangesAsync(CancellationToken.None);

        var request = new WorkingContextRequest(
            Query: $"preference-cache-no-index-hit-{Guid.NewGuid():N}",
            Limit: 1,
            RecentLogLimit: 1,
            ProjectId: ProjectContext.DefaultProjectId,
            QueryMode: MemoryQueryMode.CurrentOnly,
            UseSummaryLayer: false);
        var initialTelemetry = GetCacheKind(cacheTelemetry, "working-context-final");

        var first = await memoryService.BuildWorkingContextAsync(request, CancellationToken.None);
        var afterFirstTelemetry = GetCacheKind(cacheTelemetry, "working-context-final");

        first.UserPreferences.Should().Contain(x => x.Id == current.Id);
        first.UserPreferences[0].Id.Should().Be(current.Id,
            "the Current preference must outrank the high-score Superseded predecessor");
        first.UserPreferences.Should().NotContain(x => x.Status == MemoryStatus.Archived);
        afterFirstTelemetry.Misses.Should().Be(initialTelemetry.Misses + 1);
        afterFirstTelemetry.Sets.Should().Be(initialTelemetry.Sets + 1);

        var second = await memoryService.BuildWorkingContextAsync(request, CancellationToken.None);
        var afterSecondTelemetry = GetCacheKind(cacheTelemetry, "working-context-final");

        second.UserPreferences[0].Id.Should().Be(current.Id);
        afterSecondTelemetry.Hits.Should().Be(afterFirstTelemetry.Hits + 1);
        afterSecondTelemetry.Misses.Should().Be(afterFirstTelemetry.Misses);
        afterSecondTelemetry.Sets.Should().Be(afterFirstTelemetry.Sets);

        var versionBeforeArchive = await cacheStore.GetVersionStampAsync(
            [ProjectContext.DefaultProjectId],
            actor,
            includeShared: false,
            CancellationToken.None);

        var archived = await memoryService.ArchiveUserPreferenceAsync(
            new UserPreferenceArchiveRequest(superseded.Id, Archived: true),
            CancellationToken.None);
        archived.Status.Should().Be(MemoryStatus.Archived);

        var versionAfterArchive = await cacheStore.GetVersionStampAsync(
            [ProjectContext.DefaultProjectId],
            actor,
            includeShared: false,
            CancellationToken.None);
        versionAfterArchive.UserVersion.Should().BeGreaterThan(versionBeforeArchive.UserVersion);
        versionAfterArchive.Value.Should().NotBe(versionBeforeArchive.Value);

        var third = await memoryService.BuildWorkingContextAsync(request, CancellationToken.None);
        var afterArchiveTelemetry = GetCacheKind(cacheTelemetry, "working-context-final");

        third.UserPreferences.Should().ContainSingle(x => x.Id == current.Id);
        third.UserPreferences.Should().NotContain(x => x.Id == superseded.Id);
        third.UserPreferences[0].Status.Should().Be(MemoryStatus.Active);
        afterArchiveTelemetry.Misses.Should().Be(afterSecondTelemetry.Misses + 1,
            "the changed actor version must address a new final-context cache key");
        afterArchiveTelemetry.Sets.Should().Be(afterSecondTelemetry.Sets + 1);
    }

    private static RedisCacheKindTelemetry GetCacheKind(IRedisCacheTelemetry telemetry, string kind)
        => telemetry.GetSnapshot().Kinds.TryGetValue(kind, out var counters)
            ? counters
            : new RedisCacheKindTelemetry(0, 0, 0, 0, 0);

    private static ContextHubRequestActor UseBootstrapActor(IServiceProvider services)
    {
        var db = services.GetRequiredService<MemoryDbContext>();
        var user = db.TenantUsers
            .Include(x => x.Tenant)
            .Single(x => x.Username == "contract-test-admin");
        var actor = new ContextHubRequestActor(
            user.TenantId,
            user.Id,
            user.Username,
            user.Role,
            [
                SecurityScopes.MemoryRead,
                SecurityScopes.MemoryWrite,
                SecurityScopes.PreferencesRead,
                SecurityScopes.PreferencesWrite
            ],
            [],
            IsAuthenticated: true);
        services.GetRequiredService<IRequestActorAccessor>().Current = actor;
        return actor;
    }

    private static MemoryItem CreatePreference(
        TenantUser owner,
        string key,
        string authorityKey,
        decimal importance,
        DateTimeOffset updatedAt,
        MemoryAuthorityState authorityState)
        => new()
        {
            TenantId = owner.TenantId,
            OwnerUserId = owner.Id,
            ProjectId = ProjectContext.UserProjectId,
            ExternalKey = $"user-preference:{key}",
            Scope = MemoryScope.User,
            MemoryType = MemoryType.Preference,
            Title = key,
            Content = $"Preference content for {key}.",
            Summary = key,
            Tags = ["user-preference", nameof(UserPreferenceKind.EngineeringPrinciple)],
            SourceType = "user-preference",
            SourceRef = key,
            Importance = importance,
            Confidence = importance,
            AuthorityState = authorityState,
            Status = MemoryStatus.Active,
            MetadataJson = $$"""{"kind":"EngineeringPrinciple","rationale":"Authority-aware cache integration fixture.","authorityKey":"{{authorityKey}}"}""",
            CreatedAt = updatedAt,
            UpdatedAt = updatedAt
        };
}
