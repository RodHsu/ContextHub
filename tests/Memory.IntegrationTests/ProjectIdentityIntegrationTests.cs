using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Memory.IntegrationTests;

public sealed class ProjectIdentityIntegrationTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task PostgreSql_identity_preserves_all_supported_ascii_characters_and_unicode_boundaries()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var samples = Enumerable.Range(1, 127)
            .Select(code => $"Tt-{(char)code}-abc_123")
            .Concat(["", "TT", "Tt", "tT", "tt", " \u2003tt\u3000 ", "\u0085Tt\u00a0",
                "é-Proj", "É-Proj", "Σ", "ς", "\U00010428", "\U00010400", "ß", "SS", "ﬃ", "ffi",
                "Å", "A\u030a", "café", "cafe", "ＴＴ", "TT-1"])
            .ToArray();
        await using var command = source.CreateCommand("""
            SELECT value, public.project_identity_key(value)
            FROM unnest(@samples) WITH ORDINALITY AS inputs(value, ordinal)
            ORDER BY ordinal;
            """);
        command.Parameters.AddWithValue("samples", samples);
        await using var reader = await command.ExecuteReaderAsync();
        var count = 0;
        while (await reader.ReadAsync())
        {
            reader.GetString(0).Should().Be(samples[count]);
            reader.GetString(1).Should().Be(ProjectContext.IdentityKey(samples[count]));
            count++;
        }
        count.Should().Be(samples.Length);
    }

    [DockerRequiredFact]
    public async Task PostgreSql_and_application_identity_contracts_agree_without_noncase_aliases()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var pairs = new[] { ("TT", "tt"), ("Tt", "tT"), (" É-Proj ", "é-proj"), ("Σ", "ς"),
            ("\U00010400", "\U00010428"), ("\u2003TT\u2003", "tt"), ("Å", "A\u030a"),
            ("ff", "ﬀ"), ("café", "cafe"), ("ß", "SS"), ("TT", "TT-1") };
        foreach (var (left, right) in pairs)
        {
            await using var command = source.CreateCommand("SELECT public.project_identity_key(@left), public.project_identity_equals(@left, @right)");
            command.Parameters.AddWithValue("left", left);
            command.Parameters.AddWithValue("right", right);
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetString(0).Should().Be(ProjectContext.IdentityKey(left));
            reader.GetBoolean(1).Should().Be(ProjectContext.Matches(left, right));
        }
    }

    [DockerRequiredFact]
    public async Task Metadata_alias_upserts_preserve_spelling_and_separate_owner_and_tenant_authority()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<MemoryDbContext>();
        var accessor = services.GetRequiredService<IRequestActorAccessor>();
        var admin = await db.TenantUsers.SingleAsync(x => x.Username == "contract-test-admin");
        var actor = new ContextHubRequestActor(admin.TenantId, admin.Id, admin.Username, admin.Role,
            [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite], [], true);
        accessor.Current = actor;
        var service = services.GetRequiredService<IProjectInformationService>();
        var suffix = "-" + Guid.NewGuid().ToString("N");
        var variants = new[] { "TT", "Tt", "tT", "tt" }.Select(x => x + suffix).ToArray();
        var original = await service.UpsertAsync(new(variants[1], null, "Original"), default);
        foreach (var alias in variants)
        {
            var updated = await service.UpsertAsync(new(alias, null, "Updated through alias"), default);
            updated.MemoryId.Should().Be(original.MemoryId);
            updated.ProjectId.Should().Be(variants[1]);
            (await service.GetAsync(alias, default))!.MemoryId.Should().Be(original.MemoryId);
        }
        (await db.MemoryItems.CountAsync(x => x.OwnerUserId == admin.Id && ProjectContext.Matches(x.ProjectId, variants[0])))
            .Should().Be(1);

        var tenant = new Tenant { Slug = "identity-" + Guid.NewGuid().ToString("N"), DisplayName = "Identity isolation" };
        var otherOwner = new TenantUser { TenantId = admin.TenantId, Username = "identity-owner-" + Guid.NewGuid().ToString("N") };
        var foreignOwner = new TenantUser { TenantId = tenant.Id, Username = "identity-foreign-" + Guid.NewGuid().ToString("N") };
        db.Tenants.Add(tenant);
        db.TenantUsers.AddRange(otherOwner, foreignOwner);
        await db.SaveChangesAsync();
        foreach (var owner in new[] { otherOwner, foreignOwner })
        {
            accessor.Current = actor with { TenantId = owner.TenantId, UserId = owner.Id };
            var separate = await service.UpsertAsync(new(variants[3], null, "Separate authority"), default);
            separate.MemoryId.Should().NotBe(original.MemoryId);
            (await service.GetAsync(variants[0], default))!.MemoryId.Should().Be(separate.MemoryId);
        }
        accessor.Current = actor;
        (await service.GetAsync(variants[3], default))!.MemoryId.Should().Be(original.MemoryId);
        accessor.Current = actor with { AllowedProjectIds = ["Unrelated"] };
        var denied = () => service.GetAsync(variants[3], default);
        await denied.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [DockerRequiredFact]
    public async Task Legacy_revision_aliases_and_redis_signals_invalidate_every_case_variant()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<MemoryDbContext>();
        var admin = await db.TenantUsers.SingleAsync(x => x.Username == "contract-test-admin");
        var actor = new ContextHubRequestActor(admin.TenantId, admin.Id, admin.Username, admin.Role,
            [SecurityScopes.MemoryRead], [], true);
        var suffix = "-" + Guid.NewGuid().ToString("N");
        var projects = new[] { "TT", "Tt", "tT", "tt" }.Select(x => x + suffix).ToArray();
        var revisions = services.GetRequiredService<DurableCacheRevisionStore>();
        var versions = services.GetRequiredService<ICacheVersionStore>();
        var before = await revisions.ReadAsync([projects[0]], actor, false, default);
        foreach (var project in projects)
            await revisions.IncrementAsync("project:" + project, default);
        var after = await revisions.ReadAsync([projects[0]], actor, false, default);
        after.ProjectVersions[projects[0]].Should().Be(before.ProjectVersions[projects[0]] + 4);
        foreach (var alias in projects)
        {
            var stamp = await revisions.ReadAsync([alias], actor, false, default);
            stamp.Value.Should().Be(after.Value);
            stamp.ProjectVersions[alias].Should().Be(after.ProjectVersions[projects[0]]);
        }
        var signalBefore = await versions.GetVersionStampAsync([projects[0]], actor, false, default);
        await versions.IncrementProjectAsync(projects[3], default);
        var signalAfter = await versions.GetVersionStampAsync([projects[0]], actor, false, default);
        signalAfter.Value.Should().NotBe(signalBefore.Value);
        foreach (var alias in projects)
            (await versions.GetVersionStampAsync([alias], actor, false, default)).Value.Should().Be(signalAfter.Value);
    }
}
