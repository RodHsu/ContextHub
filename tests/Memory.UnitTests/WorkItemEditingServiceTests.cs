using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Memory.UnitTests;

public sealed class WorkItemEditingServiceTests
{
    private static readonly Guid Tenant = Guid.NewGuid(), ActorId = Guid.NewGuid(), WorkId = Guid.NewGuid();
    private static ContextHubRequestActor Actor() => new(Tenant, ActorId, "synthetic", TenantUserRole.Member,
        [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite], ["ContextHub"], true);
    private static WorkItemEditCommand Command() => new(" ContextHub ", WorkId, 1, " synthetic-request ",
        WorkItemEditOperation.Update, new(Title: " trimmed "));
    private static WorkItemEditRecoveryRequest Recovery() => new(" ContextHub ", WorkId, " key ", WorkItemEditContract.Hash(Command()));

    [Theory]
    [InlineData("anonymous")]
    [InlineData("scope")]
    [InlineData("project")]
    public async Task Apply_denies_before_reaching_the_store(string scenario)
    {
        var actor = Actor(); actor = scenario switch
        {
            "anonymous" => actor with { IsAuthenticated = false },
            "scope" => actor with { Scopes = [SecurityScopes.MemoryRead] },
            _ => actor with { AllowedProjectIds = ["Other"] }
        };
        var store = new Store(); var service = Service(store, actor);
        await FluentActions.Awaiting(() => service.ApplyAsync(Command(), default)).Should().ThrowAsync<UnauthorizedAccessException>();
        store.ApplyCalls.Should().Be(0);
    }

    [Fact]
    public async Task Missing_original_revision_rejects_before_store_and_never_asks_for_latest()
    {
        var store = new Store(); var service = Service(store, Actor());
        var error = await FluentActions.Awaiting(() => service.ApplyAsync(Command() with { ExpectedRevision = null }, default))
            .Should().ThrowAsync<SafeEditConflictException>();
        error.Which.Code.Should().Be(SafeEditConflictCodes.MissingReadVersion);
        store.ApplyCalls.Should().Be(0); store.ReadCalls.Should().Be(0);
    }

    [Fact]
    public async Task Service_derives_actor_scope_and_passes_normalized_intent_without_substituting_revision()
    {
        var store = new Store(); var service = Service(store, Actor() with { IsServiceActor = true });
        await service.ApplyAsync(Command() with { ExpectedRevision = 7 }, default);
        store.Scope.Should().Be(new WorkItemEditScope(Tenant, ActorId, true, "ContextHub", ActorName: "synthetic"));
        store.Command!.ExpectedRevision.Should().Be(7);
        store.Command.RequestId.Should().Be("synthetic-request");
        store.Command.Patch.Title.Should().Be("trimmed");
        store.ReadCalls.Should().Be(0);
    }

    [Fact]
    public async Task Receipt_read_requires_current_read_authority_and_exact_request_identity()
    {
        var store = new Store(); var denied = Service(store, Actor() with { Scopes = [SecurityScopes.MemoryWrite] });
        await FluentActions.Awaiting(() => denied.ReadReceiptAsync(Recovery(), default))
            .Should().ThrowAsync<UnauthorizedAccessException>();
        store.ReadCalls.Should().Be(0);
        var service = Service(store, Actor());
        (await service.ReadReceiptAsync(Recovery(), default)).Should().BeNull();
        store.ReadId.Should().Be((WorkId, "key")); store.ApplyCalls.Should().Be(0);
    }

