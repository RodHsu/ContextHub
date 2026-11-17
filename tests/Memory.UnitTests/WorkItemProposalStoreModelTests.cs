using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Memory.UnitTests;

/// <summary>Model/query translation and actual production helper tests; no connection or transaction is opened.</summary>
public sealed class WorkItemProposalStoreModelTests
{
    [Fact]
    public void Dispatch_model_has_one_marker_per_proposal_restrict_links_and_state_constraints()
    {
        using var db = Model();
        var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(WorkItemProposalOperation))!;
        entity.Should().NotBeNull();
        entity.GetTableName().Should().Be("work_item_proposal_operations");
        entity.FindPrimaryKey()!.Properties.Select(x => x.Name).Should().Equal(nameof(WorkItemProposalOperation.ProposalId));
        entity.FindProperty(nameof(WorkItemProposalOperation.State))!.IsConcurrencyToken.Should().BeTrue();
        entity.FindProperty(nameof(WorkItemProposalOperation.RequestId))!.GetMaxLength().Should().Be(200);
        entity.GetForeignKeys().Should().HaveCount(3).And.OnlyContain(x => x.DeleteBehavior == DeleteBehavior.Restrict);
        entity.GetForeignKeys().Select(x => x.PrincipalEntityType.ClrType).Should().BeEquivalentTo(
            new[] { typeof(ConversationInsight), typeof(ProjectWorkItem), typeof(WorkItemEditReceiptRecord) });
        var checks = entity.GetCheckConstraints().ToDictionary(x => x.Name ?? throw new InvalidOperationException("Named constraint required."), x => x.Sql);
        checks["ck_work_item_proposal_original_revision"].Should().Contain("original_revision > 0");
        checks["ck_work_item_proposal_hash"].Should().Contain("^[0-9a-f]{64}$").And.Contain(WorkItemEditContract.IntentHashVersion);
        checks["ck_work_item_proposal_state"].Should().Contain("conflict_code IS NOT NULL").And.Contain("receipt_id IS NOT NULL").And.Contain("finalized_at IS NOT NULL");
    }

    [Fact]
    public void Proposal_scope_is_owner_bound_even_for_a_service_actor()
    {
        using var db = Model();
        var actor = Actor();
        var ownerQuery = WorkItemProposalStore.VisibleProposals(db.ConversationInsights.AsNoTracking(), actor).Select(x => x.Id).ToQueryString();
        var serviceQuery = WorkItemProposalStore.VisibleProposals(db.ConversationInsights.AsNoTracking(), actor with { IsServiceActor = true }).Select(x => x.Id).ToQueryString();
        serviceQuery.Should().Be(ownerQuery);
        ownerQuery.Should().Contain("tenant_id").And.Contain("owner_user_id").And.Contain("source_system").And.Contain("promotion_status")
            .And.Contain("chatgpt-mcp-gateway").And.Contain("chatgpt-proposal").And.Contain("project_identity_key");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unauthenticated_or_incomplete_actors_cannot_observe_proposals(bool incomplete)
    {
        using var db = Model();
        var actor = incomplete ? Actor() with { UserId = null } : Actor() with { IsAuthenticated = false };
        WorkItemProposalStore.VisibleProposals(db.ConversationInsights.AsNoTracking(), actor).Select(x => x.Id)
            .ToQueryString().ToUpperInvariant().Should().Contain("WHERE FALSE");
    }

    [Fact]
    public void Target_visibility_query_never_projects_a_current_revision_for_dispatch()
    {
        using var db = Model();
        var sql = WorkItemProposalStore.TargetIdentities(db.ProjectWorkItems.AsNoTracking(), Actor(), Command()).ToQueryString();
        var selectStart = sql.IndexOf("SELECT", StringComparison.OrdinalIgnoreCase);
        var fromStart = sql.IndexOf("FROM", selectStart, StringComparison.OrdinalIgnoreCase);
        var projection = sql[selectStart..fromStart];
        projection.Should().Contain(".id").And.NotContain("revision").And.NotContain("title").And.NotContain("description");
        sql.Should().Contain("tenant_id").And.Contain("owner_user_id").And.Contain("project_identity_equals");
    }

    [Fact]
    public void Persisted_receipt_lookup_binds_every_original_identity_component()
    {
        using var db = Model();
        var operation = Operation();
        var sql = WorkItemProposalStore.CoreReceipts(db.WorkItemEditReceipts.AsNoTracking(), operation, Guid.NewGuid()).ToQueryString();
        var where = sql[sql.IndexOf("WHERE", StringComparison.OrdinalIgnoreCase)..];
        foreach (var column in new[] { ".id", "tenant_id", "actor_user_id", "project_key", "work_item_id", "request_id", "request_hash" })
            where.Should().Contain(column);
    }

    [Theory]
    [InlineData(SafeEditConflictCodes.MissingReadVersion)]
    [InlineData(SafeEditConflictCodes.StaleReadVersion)]
    [InlineData(SafeEditConflictCodes.RequestIdentityMismatch)]
    [InlineData(SafeEditConflictCodes.RequiresReopen)]
    public void Definite_conflict_is_terminal_and_same_conflict_replay_has_no_second_transition(string code)
    {
        var operation = Operation();
        var original = JsonSerializer.Serialize(new { operation.OriginalRevision, operation.RequestId, operation.RequestHash });
        var now = DateTimeOffset.UnixEpoch.AddMinutes(1);
        WorkItemProposalStore.MarkConflict(operation, code, now).Should().BeTrue();
        operation.State.Should().Be(WorkItemProposalOperationState.Conflict);
        operation.ConflictCode.Should().Be(code);
        operation.ReceiptId.Should().BeNull(); operation.FinalizedAt.Should().BeNull();
        WorkItemProposalStore.MarkConflict(operation, code, now.AddMinutes(1)).Should().BeFalse();
        operation.UpdatedAt.Should().Be(now);
        JsonSerializer.Serialize(new { operation.OriginalRevision, operation.RequestId, operation.RequestHash }).Should().Be(original);
        FluentActions.Invoking(() => WorkItemProposalStore.MarkApplied(operation, Guid.NewGuid(), now)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Unknown_or_different_conflict_cannot_overwrite_a_durable_terminal_decision()
    {
        var operation = Operation();
        FluentActions.Invoking(() => WorkItemProposalStore.MarkConflict(operation, "private-error-text", DateTimeOffset.UnixEpoch))
            .Should().Throw<ArgumentException>();
        operation.State.Should().Be(WorkItemProposalOperationState.Dispatched);
        WorkItemProposalStore.MarkConflict(operation, SafeEditConflictCodes.StaleReadVersion, DateTimeOffset.UnixEpoch);
        FluentActions.Invoking(() => WorkItemProposalStore.MarkConflict(operation, SafeEditConflictCodes.RequiresReopen, DateTimeOffset.UnixEpoch))
            .Should().Throw<InvalidOperationException>();
        operation.ConflictCode.Should().Be(SafeEditConflictCodes.StaleReadVersion);
    }

    [Fact]
    public void Applied_transition_is_idempotent_only_for_the_same_receipt_and_preserves_finalize_time()
    {
        var operation = Operation(); var receiptId = Guid.NewGuid(); var now = DateTimeOffset.UnixEpoch.AddMinutes(1);
        WorkItemProposalStore.MarkApplied(operation, receiptId, now).Should().BeTrue();
        WorkItemProposalStore.MarkApplied(operation, receiptId, now.AddMinutes(1)).Should().BeFalse();
        operation.State.Should().Be(WorkItemProposalOperationState.Applied); operation.ReceiptId.Should().Be(receiptId);
        operation.FinalizedAt.Should().Be(now); operation.UpdatedAt.Should().Be(now);
        FluentActions.Invoking(() => WorkItemProposalStore.MarkApplied(operation, Guid.NewGuid(), now)).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => WorkItemProposalStore.MarkConflict(operation, SafeEditConflictCodes.StaleReadVersion, now)).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => WorkItemProposalStore.MarkApplied(Operation(), Guid.Empty, now)).Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData("dispatched-receipt")]
    [InlineData("conflict-without-code")]
    [InlineData("applied-without-time")]
    [InlineData("unknown")]
    public void Corrupt_marker_shapes_fail_closed_before_transition(string scenario)
    {
        var operation = Operation();
        if (scenario == "dispatched-receipt") operation.ReceiptId = Guid.NewGuid();
        if (scenario == "conflict-without-code") operation.State = WorkItemProposalOperationState.Conflict;
        if (scenario == "applied-without-time") { operation.State = WorkItemProposalOperationState.Applied; operation.ReceiptId = Guid.NewGuid(); }
        if (scenario == "unknown") operation.State = (WorkItemProposalOperationState)999;
        FluentActions.Invoking(() => WorkItemProposalStore.RequireShape(operation)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Supplied_receipt_may_only_differ_in_the_transport_replayed_flag()
    {
        var receipt = Receipt();
        FluentActions.Invoking(() => WorkItemProposalStore.RequireEquivalentReceipt(receipt, receipt with { Replayed = true })).Should().NotThrow();
        foreach (var supplied in new[]
        {
            receipt with { ReceiptId = Guid.NewGuid() },
            receipt with { AppliedAt = receipt.AppliedAt.AddSeconds(1) },
            receipt with { Result = receipt.Result with { Title = "forged result" } },
            receipt with { RequestHash = new string('b', 64) }
        }) FluentActions.Invoking(() => WorkItemProposalStore.RequireEquivalentReceipt(receipt, supplied)).Should().Throw<InvalidDataException>();
        // This exercises the real comparer only; persisted-row lookup/commit acceptance needs DB evidence.
    }

    [Fact]
    public void Saved_archive_intent_is_strict_and_never_synthesizes_missing_preconditions()
    {
        var valid = JsonSerializer.Serialize(new ProjectWorkItemArchiveRequest(Command().WorkItemId, true, 7, Command().RequestId),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        WorkItemProposalStore.ParseArchivePayload(valid).Should().Be(new ProjectWorkItemArchiveRequest(Command().WorkItemId, true, 7, Command().RequestId));
        var legacy = WorkItemProposalStore.ParseArchivePayload("{\"workItemId\":\"" + Command().WorkItemId + "\"}");
        FluentActions.Invoking(() => WorkItemProposalContract.Command("ContextHub", legacy)).Should().Throw<SafeEditConflictException>()
            .Which.Code.Should().Be(SafeEditConflictCodes.MissingReadVersion);
        foreach (var json in new[] { valid.TrimEnd('}') + ",\"unexpected\":true}", valid.TrimEnd('}') + ",\"EXPECTEDREVISION\":8}", "[]", "not-json" })
            FluentActions.Invoking(() => WorkItemProposalStore.ParseArchivePayload(json)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Embedded_migration_preserves_intent_terminal_guards_and_restrict_evidence_links()
    {
        var assembly = typeof(MemoryDbContext).Assembly;
        var name = assembly.GetManifestResourceNames().Single(x => x.EndsWith(".060_work_item_proposal_dispatch.sql", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        var sql = reader.ReadToEnd();
        sql.Should().Contain("hash_version = '" + WorkItemEditContract.IntentHashVersion + "'");
        sql.Should().Contain("original_revision > 0").And.Contain("conflict_code IS NOT NULL")
            .And.Contain("REFERENCES conversation_insights(id) ON DELETE RESTRICT")
            .And.Contain("REFERENCES work_item_edit_receipts(id) ON DELETE RESTRICT")
            .And.Contain("NEW.original_revision IS DISTINCT FROM OLD.original_revision")
            .And.Contain("NEW.request_hash IS DISTINCT FROM OLD.request_hash")
            .And.Contain("OLD.state IN ('Conflict', 'Applied')")
            .And.Contain("TG_OP = 'DELETE'").And.NotContain("ON DELETE CASCADE");
        sql.Should().Contain("IF EXISTS (SELECT 1 FROM work_item_proposal_operations WHERE proposal_id = OLD.id)")
            .And.Contain("BEFORE UPDATE ON conversation_insights")
            .And.Contain("EXECUTE FUNCTION guard_dispatched_work_item_proposal_intent()");
        var intentGuard = sql[sql.IndexOf("CREATE OR REPLACE FUNCTION guard_dispatched_work_item_proposal_intent()", StringComparison.Ordinal)..];
        foreach (var column in new[] { "tenant_id", "owner_user_id", "project_id", "source_system", "tags", "metadata_json", "content" })
            intentGuard.Should().Contain($"NEW.{column} IS DISTINCT FROM OLD.{column}");
        foreach (var column in new[] { "promotion_status", "promoted_memory_id", "error", "governance_reason", "updated_at" })
            intentGuard.Should().NotContain($"NEW.{column} IS DISTINCT FROM OLD.{column}");
        // Source-shape evidence only: the SQL has not been executed or accepted by PostgreSQL here.
    }

    private static MemoryDbContext Model() => new(new DbContextOptionsBuilder<MemoryDbContext>()
        .UseNpgsql("Host=localhost;Database=proposal_metadata_only;Username=synthetic").Options);

    private static ContextHubRequestActor Actor() => new(Guid.Parse("9696cc79-d6d4-4cf1-bdb9-1230b0b9d366"),
        Guid.Parse("bb4addcb-04a7-444d-b0a7-55599b2480c2"), "synthetic", TenantUserRole.Member,
        [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite], ["ContextHub"], true);

    private static WorkItemEditCommand Command() => new("ContextHub", Guid.Parse("6f5be4d8-2e40-4679-ae48-6be05f7c887e"),
        7, "synthetic-proposal-key", WorkItemEditOperation.Archive, new());

    private static WorkItemProposalOperation Operation() => new()
    {
        ProposalId = Guid.Parse("0b74e737-282d-4419-9347-9f237c512991"),
        TenantId = Actor().TenantId!.Value,
        ActorUserId = Actor().UserId!.Value,
        ProjectId = "ContextHub",
        ProjectKey = ProjectContext.IdentityKey("ContextHub"),
        WorkItemId = Command().WorkItemId,
        OriginalRevision = 7,
        RequestId = Command().RequestId,
        RequestHash = WorkItemEditContract.Hash(Command()),
        HashVersion = WorkItemEditContract.IntentHashVersion,
        Archived = true,
        State = WorkItemProposalOperationState.Dispatched,
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch
    };

    private static WorkItemEditReceipt Receipt()
    {
        var command = Command();
        var result = new ProjectWorkItemResult(command.WorkItemId, command.ProjectId, "Synthetic", "", [], [],
            ProjectWorkItemStatus.Completed, 0, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)
        { Revision = 8 };
        return new(Guid.NewGuid(), Guid.NewGuid(), command.ProjectId, command.WorkItemId, command.RequestId,
            WorkItemEditContract.Hash(command), WorkItemEditContract.IntentHashVersion, command.Operation, 7, 8, true,
            DateTimeOffset.UnixEpoch, result);
    }
}
