using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memory.IntegrationTests;

public sealed class MemoryManifestNetworkTimeoutTests(ContainerTestEnvironment environment)
    : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Socket_response_timeout_after_freeze_and_continuation_preserves_one_complete_snapshot()
    {
        var factory = environment.GetFactory();
        var project = "manifest-network-" + Guid.NewGuid().ToString("N");
        Guid[] expectedIds;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var user = await db.TenantUsers.SingleAsync(x => x.Username == "contract-test-admin");
            var now = DateTimeOffset.UtcNow;
            var rows = Enumerable.Range(0, 213).Select(i => new MemoryItem
            {
                TenantId = user.TenantId,
                OwnerUserId = user.Id,
                ProjectId = project,
                ExternalKey = "network-" + i,
                Content = "stopped-body-not-for-manifest-read",
                MemoryType = MemoryType.Fact,
                Scope = MemoryScope.Project,
                Status = MemoryStatus.Active,
                Version = i + 1,
                CreatedAt = now,
                UpdatedAt = now
            }).ToArray();
            db.MemoryItems.AddRange(rows);
            await db.SaveChangesAsync();
            expectedIds = rows.Select(x => x.Id).Order().ToArray();
        }

        using var upstream = factory.CreateClient();
        var request = new MemoryManifestReadRequest(project, Guid.NewGuid(), PageSize: 71, AllAuthorizedProjects: false);
        var first = await TimeoutThenReplay(upstream, request);
        first.EndOfSnapshot.Should().BeFalse();
        first.CountInvariantSatisfied.Should().BeTrue();
        var second = await TimeoutThenReplay(upstream, request with { SnapshotId = first.SnapshotId, Cursor = first.NextCursor });
        second.SnapshotId.Should().Be(first.SnapshotId);
        var pages = new List<MemoryManifestPage> { first, second };
        while (!pages[^1].EndOfSnapshot)
        {
            using var response = await upstream.PostAsJsonAsync("/api/memories/manifest/read",
                request with { SnapshotId = first.SnapshotId, Cursor = pages[^1].NextCursor });
            response.EnsureSuccessStatusCode();
            pages.Add((await response.Content.ReadFromJsonAsync<MemoryManifestPage>())!);
        }
        var actualIds = pages.SelectMany(x => x.Items).Select(x => x.MemoryId).ToArray();
        actualIds.Distinct().Count().Should().Be(first.AuthorizedTotal);
        actualIds.Should().Equal(expectedIds);
        pages.All(x => x.SnapshotId == first.SnapshotId && x.CountInvariantSatisfied).Should().BeTrue();
        using var finalScope = factory.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var requestIdentity = request.RequestId.ToString("N");
        var saved = await finalDb.KnowledgeGovernanceSnapshots.AsNoTracking()
            .Where(x => x.GovernanceRunId.EndsWith(requestIdentity)).Select(x => x.Id).ToListAsync();
        saved.Count.Should().Be(1);
        saved[0].Should().Be(first.SnapshotId);
        finalDb.MemoryItems.Where(x => x.ProjectId == project).Count().Should().Be(213);
    }

    private static async Task<MemoryManifestPage> TimeoutThenReplay(HttpClient upstream, MemoryManifestReadRequest request)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var proxy = builder.Build();
        var forwarded = new TaskCompletionSource<MemoryManifestPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRequest = 0;
        proxy.Run(async context =>
        {
            using var body = new StreamContent(context.Request.Body);
            body.Headers.ContentType = new("application/json");
            using var result = await upstream.PostAsync("/api/memories/manifest/read", body);
            result.EnsureSuccessStatusCode();
            var bytes = await result.Content.ReadAsByteArrayAsync();
            if (Interlocked.Increment(ref firstRequest) == 1)
            {
                var completed = JsonSerializer.Deserialize<MemoryManifestPage>(bytes, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                forwarded.TrySetResult(completed);
                // The real app has completed; only the downstream socket response is withheld.
                await release.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
            context.Response.StatusCode = (int)result.StatusCode;
            context.Response.ContentType = "application/json";
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
        });
        await proxy.StartAsync();
        var addresses = proxy.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        using var downstream = new HttpClient { BaseAddress = new Uri(addresses.Addresses.Single()), Timeout = Timeout.InfiniteTimeSpan };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pending = downstream.PostAsJsonAsync("/api/memories/manifest/read", request, deadline.Token);
        try
        {
            var completed = await forwarded.Task.WaitAsync(TimeSpan.FromSeconds(15));
            // Start the timeout after upstream completion so this proves a lost response, not a cancelled freeze.
            deadline.CancelAfter(TimeSpan.FromMilliseconds(100));
            var awaitResponse = async () => { using var response = await pending; };
            await awaitResponse.Should().ThrowAsync<OperationCanceledException>();
            release.TrySetResult();
            using var replay = await downstream.PostAsJsonAsync("/api/memories/manifest/read", request);
            replay.EnsureSuccessStatusCode();
            var page = (await replay.Content.ReadFromJsonAsync<MemoryManifestPage>())!;
            // A boolean assertion prevents opaque signing/cursor state from being printed on failure.
            (JsonSerializer.Serialize(page) == JsonSerializer.Serialize(completed)).Should().BeTrue();
            return page;
        }
        finally
        {
            deadline.Cancel();
            release.TrySetResult();
            try { using var abandoned = await pending; }
            catch (OperationCanceledException) { }
            await proxy.StopAsync();
        }
    }
}