    [Fact]
    public async Task Store_conflicts_are_not_retried_or_converted_to_success()
    {
        var store = new Store { Failure = new SafeEditConflictException(SafeEditConflictCodes.RequestIdentityMismatch) };
        var error = await FluentActions.Awaiting(() => Service(store, Actor()).ApplyAsync(Command(), default))
            .Should().ThrowAsync<SafeEditConflictException>();
        error.Which.Code.Should().Be(SafeEditConflictCodes.RequestIdentityMismatch);
        store.ApplyCalls.Should().Be(1); store.ReadCalls.Should().Be(0);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Governance_exclusion_requires_both_existing_admin_and_tracker_scope(bool admin, bool trackerScope)
    {
        var actor = Actor() with
        {
            Role = admin ? TenantUserRole.Admin : TenantUserRole.Member,
            Scopes = trackerScope ? [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite, SecurityScopes.GovernanceTrackerManage]
                : [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite]
        };
        var store = new Store();
        await FluentActions.Awaiting(() => Service(store, actor).ApplyAsync(Command() with
        {
            Operation = WorkItemEditOperation.GovernanceExclusion,
            Patch = new(GovernanceExclusion: new("review-id", "reason"))
        }, default)).Should().ThrowAsync<UnauthorizedAccessException>();
        store.ApplyCalls.Should().Be(0);
    }

    [Fact]
    public async Task Governance_authority_and_actor_name_are_server_resolved_not_client_patch_fields()
    {
        var actor = Actor() with
        {
            Role = TenantUserRole.Admin,
            Scopes = [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite, SecurityScopes.GovernanceTrackerManage]
        };
        var store = new Store();
        await Service(store, actor).ApplyAsync(Command() with
        {
            Operation = WorkItemEditOperation.GovernanceExclusion,
            Patch = new(GovernanceExclusion: new(" review-id ", " reason "))
        }, default);
        store.Scope!.CanManageGovernanceTrackers.Should().BeTrue();
        store.Scope.ActorName.Should().Be("synthetic");
        store.Command!.Patch.GovernanceExclusion.Should().Be(new WorkItemGovernanceExclusionChange("review-id", "reason"));
        // Snapshot ownership and audit/receipt atomicity require the real PostgreSQL store; this only proves dispatch authority.
    }

    [Fact]
    public void Privileged_relationship_change_is_hashed_and_cannot_be_smuggled_into_an_ordinary_edit()
    {
        var command = Command() with
        {
            Operation = WorkItemEditOperation.GovernanceExclusion,
            Patch = new(GovernanceExclusion: new("review", "reason"))
        };
        WorkItemEditContract.Hash(command).Should().NotBe(WorkItemEditContract.Hash(command with
        { Patch = new(GovernanceExclusion: new("review", "different")) }));
        WorkItemEditContract.Hash(command).Should().NotBe(WorkItemEditContract.Hash(command with
        { Patch = new(GovernanceExclusion: new("review", "reason", false)) }));
        FluentActions.Invoking(() => WorkItemEditContract.Normalize(command with { Operation = WorkItemEditOperation.Update }))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Governance_relationship_change_preserves_revocation_history_and_real_noop_revision()
    {
        var item = new ProjectWorkItem { Id = WorkId, ProjectId = "ContextHub", Revision = 1 };
        var command = Command() with
        {
            Operation = WorkItemEditOperation.GovernanceExclusion,
            Patch = new(GovernanceExclusion: new("review", "reason"))
        };
        WorkItemEditAggregate.Apply(item, command, DateTimeOffset.UnixEpoch, "server-actor").Should().BeTrue();
        item.Revision.Should().Be(2);
        WorkItemEditAggregate.Apply(item, command with { ExpectedRevision = 2 }, DateTimeOffset.UnixEpoch.AddHours(1), "other-server-actor")
            .Should().BeFalse();
        item.Revision.Should().Be(2);
        WorkItemEditAggregate.Apply(item, command with { ExpectedRevision = 2, Patch = new(GovernanceExclusion: new("review", "revoked", false)) },
            DateTimeOffset.UnixEpoch.AddHours(2), "server-actor").Should().BeTrue();
        var relation = WorkItemEditProjection.ToResult(item).GovernanceExclusions.Single();
        relation.IsActive.Should().BeFalse(); relation.Actor.Should().Be("server-actor");
        relation.RevokedAt.Should().Be(DateTimeOffset.UnixEpoch.AddHours(2)); item.Revision.Should().Be(3);
    }

    [Fact]
    public void Real_Npgsql_model_has_parent_concurrency_and_scoped_receipt_history_keys_without_opening_a_connection()
    {
        using var db = new MemoryDbContext(new DbContextOptionsBuilder<MemoryDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options);
        var parent = db.Model.FindEntityType(typeof(ProjectWorkItem))!;
        parent.FindProperty(nameof(ProjectWorkItem.Revision))!.IsConcurrencyToken.Should().BeTrue();
        var history = db.Model.FindEntityType(typeof(WorkItemEditHistory))!;
        history.GetIndexes().Should().Contain(index => index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual(new[] { "WorkItemId", "Revision" }));
        var receipt = db.Model.FindEntityType(typeof(WorkItemEditReceiptRecord))!;
        receipt.GetIndexes().Should().Contain(index => index.IsUnique && index.Properties.Select(property => property.Name)
            .SequenceEqual(new[] { "TenantId", "ActorUserId", "ProjectKey", "WorkItemId", "RequestId" }));
        var sql = db.WorkItemEditReceipts.Where(row => row.TenantId == Tenant && row.ActorUserId == ActorId
            && row.ProjectKey == "ContextHub" && row.WorkItemId == WorkId && row.RequestId == "key")
            .Select(row => row.ResultJson).ToQueryString();
        sql.Should().Contain("tenant_id").And.Contain("actor_user_id").And.Contain("project_key")
            .And.Contain("work_item_id").And.Contain("request_id");
        // Metadata/translation is not PostgreSQL execution, uniqueness enforcement or transaction proof.
    }

    [Fact]
    public void Legacy_read_payload_without_revision_stays_unknown_and_cannot_become_a_baseline_one_token()
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var result = WorkItemEditProjection.ToResult(new ProjectWorkItem { Id = WorkId, ProjectId = "ContextHub", Revision = 1 });
        var payload = JsonSerializer.SerializeToNode(result, json)!.AsObject();
        payload.Remove("revision");
        var legacy = payload.Deserialize<ProjectWorkItemResult>(json)!;
        legacy.Revision.Should().BeNull();
        FluentActions.Invoking(() => WorkItemEditContract.RequireRevision(legacy.Revision, 1)).Should()
            .Throw<SafeEditConflictException>().Which.Code.Should().Be(SafeEditConflictCodes.MissingReadVersion);
        payload["revision"] = 1;
        payload.Deserialize<ProjectWorkItemResult>(json)!.Revision.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Real_EF_tracker_selects_insert_for_a_new_caller_assigned_child_key(bool collisionShapedKey)
    {
        using var db = new MemoryDbContext(new DbContextOptionsBuilder<MemoryDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options);
        var item = new ProjectWorkItem { Id = WorkId, ProjectId = "ContextHub", Title = "existing", Revision = 1 };
        var existing = new ProjectWorkItemChecklistItem { WorkItemId = WorkId, Content = "existing", WorkItem = item };
        item.ChecklistItems.Add(existing); db.Attach(item);
        var original = item.ChecklistItems.ToDictionary(row => row.Id);
        var newId = collisionShapedKey ? Guid.Parse("8efd8262-686a-4f48-8d88-6ed40a436b7b") : Guid.NewGuid();
        WorkItemEditAggregate.Apply(item, Command() with
        {
            Operation = WorkItemEditOperation.ChecklistAdd,
            Patch = new(ChecklistItemId: newId, ChecklistContent: "new")
        }, DateTimeOffset.UnixEpoch);
        WorkItemEditStore.TrackChecklistChanges(db, item, original);
        db.ChangeTracker.DetectChanges();
        var added = item.ChecklistItems.Single(row => row.Id == newId);
        db.Entry(added).State.Should().Be(EntityState.Added, "even a key matching an untracked foreign row must INSERT, never UPDATE/reparent");
        item.ChecklistItems.Single(row => row.Id == existing.Id).Should().BeSameAs(existing);
        db.Entry(existing).State.Should().NotBe(EntityState.Added);
        // No INSERT or uniqueness check ran. Actual PostgreSQL collision/rollback remains unverified.
    }

    [Fact]
    public void Receipt_read_binds_the_original_fingerprint_and_stored_target_before_returning_data()
    {
        var command = WorkItemEditContract.Normalize(Command());
        var hash = WorkItemEditContract.Hash(command);
        var entity = new ProjectWorkItem { Id = WorkId, ProjectId = "ContextHub", Revision = 2 };
        var result = WorkItemEditProjection.ToResult(entity);
        var receipt = new WorkItemEditReceipt(Guid.NewGuid(), Guid.NewGuid(), "ContextHub", WorkId, command.RequestId,
            hash, WorkItemEditContract.IntentHashVersion, command.Operation, 1, 2, true, DateTimeOffset.UnixEpoch, result);
        var row = new WorkItemEditReceiptRecord
        {
            Id = receipt.ReceiptId,
            TenantId = Tenant,
            ActorUserId = ActorId,
            WorkItemId = WorkId,
            ProjectId = "ContextHub",
            ProjectKey = ProjectContext.IdentityKey("ContextHub"),
            RequestId = command.RequestId,
            RequestHash = hash,
            ResultJson = JsonSerializer.Serialize(receipt, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };
        WorkItemEditStore.ReadBoundReceipt(row, hash).Should().BeEquivalentTo(receipt);
        FluentActions.Invoking(() => WorkItemEditStore.ReadBoundReceipt(row, new string('0', 64)))
            .Should().Throw<SafeEditConflictException>().Which.Code.Should().Be(SafeEditConflictCodes.RequestIdentityMismatch);
        row.WorkItemId = Guid.NewGuid();
        FluentActions.Invoking(() => WorkItemEditStore.ReadBoundReceipt(row, hash)).Should().Throw<InvalidDataException>();
        // This executes the real binding helper, not a ledger or transaction simulation.
    }

    private static WorkItemEditingService Service(Store store, ContextHubRequestActor actor)
        => new(store, new RequestActorAccessor { Current = actor });
    private sealed class Store : IWorkItemEditStore
    {
        public int ApplyCalls { get; private set; }
        public int ReadCalls { get; private set; }
        public WorkItemEditScope? Scope { get; private set; }
        public WorkItemEditCommand? Command { get; private set; }
        public (Guid, string) ReadId { get; private set; }
        public Exception? Failure { get; init; }
        public Task<WorkItemEditReceipt> ApplyAsync(WorkItemEditScope scope, WorkItemEditCommand command, CancellationToken ct)
        {
            ApplyCalls++; Scope = scope; Command = command;
            if (Failure is not null) throw Failure;
            return Task.FromResult<WorkItemEditReceipt>(null!); // Only observes dispatch; it claims no persistence or replay behavior.
        }
        public Task<WorkItemEditReceipt?> ReadReceiptAsync(WorkItemEditScope scope, WorkItemEditRecoveryRequest request, CancellationToken ct)
        { ReadCalls++; ReadId = (request.WorkItemId, request.RequestId); return Task.FromResult<WorkItemEditReceipt?>(null); }
    }
}
