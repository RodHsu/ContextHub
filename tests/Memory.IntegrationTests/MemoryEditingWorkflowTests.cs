using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Memory.IntegrationTests;

public sealed class MemoryEditingWorkflowTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Concurrent_exact_replay_and_restart_acknowledge_only_one_edit_with_scoped_metadata()
    {
        var (actor, before) = await SeedAsync();
        const string body = "guarded replacement body";
        var command = Command(before, new(Content: body, Tags: [], Confidence: 0.75m));
        var alias = command with { ProjectId = command.ProjectId.ToUpperInvariant() };
        var results = await Task.WhenAll(ApplyAsync(actor, command), ApplyAsync(actor, alias));
        var original = results.Single(receipt => !receipt.Replayed);
        results.Single(receipt => receipt.Replayed).Should().BeEquivalentTo(original with { Replayed = true });
        original.ProjectId.Should().Be(before.ProjectId);
        original.PreviousVersion.Should().Be(before.Version);
        original.Version.Should().Be(before.Version + 1);
        original.RequestHash.Should().Be(MemoryEditContract.Hash(command));

        using (var scope = NewScope(actor))
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var memory = await db.MemoryItems.SingleAsync(row => row.Id == before.Id);
            memory.Content.Should().Be(body);
            memory.Title.Should().Be(before.Title);
            memory.Tags.Should().BeEmpty();
            memory.Confidence.Should().Be(0.75m);
            (await db.MemoryItemChunks.Where(row => row.MemoryItemId == before.Id).Select(row => row.ChunkText).ToArrayAsync())
                .Should().ContainSingle().Which.Should().Contain(body);
            var revisions = await db.MemoryItemRevisions.Where(row => row.MemoryItemId == before.Id).ToArrayAsync();
            revisions.Should().HaveCount(2).And.Contain(row => row.Id == original.RevisionId && row.Content == body);
            (await db.MemoryJobs.CountAsync(row => row.ProjectId == before.ProjectId && row.JobType == MemoryJobType.Reindex)).Should().Be(2);
            var record = await db.MemoryEditReceipts.SingleAsync(row => row.MemoryItemId == before.Id);
            record.Id.Should().Be(original.ReceiptId);
            record.RequestHash.Should().Be(original.RequestHash);
            record.RevisionId.Should().Be(original.RevisionId);
            Func<Task> rewrite = () => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE memory_edit_receipts SET request_hash = request_hash WHERE id = {record.Id}");
            await rewrite.Should().ThrowAsync<Npgsql.PostgresException>();
        }

        var after = await ApplyAsync(actor, Command(before, new(Title: "next title")) with { ExpectedVersion = original.Version });
        after.Version.Should().Be(original.Version + 1);
        await environment.RestartApplicationAsync();
        var recovered = await ReadReceiptAsync(actor, Recovery(alias));
        recovered.Should().BeEquivalentTo(original with { Replayed = true });
        var lateReplay = await ApplyAsync(actor, alias);
        lateReplay.Should().BeEquivalentTo(original with { Replayed = true });
        Func<Task> conflictingKey = () => ApplyAsync(actor, command with { Patch = new(Content: "different intent") });
        (await conflictingKey.Should().ThrowAsync<SafeEditConflictException>()).Which.Code.Should().Be(SafeEditConflictCodes.RequestIdentityMismatch);
    }

    [DockerRequiredFact]
    public async Task Losing_editor_and_already_loaded_legacy_writer_cannot_overwrite_a_committed_version()
    {
        var (actor, before) = await SeedAsync();
        using var legacy = NewScope(actor);
        var legacyDb = legacy.ServiceProvider.GetRequiredService<MemoryDbContext>();
        await legacyDb.MemoryItems.SingleAsync(row => row.Id == before.Id);
        var first = Command(before, new(Content: "editor one"));
        var second = Command(before, new(Content: "editor two"));

        var contenders = await Task.WhenAll(AttemptAsync(first), AttemptAsync(second));
        var winner = contenders.Single(result => result.Receipt is not null);
        var conflict = contenders.Single(result => result.Conflict is not null).Conflict!;
        conflict.Code.Should().Be(SafeEditConflictCodes.StaleReadVersion);
        conflict.CurrentRevision.Should().Be(winner.Receipt!.Version);
        Func<Task> legacySave = () => legacy.ServiceProvider.GetRequiredService<IMemoryService>()
            .UpdateAsync(new(before.Id, Content: "stale legacy edit", ProjectId: before.ProjectId), default);
        await legacySave.Should().ThrowAsync<DbUpdateConcurrencyException>();

        using var verification = NewScope(actor);
        var db = verification.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var current = await db.MemoryItems.SingleAsync(row => row.Id == before.Id);
        current.Version.Should().Be(before.Version + 1);
        current.Content.Should().Be(winner.Content);
        (await db.MemoryItemRevisions.CountAsync(row => row.MemoryItemId == before.Id)).Should().Be(2);
        (await db.MemoryEditReceipts.CountAsync(row => row.MemoryItemId == before.Id)).Should().Be(1);

        async Task<(MemoryEditReceipt? Receipt, SafeEditConflictException? Conflict, string? Content)> AttemptAsync(MemoryEditCommand command)
        {
            try { return (await ApplyAsync(actor, command), null, command.Patch.Content); }
            catch (SafeEditConflictException exception) { return (null, exception, null); }
        }
    }

    [DockerRequiredFact]
    public async Task Memory_scope_and_current_owner_are_required_for_apply_and_recovery()
    {
        var (actor, before) = await SeedAsync();
        var command = Command(before, new(Title: "new title"));
        var receipt = await ApplyAsync(actor, command);
        var otherProject = "memory-edit-adjacent";
        var wrongScope = command with { ProjectId = otherProject };
        foreach (var unauthorized in new[]
                 {
                     actor with { UserId = Guid.NewGuid() },
                     actor with { TenantId = Guid.NewGuid() },
                     actor with { Role = TenantUserRole.Owner, UserId = Guid.NewGuid(), IsServiceActor = true }
                 })
        {
            Func<Task> edit = () => ApplyAsync(unauthorized, command);
            await edit.Should().ThrowAsync<KeyNotFoundException>();
            (await ReadReceiptAsync(unauthorized, Recovery(command))).Should().BeNull();
        }
        Func<Task> crossProject = () => ApplyAsync(actor, wrongScope);
        await crossProject.Should().ThrowAsync<KeyNotFoundException>();
        (await ReadReceiptAsync(actor, Recovery(wrongScope))).Should().BeNull();
        Func<Task> deniedWrite = () => ApplyAsync(actor with { Scopes = [SecurityScopes.MemoryRead] }, command);
        await deniedWrite.Should().ThrowAsync<UnauthorizedAccessException>();
        Func<Task> deniedProject = () => ApplyAsync(actor with { AllowedProjectIds = [otherProject] }, command);
        await deniedProject.Should().ThrowAsync<UnauthorizedAccessException>();
        Func<Task> unversioned = () => ApplyAsync(actor, command with { ExpectedVersion = 0 });
        (await unversioned.Should().ThrowAsync<SafeEditConflictException>()).Which.Code.Should().Be(SafeEditConflictCodes.MissingReadVersion);

        using var mutation = NewScope(actor);
        var db = mutation.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var memory = await db.MemoryItems.SingleAsync(row => row.Id == before.Id);
        memory.IsReadOnly = true;
        await db.SaveChangesAsync();
        Func<Task> readOnly = () => ApplyAsync(actor, command with { ExpectedVersion = receipt.Version });
        await readOnly.Should().ThrowAsync<KeyNotFoundException>();
        (await ReadReceiptAsync(actor, Recovery(command))).Should().BeNull();
        memory.IsReadOnly = false;
        memory.MemoryType = MemoryType.Preference;
        await db.SaveChangesAsync();
        await readOnly.Should().ThrowAsync<KeyNotFoundException>();
        Func<Task> reserved = () => ApplyAsync(actor, command with { ProjectId = ProjectContext.SharedProjectId });
        await reserved.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [DockerRequiredFact]
    public async Task Failed_receipt_insert_leaves_the_previous_content_revision_chunks_and_work_intact()
    {
        var (actor, before) = await SeedAsync();
        const string requestId = "guarded-edit-rollback-fixture";
        var command = Command(before, new(Content: "must not commit")) with { RequestId = requestId };
        using var owner = NewScope(actor);
        var db = owner.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var chunksBefore = await db.MemoryItemChunks.AsNoTracking().Where(row => row.MemoryItemId == before.Id)
            .Select(row => row.Id).ToArrayAsync();
        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE memory_edit_receipts ADD CONSTRAINT ck_memory_edit_forced_rollback_fixture
            CHECK (request_id <> 'guarded-edit-rollback-fixture') NOT VALID
            """);
        try
        {
            Func<Task> edit = () => ApplyAsync(actor, command);
            await edit.Should().ThrowAsync<DbUpdateException>();
            var current = await db.MemoryItems.AsNoTracking().SingleAsync(row => row.Id == before.Id);
            current.Version.Should().Be(before.Version);
            current.Content.Should().Be(before.Content);
            (await db.MemoryItemRevisions.CountAsync(row => row.MemoryItemId == before.Id)).Should().Be(1);
            (await db.MemoryItemChunks.Where(row => row.MemoryItemId == before.Id).Select(row => row.Id).ToArrayAsync())
                .Should().BeEquivalentTo(chunksBefore);
            (await db.MemoryJobs.CountAsync(row => row.ProjectId == before.ProjectId && row.JobType == MemoryJobType.Reindex)).Should().Be(1);
            (await ReadReceiptAsync(actor, Recovery(command))).Should().BeNull();
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE memory_edit_receipts DROP CONSTRAINT ck_memory_edit_forced_rollback_fixture");
        }
        var original = await ApplyAsync(actor, command);
        original.Version.Should().Be(before.Version + 1);
        original.Replayed.Should().BeFalse();
    }

    private async Task<(ContextHubRequestActor Actor, MemoryDocument Before)> SeedAsync()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<MemoryDbContext>();
        var user = await db.TenantUsers.SingleAsync(row => row.Username == "contract-test-admin");
        var actor = new ContextHubRequestActor(user.TenantId, user.Id, user.Username, user.Role,
            [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite], [], IsAuthenticated: true);
        services.GetRequiredService<IRequestActorAccessor>().Current = actor;
        var before = await services.GetRequiredService<IMemoryService>().UpsertAsync(new(
            "original", MemoryScope.Project, MemoryType.Episode, "original title", "original body", "original summary",
            "document", "synthetic-fixture", ["original"], 0.5m, 0.5m, ProjectId: $"memory-edit-{Guid.NewGuid():N}"), default);
        return (actor, before);
    }

    private async Task<MemoryEditReceipt> ApplyAsync(ContextHubRequestActor actor, MemoryEditCommand command)
    {
        using var scope = NewScope(actor);
        return await scope.ServiceProvider.GetRequiredService<IMemoryEditingService>().ApplyAsync(command, default);
    }

    private async Task<MemoryEditReceipt?> ReadReceiptAsync(ContextHubRequestActor actor, MemoryEditRecoveryRequest request)
    {
        using var scope = NewScope(actor);
        return await scope.ServiceProvider.GetRequiredService<IMemoryEditingService>().ReadReceiptAsync(request, default);
    }

    private IServiceScope NewScope(ContextHubRequestActor actor)
    {
        var scope = environment.GetFactory().Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>().Current = actor;
        return scope;
    }

    private static MemoryEditCommand Command(MemoryDocument doc, MemoryEditPatch patch)
        => new(doc.ProjectId, doc.Id, doc.Version, Guid.NewGuid().ToString("N"), patch);

    private static MemoryEditRecoveryRequest Recovery(MemoryEditCommand command)
        => new(command.ProjectId, command.MemoryId, command.RequestId, MemoryEditContract.Hash(command));
}
