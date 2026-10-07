using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Memory.IntegrationTests;

public sealed class ProjectIdentityLogQueryTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Project_alias_log_lookup_preserves_order_authorization_and_selective_hit_and_miss_plans()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var accessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        var admin = await db.TenantUsers.AsNoTracking().SingleAsync(x => x.Username == "contract-test-admin");
        accessor.Current = new ContextHubRequestActor(admin.TenantId, admin.Id, admin.Username, admin.Role,
            [SecurityScopes.LogsRead], [], true);
        var suffix = "-" + Guid.NewGuid().ToString("N");
        var aliases = new[] { "TT", "Tt", "tT", "tt" }.Select(x => x + suffix).ToArray();
        var absent = "absent" + suffix;
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO runtime_log_entries(project_id, service_name, category, level, message, created_at)
            SELECT CASE WHEN n % 50 = 0 THEN
                CASE (n / 50) % 4 WHEN 0 THEN {{aliases[0]}} WHEN 1 THEN {{aliases[1]}}
                    WHEN 2 THEN {{aliases[2]}} ELSE {{aliases[3]}} END
                ELSE 'other-' || {{suffix}} || '-' || (n % 49)::text END,
                'identity-log-fixture', 'fixture', 'Information', 'Owned synthetic log',
                {{start}} + n * interval '1 second'
            FROM generate_series(1, 5000) n
            """);
        await db.Database.ExecuteSqlRawAsync("ANALYZE runtime_log_entries");

        var service = scope.ServiceProvider.GetRequiredService<ILogQueryService>();
        var expectedIds = await db.RuntimeLogEntries
            .Where(x => aliases.Contains(x.ProjectId))
            .OrderByDescending(x => x.CreatedAt)
            .Take(20)
            .Select(x => x.Id)
            .ToListAsync();
        expectedIds.Should().HaveCount(20);
        foreach (var alias in aliases)
        {
            var result = await service.SearchAsync(new(From: start, Limit: 20, ProjectId: alias), default);
            result.Select(x => x.Id).Should().Equal(expectedIds);
            result.Select(x => x.ProjectId).Should().OnlyContain(x => aliases.Contains(x));
        }
        (await service.SearchAsync(new(From: start, Limit: 20, ProjectId: absent), default)).Should().BeEmpty();

        await using var connection = new NpgsqlConnection(environment.PostgresConnectionString!);
        await connection.OpenAsync();
        foreach (var project in new[] { aliases[3], absent })
        {
            await using var command = new NpgsqlCommand("""
                EXPLAIN (FORMAT JSON) SELECT id FROM runtime_log_entries
                WHERE public.project_identity_equals(project_id, @project) AND created_at >= @from
                ORDER BY created_at DESC LIMIT 20
                """, connection);
            command.Parameters.AddWithValue("project", project);
            command.Parameters.AddWithValue("from", start);
            using var plan = JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!);
            UsesIdentityIndex(plan.RootElement[0].GetProperty("Plan")).Should().BeTrue(
                "both an alias hit and a missing project must avoid scanning unrelated projects");
        }

        accessor.Current = accessor.Current with { AllowedProjectIds = [aliases[1]] };
        (await service.SearchAsync(new(From: start, Limit: 20, ProjectId: aliases[2]), default))
            .Select(x => x.Id).Should().Equal(expectedIds);
        var denied = () => service.SearchAsync(new(ProjectId: aliases[0] + "-other"), default);
        await denied.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    private static bool UsesIdentityIndex(JsonElement node)
    {
        if (node.TryGetProperty("Index Name", out var name)
            && name.GetString() == "pi1_runtime_logs_project_created")
        {
            return true;
        }
        return node.TryGetProperty("Plans", out var plans) && plans.EnumerateArray().Any(UsesIdentityIndex);
    }
}
