using System.Text.Json;
using Memory.Application;
using Npgsql;

namespace Memory.Infrastructure;

/// <summary>Cache validity follows committed database state, including when Redis is unavailable or restored.</summary>
public sealed class DurableCacheRevisionStore(NpgsqlDataSource dataSource)
{
    public static string ProjectScope(string projectId)
        => $"project:{ProjectContext.Normalize(projectId)}";

    public static string UserScope(ContextHubRequestActor actor)
        => actor.HasUser ? $"user:{actor.TenantId:N}:{actor.UserId:N}" : ProjectScope(ProjectContext.UserProjectId);

    public async Task<CacheVersionStamp> ReadAsync(
        IReadOnlyList<string> projectIds, ContextHubRequestActor actor, bool includeShared, CancellationToken cancellationToken)
    {
        var projects = projectIds.Select(x => ProjectContext.Normalize(x)).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        var scopes = projects.Select(ProjectScope).Append("global").Append("security").Append(UserScope(actor));
        if (includeShared) scopes = scopes.Append(ProjectScope(ProjectContext.SharedProjectId));
        // A single statement gives every dimension the same committed snapshot. Missing scopes are revision zero.
        await using var command = dataSource.CreateCommand("SELECT scope, revision FROM cache_scope_revisions WHERE scope = ANY(@scopes) OR (@all_projects AND scope LIKE 'project:%') ORDER BY scope");
        command.Parameters.AddWithValue("scopes", scopes.Distinct(StringComparer.Ordinal).ToArray());
        command.Parameters.AddWithValue("all_projects", projects.Length == 0);
        var revisions = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) revisions[reader.GetString(0)] = reader.GetInt64(1);
        // An unfiltered Dashboard catalog depends on all existing scopes and on newly created ones.
        var projectVersions = projects.Length == 0
            ? revisions.Where(x => x.Key.StartsWith("project:", StringComparison.Ordinal)).ToDictionary(x => x.Key[8..], x => x.Value, StringComparer.Ordinal)
            : projects.ToDictionary(x => x, x => revisions.GetValueOrDefault(ProjectScope(x)), StringComparer.Ordinal);
        var global = revisions.GetValueOrDefault("global");
        var security = revisions.GetValueOrDefault("security");
        var shared = includeShared ? revisions.GetValueOrDefault(ProjectScope(ProjectContext.SharedProjectId)) : 0;
        var user = revisions.GetValueOrDefault(UserScope(actor));
        var value = JsonSerializer.Serialize(new
        {
            Contract = "durable-v1",
            Global = global,
            Security = security,
            Shared = shared,
            User = user,
            Projects = projectVersions.Select(x => new { Scope = ProjectScope(x.Key), Revision = x.Value }).ToArray()
        });
        return new(value, global, security, shared, user, projectVersions);
    }

    public async Task<long> ReadScopeAsync(string scope, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT COALESCE((SELECT revision FROM cache_scope_revisions WHERE scope = @scope), 0)");
        command.Parameters.AddWithValue("scope", scope);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<long> IncrementAsync(string scope, CancellationToken cancellationToken, string? projectId = null)
    {
        await using var command = dataSource.CreateCommand("SELECT bump_cache_scope_revision(@scope, @project)");
        command.Parameters.AddWithValue("scope", scope);
        command.Parameters.Add(new NpgsqlParameter("project", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)projectId ?? DBNull.Value });
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}
