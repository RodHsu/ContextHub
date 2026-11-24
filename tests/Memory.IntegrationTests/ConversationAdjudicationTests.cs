using System.Data;
using System.Data.Common;
using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Memory.IntegrationTests;

public sealed class ConversationAdjudicationTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Preview_uses_latest_scoped_adjudication_evidence_and_counts_the_complete_ledger()
    {
        using var scope = CreateScope(out var db, out var service, out var actor);
        var insight = await SeedAsync(db, actor);
        var initial = (await Preview(service, insight)).Items.Single();
        var resolved = await service.AdjudicateAsync(Request(insight, initial, ConversationAdjudicationAction.ResolveSkipped), default);
        var tiedAt = resolved.CreatedAt.AddSeconds(1);
        const int olderReceiptCount = 512;

        ConversationAdjudication FixtureReceipt(Guid id, DateTimeOffset createdAt, string fingerprint, Guid ownerId)
            => new()
            {
                Id = id,
                TenantId = actor.TenantId!.Value,
                OwnerUserId = ownerId,
                ProjectId = insight.ProjectId,
                InsightId = insight.Id,
                RequestHash = id.ToString("N"),
                EvidenceFingerprint = fingerprint,
                Action = nameof(ConversationAdjudicationAction.ResolveSkipped),
                Outcome = "ResolvedSkipped",
                PreviousStatus = nameof(ConversationPromotionStatus.RequiresUserDecision),
                ResultStatus = nameof(ConversationPromotionStatus.Skipped),
                Reason = "Retained history fixture",
                GovernanceRunId = "retained-history",
                Actor = "fixture",
                CreatedAt = createdAt
            };

        var olderFingerprint = new string('0', 64);
        var ownerId = actor.UserId!.Value;
        db.ConversationAdjudications.AddRange(Enumerable.Range(1, olderReceiptCount).Select(index =>
            FixtureReceipt(Guid.NewGuid(), tiedAt.AddSeconds(-index - 1), olderFingerprint, ownerId)));
        db.ConversationAdjudications.AddRange(
            FixtureReceipt(Guid.Parse("00000000-0000-0000-0000-000000000001"), tiedAt, olderFingerprint, ownerId),
            FixtureReceipt(Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"), tiedAt, initial.EvidenceFingerprint, ownerId),
            FixtureReceipt(Guid.NewGuid(), tiedAt.AddSeconds(1), olderFingerprint, Guid.NewGuid()));
        await db.SaveChangesAsync();

        var commands = new List<string>();
        var options = new DbContextOptionsBuilder<MemoryDbContext>(
                scope.ServiceProvider.GetRequiredService<DbContextOptions<MemoryDbContext>>())
            .LogTo(commands.Add, [RelationalEventId.CommandExecuted])
            .Options;
        await using var readDb = new MemoryDbContext(options);
        var previewReader = new ConversationAdjudicationService(
            readDb, scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>(),
            scope.ServiceProvider.GetRequiredService<IClock>());
        var preview = await previewReader.PreviewAsync(new(insight.ProjectId, IncludeInventoryToken: true), default);
        commands.Should().Contain(command =>
            command.Contains("ROW_NUMBER() OVER(PARTITION BY", StringComparison.Ordinal) &&
            command.Contains("<= 1", StringComparison.Ordinal),
            "the query must select one receipt per insight in PostgreSQL");
        preview.Items.Single().CanReopen.Should().BeFalse();
        preview.ResolvedHumanDecisionCount.Should().Be(1);
        preview.NewlyResolvedCount.Should().Be(olderReceiptCount + 3);
        var reread = await service.PreviewAsync(new(insight.ProjectId, ExpectedInventoryToken: preview.InventoryToken), default);
        reread.InventoryToken.Should().Be(preview.InventoryToken);
        reread.Items.Should().BeEquivalentTo(preview.Items);

        await db.ConversationInsights.Where(x => x.Id == insight.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Content, "Changed decision evidence"));
        (await Preview(service, insight)).Items.Single().CanReopen.Should().BeTrue();
    }

    [DockerRequiredTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Durable_adjudication_grants_are_checked_on_preview_receipt_apply_and_replay(bool canRead)
    {
        using var scope = CreateScope(out var db, out var service, out var actor);
        var completed = await SeedAsync(db, actor);
        var pending = new ConversationInsight
        {
            SessionId = completed.SessionId,
            CheckpointId = completed.CheckpointId,
            TenantId = completed.TenantId,
            OwnerUserId = completed.OwnerUserId,
            ProjectId = completed.ProjectId,
            Title = "Still requires an adjudication",
            Content = "Original evidence",
            DedupKey = "pending-" + Guid.NewGuid().ToString("N"),
            PromotionStatus = ConversationPromotionStatus.RequiresUserDecision,
            CreatedAt = completed.CreatedAt,
            UpdatedAt = completed.UpdatedAt
        };
        var grant = new TenantProjectGrant
        {
            TenantId = actor.TenantId!.Value,
            ProjectId = completed.ProjectId.ToUpperInvariant(),
            CanRead = true,
            CanWrite = true
        };
        db.ConversationInsights.Add(pending);
        db.TenantProjectGrants.Add(grant);
        await db.SaveChangesAsync();
        scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>().Current =
            actor with { AllowedProjectIds = [completed.ProjectId] };

        var initial = await Preview(service, completed);
        var completedRequest = Request(completed, initial.Items.Single(x => x.InsightId == completed.Id),
            ConversationAdjudicationAction.ResolveSkipped);
        var pendingRequest = Request(pending, initial.Items.Single(x => x.InsightId == pending.Id),
            ConversationAdjudicationAction.ResolveSkipped);
        await service.AdjudicateAsync(completedRequest, default);
        grant.CanRead = canRead;
        grant.CanWrite = false;
        await db.SaveChangesAsync();

        var previewRequest = new ConversationAdjudicationPreviewRequest(completed.ProjectId, ReceiptRequest: completedRequest);
        if (canRead)
        {
            (await service.PreviewAsync(previewRequest, default)).ReceiptLookup!.Found.Should().BeTrue();
            (await service.ReadReceiptAsync(completedRequest, default)).Found.Should().BeTrue();
        }
        else
        {
            await FluentActions.Invoking(() => service.PreviewAsync(previewRequest, default))
                .Should().ThrowAsync<UnauthorizedAccessException>();
            await FluentActions.Invoking(() => service.ReadReceiptAsync(completedRequest, default))
                .Should().ThrowAsync<UnauthorizedAccessException>();
        }
        await FluentActions.Invoking(() => service.AdjudicateAsync(pendingRequest, default))
            .Should().ThrowAsync<UnauthorizedAccessException>();
        await FluentActions.Invoking(() => service.AdjudicateAsync(completedRequest, default))
            .Should().ThrowAsync<UnauthorizedAccessException>();

        (await db.ConversationAdjudications.CountAsync(x => x.ProjectId == completed.ProjectId)).Should().Be(1);
        (await db.ConversationInsights.AsNoTracking().SingleAsync(x => x.Id == pending.Id))
            .PromotionStatus.Should().Be(ConversationPromotionStatus.RequiresUserDecision);
        var operationBinding = JsonSerializer.Serialize(new { operationId = pendingRequest.OperationId });
        (await db.SecurityAuditEvents.AnyAsync(x =>
            x.EventType == SecurityAuditEventType.ConversationInsightGovernanceUpdated &&
            EF.Functions.JsonContains(x.DetailsJson, operationBinding))).Should().BeFalse();
    }

    [DockerRequiredFact]
    public async Task Exact_receipt_read_should_recover_after_commit_when_the_response_is_lost()
    {
        using var scope = CreateScope(out var db, out var service, out var actor);
        var insight = await SeedAsync(db, actor);
        var request = Request(insight, (await Preview(service, insight)).Items.Single(),
            ConversationAdjudicationAction.ResolveSkipped) with
        {
            Reason = "  Exact historical decision  ",
            GovernanceRunId = "  exact-receipt-recovery  "
        };
        var missing = await service.ReadReceiptAsync(request, default);
        missing.Found.Should().BeFalse();
        missing.Receipt.Should().BeNull();

        var interceptor = new InterruptCommittedReceiptRead();
        var options = new DbContextOptionsBuilder<MemoryDbContext>(
                scope.ServiceProvider.GetRequiredService<DbContextOptions<MemoryDbContext>>())
            .AddInterceptors(interceptor)
            .Options;
        await using var interruptedDb = new MemoryDbContext(options);
        var interruptedService = new ConversationAdjudicationService(
            interruptedDb,
            scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>(),
            scope.ServiceProvider.GetRequiredService<IClock>());
        await FluentActions.Invoking(() => interruptedService.AdjudicateAsync(request, default))
            .Should().ThrowAsync<IOException>().WithMessage("Committed receipt response was interrupted.");
        interceptor.Interrupted.Should().BeTrue();

        using var recoveryScope = CreateScope(out _, out var recoveryService, out var recoveryActor);
        var actors = recoveryScope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        var readOnly = recoveryActor with { Scopes = [SecurityScopes.MemoryRead] };
        actors.Current = readOnly;
        var recovered = await recoveryService.ReadReceiptAsync(request with { ProjectId = insight.ProjectId.ToUpperInvariant() }, default);
        recovered.Found.Should().BeTrue();
        recovered.Receipt.Should().NotBeNull();
        recovered.Receipt!.OperationId.Should().Be(request.OperationId);
        recovered.Receipt.InsightId.Should().Be(insight.Id);
        recovered.Receipt.ResultStatus.Should().Be(nameof(ConversationPromotionStatus.Skipped));
        recovered.Receipt.Reason.Should().Be(request.Reason.Trim());
        recovered.Receipt.GovernanceRunId.Should().Be(request.GovernanceRunId.Trim());
        recovered.Receipt.IsReplay.Should().BeFalse();

        foreach (var mismatched in new[]
                 {
                     request with { Reason = request.Reason.Trim() },
                     request with { Action = ConversationAdjudicationAction.Reopen },
                     request with { ExpectedEvidenceToken = new string('0', 64) },
                     request with { GovernanceRunId = "different-recovery-run" },
                     request with { InsightId = Guid.NewGuid() },
                     request with { ProjectId = "other-project" }
                 })
        {
            var result = await recoveryService.ReadReceiptAsync(mismatched, default);
            result.Found.Should().BeFalse();
            result.Receipt.Should().BeNull();
        }

        actors.Current = readOnly with { UserId = Guid.NewGuid() };
        (await recoveryService.ReadReceiptAsync(request, default)).Found.Should().BeFalse();
        actors.Current = readOnly with { TenantId = Guid.NewGuid() };
        (await recoveryService.ReadReceiptAsync(request, default)).Found.Should().BeFalse();
        foreach (var denied in new[]
                 {
                     readOnly with { Role = TenantUserRole.Member },
                     readOnly with { IsServiceActor = true },
                     readOnly with { AllowedProjectIds = ["other-project"] },
                     readOnly with { Scopes = [SecurityScopes.ScheduledGovernance, SecurityScopes.MemoryRead] }
                 })
        {
            actors.Current = denied;
            await FluentActions.Invoking(() => recoveryService.ReadReceiptAsync(request, default))
                .Should().ThrowAsync<UnauthorizedAccessException>();
        }

        actors.Current = readOnly;
        await FluentActions.Invoking(() => recoveryService.AdjudicateAsync(request, default))
            .Should().ThrowAsync<UnauthorizedAccessException>();
        (await db.ConversationInsights.AsNoTracking().SingleAsync(row => row.Id == insight.Id)).PromotionStatus
            .Should().Be(ConversationPromotionStatus.Skipped);
        (await db.ConversationAdjudications.CountAsync(row => row.InsightId == insight.Id)).Should().Be(1);
        var operationBinding = JsonSerializer.Serialize(new { operationId = request.OperationId });
        (await db.SecurityAuditEvents.CountAsync(row =>
                row.TenantId == actor.TenantId && row.ActorUserId == actor.UserId &&
                row.EventType == SecurityAuditEventType.ConversationInsightGovernanceUpdated &&
                EF.Functions.JsonContains(row.DetailsJson, operationBinding)))
            .Should().Be(1);
    }

    private sealed class InterruptCommittedReceiptRead : DbCommandInterceptor
    {
        public bool Interrupted { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.Transaction is null && eventData.CommandSource == CommandSource.LinqQuery &&
                command.CommandText.Contains("conversation_adjudications", StringComparison.OrdinalIgnoreCase))
            {
                Interrupted = true;
                throw new IOException("Committed receipt response was interrupted.");
            }

            return ValueTask.FromResult(result);
        }
    }

    [DockerRequiredFact]
    public async Task Adjudication_cannot_report_a_receipt_from_the_callers_uncommitted_transaction()
    {
        using var scope = CreateScope(out var db, out var service, out var actor);
        var insight = await SeedAsync(db, actor);
        var request = Request(insight, (await Preview(service, insight)).Items.Single(), ConversationAdjudicationAction.ResolveSkipped);

        await using (var caller = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable))
        {
            var applyFailure = await Record.ExceptionAsync(() => service.AdjudicateAsync(request, default));
            var lookupFailure = await Record.ExceptionAsync(() => service.ReadReceiptAsync(request, default));
            var previewFailure = await Record.ExceptionAsync(() => service.PreviewAsync(
                new(insight.ProjectId) { ReceiptRequest = request }, default));
            (await db.ConversationAdjudications.AnyAsync(row => row.Id == request.OperationId)).Should().BeFalse();
            db.Database.CurrentTransaction.Should().BeSameAs(caller);
            applyFailure.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("own transaction");
            lookupFailure.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("own transaction");
            previewFailure.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("own transaction");
            await caller.RollbackAsync();
        }

        (await service.ReadReceiptAsync(request, default)).Found.Should().BeFalse();
        (await service.AdjudicateAsync(request, default)).IsReplay.Should().BeFalse();
        (await service.ReadReceiptAsync(request, default)).Found.Should().BeTrue();
    }

    [DockerRequiredFact]
    public async Task Adjudication_cannot_flush_or_discard_pending_writes()
    {
        using var scope = CreateScope(out var db, out var service, out var actor);
        var insight = await SeedAsync(db, actor);
        var request = Request(insight, (await Preview(service, insight)).Items.Single(), ConversationAdjudicationAction.ResolveSkipped);
        var pending = new MemoryItem
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = insight.ProjectId,
            Title = "Unrelated pending memory",
            Content = "Owned by the caller",
            CreatedAt = insight.CreatedAt,
            UpdatedAt = insight.UpdatedAt
        };
        db.MemoryItems.Add(pending);

        var failure = await Record.ExceptionAsync(() => service.AdjudicateAsync(request, default));
        var previewFailure = await Record.ExceptionAsync(() => Preview(service, insight));
        var lookupFailure = await Record.ExceptionAsync(() => service.ReadReceiptAsync(request, default));
        using var readScope = CreateScope(out var readDb, out var readService, out _);
        (await readDb.MemoryItems.AsNoTracking().AnyAsync(row => row.Id == pending.Id)).Should().BeFalse();
        (await readService.ReadReceiptAsync(request, default)).Found.Should().BeFalse();
        (await Preview(readService, insight)).Items.Single().Status.Should().Be(ConversationPromotionStatus.RequiresUserDecision);
        db.Entry(pending).State.Should().Be(EntityState.Added);
        failure.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("pending changes");
        previewFailure.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("pending changes");
        lookupFailure.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("pending changes");

        db.Entry(pending).State = EntityState.Detached;
        (await service.AdjudicateAsync(request, default)).IsReplay.Should().BeFalse();
        (await readService.ReadReceiptAsync(request, default)).Found.Should().BeTrue();
        (await readDb.MemoryItems.AsNoTracking().AnyAsync(row => row.Id == pending.Id)).Should().BeFalse();
    }

    [DockerRequiredFact]
    public async Task Receipt_collision_should_roll_back_the_disposition_and_audit_atomically()
    {
        using var scope = CreateScope(out var db, out var service, out var actor);
        var first = await SeedAsync(db, actor);
        var firstRequest = Request(first, (await Preview(service, first)).Items.Single(), ConversationAdjudicationAction.ResolveSkipped);
        await service.AdjudicateAsync(firstRequest, default);
        var second = await SeedAsync(db, actor);
        db.ChangeTracker.Clear();
        var request = Request(second, (await Preview(service, second)).Items.Single(), ConversationAdjudicationAction.ResolveSkipped)
            with
        { OperationId = firstRequest.OperationId };
        await FluentActions.Invoking(() => service.AdjudicateAsync(request, default)).Should().ThrowAsync<DbUpdateException>();
        (await db.ConversationInsights.AsNoTracking().SingleAsync(x => x.Id == second.Id)).PromotionStatus
            .Should().Be(ConversationPromotionStatus.RequiresUserDecision);
        (await db.ConversationAdjudications.CountAsync(x => x.ProjectId == second.ProjectId)).Should().Be(0);
        var audits = await db.SecurityAuditEvents.AsNoTracking().Where(x =>
            x.TenantId == actor.TenantId && x.EventType == SecurityAuditEventType.ConversationInsightGovernanceUpdated).ToArrayAsync();
        audits.Should().NotContain(x => x.DetailsJson.Contains(second.Id.ToString()));
    }

    [DockerRequiredFact]
    public async Task Concurrent_replays_should_commit_only_one_receipt_and_stale_competing_decision_should_fail()
    {
        using var seedScope = CreateScope(out var db, out var service, out var actor);
        var row = await SeedAsync(db, actor);
        var candidate = (await Preview(service, row)).Items.Single();
        var request = Request(row, candidate, ConversationAdjudicationAction.ResolveSkipped);
        var interceptor = new ConcurrentAdjudicationUpdateBarrier();
        var options = new DbContextOptionsBuilder<MemoryDbContext>(
                seedScope.ServiceProvider.GetRequiredService<DbContextOptions<MemoryDbContext>>())
            .AddInterceptors(interceptor)
            .Options;
        await using var firstDb = new MemoryDbContext(options);
        await using var secondDb = new MemoryDbContext(options);
        var actors = seedScope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        var clock = seedScope.ServiceProvider.GetRequiredService<IClock>();
        var firstService = new ConversationAdjudicationService(firstDb, actors, clock);
        var secondService = new ConversationAdjudicationService(secondDb, actors, clock);

        var results = await Task.WhenAll(
            firstService.AdjudicateAsync(request, default),
            secondService.AdjudicateAsync(request, default));

        interceptor.SerializationFailures.Should().Be(1);
        var committed = results.Should().ContainSingle(x => !x.IsReplay).Subject;
        var replayed = results.Should().ContainSingle(x => x.IsReplay).Subject;
        replayed.Should().Be(committed with { IsReplay = true });
        (await db.ConversationAdjudications.CountAsync(x => x.InsightId == row.Id)).Should().Be(1);
        var operationBinding = JsonSerializer.Serialize(new { operationId = request.OperationId });
        (await db.SecurityAuditEvents.CountAsync(audit =>
            audit.TenantId == actor.TenantId && audit.ActorUserId == actor.UserId &&
            audit.EventType == SecurityAuditEventType.ConversationInsightGovernanceUpdated &&
            EF.Functions.JsonContains(audit.DetailsJson, operationBinding))).Should().Be(1);
        using var retry = CreateScope(out _, out var retryService, out _);
        (await retryService.AdjudicateAsync(request, default)).IsReplay.Should().BeTrue();
        await FluentActions.Invoking(() => retryService.AdjudicateAsync(request with { OperationId = Guid.NewGuid() }, default))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    private sealed class ConcurrentAdjudicationUpdateBarrier : DbCommandInterceptor
    {
        private readonly TaskCompletionSource bothReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrived;
        private int serializationFailures;

        public int SerializationFailures => Volatile.Read(ref serializationFailures);

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.CommandSource == CommandSource.ExecuteUpdate)
            {
                // Both attempts have read the receipt and insight before either is allowed to update.
                if (Interlocked.Increment(ref arrived) == 2)
                    bothReady.TrySetResult();
                await bothReady.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }

            return result;
        }

        public override Task CommandFailedAsync(
            DbCommand command,
            CommandErrorEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Exception.GetBaseException() is Npgsql.PostgresException
                { SqlState: Npgsql.PostgresErrorCodes.SerializationFailure })
            {
                Interlocked.Increment(ref serializationFailures);
            }

            return Task.CompletedTask;
        }
    }

    [DockerRequiredFact]
    public async Task Rejected_proposal_reconciliation_should_require_an_exact_scoped_link()
    {
        using var scope = CreateScope(out var db, out var service, out var actor);
        var row = await SeedAsync(db, actor);
        var proposal = new ConversationInsight
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = row.ProjectId,
            SessionId = row.SessionId,
            CheckpointId = row.CheckpointId,
            SourceSystem = "chatgpt-mcp-gateway",
            Tags = ["chatgpt-proposal"],
            DedupKey = Guid.NewGuid().ToString(),
            PromotionStatus = ConversationPromotionStatus.Skipped,
            CreatedAt = row.CreatedAt,
            UpdatedAt = row.UpdatedAt
        };
        db.ConversationInsights.Add(proposal);
        row.MetadataJson = JsonSerializer.Serialize(new { proposalId = proposal.Id });
        await db.SaveChangesAsync();
        (await Preview(service, row)).Items.Single().CanReconcile.Should().BeFalse();
        row.SourceRef = $"chatgpt-proposal:{proposal.Id:D}";
        await db.SaveChangesAsync();
        var candidate = (await Preview(service, row)).Items.Single();
        candidate.Classification.Should().Be("ResolvedRejectedProposal");
        (await service.AdjudicateAsync(Request(row, candidate, ConversationAdjudicationAction.Reconcile), default)).Outcome
            .Should().Be("ResolvedRejectedProposal");
        proposal.PromotionStatus.Should().Be(ConversationPromotionStatus.Skipped);
    }

    [DockerRequiredFact]
    public async Task Failed_linked_proposal_requires_manual_review_and_inherits_a_host_block()
    {
        using var scope = CreateScope(out var db, out var service, out var actor);
        var row = await SeedAsync(db, actor);
        var proposal = new ConversationInsight
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = row.ProjectId,
            SessionId = row.SessionId,
            CheckpointId = row.CheckpointId,
            SourceSystem = "chatgpt-mcp-gateway",
            Tags = ["chatgpt-proposal"],
            DedupKey = Guid.NewGuid().ToString(),
            PromotionStatus = ConversationPromotionStatus.Failed,
            CreatedAt = row.CreatedAt,
            UpdatedAt = row.UpdatedAt
        };
        db.ConversationInsights.Add(proposal);
        row.MetadataJson = JsonSerializer.Serialize(new { proposalId = proposal.Id });
        await db.SaveChangesAsync();

        var unbound = (await Preview(service, row)).Items.ToDictionary(item => item.InsightId);
        unbound[row.Id].Classification.Should().Be("RequiresManualDecision");
        unbound[proposal.Id].Classification.Should().Be("ProposalApplyFailed");

        row.SourceRef = $"chatgpt-proposal:{proposal.Id:D}";
        await db.SaveChangesAsync();
        var preview = await Preview(service, row);
        var failed = preview.Items.Single(item => item.InsightId == row.Id);
        failed.Classification.Should().Be("ProposalApplyFailed");
        failed.CanReconcile.Should().BeFalse();
        preview.UnresolvedHumanDecisionCount.Should().Be(1);
        preview.ResolvedHumanDecisionCount.Should().Be(0);
        var reconcile = Request(row, failed, ConversationAdjudicationAction.Reconcile);
        await FluentActions.Invoking(() => service.AdjudicateAsync(reconcile, default))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*terminal resource evidence*");

        proposal = await db.ConversationInsights.SingleAsync(item => item.Id == proposal.Id);
        proposal.PromotionStatus = ConversationPromotionStatus.HostBlocked;
        await db.SaveChangesAsync();
        var blocked = (await Preview(service, row)).Items.Single(item => item.InsightId == row.Id);
        blocked.Classification.Should().Be("HostBlocked");
        blocked.CanReconcile.Should().BeFalse();
        blocked.CanReopen.Should().BeFalse();
        await FluentActions.Invoking(() => service.AdjudicateAsync(reconcile, default))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*evidence changed*");
        await FluentActions.Invoking(() => service.AdjudicateAsync(
                Request(row, blocked, ConversationAdjudicationAction.ResolveSkipped), default))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*Host-blocked*");
        (await db.ConversationAdjudications.CountAsync(item => item.InsightId == row.Id)).Should().Be(0);
        (await db.ConversationInsights.AsNoTracking().SingleAsync(item => item.Id == row.Id))
            .PromotionStatus.Should().Be(ConversationPromotionStatus.RequiresUserDecision);
    }

    [DockerRequiredFact]
    public async Task Applied_proposal_reconciliation_should_require_the_exact_promoted_memory_and_only_resolve_the_exception()
    {
        using var scope = CreateScope(out var db, out var service, out var actor);
        var row = await SeedAsync(db, actor);
        var memories = new[] { "Applied proposal result", "Unrelated memory" }
            .Select(title => new MemoryItem
            {
                TenantId = actor.TenantId,
                OwnerUserId = actor.UserId,
                ProjectId = row.ProjectId,
                ExternalKey = Guid.NewGuid().ToString("N"),
                Title = title,
                Content = title,
                Status = MemoryStatus.Active,
                CreatedAt = row.CreatedAt,
                UpdatedAt = row.UpdatedAt
            })
            .ToArray();
        var target = memories[0];
        var proposal = new ConversationInsight
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = row.ProjectId,
            SessionId = row.SessionId,
            CheckpointId = row.CheckpointId,
            SourceSystem = "chatgpt-mcp-gateway",
            Tags = ["chatgpt-proposal"],
            DedupKey = Guid.NewGuid().ToString(),
            PromotionStatus = ConversationPromotionStatus.Promoted,
            PromotedMemoryId = target.Id,
            CreatedAt = row.CreatedAt,
            UpdatedAt = row.UpdatedAt
        };
        db.MemoryItems.AddRange(memories);
        db.ConversationInsights.Add(proposal);
        row.MetadataJson = JsonSerializer.Serialize(new { proposalId = proposal.Id });
        row.SourceRef = $"chatgpt-proposal:{proposal.Id:D}";
        row.PromotedMemoryId = memories[1].Id;
        await db.SaveChangesAsync();

        var originalProposal = await db.ConversationInsights.AsNoTracking().SingleAsync(x => x.Id == proposal.Id);
        var originalTarget = await db.MemoryItems.AsNoTracking().SingleAsync(x => x.Id == target.Id);

        var mismatched = (await Preview(service, row)).Items.Single();
        mismatched.CanReconcile.Should().BeFalse();
        await FluentActions.Invoking(() => service.AdjudicateAsync(
                Request(row, mismatched, ConversationAdjudicationAction.Reconcile), default))
            .Should().ThrowAsync<InvalidOperationException>();

        row = await db.ConversationInsights.SingleAsync(x => x.Id == row.Id);
        row.PromotedMemoryId = target.Id;
        await db.SaveChangesAsync();

        var candidate = (await Preview(service, row)).Items.Single();
        candidate.Classification.Should().Be("ResolvedAppliedProposal");
        candidate.CanReconcile.Should().BeTrue();
        var request = Request(row, candidate, ConversationAdjudicationAction.Reconcile);
        var receipt = await service.AdjudicateAsync(request, default);
        receipt.Outcome.Should().Be("ResolvedAppliedProposal");
        receipt.ResultStatus.Should().Be("Skipped");
        (await service.AdjudicateAsync(request, default)).IsReplay.Should().BeTrue();

        var preview = await Preview(service, row);
        preview.UnresolvedHumanDecisionCount.Should().Be(0);
        preview.ResolvedHumanDecisionCount.Should().Be(1);
        (await db.ConversationAdjudications.CountAsync(x => x.InsightId == row.Id)).Should().Be(1);
        var persistedProposal = await db.ConversationInsights.AsNoTracking().SingleAsync(x => x.Id == proposal.Id);
        persistedProposal.PromotionStatus.Should().Be(ConversationPromotionStatus.Promoted);
        persistedProposal.PromotedMemoryId.Should().Be(target.Id);
        persistedProposal.UpdatedAt.Should().Be(originalProposal.UpdatedAt);
        var persistedTarget = await db.MemoryItems.AsNoTracking().SingleAsync(x => x.Id == target.Id);
        persistedTarget.Status.Should().Be(MemoryStatus.Active);
        persistedTarget.Content.Should().Be(originalTarget.Content);
        persistedTarget.UpdatedAt.Should().Be(originalTarget.UpdatedAt);
    }

    [DockerRequiredFact]
    public async Task Resolution_replay_reopen_and_ledger_should_preserve_audit_and_require_changed_evidence()
    {
        using var scope = CreateScope(out var db, out var service, out var actor);
        var row = await SeedAsync(db, actor);
        var preview = await Preview(service, row);
        var request = Request(row, preview.Items.Single(), ConversationAdjudicationAction.ResolveSkipped);
        var alias = row.ProjectId.ToUpperInvariant();
        (await service.PreviewAsync(new(alias), default)).Items.Single().EvidenceToken.Should().Be(request.ExpectedEvidenceToken);
        var receipt = await service.AdjudicateAsync(request with { ProjectId = alias }, default);
        receipt.ProjectId.Should().Be(row.ProjectId);
        receipt.ResultStatus.Should().Be("Skipped");
        (await service.AdjudicateAsync(request, default)).IsReplay.Should().BeTrue();
        (await db.ConversationAdjudications.CountAsync(x => x.InsightId == row.Id)).Should().Be(1);
        (await Preview(service, row)).ResolvedHumanDecisionCount.Should().Be(1);
        (await Preview(service, row)).UnresolvedHumanDecisionCount.Should().Be(0);
        await FluentActions.Invoking(() => service.AdjudicateAsync(request with { Reason = "different" }, default))
            .Should().ThrowAsync<InvalidOperationException>();
        var terminal = (await Preview(service, row)).Items.Single();
        terminal.CanReopen.Should().BeFalse();
        await FluentActions.Invoking(() => service.AdjudicateAsync(Request(row, terminal, ConversationAdjudicationAction.Reopen), default))
            .Should().ThrowAsync<InvalidOperationException>();
        row = await db.ConversationInsights.SingleAsync(x => x.Id == row.Id);
        row.Content = "Materially changed evidence";
        await db.SaveChangesAsync();
        var changed = (await Preview(service, row)).Items.Single();
        changed.CanReopen.Should().BeTrue();
        (await service.AdjudicateAsync(Request(row, changed, ConversationAdjudicationAction.Reopen), default)).ResultStatus
            .Should().Be("RequiresUserDecision");
        (await Preview(service, row)).UnresolvedHumanDecisionCount.Should().Be(1);
        var counters = await service.PreviewAsync(new(row.ProjectId, GovernanceRunId: "test-adjudication"), default);
        counters.NewlyResolvedCount.Should().Be(1);
        counters.ReopenedCount.Should().Be(1);
        counters.UnchangedCount.Should().Be(0);
        (await service.PreviewAsync(new(row.ProjectId, GovernanceRunId: "another-run"), default)).UnchangedCount.Should().Be(1);
        await db.ConversationInsights.Where(x => x.Id == row.Id).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.GovernanceEvidenceFingerprint, "previous-evidence")
            .SetProperty(x => x.GovernancePolicyVersion, "previous-policy"));
        var emptyCoverage = new KnowledgeGovernanceCoverageResult(Guid.NewGuid(), "empty", DateTimeOffset.UtcNow,
            0, 0, 0, 0, 0, 0, true, false, null);
        var plan = await scope.ServiceProvider.GetRequiredService<IFullGovernancePlanService>().BuildAsync(
            [row.ProjectId], "test-adjudication", new(emptyCoverage, [], []), default);
        plan.ResolvedHumanDecisionCount.Should().Be(0);
        plan.NewlyResolvedHumanDecisionCount.Should().Be(1);
        plan.ReopenedHumanDecisionCount.Should().Be(1);
        (await Preview(service, row)).Items.Single().Status.Should().Be(ConversationPromotionStatus.RequiresUserDecision);
        var audits = await db.SecurityAuditEvents.AsNoTracking().Where(x =>
            x.TenantId == actor.TenantId && x.EventType == SecurityAuditEventType.ConversationInsightGovernanceUpdated).ToArrayAsync();
        audits.Count(x => x.DetailsJson.Contains(row.Id.ToString())).Should().Be(2);
        var assembly = typeof(MemoryDbContext).Assembly;
        var migrationName = assembly.GetManifestResourceNames().Single(x => x.EndsWith(".052_conversation_adjudication.sql", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(migrationName)!);
        await db.Database.ExecuteSqlRawAsync(await reader.ReadToEndAsync());
        (await db.ConversationAdjudications.CountAsync(x => x.InsightId == row.Id)).Should().Be(2);
        await FluentActions.Invoking(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE conversation_adjudications SET reason = 'overwrite' WHERE id = {receipt.OperationId}"))
            .Should().ThrowAsync<Exception>().WithMessage("*append-only*");
        await FluentActions.Invoking(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM conversation_adjudications WHERE id = {receipt.OperationId}"))
            .Should().ThrowAsync<Exception>().WithMessage("*append-only*");
    }

    [DockerRequiredFact]
    public async Task Archived_delete_proposal_should_close_without_deleting_target_and_stale_token_should_fail()
    {
        using var scope = CreateScope(out var db, out var service, out var actor);
        var row = await SeedAsync(db, actor);
        var target = new MemoryItem
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = row.ProjectId,
            Title = "Archived evidence",
            Content = "Retain original",
            Status = MemoryStatus.Archived,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.MemoryItems.Add(target);
        row.SourceSystem = "chatgpt-mcp-gateway";
        row.Tags = ["chatgpt-proposal"];
        row.MetadataJson = JsonSerializer.Serialize(new { proposal = true, toolName = "memory_delete", payload = new { id = target.Id, projectId = row.ProjectId } });
        await db.SaveChangesAsync();
        var candidate = (await Preview(service, row)).Items.Single();
        candidate.Classification.Should().Be("ResolvedWithoutDelete");
        target.Content = "Changed archived evidence";
        await db.SaveChangesAsync();
        await FluentActions.Invoking(() => service.AdjudicateAsync(Request(row, candidate, ConversationAdjudicationAction.Reconcile), default))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*changed*");
        candidate = (await Preview(service, row)).Items.Single();
        var receipt = await service.AdjudicateAsync(Request(row, candidate, ConversationAdjudicationAction.Reconcile), default);
        receipt.Outcome.Should().Be("ResolvedWithoutDelete");
        (await db.MemoryItems.AsNoTracking().SingleAsync(x => x.Id == target.Id)).Content.Should().Be("Changed archived evidence");
    }

    [DockerRequiredFact]
    public async Task Archive_proposal_reconciles_only_when_the_exact_scoped_target_is_already_archived()
    {
        using var scope = CreateScope(out var db, out var service, out var actor);
        var row = await SeedAsync(db, actor);
        var target = new MemoryItem
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = row.ProjectId,
            Title = "Retain exact archive target",
            Content = "The reconciliation may only update the exception.",
            Status = MemoryStatus.Active,
            CreatedAt = row.CreatedAt,
            UpdatedAt = row.UpdatedAt
        };
        db.MemoryItems.Add(target);
        row.SourceSystem = "chatgpt-mcp-gateway";
        row.Tags = ["chatgpt-proposal"];
        row.MetadataJson = Payload(row.ProjectId);
        await db.SaveChangesAsync();

        (await Preview(service, row)).Items.Single().CanReconcile.Should().BeFalse();
        target.Status = MemoryStatus.Archived;
        row.MetadataJson = Payload("other-project");
        row.PromotedMemoryId = target.Id;
        await db.SaveChangesAsync();
        (await Preview(service, row)).Items.Single().CanReconcile.Should().BeFalse(
            "a promoted memory ID cannot supply a missing exact archive intent binding");

        row.MetadataJson = Payload(row.ProjectId, archived: false);
        await db.SaveChangesAsync();
        (await Preview(service, row)).Items.Single().CanReconcile.Should().BeFalse(
            "memory_archive with archived=false requests restoration");

        row.MetadataJson = Payload(row.ProjectId.ToUpperInvariant());
        target.Tags = ["legal-hold"];
        await db.SaveChangesAsync();
        var protectedTarget = (await Preview(service, row)).Items.Single();
        protectedTarget.Classification.Should().Be("ProtectedHold");
        protectedTarget.CanReconcile.Should().BeFalse();

        target.Tags = [];
        await db.SaveChangesAsync();
        var candidate = (await Preview(service, row)).Items.Single();
        candidate.Classification.Should().Be("ResolvedAlreadyArchived");
        candidate.CanReconcile.Should().BeTrue();
        var original = await db.MemoryItems.AsNoTracking().SingleAsync(item => item.Id == target.Id);
        var request = Request(row, candidate, ConversationAdjudicationAction.Reconcile);
        var receipt = await service.AdjudicateAsync(request, default);
        receipt.Outcome.Should().Be("ResolvedAlreadyArchived");
        receipt.ResultStatus.Should().Be("Skipped");
        (await service.AdjudicateAsync(request, default)).Should().Be(receipt with { IsReplay = true });
        (await db.ConversationAdjudications.CountAsync(item => item.InsightId == row.Id)).Should().Be(1);
        var persisted = await db.MemoryItems.AsNoTracking().SingleAsync(item => item.Id == target.Id);
        persisted.Should().BeEquivalentTo(original);

        string Payload(string project, bool archived = true) => JsonSerializer.Serialize(new
        {
            proposal = true,
            toolName = "memory_archive",
            payload = new { id = target.Id, projectId = project, archived }
        });
    }

    [DockerRequiredFact]
    public async Task Holds_host_blocks_and_scheduled_or_wrong_owner_actors_should_fail_closed()
    {
        using var scope = CreateScope(out var db, out var service, out var actor);
        var row = await SeedAsync(db, actor);
        foreach (var metadata in new[] { "[]", "{\"legalHold\":true}", "{\"securityHold\":\"unknown\"}" })
        {
            row = await db.ConversationInsights.SingleAsync(x => x.Id == row.Id);
            row.MetadataJson = metadata;
            await db.SaveChangesAsync();
            var candidate = (await Preview(service, row)).Items.Single();
            candidate.Classification.Should().Be("ProtectedHold");
            await FluentActions.Invoking(() => service.AdjudicateAsync(Request(row, candidate, ConversationAdjudicationAction.ResolveSkipped), default))
                .Should().ThrowAsync<InvalidOperationException>();
        }
        row = await db.ConversationInsights.SingleAsync(x => x.Id == row.Id);
        row.MetadataJson = "{}";
        row.PromotionStatus = ConversationPromotionStatus.HostBlocked;
        await db.SaveChangesAsync();
        var blocked = (await Preview(service, row)).Items.Single();
        await FluentActions.Invoking(() => service.AdjudicateAsync(Request(row, blocked, ConversationAdjudicationAction.ResolveSkipped), default))
            .Should().ThrowAsync<InvalidOperationException>();
        var actors = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        foreach (var denied in new[] { actor with { IsServiceActor = true }, actor with { Role = TenantUserRole.Member },
            actor with { Scopes = [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite, SecurityScopes.ScheduledGovernance] },
            actor with { AllowedProjectIds = ["another-project"] } })
        {
            actors.Current = denied;
            await FluentActions.Invoking(() => Preview(service, row)).Should().ThrowAsync<UnauthorizedAccessException>();
        }
        actors.Current = actor with { UserId = Guid.NewGuid() };
        (await Preview(service, row)).TotalCount.Should().Be(0);
        await FluentActions.Invoking(() => service.AdjudicateAsync(Request(row, blocked, ConversationAdjudicationAction.ResolveSkipped), default))
            .Should().ThrowAsync<KeyNotFoundException>();
        actors.Current = actor;
        (await db.ConversationAdjudications.CountAsync(x => x.InsightId == row.Id)).Should().Be(0);
    }

    [DockerRequiredFact]
    public async Task Guarded_baseline_should_reconcile_only_eligible_evidence_among_1492_exceptions()
    {
        using var scope = CreateScope(out var db, out var service, out var actor);
        var seed = await SeedAsync(db, actor);
        const int baselineCount = 1492;
        const string runId = "backfill-test";
        var rows = Enumerable.Range(0, baselineCount - 1).Select(i => new ConversationInsight
        {
            SessionId = seed.SessionId,
            CheckpointId = seed.CheckpointId,
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = seed.ProjectId,
            Title = $"historical {i}",
            Content = "Unbound historical record",
            DedupKey = Guid.NewGuid().ToString(),
            PromotionStatus = ConversationPromotionStatus.RequiresUserDecision,
            CreatedAt = seed.CreatedAt,
            UpdatedAt = seed.UpdatedAt
        }).ToArray();
        var rejectedProposal = new ConversationInsight
        {
            SessionId = seed.SessionId,
            CheckpointId = seed.CheckpointId,
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = seed.ProjectId,
            SourceSystem = ChatGptProposalService.SourceSystem,
            Tags = ["chatgpt-proposal"],
            DedupKey = Guid.NewGuid().ToString("N"),
            PromotionStatus = ConversationPromotionStatus.Skipped,
            CreatedAt = seed.CreatedAt,
            UpdatedAt = seed.UpdatedAt
        };
        var archivedTarget = new MemoryItem
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = seed.ProjectId,
            Title = "Archived historical evidence",
            Content = "Retain this evidence",
            Status = MemoryStatus.Archived,
            CreatedAt = seed.CreatedAt,
            UpdatedAt = seed.UpdatedAt
        };
        var rejected = rows[0];
        var protectedRow = rows[1];
        foreach (var bound in new[] { rejected, protectedRow })
        {
            bound.SourceRef = $"chatgpt-proposal:{rejectedProposal.Id:D}";
            bound.MetadataJson = JsonSerializer.Serialize(new { proposalId = rejectedProposal.Id });
        }
        protectedRow.Tags = ["legal-hold"];
        rows[2].PromotionStatus = ConversationPromotionStatus.HostBlocked;
        rows[3].PromotionStatus = ConversationPromotionStatus.Deferred;
        var archived = rows[4];
        archived.SourceSystem = ChatGptProposalService.SourceSystem;
        archived.Tags = ["chatgpt-proposal"];
        archived.MetadataJson = JsonSerializer.Serialize(new
        {
            proposal = true,
            toolName = "memory_delete",
            payload = new { id = archivedTarget.Id, projectId = seed.ProjectId }
        });
        db.ConversationInsights.AddRange(rows);
        db.ConversationInsights.Add(rejectedProposal);
        db.MemoryItems.Add(archivedTarget);
        await db.SaveChangesAsync();
        var interceptor = new CaptureOrderedCandidateQueries();
        var options = new DbContextOptionsBuilder<MemoryDbContext>(
                scope.ServiceProvider.GetRequiredService<DbContextOptions<MemoryDbContext>>())
            .AddInterceptors(interceptor)
            .Options;
        await using var reader = new MemoryDbContext(options);
        var guardedService = new ConversationAdjudicationService(
            reader,
            scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>(),
            scope.ServiceProvider.GetRequiredService<IClock>());
        var crossBoundaryRequest = new ConversationAdjudicationPreviewRequest(seed.ProjectId, 190, 30, runId);
        var ordinaryPage = await service.PreviewAsync(crossBoundaryRequest, default);
        var guardedPage = await guardedService.PreviewAsync(crossBoundaryRequest with { IncludeInventoryToken = true }, default);
        guardedPage.Should().BeEquivalentTo(ordinaryPage with { InventoryToken = guardedPage.InventoryToken },
            comparison => comparison.WithStrictOrdering());
        interceptor.Commands.Should().HaveCount(8);
        interceptor.Commands.Should().OnlyContain(sql => sql.Contains("LIMIT ", StringComparison.Ordinal));

        var candidates = new Dictionary<Guid, ConversationAdjudicationCandidate>();
        string? inventoryToken = null;
        var offset = 0;
        while (true)
        {
            var page = await service.PreviewAsync(new(seed.ProjectId, offset, 200, runId,
                IncludeInventoryToken: offset == 0, ExpectedInventoryToken: inventoryToken), default);
            if (offset == 0)
            {
                inventoryToken = page.InventoryToken;
                inventoryToken.Should().Be(guardedPage.InventoryToken);
                inventoryToken.Should().NotBeNullOrWhiteSpace();
            }
            page.InventoryToken.Should().Be(inventoryToken);
            page.TotalCount.Should().Be(baselineCount);
            page.UnresolvedHumanDecisionCount.Should().Be(baselineCount - 2);
            page.UnchangedCount.Should().Be(baselineCount);
            foreach (var item in page.Items) candidates.TryAdd(item.InsightId, item).Should().BeTrue();
            if (!page.HasMore) break;
            offset += page.Items.Count;
        }
        candidates.Keys.Should().BeEquivalentTo(rows.Select(row => row.Id).Append(seed.Id));
        candidates.Values.Where(candidate => candidate.CanReconcile).Select(candidate => candidate.InsightId)
            .Should().BeEquivalentTo(new[] { rejected.Id, archived.Id });
        candidates[protectedRow.Id].Classification.Should().Be("ProtectedHold");
        candidates[rows[2].Id].Classification.Should().Be("HostBlocked");
        candidates[rows[3].Id].Status.Should().Be(ConversationPromotionStatus.Deferred);
        (await db.ConversationAdjudications.CountAsync(x => x.ProjectId == seed.ProjectId)).Should().Be(0);

        foreach (var (row, expectedOutcome) in new[] { (rejected, "ResolvedRejectedProposal"), (archived, "ResolvedWithoutDelete") })
        {
            var request = new ConversationAdjudicationRequest(seed.ProjectId, row.Id, Guid.NewGuid(),
                ConversationAdjudicationAction.Reconcile, candidates[row.Id].EvidenceToken, "Reconcile bound terminal evidence", runId);
            var receipt = await service.AdjudicateAsync(request, default);
            receipt.Outcome.Should().Be(expectedOutcome);
            receipt.IsReplay.Should().BeFalse();
            var replay = await service.AdjudicateAsync(request, default);
            replay.Should().Be(receipt with { IsReplay = true });
        }
        await FluentActions.Invoking(() => service.PreviewAsync(new(seed.ProjectId, GovernanceRunId: runId,
                ExpectedInventoryToken: inventoryToken), default))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*preview*");
        var readBack = await service.PreviewAsync(new(seed.ProjectId, GovernanceRunId: runId), default);
        readBack.InventoryToken.Should().BeNull();
        readBack.TotalCount.Should().Be(baselineCount);
        readBack.UnresolvedHumanDecisionCount.Should().Be(baselineCount - 4);
        readBack.ResolvedHumanDecisionCount.Should().Be(2);
        readBack.NewlyResolvedCount.Should().Be(2);
        readBack.UnchangedCount.Should().Be(baselineCount - 2);
        (await db.ConversationAdjudications.Where(row => row.ProjectId == seed.ProjectId).Select(row => row.InsightId).ToListAsync())
            .Should().BeEquivalentTo(new[] { rejected.Id, archived.Id });
        var retainedTarget = await db.MemoryItems.AsNoTracking().SingleAsync(row => row.Id == archivedTarget.Id);
        retainedTarget.Status.Should().Be(MemoryStatus.Archived);
        retainedTarget.Content.Should().Be(archivedTarget.Content);
        (await db.ConversationInsights.AsNoTracking().SingleAsync(row => row.Id == rejectedProposal.Id))
            .PromotionStatus.Should().Be(ConversationPromotionStatus.Skipped);
        (await db.ConversationInsights.CountAsync(x => x.ProjectId == seed.ProjectId && x.PromotionStatus == ConversationPromotionStatus.Promoted)).Should().Be(0);
    }

    [DockerRequiredFact]
    public async Task Inventory_guard_should_detect_off_page_evidence_and_same_count_membership_changes()
    {
        using var scope = CreateScope(out var db, out var service, out var actor);
        var seed = await SeedAsync(db, actor);
        ConversationInsight NewInsight(ConversationPromotionStatus status) => new()
        {
            SessionId = seed.SessionId,
            CheckpointId = seed.CheckpointId,
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = seed.ProjectId,
            Title = "Additional historical record",
            Content = "Scoped inventory fixture",
            DedupKey = Guid.NewGuid().ToString("N"),
            PromotionStatus = status,
            CreatedAt = seed.CreatedAt,
            UpdatedAt = seed.UpdatedAt
        };

        var second = NewInsight(ConversationPromotionStatus.RequiresUserDecision);
        var proposal = NewInsight(ConversationPromotionStatus.Pending);
        proposal.SourceSystem = "chatgpt-mcp-gateway";
        proposal.Tags = ["chatgpt-proposal"];
        db.ConversationInsights.AddRange(second, proposal);
        await db.SaveChangesAsync();

        const string runId = "inventory-watch";
        var baseline = await service.PreviewAsync(new(seed.ProjectId, Limit: 1, GovernanceRunId: runId),
            default);
        var offPage = baseline.Items.Single().InsightId == seed.Id ? second : seed;
        offPage.MetadataJson = JsonSerializer.Serialize(new { proposalId = proposal.Id });
        offPage.SourceRef = $"chatgpt-proposal:{proposal.Id:D}";
        await db.SaveChangesAsync();

        var request = new ConversationAdjudicationPreviewRequest(seed.ProjectId, Limit: 1, GovernanceRunId: runId,
            IncludeInventoryToken: true);
        var firstPage = await service.PreviewAsync(request, default);
        var nextPage = await service.PreviewAsync(request with
        {
            Offset = 1,
            IncludeInventoryToken = false,
            ExpectedInventoryToken = firstPage.InventoryToken
        }, default);
        nextPage.Items.Single().InsightId.Should().Be(offPage.Id);
        nextPage.Items.Single().CanReconcile.Should().BeFalse();
        nextPage.InventoryToken.Should().Be(firstPage.InventoryToken);
        await FluentActions.Invoking(() => service.PreviewAsync(request with
        {
            GovernanceRunId = "another-counter-scope",
            ExpectedInventoryToken = firstPage.InventoryToken
        }, default)).Should().ThrowAsync<InvalidOperationException>().WithMessage("*preview*");

        proposal.PromotionStatus = ConversationPromotionStatus.Skipped;
        await db.SaveChangesAsync();
        var afterEvidenceChange = await service.PreviewAsync(request, default);
        afterEvidenceChange.TotalCount.Should().Be(firstPage.TotalCount);
        afterEvidenceChange.Items.Single().EvidenceToken.Should().Be(firstPage.Items.Single().EvidenceToken);
        await FluentActions.Invoking(() => service.PreviewAsync(request with
        {
            Offset = 1,
            ExpectedInventoryToken = firstPage.InventoryToken
        }, default)).Should().ThrowAsync<InvalidOperationException>().WithMessage("*preview*");
        (await service.PreviewAsync(request with { Offset = 1 }, default)).Items.Single().CanReconcile.Should().BeTrue();

        offPage.PromotionStatus = ConversationPromotionStatus.Pending;
        var replacement = NewInsight(ConversationPromotionStatus.RequiresUserDecision);
        db.ConversationInsights.Add(replacement);
        await db.SaveChangesAsync();
        var afterReplacement = await service.PreviewAsync(request with { Limit = 200 }, default);
        afterReplacement.TotalCount.Should().Be(firstPage.TotalCount);
        afterReplacement.UnresolvedHumanDecisionCount.Should().Be(firstPage.UnresolvedHumanDecisionCount);
        afterReplacement.Items.Select(item => item.InsightId).Should().Contain(replacement.Id).And.NotContain(offPage.Id);
        await FluentActions.Invoking(() => service.PreviewAsync(request with
        {
            ExpectedInventoryToken = afterEvidenceChange.InventoryToken
        }, default)).Should().ThrowAsync<InvalidOperationException>().WithMessage("*preview*");
        (await db.ConversationAdjudications.CountAsync(x => x.ProjectId == seed.ProjectId)).Should().Be(0);
    }

    [DockerRequiredFact]
    public async Task Preview_should_hold_one_snapshot_when_a_change_commits_after_the_first_count()
    {
        using var scope = CreateScope(out var db, out var service, out var actor);
        var seed = await SeedAsync(db, actor);
        var request = new ConversationAdjudicationPreviewRequest(seed.ProjectId, IncludeInventoryToken: true);
        var baseline = await service.PreviewAsync(request, default);
        baseline.InventoryToken.Should().NotBeNullOrWhiteSpace();

        using var writerScope = environment.GetFactory().Services.CreateScope();
        var writer = writerScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var interceptor = new FirstConversationCountInterceptor(async cancellationToken =>
        {
            await using var transaction = await writer.Database.BeginTransactionAsync(cancellationToken);
            var changed = await writer.ConversationInsights.Where(row => row.Id == seed.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.PromotionStatus,
                    ConversationPromotionStatus.Pending), cancellationToken);
            changed.Should().Be(1);
            await transaction.CommitAsync(cancellationToken);
        });
        var readerOptions = new DbContextOptionsBuilder<MemoryDbContext>(
                scope.ServiceProvider.GetRequiredService<DbContextOptions<MemoryDbContext>>())
            .AddInterceptors(interceptor)
            .Options;
        await using var reader = new MemoryDbContext(readerOptions);
        var guardedService = new ConversationAdjudicationService(
            reader,
            scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>(),
            scope.ServiceProvider.GetRequiredService<IClock>());
        var guardedRequest = request with { ExpectedInventoryToken = baseline.InventoryToken };
        var observed = await guardedService.PreviewAsync(guardedRequest, default);

        interceptor.CommittedChange.Should().BeTrue();
        observed.TotalCount.Should().Be(1);
        observed.UnresolvedHumanDecisionCount.Should().Be(1);
        observed.UnchangedCount.Should().Be(1);
        observed.Items.Should().BeEquivalentTo(baseline.Items);
        observed.InventoryToken.Should().Be(baseline.InventoryToken);

        var refreshed = await service.PreviewAsync(request, default);
        refreshed.TotalCount.Should().Be(0);
        refreshed.Items.Should().BeEmpty();
        refreshed.InventoryToken.Should().NotBe(baseline.InventoryToken);
        await FluentActions.Invoking(() => service.PreviewAsync(guardedRequest, default))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*preview*");
        (await db.ConversationAdjudications.CountAsync(row => row.InsightId == seed.Id)).Should().Be(0);
    }

    [DockerRequiredTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reconciliation_fails_when_bound_authority_changes_before_the_subject_update(bool linkedProposal)
    {
        using var scope = CreateScope(out var db, out var service, out var actor);
        var insight = await SeedAsync(db, actor);
        var target = new MemoryItem
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = insight.ProjectId,
            Title = "Archived authority",
            Content = "Preserve the original evidence",
            Status = MemoryStatus.Archived,
            CreatedAt = insight.CreatedAt,
            UpdatedAt = insight.UpdatedAt
        };
        var proposal = new ConversationInsight
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = insight.ProjectId,
            SessionId = insight.SessionId,
            CheckpointId = insight.CheckpointId,
            SourceSystem = ChatGptProposalService.SourceSystem,
            Tags = ["chatgpt-proposal"],
            DedupKey = Guid.NewGuid().ToString("N"),
            PromotionStatus = ConversationPromotionStatus.Skipped,
            CreatedAt = insight.CreatedAt,
            UpdatedAt = insight.UpdatedAt
        };
        if (linkedProposal)
        {
            db.ConversationInsights.Add(proposal);
            insight.SourceRef = $"chatgpt-proposal:{proposal.Id:D}";
            insight.MetadataJson = JsonSerializer.Serialize(new { proposalId = proposal.Id });
        }
        else
        {
            db.MemoryItems.Add(target);
            insight.SourceSystem = ChatGptProposalService.SourceSystem;
            insight.Tags = ["chatgpt-proposal"];
            insight.MetadataJson = JsonSerializer.Serialize(new
            {
                proposal = true,
                toolName = "memory_archive",
                payload = new { id = target.Id, insight.ProjectId }
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        await db.SaveChangesAsync();
        var before = (await Preview(service, insight)).Items.Single();
        before.CanReconcile.Should().BeTrue();
        var request = Request(insight, before, ConversationAdjudicationAction.Reconcile);

        using var writerScope = CreateScope(out var writer, out _, out _);
        var interceptor = new ChangeAdjudicationEvidenceAfterRead(async token =>
        {
            if (linkedProposal)
            {
                var changed = await writer.ConversationInsights.Where(x => x.Id == proposal.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.PromotionStatus, ConversationPromotionStatus.HostBlocked), token);
                changed.Should().Be(1);
            }
            else
            {
                var restored = await writerScope.ServiceProvider.GetRequiredService<IMemoryService>()
                    .ArchiveAsync(new MemoryArchiveRequest(target.Id, insight.ProjectId, false, "Reopened authority"), token);
                restored.Status.Should().Be(MemoryStatus.Active);
            }
        });
        var options = new DbContextOptionsBuilder<MemoryDbContext>(
                scope.ServiceProvider.GetRequiredService<DbContextOptions<MemoryDbContext>>())
            .AddInterceptors(interceptor)
            .Options;
        await using var reader = new MemoryDbContext(options);
        var guarded = new ConversationAdjudicationService(reader,
            scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>(),
            scope.ServiceProvider.GetRequiredService<IClock>());

        await FluentActions.Invoking(() => guarded.AdjudicateAsync(request, default))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*new preview*");
        interceptor.CommittedChange.Should().BeTrue();
        (await service.ReadReceiptAsync(request, default)).Found.Should().BeFalse();
        var operationBinding = JsonSerializer.Serialize(new { operationId = request.OperationId });
        (await db.SecurityAuditEvents.AnyAsync(x =>
            x.EventType == SecurityAuditEventType.ConversationInsightGovernanceUpdated &&
            EF.Functions.JsonContains(x.DetailsJson, operationBinding))).Should().BeFalse();
        (await db.ConversationInsights.AsNoTracking().SingleAsync(x => x.Id == insight.Id))
            .PromotionStatus.Should().Be(ConversationPromotionStatus.RequiresUserDecision);
        var refreshed = (await Preview(service, insight)).Items.Single(x => x.InsightId == insight.Id);
        refreshed.Classification.Should().Be(linkedProposal ? "HostBlocked" : "RequiresManualDecision");
        refreshed.CanReconcile.Should().BeFalse();
    }

    [DockerRequiredFact]
    public async Task A_self_reference_does_not_reopen_a_manually_resolved_insight()
    {
        using var scope = CreateScope(out var db, out var service, out var actor);
        var row = await SeedAsync(db, actor);
        row.SourceRef = $"chatgpt-proposal:{row.Id:D}";
        row.MetadataJson = JsonSerializer.Serialize(new { proposalId = row.Id });
        await db.SaveChangesAsync();
        var before = (await Preview(service, row)).Items.Single();
        await service.AdjudicateAsync(Request(row, before, ConversationAdjudicationAction.ResolveSkipped), default);
        var resolved = (await Preview(service, row)).Items.Single();
        resolved.CanReopen.Should().BeFalse();
        resolved.EvidenceFingerprint.Should().Be(before.EvidenceFingerprint);
    }

    private sealed class ChangeAdjudicationEvidenceAfterRead(Func<CancellationToken, Task> write) : DbCommandInterceptor
    {
        public bool CommittedChange { get; private set; }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!CommittedChange && command.Transaction is not null &&
                command.CommandText.Contains("conversation_adjudications", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase))
            {
                await write(cancellationToken);
                CommittedChange = true;
            }

            return result;
        }
    }

    private sealed class CaptureOrderedCandidateQueries : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM conversation_insights", StringComparison.Ordinal) &&
                command.CommandText.Contains("ORDER BY", StringComparison.Ordinal))
                Commands.Add(command.CommandText);

            return ValueTask.FromResult(result);
        }
    }

    private sealed class FirstConversationCountInterceptor(Func<CancellationToken, Task> afterCount) : DbCommandInterceptor
    {
        public bool CommittedChange { get; private set; }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (!CommittedChange && eventData.CommandSource == CommandSource.LinqQuery &&
                command.CommandText.Contains("count(", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("conversation_insights", StringComparison.OrdinalIgnoreCase))
            {
                await afterCount(cancellationToken);
                CommittedChange = true;
            }

            return result;
        }
    }

    private IServiceScope CreateScope(out MemoryDbContext db, out IConversationAdjudicationService service, out ContextHubRequestActor actor)
    {
        var scope = environment.GetFactory().Services.CreateScope();
        db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var user = db.TenantUsers.Single(x => x.Username == "contract-test-admin");
        actor = new(user.TenantId, user.Id, user.Username, TenantUserRole.Owner,
            [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite, SecurityScopes.SkillsRead], [], true);
        scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>().Current = actor;
        service = scope.ServiceProvider.GetRequiredService<IConversationAdjudicationService>();
        return scope;
    }

    private static async Task<ConversationInsight> SeedAsync(MemoryDbContext db, ContextHubRequestActor actor)
    {
        var now = DateTimeOffset.UtcNow;
        var project = $"adjudication-{Guid.NewGuid():N}";
        var session = new ConversationSession
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = project,
            ConversationId = project,
            SourceSystem = "test",
            StartedAt = now,
            LastCheckpointAt = now,
            UpdatedAt = now
        };
        var checkpoint = new ConversationCheckpoint
        {
            SessionId = session.Id,
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = project,
            ConversationId = project,
            TurnId = "1",
            SourceSystem = "test",
            DedupKey = project,
            CreatedAt = now
        };
        var row = new ConversationInsight
        {
            SessionId = session.Id,
            CheckpointId = checkpoint.Id,
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = project,
            ConversationId = project,
            Title = "Historical exception",
            Content = "Original evidence",
            DedupKey = project,
            PromotionStatus = ConversationPromotionStatus.RequiresUserDecision,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.AddRange(session, checkpoint, row);
        await db.SaveChangesAsync();
        return row;
    }

    private static Task<ConversationAdjudicationPreviewResult> Preview(IConversationAdjudicationService service, ConversationInsight row)
        => service.PreviewAsync(new(row.ProjectId), default);
    private static ConversationAdjudicationRequest Request(ConversationInsight row, ConversationAdjudicationCandidate candidate, ConversationAdjudicationAction action)
        => new(row.ProjectId, row.Id, Guid.NewGuid(), action, candidate.EvidenceToken, "Reviewed current evidence", "test-adjudication");
}
