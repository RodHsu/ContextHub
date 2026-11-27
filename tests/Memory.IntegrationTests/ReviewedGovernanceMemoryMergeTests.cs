using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Memory.IntegrationTests;

public sealed class ReviewedGovernanceMemoryMergeTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Either_reviewed_version_is_required_even_when_the_pair_remains_exact(bool changePrimary)
    {
        var fixture = await SeedAsync();
        var review = await ReviewAsync(fixture);
        var edited = changePrimary ? fixture.PrimaryId : fixture.SecondaryId;
        using (var writer = Open(fixture.Actor))
        {
            // This changes Version without changing the merge classifier's exact-content inputs.
            await writer.ServiceProvider.GetRequiredService<IMemoryService>().UpdateAsync(
                new(edited, Title: "Reviewed duplicate", ProjectId: fixture.ProjectId), default);
        }

        var result = await ExecuteAsync(fixture, review.Coverage.SnapshotToken);

        AssertRequiresFreshReview(result);
        await AssertUnmergedAsync(fixture, changedId: edited);
    }

    [DockerRequiredFact]
    public async Task Historical_snapshot_is_not_backfilled_and_unversioned_authority_drift_is_rejected()
    {
        var historical = await SeedAsync();
        var review = await ReviewAsync(historical);
        using (var scope = Open(historical.Actor))
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var stored = await db.KnowledgeGovernanceSnapshots.SingleAsync(row => row.Id == review.Coverage.SnapshotId);
            var json = JsonNode.Parse(stored.ResultJson)!.AsObject();
            json.Remove("reviewedMergePairs").Should().BeTrue();
            stored.ResultJson = json.ToJsonString();
            await db.SaveChangesAsync();
        }
        AssertRequiresFreshReview(await ExecuteAsync(historical, review.Coverage.SnapshotToken));
        await AssertUnmergedAsync(historical);

        var drift = await SeedAsync();
        var reviewed = await ReviewAsync(drift);
        using (var scope = Open(drift.Actor))
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var memory = await db.MemoryItems.SingleAsync(row => row.Id == drift.SecondaryId);
            memory.MetadataJson = """{"source":"new reviewed evidence is required"}""";
            await db.SaveChangesAsync();
            memory.Version.Should().Be(1);
        }
        AssertRequiresFreshReview(await ExecuteAsync(drift, reviewed.Coverage.SnapshotToken));
        await AssertUnmergedAsync(drift);
    }

    [DockerRequiredFact]
    public async Task Protected_rows_and_excluded_merge_action_keep_their_governance_guards()
    {
        var fixture = await SeedAsync(protectSecondary: true);
        var review = await ReviewAsync(fixture);
        var result = await ExecuteAsync(fixture, review.Coverage.SnapshotToken);
        result.Items.Should().Contain(item => item.ItemKind == "Finding" &&
            item.Disposition == GovernanceBatchItemDisposition.RequiresUserDecision && item.Summary.Contains("protected"));
        await AssertUnmergedAsync(fixture);

        var excluded = await SeedAsync();
        var clean = await ReviewAsync(excluded);
        var reindex = await ExecuteAsync(excluded, clean.Coverage.SnapshotToken, [GovernanceBatchActionType.Reindex]);
        reindex.MergedCount.Should().Be(0);
        var reindexProposalIds = reindex.Items.Where(item => item.ActionType == GovernanceBatchActionType.Reindex &&
            item.Disposition == GovernanceBatchItemDisposition.Applied).SelectMany(item => item.ProposalIds).ToArray();
        reindexProposalIds.Should().NotBeEmpty();
        await AssertUnmergedAsync(excluded, permittedProposalIds: reindexProposalIds);
    }

    [DockerRequiredFact]
    public async Task A_new_interactive_server_review_preserves_the_reviewed_primary_and_exact_completed_replay()
    {
        var fixture = await SeedAsync();
        var result = await ExecuteAsync(fixture, snapshotToken: null);
        var applied = SuccessfulMerge(result, fixture);
        applied.ProposalIds.Should().HaveCount(3);
        var replay = await ExecuteAsync(fixture, result.SnapshotToken);
        replay.IsReplay.Should().BeTrue();
        SuccessfulMerge(replay, fixture).Should().BeEquivalentTo(applied);
        await AssertMergedAsync(fixture);
    }

    [DockerRequiredFact]
    public async Task Failed_progress_write_rolls_back_the_pair_proposals_links_and_versions()
    {
        var fixture = await SeedAsync();
        var review = await ReviewAsync(fixture);
        using var scope = Open(fixture.Actor);
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE governance_batch_executions ADD CONSTRAINT ck_reviewed_merge_progress_fixture
            CHECK (COALESCE((result_json::jsonb->>'mergedCount')::integer, 0) = 0) NOT VALID
            """);
        try
        {
            var failed = await ExecuteAsync(fixture, review.Coverage.SnapshotToken);
            failed.Items.Should().Contain(item => item.ItemKind == "Finding" && item.Disposition == GovernanceBatchItemDisposition.Failed);
            await AssertUnmergedAsync(fixture);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE governance_batch_executions DROP CONSTRAINT ck_reviewed_merge_progress_fixture");
        }
    }

    [DockerRequiredFact]
    public async Task Interruption_after_item_commit_returns_its_original_progress_without_second_mutation()
    {
        var fixture = await SeedAsync();
        var review = await ReviewAsync(fixture);
        GovernanceBatchItemResult committedItem;
        using (var scope = Open(fixture.Actor))
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            await db.Database.ExecuteSqlRawAsync("""
                ALTER TABLE security_audit_events ADD CONSTRAINT ck_reviewed_merge_completion_fixture
                CHECK (event_type <> 'GovernanceBatchExecutionCompleted') NOT VALID
                """);
            try
            {
                Func<Task> interrupted = () => ExecuteAsync(fixture, review.Coverage.SnapshotToken);
                await interrupted.Should().ThrowAsync<DbUpdateException>();

                var runIds = db.GovernanceBatchRuns.Where(row => row.GovernanceRunId == fixture.RunId).Select(row => row.Id);
                var execution = await db.GovernanceBatchExecutions.AsNoTracking()
                    .SingleAsync(row => runIds.Contains(row.GovernanceBatchRunId));
                execution.Status.Should().Be("Running");
                execution.CompletedAt.Should().BeNull();
                var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
                var progress = JsonSerializer.Deserialize<GovernanceBatchExecuteResult>(execution.ResultJson, options)!;
                progress.StoppedReason.Should().Be("Running");
                committedItem = SuccessfulMerge(progress, fixture);
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE security_audit_events DROP CONSTRAINT ck_reviewed_merge_completion_fixture");
            }
        }

        await AssertMergedAsync(fixture);
        var replay = await ExecuteAsync(fixture, review.Coverage.SnapshotToken);
        replay.IsReplay.Should().BeTrue();
        replay.StoppedReason.Should().Be("UnknownResult");
        replay.RequiresReReview.Should().BeTrue();
        var item = SuccessfulMerge(replay, fixture);
        item.Should().BeEquivalentTo(committedItem);
        item.ProposalIds.Should().HaveCount(3);
        (await ExecuteAsync(fixture, review.Coverage.SnapshotToken)).Items
            .Should().Contain(result => result.ItemKey == item.ItemKey && result.AuditIds.SequenceEqual(item.AuditIds));
        await AssertMergedAsync(fixture);
    }

    private async Task<Fixture> SeedAsync(bool protectSecondary = false)
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var user = await db.TenantUsers.SingleAsync(row => row.Username == "contract-test-admin");
        var actor = new ContextHubRequestActor(user.TenantId, user.Id, user.Username, user.Role,
            [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite], [], IsAuthenticated: true);
        var project = $"reviewed-pair-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        // Populate the finding's earlier peer as the secondary. Authority ranking must
        // select the later peer even though the finding order is the opposite.
        var secondary = NewItem("secondary", now.AddMinutes(-2), protectSecondary ? ["protected"] : [], 0.4m);
        var primary = NewItem("primary", now.AddMinutes(-1), ["authoritative"], 0.7m);
        db.MemoryItems.AddRange(secondary, primary);
        await db.SaveChangesAsync();
        return new(actor, project, $"reviewed-pair-{Guid.NewGuid():N}", primary.Id, secondary.Id);

        MemoryItem NewItem(string key, DateTimeOffset timestamp, string[] tags, decimal confidence)
            => new()
            {
                TenantId = actor.TenantId,
                OwnerUserId = actor.UserId,
                ProjectId = project,
                ExternalKey = key,
                Scope = MemoryScope.Project,
                MemoryType = MemoryType.Artifact,
                Title = "Reviewed duplicate",
                Content = "Identical immutable duplicate content",
                Summary = "Identical reviewed duplicate summary",
                SourceType = "batch-fixture",
                SourceRef = "fixture:" + key,
                Importance = 0.5m,
                Confidence = confidence,
                Tags = tags,
                MetadataJson = """{"source":"original"}""",
                CreatedAt = timestamp,
                UpdatedAt = timestamp
            };
    }

    private async Task<DurableMemoryGovernanceSnapshotResult> ReviewAsync(Fixture fixture)
    {
        using var scope = Open(fixture.Actor);
        var review = await scope.ServiceProvider.GetRequiredService<IDurableMemoryGovernanceService>()
            .GetOrCreateSnapshotAsync([fixture.ProjectId], fixture.RunId, false, default);
        review.ReviewedMergePairs.Should().Contain(pair => pair.PrimaryId == fixture.PrimaryId &&
            pair.Left.Version == 1 && pair.Right.Version == 1);
        return review;
    }

    private async Task<GovernanceBatchExecuteResult> ExecuteAsync(Fixture fixture, string? snapshotToken,
        IReadOnlyList<GovernanceBatchActionType>? allowedActions = null)
    {
        using var scope = Open(fixture.Actor);
        return await scope.ServiceProvider.GetRequiredService<IGovernanceBatchExecutor>().ExecuteAsync(new(
            fixture.RunId, [fixture.ProjectId], snapshotToken, MaxMutations: 100, MaxDurationSeconds: 120,
            AllowedActionTypes: allowedActions ?? [GovernanceBatchActionType.Merge]), default);
    }

    private async Task AssertUnmergedAsync(Fixture fixture, Guid? changedId = null, IReadOnlyList<Guid>? permittedProposalIds = null)
    {
        using var scope = Open(fixture.Actor);
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var rows = await db.MemoryItems.Where(row => row.Id == fixture.PrimaryId || row.Id == fixture.SecondaryId).ToArrayAsync();
        rows.Should().HaveCount(2).And.OnlyContain(row => row.Status == MemoryStatus.Active && row.SupersedesId == null &&
            row.SupersededById == null && row.Version == (row.Id == changedId ? 2 : 1));
        (await db.ConversationInsights.Where(row => row.ProjectId == fixture.ProjectId && row.Tags.Contains("chatgpt-proposal"))
            .Select(row => row.Id).ToArrayAsync()).Should().BeEquivalentTo(permittedProposalIds ?? []);
        (await db.MemoryLinks.AnyAsync(row => row.LinkType == "replaced_by" &&
            (row.FromId == fixture.SecondaryId || row.FromId == fixture.PrimaryId))).Should().BeFalse();
    }

    private async Task AssertMergedAsync(Fixture fixture)
    {
        using var scope = Open(fixture.Actor);
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var primary = await db.MemoryItems.SingleAsync(row => row.Id == fixture.PrimaryId);
        var secondary = await db.MemoryItems.SingleAsync(row => row.Id == fixture.SecondaryId);
        primary.SupersedesId.Should().Be(fixture.SecondaryId);
        primary.Version.Should().Be(3);
        secondary.SupersededById.Should().Be(fixture.PrimaryId);
        secondary.Status.Should().Be(MemoryStatus.Archived);
        secondary.Version.Should().Be(4);
        (await db.ConversationInsights.CountAsync(row => row.ProjectId == fixture.ProjectId && row.Tags.Contains("chatgpt-proposal"))).Should().Be(3);
        (await db.MemoryLinks.CountAsync(row => row.LinkType == "replaced_by" &&
            row.FromId == fixture.SecondaryId && row.ToId == fixture.PrimaryId)).Should().Be(1);
    }

    private static void AssertRequiresFreshReview(GovernanceBatchExecuteResult result)
    {
        result.MergedCount.Should().Be(0);
        result.Items.Should().Contain(item => item.ItemKind == "Finding" && item.ActionType == GovernanceBatchActionType.Merge &&
            item.Disposition == GovernanceBatchItemDisposition.RequiresUserDecision && item.Summary.Contains("fresh governance review") &&
            item.ProposalIds.Count == 0);
    }

    private static GovernanceBatchItemResult SuccessfulMerge(GovernanceBatchExecuteResult result, Fixture fixture)
    {
        result.MergedCount.Should().Be(1);
        var item = result.Items.Single(row => row.ActionType == GovernanceBatchActionType.Merge &&
            row.Disposition == GovernanceBatchItemDisposition.Applied);
        item.ResourceIds.Should().Equal(fixture.PrimaryId, fixture.SecondaryId);
        return item;
    }

    private IServiceScope Open(ContextHubRequestActor actor)
    {
        var scope = environment.GetFactory().Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>().Current = actor;
        return scope;
    }

    private sealed record Fixture(ContextHubRequestActor Actor, string ProjectId, string RunId, Guid PrimaryId, Guid SecondaryId);
}
