using FluentAssertions;
using Memory.Application;
using Memory.Domain;

namespace Memory.UnitTests;

public sealed class DashboardGraphRefreshServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Global_build_should_use_service_actor_and_restore_caller_even_on_failure(bool fail)
    {
        var caller = new ContextHubRequestActor(Guid.NewGuid(), Guid.NewGuid(), "caller", TenantUserRole.Admin,
            [SecurityScopes.MemoryRead], ["one-project"], true);
        var actor = new RequestActorAccessor { Current = caller };
        var builder = new ActorCheckingBuilder(actor, fail);
        var store = new SnapshotStore();
        var service = new DashboardMemoryGraphIndexRefreshService(builder, store, null!, TimeProvider.System,
            new TestDashboardGraphRefreshCoordinator(store), actor);
        if (fail)
        {
            await service.Invoking(value => value.RefreshAsync("manual", 15, default)).Should().ThrowAsync<InvalidOperationException>();
        }
        else
        {
            await service.RefreshAsync("manual", 15, default);
        }
        builder.Calls.Should().Be(1);
        actor.Current.Should().BeSameAs(caller);
    }

    [Fact]
    public async Task Quiet_or_competing_refresh_should_not_invoke_builder()
    {
        var actor = new RequestActorAccessor();
        var builder = new ActorCheckingBuilder(actor, false);
        var store = new SnapshotStore();
        var service = new DashboardMemoryGraphIndexRefreshService(builder, store, null!, TimeProvider.System,
            new TestDashboardGraphRefreshCoordinator(store) { Skip = true }, actor);
        for (var i = 0; i < 4; i++) await service.RefreshAsync("scheduled", 15, default);
        builder.Calls.Should().Be(0);
    }

    private sealed class ActorCheckingBuilder(IRequestActorAccessor actor, bool fail) : IDashboardMemoryGraphIndexBuilder
    {
        public int Calls { get; private set; }
        public Task<DashboardMemoryGraphIndexSnapshotPayload> BuildAsync(CancellationToken cancellationToken)
        {
            Calls++;
            actor.Current.IsServiceActor.Should().BeTrue();
            actor.Current.IsAuthenticated.Should().BeTrue();
            actor.Current.TenantId.Should().BeNull();
            actor.Current.UserId.Should().BeNull();
            actor.Current.Scopes.Should().Equal(SecurityScopes.MemoryRead);
            if (fail) throw new InvalidOperationException("fixture");
            return Task.FromResult(new DashboardMemoryGraphIndexSnapshotPayload(new([], [], new(0, 0, 0, false)), []));
        }
    }
    private sealed class SnapshotStore : IDashboardSnapshotStore
    {
        private object? _snapshot;
        public Task<DashboardSnapshotEnvelope<TPayload>?> GetAsync<TPayload>(string key, CancellationToken cancellationToken) => Task.FromResult(_snapshot as DashboardSnapshotEnvelope<TPayload>);
        public Task SetAsync<TPayload>(DashboardSnapshotEnvelope<TPayload> envelope, CancellationToken cancellationToken)
        {
            _snapshot = envelope;
            return Task.CompletedTask;
        }
    }
}
