using System.Net.Http.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Memory.IntegrationTests;

public sealed class ProjectionFreshnessAcceptanceTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Operations_must_report_new_unprojected_authority_without_counting_another_project()
    {
        var factory = environment.GetFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var owner = await db.TenantUsers.AsNoTracking().SingleAsync(x => x.Username == "contract-test-admin");
        var project = "freshness-" + Guid.NewGuid().ToString("N");
        var first = Event(project);
        db.AuthorityOutboxEvents.Add(first);
        await db.SaveChangesAsync();
        var state = new MonitoringProjectionState
        {
            ProjectionName = PlatformProjectionContract.ProjectionName,
            TenantScopeKey = owner.TenantId.ToString("D"),
            TenantId = owner.TenantId,
            ProjectId = project,
            Generation = 1,
            AuthoritySequence = first.Sequence,
            Cursor = first.Sequence,
            LastSuccessAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.MonitoringProjectionStates.Add(state);
        await db.SaveChangesAsync();
        using var client = factory.CreateClient();
        var uri = "/api/dashboard/operations?projectId=" + project;
        var current = (await client.GetFromJsonAsync<DashboardOperationsResult>(uri))!.Projections.Single();
        current.IsStale.Should().BeFalse();
        current.Lag.Should().Be(0);
        var pending = Event(project.ToUpperInvariant());
        db.AuthorityOutboxEvents.Add(pending);
        await db.SaveChangesAsync();
        var unrelated = Event(project + "-unrelated");
        db.AuthorityOutboxEvents.Add(unrelated);
        await db.SaveChangesAsync();
        unrelated.Sequence.Should().BeGreaterThan(pending.Sequence);
        var result = (await client.GetFromJsonAsync<DashboardOperationsResult>(uri))!;
        var stale = result.Projections.Single();
        stale.AuthoritySequence.Should().Be(first.Sequence, "the stored projector boundary remains compatible");
        stale.ObservedAuthoritySequence.Should().Be(pending.Sequence);
        stale.Cursor.Should().Be(first.Sequence);
        stale.Lag.Should().Be(pending.Sequence - first.Sequence);
        stale.IsStale.Should().BeTrue();
        result.Authority.LatestSequence.Should().Be(pending.Sequence);

        AuthorityOutboxEvent Event(string target) => new()
        {
            TenantId = owner.TenantId,
            ProjectId = target,
            Category = "Authorization",
            AggregateType = "Fixture",
            AggregateId = Guid.NewGuid().ToString("N"),
            EventType = "Added",
            OccurredAt = DateTimeOffset.UtcNow,
            PayloadJson = "{}"
        };
    }
}
