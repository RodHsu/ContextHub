using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.Logging.Abstractions;

namespace Memory.UnitTests;

/// <summary>
/// Calls the actual proposal service against a narrow synthetic persistence boundary.
/// These tests establish dispatch/canonicalization, not SQL transactions, durable
/// one-shot markers, row locks, or production authorization integration.
/// </summary>
public sealed class WorkItemProposalServiceTests
{
    private static readonly Guid Tenant = Guid.Parse("4050a2cf-d5ea-4b06-a0b8-3fd36e6dbe29");
    private static readonly Guid Owner = Guid.Parse("d2ec5995-7412-4556-afd2-a9c18a3059f4");
    private static readonly Guid Target = Guid.Parse("0691f675-a516-47cd-9172-c63b18a92aa8");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Project = "ContextHub";

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-1L)]
    public async Task Create_requires_the_original_read_revision_before_adding_or_saving(long? revision)
    {
        var fixture = new Fixture();
        Func<Task> create = () => fixture.Service.CreateAsync(Request(revision: revision), default);

        (await create.Should().ThrowAsync<SafeEditConflictException>()).Which.Code
            .Should().Be(SafeEditConflictCodes.MissingReadVersion);
        fixture.Db.Saves.Should().Be(0);
        fixture.Db.Insights.Rows.Should().BeEmpty();
        fixture.Db.Sessions.Rows.Should().BeEmpty();
        fixture.Db.Checkpoints.Rows.Should().BeEmpty();
        fixture.Db.Audits.Rows.Should().BeEmpty();
        fixture.AssertNoOtherStoreOrLegacyCalls();
    }

    [Fact]
    public async Task Create_preserves_provided_key_and_original_version_and_stores_one_canonical_payload()
    {
        var fixture = new Fixture();
        var result = await fixture.Service.CreateAsync(Request(revision: 7, key: "caller-original-key"), default);

        var payload = Payload(result);
        payload.ExpectedRevision.Should().Be(7);
        payload.RequestId.Should().Be("caller-original-key");
        payload.WorkItemId.Should().Be(Target);
        AssertCanonicalStorage(fixture, result);
        fixture.Db.Saves.Should().Be(1);
        fixture.Workflow.Calls.Should().BeEmpty();
        fixture.AssertNoOtherStoreOrLegacyCalls();
    }

    [Fact]
    public async Task Only_create_fills_a_missing_key_and_separate_manual_proposals_get_distinct_keys()
    {
        var fixture = new Fixture();
        var first = await fixture.Service.CreateAsync(Request(), default);
        var second = await fixture.Service.CreateAsync(Request(), default);

        Payload(first).RequestId.Should().NotBeNullOrWhiteSpace();
        Payload(second).RequestId.Should().NotBe(Payload(first).RequestId);
        first.Id.Should().NotBe(second.Id);
        fixture.Db.Saves.Should().Be(2);
        AssertCanonicalStorage(fixture, first);
        AssertCanonicalStorage(fixture, second);
        fixture.AssertNoOtherStoreOrLegacyCalls();
    }

    [Fact]
    public async Task Governance_create_without_key_deduplicates_the_same_actor_run_and_intent()
    {
        var fixture = new Fixture();
        var request = Request(run: "synthetic-governance-run");
        var first = await fixture.Service.CreateAsync(request, default);
        var second = await fixture.Service.CreateAsync(request, default);

        second.Id.Should().Be(first.Id);
        Payload(second).RequestId.Should().Be(Payload(first).RequestId);
        fixture.Db.Insights.Rows.Should().ContainSingle();
        fixture.Db.Checkpoints.Rows.Should().ContainSingle();
        fixture.Db.Audits.Rows.Should().ContainSingle();
        fixture.Db.Saves.Should().Be(1);
        AssertCanonicalStorage(fixture, first);
        fixture.AssertNoOtherStoreOrLegacyCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Decision_does_not_backfill_a_missing_key_in_an_already_persisted_legacy_proposal(bool reject)
    {
        var fixture = new Fixture();
        var created = await fixture.Service.CreateAsync(Request(), default);
        var row = fixture.Db.Insights.Rows.Single();
        var legacyPayload = JsonSerializer.Serialize(new ProjectWorkItemArchiveRequest(Target, ExpectedRevision: 7), Json);
        row.Content = legacyPayload;
        row.MetadataJson = JsonSerializer.Serialize(new
        {
            toolName = WorkItemProposalContract.ToolName,
            payload = JsonSerializer.Deserialize<JsonElement>(legacyPayload)
        }, Json);
        var originalMetadata = row.MetadataJson; var saves = fixture.Db.Saves;
        fixture.Workflow.Handler = (_, _) =>
        {
            Payload(ChatGptProposalProjection.ToResult(row)).RequestId.Should().BeNull();
            return Task.FromException<ChatGptProposalResult>(new WorkItemProposalOutcomeUnknownException());
        };

        Func<Task> decide = () => Decide(fixture.Service, new(created.Id), reject);

        await decide.Should().ThrowAsync<WorkItemProposalOutcomeUnknownException>();
        row.Content.Should().Be(legacyPayload); row.MetadataJson.Should().Be(originalMetadata);
        row.PromotionStatus.Should().Be(ConversationPromotionStatus.Pending);
        fixture.Db.Saves.Should().Be(saves); fixture.Workflow.Calls.Should().ContainSingle();
        fixture.AssertNoOtherStoreOrLegacyCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Original_work_item_tag_cannot_fall_through_to_generic_writer_after_metadata_tool_retag(bool reject)
    {
        var fixture = new Fixture();
        var pending = await fixture.Service.CreateAsync(Request(key: "original-key"), default);
        var row = fixture.Db.Insights.Rows.Single();
        row.MetadataJson = JsonSerializer.Serialize(new { toolName = "memory_archive", payload = new { id = Target } }, Json);
        var saves = fixture.Db.Saves;
        fixture.Workflow.Handler = (_, _) => Task.FromException<ChatGptProposalResult>(new WorkItemProposalOutcomeUnknownException());

        Func<Task> decide = () => Decide(fixture.Service, new(pending.Id), reject);

        await decide.Should().ThrowAsync<WorkItemProposalOutcomeUnknownException>();
        fixture.Workflow.Calls.Should().ContainSingle();
        fixture.Db.Saves.Should().Be(saves);
        row.PromotionStatus.Should().Be(ConversationPromotionStatus.Pending);
        fixture.AssertNoOtherStoreOrLegacyCalls();
    }

    [Theory]
    [InlineData("target")]
    [InlineData("revision")]
    [InlineData("run")]
    [InlineData("operation")]
    [InlineData("actor")]
    [InlineData("tenant")]
    public async Task Governance_generated_request_identity_does_not_cross_intent_or_actor_boundaries(string changed)
    {
        var fixture = new Fixture();
        var first = await fixture.Service.CreateAsync(Request(run: "run-a"), default);
        var request = changed switch
        {
            "target" => Request(target: Guid.NewGuid(), run: "run-a"),
            "revision" => Request(revision: 8, run: "run-a"),
            "run" => Request(run: "run-b"),
            "operation" => Request(archived: false, run: "run-a"),
            _ => Request(run: "run-a")
        };
        if (changed == "actor") fixture.Actors.Current = Actor() with { UserId = Guid.NewGuid() };
        if (changed == "tenant") fixture.Actors.Current = Actor() with { TenantId = Guid.NewGuid() };
        var second = await fixture.Service.CreateAsync(request, default);

        second.Id.Should().NotBe(first.Id);
        Payload(second).RequestId.Should().NotBe(Payload(first).RequestId);
        fixture.Db.Insights.Rows.Should().HaveCount(2);
        fixture.Db.Saves.Should().Be(2);
        fixture.AssertNoOtherStoreOrLegacyCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Work_item_decisions_delegate_the_original_request_and_return_workflow_result_without_generic_save(bool reject)
    {
        var fixture = new Fixture();
        var pending = await fixture.Service.CreateAsync(Request(key: "already-persisted-key"), default);
        var row = fixture.Db.Insights.Rows.Single();
        var expected = pending with { Status = reject ? ChatGptProposalStatus.Rejected : ChatGptProposalStatus.Applied };
        fixture.Workflow.Handler = (_, _) => Task.FromResult(expected);
        var decision = new ChatGptProposalDecisionRequest(pending.Id, "original operator note");
        var saves = fixture.Db.Saves; var audits = fixture.Db.Audits.Rows.Count;

        var actual = await Decide(fixture.Service, decision, reject);

        actual.Should().BeSameAs(expected);
        fixture.Workflow.Calls.Should().ContainSingle().Which.Should().Be((reject, decision));
        fixture.Db.Saves.Should().Be(saves);
        fixture.Db.Audits.Rows.Should().HaveCount(audits);
        row.PromotionStatus.Should().Be(ConversationPromotionStatus.Pending,
            "the workflow owns lifecycle finalization, not the generic service branch");
        Payload(actual).RequestId.Should().Be("already-persisted-key");
        fixture.AssertNoOtherStoreOrLegacyCalls();
    }

    [Theory]
    [InlineData(false, "missing")]
    [InlineData(false, "stale")]
    [InlineData(false, "unknown")]
    [InlineData(false, "cancel")]
    [InlineData(true, "missing")]
    [InlineData(true, "stale")]
    [InlineData(true, "unknown")]
    [InlineData(true, "cancel")]
    public async Task Typed_workflow_failures_keep_the_pending_payload_and_never_fall_into_generic_failed_lifecycle(bool reject, string kind)
    {
        var fixture = new Fixture();
        var pending = await fixture.Service.CreateAsync(Request(key: "persisted-original-key"), default);
        var row = fixture.Db.Insights.Rows.Single();
        var originalMetadata = row.MetadataJson; var originalContent = row.Content;
        var saves = fixture.Db.Saves; var audits = fixture.Db.Audits.Rows.Count;
        Exception expected = kind switch
        {
            "missing" => new SafeEditConflictException(SafeEditConflictCodes.MissingReadVersion),
            "stale" => new SafeEditConflictException(SafeEditConflictCodes.StaleReadVersion, 12),
            "unknown" => new WorkItemProposalOutcomeUnknownException(),
            _ => new OperationCanceledException("synthetic cancellation")
        };
        fixture.Workflow.Handler = (_, _) => Task.FromException<ChatGptProposalResult>(expected);

        var actual = await Record.ExceptionAsync(() => Decide(fixture.Service, new(pending.Id, "keep exact intent"), reject));

        actual.Should().BeSameAs(expected);
        row.PromotionStatus.Should().Be(ConversationPromotionStatus.Pending);
        row.Error.Should().BeEmpty(); row.MetadataJson.Should().Be(originalMetadata); row.Content.Should().Be(originalContent);
        Payload(ChatGptProposalProjection.ToResult(row)).RequestId.Should().Be("persisted-original-key");
        fixture.Workflow.Calls.Should().ContainSingle();
        fixture.Db.Saves.Should().Be(saves); fixture.Db.Audits.Rows.Should().HaveCount(audits);
        fixture.AssertNoOtherStoreOrLegacyCalls();
    }

    [Theory]
    [InlineData(false, "tenant")]
    [InlineData(false, "owner")]
    [InlineData(false, "project")]
    [InlineData(true, "tenant")]
    [InlineData(true, "owner")]
    [InlineData(true, "project")]
    public async Task Foreign_proposals_do_not_reach_workflow_or_expose_the_private_project(bool reject, string boundary)
    {
        var fixture = new Fixture();
        var pending = await fixture.Service.CreateAsync(Request(), default);
        var row = fixture.Db.Insights.Rows.Single();
        if (boundary == "tenant") row.TenantId = Guid.NewGuid();
        if (boundary == "owner") row.OwnerUserId = Guid.NewGuid();
        row.ProjectId = "PrivateProjectThatMustNotAppear";
        if (boundary != "project") fixture.Actors.Current = Actor() with { AllowedProjectIds = [] };
        var saves = fixture.Db.Saves;

        var error = await Record.ExceptionAsync(() => Decide(fixture.Service, new(pending.Id), reject));

        error.Should().NotBeNull();
        // The existing missing-row exception may become an opaque KeyNotFoundException.
        // Neither form may expose the hidden row or fall through to a writer.
        (error is InvalidOperationException or KeyNotFoundException).Should().BeTrue();
        error!.Message.Should().NotContain(row.ProjectId);
        fixture.Workflow.Calls.Should().BeEmpty(); fixture.Db.Saves.Should().Be(saves);
        row.PromotionStatus.Should().Be(ConversationPromotionStatus.Pending);
        fixture.AssertNoOtherStoreOrLegacyCalls();
    }

    [Fact]
    public async Task Non_work_item_rejection_keeps_its_existing_generic_audit_save_and_idempotent_lifecycle()
    {
        var fixture = new Fixture();
        var request = Request() with
        {
            ToolName = "discussion_thread_archive",
            PayloadJson = JsonSerializer.Serialize(new DiscussionThreadArchiveRequest(Guid.NewGuid()), Json)
        };
        var pending = await fixture.Service.CreateAsync(request, default);
        var result = await fixture.Service.RejectAsync(new(pending.Id, " retained reason "), default);

        result.Status.Should().Be(ChatGptProposalStatus.Rejected); result.Error.Should().Be("retained reason");
        fixture.Db.Saves.Should().Be(2); fixture.Db.Audits.Rows.Should().HaveCount(2);
        fixture.Db.Insights.Rows.Single().PromotionStatus.Should().Be(ConversationPromotionStatus.Skipped);
        (await fixture.Service.RejectAsync(new(pending.Id, "different retry note"), default)).Should().BeEquivalentTo(result);
        fixture.Db.Saves.Should().Be(2); fixture.Workflow.Calls.Should().BeEmpty();
        fixture.AssertNoOtherStoreOrLegacyCalls();
    }

    private static Task<ChatGptProposalResult> Decide(ChatGptProposalService service, ChatGptProposalDecisionRequest request, bool reject)
        => reject ? service.RejectAsync(request, default) : service.ApproveAsync(request, default);

    private static ChatGptProposalCreateRequest Request(long? revision = 7, string? key = null, Guid? target = null,
        string? run = null, bool archived = true)
        => new(WorkItemProposalContract.ToolName, Project,
            JsonSerializer.Serialize(new ProjectWorkItemArchiveRequest(target ?? Target, archived, revision, key), Json),
            "synthetic proposal", "source-only service boundary", "synthetic-subject", GovernanceRunId: run);

    private static ProjectWorkItemArchiveRequest Payload(ChatGptProposalResult proposal)
        => JsonSerializer.Deserialize<ProjectWorkItemArchiveRequest>(proposal.PayloadJson, Json)!;

    private static void AssertCanonicalStorage(Fixture fixture, ChatGptProposalResult result)
    {
        var insight = fixture.Db.Insights.Rows.Single(row => row.Id == result.Id);
        var checkpoint = fixture.Db.Checkpoints.Rows.Single(row => row == insight.Checkpoint);
        insight.Content.Should().Be(result.PayloadJson);
        using var metadata = JsonDocument.Parse(insight.MetadataJson);
        metadata.RootElement.GetProperty("payload").GetRawText().Should().Be(result.PayloadJson);
        checkpoint.MetadataJson.Should().Be(insight.MetadataJson);
        var archive = Payload(result);
        var command = WorkItemProposalContract.Command(result.ProjectId, archive);
        command.ExpectedRevision.Should().Be(archive.ExpectedRevision);
        command.RequestId.Should().Be(archive.RequestId);
    }

    private static ContextHubRequestActor Actor() => new(Tenant, Owner, "synthetic actor", TenantUserRole.Member,
        [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite], [Project], true);

    private sealed class Fixture
    {
        public ProposalContext Db { get; }
        public RequestActorAccessor Actors { get; } = new() { Current = Actor() };
        public WorkflowBoundary Workflow { get; } = new();
        public ChatGptProposalService Service { get; }
        public List<string> LegacyCalls { get; } = [];

        public Fixture()
        {
            var context = DispatchProxy.Create<IApplicationDbContext, ProposalContext>();
            Db = (ProposalContext)context;
            Service = new(context, Forbidden<IMemoryService>(), Forbidden<IProjectInformationService>(),
                Forbidden<IProjectArtifactExchangeService>(), Forbidden<IManagedFileService>(),
                Forbidden<IProjectDiscussionService>(), Forbidden<ISuggestedActionService>(), Actors,
                new Clock(), NullLogger<ChatGptProposalService>.Instance, () => Workflow);
        }

        private T Forbidden<T>() where T : class
        {
            var proxy = DispatchProxy.Create<T, ForbiddenDependency>();
            ((ForbiddenDependency)(object)proxy).OnCall = name => LegacyCalls.Add(typeof(T).Name + "." + name);
            return proxy;
        }

        public void AssertNoOtherStoreOrLegacyCalls()
        {
            Db.ForbiddenCalls.Should().BeEmpty(); LegacyCalls.Should().BeEmpty();
        }
    }

    public class ForbiddenDependency : DispatchProxy
    {
        public Action<string> OnCall { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        { OnCall(method!.Name); throw new InvalidOperationException("Unexpected legacy dependency access."); }
    }

    private sealed class WorkflowBoundary : IWorkItemProposalWorkflow
    {
        public List<(bool Reject, ChatGptProposalDecisionRequest Request)> Calls { get; } = [];
        public Func<ChatGptProposalDecisionRequest, CancellationToken, Task<ChatGptProposalResult>> Handler { get; set; }
            = (_, _) => throw new InvalidOperationException("Unexpected workflow dispatch.");
        public Task<ChatGptProposalResult> ApproveAsync(ChatGptProposalDecisionRequest request, CancellationToken ct)
        { Calls.Add((false, request)); return Handler(request, ct); }
        public Task<ChatGptProposalResult> RejectAsync(ChatGptProposalDecisionRequest request, CancellationToken ct)
        { Calls.Add((true, request)); return Handler(request, ct); }
    }

    private sealed class Clock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch; }

    public class ProposalContext : DispatchProxy
    {
        public ProposalSet<ConversationSession> Sessions { get; } = new();
        public ProposalSet<ConversationCheckpoint> Checkpoints { get; } = new();
        public ProposalSet<ConversationInsight> Insights { get; } = new();
        public ProposalSet<SecurityAuditEvent> Audits { get; } = new();
        public List<string> ForbiddenCalls { get; } = [];
        public int Saves { get; private set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "get_ConversationSessions": return Sessions;
                case "get_ConversationCheckpoints": return Checkpoints;
                case "get_ConversationInsights": return Insights;
                case "get_SecurityAuditEvents": return Audits;
                case "SaveChangesAsync":
                    ((CancellationToken)args![0]!).ThrowIfCancellationRequested();
                    Saves++; return Task.FromResult(1);
                default:
                    ForbiddenCalls.Add(method.Name);
                    throw new InvalidOperationException("Proposal service must not read the latest work item or access unrelated stores.");
            }
        }
    }

    public sealed class ProposalSet<T> : DbSet<T>, IQueryable<T> where T : class
    {
        public List<T> Rows { get; } = [];
        public override Microsoft.EntityFrameworkCore.Metadata.IEntityType EntityType => throw new NotSupportedException();
        private IQueryable<T> Query => new ProposalQuery<T>(Rows);
        Type IQueryable.ElementType => typeof(T);
        Expression IQueryable.Expression => Query.Expression;
        IQueryProvider IQueryable.Provider => Query.Provider;
        IEnumerator<T> IEnumerable<T>.GetEnumerator() => Rows.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => Rows.GetEnumerator();
        public override ValueTask<EntityEntry<T>> AddAsync(T entity, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Rows.Add(entity);
            return ValueTask.FromResult<EntityEntry<T>>(null!);
        }
    }

    private sealed class ProposalQuery<T> : EnumerableQuery<T>, IAsyncEnumerable<T>, IQueryable<T>
    {
        public ProposalQuery(IEnumerable<T> rows) : base(rows) { }
        public ProposalQuery(Expression expression) : base(expression) { }
        IQueryProvider IQueryable.Provider => new ProposalProvider(this);
        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            => new ProposalEnumerator<T>(this.AsEnumerable().GetEnumerator(), cancellationToken);
    }

    private sealed class ProposalProvider(IQueryProvider inner) : IAsyncQueryProvider
    {
        public IQueryable CreateQuery(Expression expression) => throw new NotSupportedException();
        public IQueryable<T> CreateQuery<T>(Expression expression) => new ProposalQuery<T>(expression);
        public object? Execute(Expression expression) => inner.Execute(expression);
        public T Execute<T>(Expression expression) => inner.Execute<T>(expression);
        public T ExecuteAsync<T>(Expression expression, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resultType = typeof(T).GenericTypeArguments.Single();
            return (T)typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(resultType)
                .Invoke(null, [inner.Execute(expression)])!;
        }
    }

    private sealed class ProposalEnumerator<T>(IEnumerator<T> inner, CancellationToken token) : IAsyncEnumerator<T>
    {
        public T Current => inner.Current;
        public ValueTask DisposeAsync() { inner.Dispose(); return ValueTask.CompletedTask; }
        public ValueTask<bool> MoveNextAsync() { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(inner.MoveNext()); }
    }
}
