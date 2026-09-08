using System.Diagnostics;
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

namespace Memory.IntegrationTests;

public sealed class SkillScaleBenchmarkTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Resolver_and_telemetry_should_preserve_top_n_quality_with_2500_persisted_skills()
    {
        const int corpusSize = 2_500;
        const int relevantCount = 25;
        const int topN = 10;
        using var scope = environment.GetFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var embedding = scope.ServiceProvider.GetRequiredService<IEmbeddingProvider>();
        var service = scope.ServiceProvider.GetRequiredService<ISkillService>();
        var now = DateTimeOffset.UtcNow;
        var generation = new SkillSearchGeneration
        {
            TenantId = actor.TenantId,
            SearchProfileVersion = "skills-scale-v1",
            EmbeddingModelId = embedding.ModelKey,
            EmbeddingModelVersion = "1",
            Threshold = 0m,
            Status = SkillSearchGenerationStatus.Active,
            BenchmarkJson = "{\"fixture\":\"2500-skills\"}",
            CreatedAt = now,
            UpdatedAt = now,
            ActivatedAt = now
        };
        db.SkillSearchGenerations.Add(generation);
        await db.SaveChangesAsync();

        var relevantText = "postgres migration rollback production schema database runbook";
        var irrelevantText = "frontend typography color layout illustration asset";
        var relevantVector = await embedding.EmbedAsync(relevantText, EmbeddingPurpose.Document, CancellationToken.None);
        var irrelevantVector = await embedding.EmbedAsync(irrelevantText, EmbeddingPurpose.Document, CancellationToken.None);
        for (var offset = 0; offset < corpusSize; offset += 500)
        {
            var skills = new List<Skill>();
            var versions = new List<SkillVersion>();
            var documents = new List<SkillSearchDocument>();
            foreach (var index in Enumerable.Range(offset, Math.Min(500, corpusSize - offset)))
            {
                var relevant = index < relevantCount;
                var text = relevant ? $"{relevantText} {index}" : $"{irrelevantText} {index}";
                var skill = new Skill
                {
                    TenantId = actor.TenantId,
                    OwnerUserId = actor.UserId,
                    StableKey = $"scale-skill-{index:D4}",
                    Name = relevant ? $"Relevant PostgreSQL {index:D4}" : $"Irrelevant design {index:D4}",
                    Description = text,
                    WhenToUse = relevant ? "Use for production PostgreSQL migration rollback" : "Use for visual design review",
                    Tags = relevant ? ["postgres", "migration", "rollback"] : ["design", "layout"],
                    License = "MIT",
                    MaintainersJson = "[]",
                    RiskLevel = SkillRiskLevel.Low,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                var version = new SkillVersion
                {
                    SkillId = skill.Id,
                    Version = "1.0.0",
                    Status = SkillLifecycleStatus.Published,
                    ContentHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(skill.StableKey))),
                    BundleJson = "{\"files\":[],\"formatVersion\":\"1.0\"}",
                    SearchText = text,
                    SourceKind = SkillSourceKind.Generated,
                    SourceRef = "benchmark:2500",
                    SourceRevision = "1",
                    TrustLevel = SkillTrustLevel.SourceVerified,
                    PublishEvidenceJson = "{\"fixture\":true}",
                    CreatedAt = now,
                    UpdatedAt = now,
                    PublishedAt = now,
                    Skill = skill
                };
                skills.Add(skill);
                versions.Add(version);
                documents.Add(new SkillSearchDocument
                {
                    GenerationId = generation.Id,
                    SkillVersionId = version.Id,
                    SearchText = text,
                    TermsJson = JsonSerializer.Serialize(SkillText.Tokenize(text)),
                    EmbeddingJson = JsonSerializer.Serialize(relevant ? relevantVector.Values : irrelevantVector.Values),
                    CreatedAt = now
                });
            }
            db.Skills.AddRange(skills);
            db.SkillVersions.AddRange(versions);
            await db.SaveChangesAsync();
            db.SkillSearchDocuments.AddRange(documents);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }

        var stopwatch = Stopwatch.StartNew();
        var search = await service.SearchForExecutionAsync(new(
            Guid.NewGuid(), null, "ContextHub", "ContextHub", "Codex",
            relevantText, ["postgres", "migration", "rollback"], ["postgres"], [], [], [], [],
            new SkillSearchPolicy(TopN: topN, Threshold: 0m, MaxSearchRounds: 2, MaxSelectedSkills: 5),
            $"scale-search-{Guid.NewGuid():N}"), CancellationToken.None);
        stopwatch.Stop();
        var precisionAtN = decimal.Divide(search.Candidates.Count(item => item.Name.StartsWith("Relevant", StringComparison.Ordinal)), topN);
        var recallAtN = decimal.Divide(search.Candidates.Count(item => item.Name.StartsWith("Relevant", StringComparison.Ordinal)), relevantCount);
        var reconcileStopwatch = Stopwatch.StartNew();
        var reconciliation = await service.ReconcileTelemetryAsync(new(90, 1095, $"scale-reconcile-{Guid.NewGuid():N}"), CancellationToken.None);
        reconcileStopwatch.Stop();

        search.Candidates.Should().HaveCount(topN);
        precisionAtN.Should().Be(1m);
        recallAtN.Should().Be(0.4m);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20));
        reconciliation.AggregatedEventCount.Should().Be(topN);
        reconcileStopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20));
        (await db.SkillTelemetryEvents.AsNoTracking().CountAsync()).Should().Be(topN);
        (await db.SkillTelemetryDailyAggregates.AsNoTracking().SumAsync(item => item.EventCount)).Should().Be(topN);
    }

    private static ContextHubRequestActor UseBootstrapActor(IServiceProvider services)
    {
        var db = services.GetRequiredService<MemoryDbContext>();
        var user = db.TenantUsers.Include(x => x.Tenant).Single(x => x.Username == "contract-test-admin");
        var actor = new ContextHubRequestActor(
            user.TenantId, user.Id, user.Username, user.Role,
            [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite, SecurityScopes.SkillsRead, SecurityScopes.SkillsExecute,
             SecurityScopes.SkillsManage, SecurityScopes.SkillsPublish, SecurityScopes.SkillsSecurity, SecurityScopes.SkillsBind, SecurityScopes.SkillsReindex],
            [], true);
        services.GetRequiredService<IRequestActorAccessor>().Current = actor;
        return actor;
    }
}
