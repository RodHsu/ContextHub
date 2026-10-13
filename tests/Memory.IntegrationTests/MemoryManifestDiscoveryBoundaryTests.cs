using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Memory.IntegrationTests;

public sealed class MemoryManifestDiscoveryBoundaryTests(ContainerTestEnvironment environment)
    : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Discovery_freezes_project_set_and_member_statement_includes_later_selected_project_inserts()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<MemoryDbContext>();
        var user = await db.TenantUsers.SingleAsync(x => x.Username == "contract-test-admin");
        var actor = new ContextHubRequestActor(user.TenantId, user.Id, user.Username, user.Role,
            [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite], [], true);
        var accessor = services.GetRequiredService<IRequestActorAccessor>();
        accessor.Current = actor;
        var selectedProject = "discovered-" + Guid.NewGuid().ToString("N");
        var futureProject = "later-" + Guid.NewGuid().ToString("N");
        MemoryItem Member(string project) => new()
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = project,
            ExternalKey = Guid.NewGuid().ToString("N")
        };
        var original = Member(selectedProject);
        db.MemoryItems.Add(original);
        await db.SaveChangesAsync();
        var barrier = new DiscoveryBarrier(services.GetRequiredService<IGovernanceProjectScopeResolver>());
        var service = new MemoryManifestService(db, barrier, accessor, services.GetRequiredService<IClock>());
        var request = new MemoryManifestReadRequest(selectedProject, Guid.NewGuid(), AllAuthorizedProjects: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var pending = service.ReadAsync(request, timeout.Token);
        try
        {
            await barrier.Discovered.WaitAsync(timeout.Token);
            barrier.Projects.Should().Contain(selectedProject).And.NotContain(futureProject);
            using var writingScope = environment.GetFactory().Services.CreateScope();
            var writer = writingScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var selectedInsert = Member(selectedProject);
            var futureInsert = Member(futureProject);
            writer.MemoryItems.AddRange(selectedInsert, futureInsert);
            await writer.SaveChangesAsync(timeout.Token);
            barrier.Release();
            var first = await pending;
            var entries = await Entries(service, request, first, timeout.Token);
            entries.Select(x => x.MemoryId).Should().Contain(original.Id).And.Contain(selectedInsert.Id).And.NotContain(futureInsert.Id);
            entries.Select(x => x.ProjectId).Should().NotContain(futureProject);
            var nextRequest = request with { RequestId = Guid.NewGuid() };
            var next = await service.ReadAsync(nextRequest, timeout.Token);
            var nextEntries = await Entries(service, nextRequest, next, timeout.Token);
            nextEntries.Select(x => x.MemoryId).Should().Contain(original.Id).And.Contain(selectedInsert.Id).And.Contain(futureInsert.Id);
            next.AuthorizedTotal.Should().Be(first.AuthorizedTotal + 1);
            next.SnapshotId.Should().NotBe(first.SnapshotId);
        }
        finally
        {
            barrier.Release();
            await timeout.CancelAsync();
            try { await pending; }
            catch (OperationCanceledException) { }
        }
    }

    private static async Task<List<MemoryManifestEntry>> Entries(IMemoryManifestService service,
        MemoryManifestReadRequest request, MemoryManifestPage first, CancellationToken cancellationToken)
    {
        var items = first.Items.ToList();
        var page = first;
        while (!page.EndOfSnapshot && items.Count <= first.AuthorizedTotal)
        {
            page = await service.ReadAsync(request with { SnapshotId = first.SnapshotId, Cursor = page.NextCursor }, cancellationToken);
            page.CountInvariantSatisfied.Should().BeTrue();
            items.AddRange(page.Items);
        }
        page.EndOfSnapshot.Should().BeTrue();
        items.Select(x => x.MemoryId).Distinct().Count().Should().Be(first.AuthorizedTotal);
        items.Count.Should().Be(first.AuthorizedTotal);
        return items;
    }

    private sealed class DiscoveryBarrier(IGovernanceProjectScopeResolver inner) : IGovernanceProjectScopeResolver
    {
        private readonly TaskCompletionSource discovered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int discoveryCount;
        public Task Discovered => discovered.Task;
        public IReadOnlyList<string> Projects { get; private set; } = [];
        public void Release() => release.TrySetResult();
        public async Task<IReadOnlyList<AccessibleProjectResult>> ResolveAsync(IReadOnlyList<string>? requestedProjectIds,
            CancellationToken cancellationToken)
        {
            var result = await inner.ResolveAsync(requestedProjectIds, cancellationToken);
            if (requestedProjectIds is null && Interlocked.Increment(ref discoveryCount) == 1)
            {
                Projects = result.Select(x => x.ProjectId).ToArray();
                discovered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
