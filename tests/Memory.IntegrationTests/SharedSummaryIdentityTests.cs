using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Memory.IntegrationTests;

public sealed class SharedSummaryIdentityTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Refreshing_project_aliases_updates_one_legacy_summary_without_rewriting_its_reference()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<MemoryDbContext>();
        var owner = await db.TenantUsers.SingleAsync(x => x.Username == "contract-test-admin");
        services.GetRequiredService<IRequestActorAccessor>().Current = new ContextHubRequestActor(
            owner.TenantId, owner.Id, owner.Username, owner.Role,
            [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite], [], true);
        var suffix = "-" + Guid.NewGuid().ToString("N");
        var originalProject = "Tt" + suffix;
        var summary = new MemoryItem
        {
            ProjectId = ProjectContext.SharedProjectId,
            ExternalKey = "shared-summary:" + originalProject,
            Scope = MemoryScope.Project,
            MemoryType = MemoryType.Summary,
            Title = "Legacy summary spelling control",
            Content = "Legacy summary content",
            Summary = "Legacy summary",
            SourceType = "summary-layer",
            SourceRef = originalProject,
            IsReadOnly = true,
            Status = MemoryStatus.Active,
            Version = 1
        };
        var ownedSummary = new MemoryItem
        {
            TenantId = owner.TenantId,
            OwnerUserId = owner.Id,
            ProjectId = ProjectContext.SharedProjectId,
            ExternalKey = "shared-summary:" + originalProject,
            MemoryType = MemoryType.Summary,
            SourceType = "summary-layer",
            SourceRef = originalProject,
            Content = "Owner authority control",
            Version = 7
        };
        var manualSummary = new MemoryItem
        {
            ProjectId = ProjectContext.SharedProjectId,
            ExternalKey = "shared-summary:" + originalProject,
            MemoryType = MemoryType.Summary,
            SourceType = "manual",
            SourceRef = originalProject,
            Content = "Manual authority control",
            Version = 8
        };
        db.MemoryItems.AddRange(summary, ownedSummary, manualSummary);
        await db.SaveChangesAsync();
        var service = services.GetRequiredService<IMemoryService>();
        var processor = services.GetRequiredService<IBackgroundJobProcessor>();
        var expectedKeys = new[] { "Tt", "TT", "tT", "tt" }
            .Select(prefix => "shared-summary:" + prefix + suffix).ToArray();
        foreach (var prefix in new[] { "Tt", "TT", "tT", "tt" })
        {
            var job = await service.EnqueueSummaryRefreshAsync(new(prefix + suffix, []), default);
            for (var attempt = 0; attempt < 50; attempt++)
            {
                var processed = await processor.ProcessNextAsync(default);
                if (processed?.Id == job.JobId)
                    break;
            }
            var persisted = await db.MemoryJobs.AsNoTracking().SingleAsync(x => x.Id == job.JobId);
            persisted.Status.Should().Be(MemoryJobStatus.Completed);
            var rows = await db.MemoryItems.AsNoTracking()
                .Where(x => x.ProjectId == ProjectContext.SharedProjectId &&
                    x.TenantId == null && x.OwnerUserId == null && x.SourceType == "summary-layer" &&
                    x.MemoryType == MemoryType.Summary && expectedKeys.Contains(x.ExternalKey))
                .ToListAsync();
            rows.Count.Should().Be(1, "project aliases must reference the same shared summary");
            rows[0].Id.Should().Be(summary.Id);
            rows[0].ExternalKey.Should().Be("shared-summary:" + originalProject);
            rows[0].SourceRef.Should().Be(originalProject);
        }
        var controls = await db.MemoryItems.AsNoTracking()
            .Where(x => x.Id == ownedSummary.Id || x.Id == manualSummary.Id).ToListAsync();
        controls.Single(x => x.Id == ownedSummary.Id).Content.Should().Be("Owner authority control");
        controls.Single(x => x.Id == ownedSummary.Id).Version.Should().Be(7);
        controls.Single(x => x.Id == manualSummary.Id).Content.Should().Be("Manual authority control");
        controls.Single(x => x.Id == manualSummary.Id).Version.Should().Be(8);
    }

    [DockerRequiredFact]
    public async Task Concurrent_generated_summary_alias_inserts_have_one_authority_and_do_not_fold_ordinary_keys()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var suffix = "-" + Guid.NewGuid().ToString("N");
        async Task<bool> InsertAsync(string project, string memoryType, string sourceType)
        {
            await using var command = source.CreateCommand("""
                INSERT INTO memory_items
                    (id, project_id, external_key, scope, memory_type, title, content, summary,
                     source_type, source_ref, status, created_at, updated_at)
                VALUES (@id, 'shared', @key, 'Project', @type, 'Identity fixture', 'Fixture', 'Fixture',
                    @source_type, @project, 'Active', now(), now())
                """);
            command.Parameters.AddWithValue("id", Guid.NewGuid());
            command.Parameters.AddWithValue("key", "shared-summary:" + project);
            command.Parameters.AddWithValue("type", memoryType);
            command.Parameters.AddWithValue("source_type", sourceType);
            command.Parameters.AddWithValue("project", project);
            try
            {
                await command.ExecuteNonQueryAsync();
                return true;
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation &&
                exception.ConstraintName == "pi1_shared_summary_source_project_identity")
            {
                return false;
            }
        }
        var inserted = await Task.WhenAll(new[] { "TT", "Tt", "tT", "tt" }
            .Select(prefix => InsertAsync(prefix + suffix, "Summary", "summary-layer")));
        inserted.Count(value => value).Should().Be(1);
        (await InsertAsync("TT" + suffix, "Fact", "manual")).Should().BeTrue();
        (await InsertAsync("tt" + suffix, "Fact", "manual")).Should().BeTrue();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await db.MemoryItems.CountAsync(x => x.ExternalKey.EndsWith(suffix))).Should().Be(3);
    }
}
