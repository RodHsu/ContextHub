using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Memory.IntegrationTests;

public sealed class BackgroundReindexIdentityTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Project_wide_alias_reindex_changes_only_the_job_owner_and_tenant()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<MemoryDbContext>();
        var admin = await db.TenantUsers.SingleAsync(user => user.Username == "contract-test-admin");
        var otherTenant = new Tenant { Slug = "reindex-" + Guid.NewGuid().ToString("N"), DisplayName = "Reindex isolation" };
        var otherOwner = new TenantUser { TenantId = admin.TenantId, Username = "reindex-owner-" + Guid.NewGuid().ToString("N") };
        var foreignOwner = new TenantUser { TenantId = otherTenant.Id, Username = "reindex-foreign-" + Guid.NewGuid().ToString("N") };
        db.Tenants.Add(otherTenant);
        db.TenantUsers.AddRange(otherOwner, foreignOwner);
        await db.SaveChangesAsync();
        var suffix = "-" + Guid.NewGuid().ToString("N");
        var owners = new[] { admin, otherOwner, foreignOwner };
        var projects = new[] { "Tt", "TT", "tt" };
        var chunks = new List<MemoryItemChunk>();
        for (var index = 0; index < owners.Length; index++)
        {
            var item = new MemoryItem
            {
                TenantId = owners[index].TenantId,
                OwnerUserId = owners[index].Id,
                ProjectId = projects[index] + suffix,
                ExternalKey = "reindex-fixture",
                Title = "Owned fixture",
                Content = "Deterministic reindex scope fixture",
                Summary = "Owned fixture",
                Status = MemoryStatus.Active
            };
            var chunk = new MemoryItemChunk { MemoryItem = item, ChunkText = item.Content, ChunkKind = ChunkKind.Document };
            db.MemoryItems.Add(item);
            db.MemoryItemChunks.Add(chunk);
            chunks.Add(chunk);
        }
        await db.SaveChangesAsync();
        var provider = services.GetRequiredService<IEmbeddingProvider>();
        var vectors = services.GetRequiredService<IVectorStore>();
        foreach (var chunk in chunks)
            await vectors.ReplaceChunkVectorAsync(chunk.Id, await provider.EmbedAsync(chunk.ChunkText, EmbeddingPurpose.Document, default), default);
        var foreignIds = chunks.Skip(1).Select(chunk => chunk.Id).ToArray();
        var foreignBefore = await Snapshot(services, foreignIds);
        var processor = services.GetRequiredService<IBackgroundJobProcessor>();
        foreach (var alias in new[] { "TT", "Tt", "tT", "tt" })
        {
            var job = CreateJob(admin, alias + suffix, provider.ModelKey);
            db.MemoryJobs.Add(job);
            await db.SaveChangesAsync();
            var result = await processor.ProcessNextAsync(default);
            result.Should().NotBeNull();
            result!.Id.Should().Be(job.Id);
            var persisted = await db.MemoryJobs.AsNoTracking().SingleAsync(row => row.Id == job.Id);
            persisted.Status.Should().Be(MemoryJobStatus.Completed);
            (await Snapshot(services, foreignIds)).Should().Equal(foreignBefore);
            (await db.MemoryChunkVectors.CountAsync(vector => vector.ChunkId == chunks[0].Id && vector.Status == "Active"))
                .Should().Be(1);
        }
        (await db.MemoryChunkVectors.CountAsync(vector => vector.ChunkId == chunks[0].Id && vector.Status == "Superseded"))
            .Should().Be(4);
    }

    [DockerRequiredFact]
    public async Task Persisted_reindex_for_a_different_provider_fails_before_any_vector_write()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<MemoryDbContext>();
        var admin = await db.TenantUsers.SingleAsync(user => user.Username == "contract-test-admin");
        var item = new MemoryItem
        {
            TenantId = admin.TenantId,
            OwnerUserId = admin.Id,
            ProjectId = "reindex-stale-" + Guid.NewGuid().ToString("N"),
            ExternalKey = "stale-model",
            Title = "Stale model fixture",
            Content = "Owned existing vector",
            Status = MemoryStatus.Active
        };
        var chunk = new MemoryItemChunk { MemoryItem = item, ChunkText = item.Content, ChunkKind = ChunkKind.Document };
        db.MemoryItemChunks.Add(chunk);
        await db.SaveChangesAsync();
        var provider = services.GetRequiredService<IEmbeddingProvider>();
        await services.GetRequiredService<IVectorStore>().ReplaceChunkVectorAsync(chunk.Id,
            await provider.EmbedAsync(chunk.ChunkText, EmbeddingPurpose.Document, default), default);
        var before = await Snapshot(services, [chunk.Id]);
        var job = CreateJob(admin, item.ProjectId, provider.ModelKey + "-another-generation");
        db.MemoryJobs.Add(job);
        await db.SaveChangesAsync();
        var result = await services.GetRequiredService<IBackgroundJobProcessor>().ProcessNextAsync(default);
        result.Should().NotBeNull();
        result!.Id.Should().Be(job.Id);
        var persisted = await db.MemoryJobs.AsNoTracking().SingleAsync(row => row.Id == job.Id);
        persisted.Status.Should().Be(MemoryJobStatus.Failed);
        persisted.Error.Should().Contain("configured embedding provider");
        (await Snapshot(services, [chunk.Id])).Should().Equal(before);
    }

    private static MemoryJob CreateJob(TenantUser owner, string projectId, string modelKey) => new()
    {
        TenantId = owner.TenantId,
        OwnerUserId = owner.Id,
        ProjectId = projectId,
        JobType = MemoryJobType.Reindex,
        Status = MemoryJobStatus.Pending,
        PayloadJson = JsonSerializer.Serialize(new { ModelKey = modelKey, MemoryItemId = (Guid?)null, ProjectId = projectId }),
        CreatedAt = DateTimeOffset.UtcNow.AddDays(-1)
    };

    private static async Task<string[]> Snapshot(IServiceProvider services, Guid[] chunkIds)
    {
        await using var command = services.GetRequiredService<NpgsqlDataSource>().CreateCommand("""
            SELECT id::text, chunk_id::text, model_key, dimension::text, status, embedding::text, created_at::text
            FROM memory_chunk_vectors WHERE chunk_id = ANY(@ids) ORDER BY id
            """);
        command.Parameters.AddWithValue("ids", chunkIds);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            var fields = Enumerable.Range(0, reader.FieldCount).Select(reader.GetString).ToArray();
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fields)));
            rows.Add(fields[0] + ":" + Convert.ToHexString(digest));
        }
        return rows.ToArray();
    }
}
