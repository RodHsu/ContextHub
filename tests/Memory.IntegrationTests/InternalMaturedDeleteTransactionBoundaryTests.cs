using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Memory.IntegrationTests;

public sealed class InternalMaturedDeleteTransactionBoundaryTests(ContainerTestEnvironment environment)
    : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Cancellation_After_Tombstone_Insert_Should_Rollback_Delete_And_Allow_Claim_Retry()
    {
        var (owner, memoryId) = await PrepareMaturedCandidateAsync();
        var dataSource = GetDataSource();
        var pause = await InstallTombstonePauseTriggerAsync(dataSource, memoryId);
        using var cancellation = new CancellationTokenSource();
        Task<InternalMaturedDeleteBatchResult>? operation = null;

        try
        {
            var executor = environment.GetFactory().Services.GetRequiredService<IInternalMaturedDeleteExecutor>();
            operation = executor.ExecuteNextBatchAsync(cancellation.Token);
            await WaitForPausedBackendAsync(pause);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            await WaitForBackendGoneAsync(pause);
        }
        finally
        {
            cancellation.Cancel();
            if (operation is not null)
            {
                try
                {
                    await operation.WaitAsync(TimeSpan.FromSeconds(15));
                }
                catch
                {
                }
            }

            await RemoveTombstonePauseTriggerAsync(pause);
        }

        await AssertRolledBackAsync(owner, memoryId, expectedClaimError: "Cancelled");
        await RetryAndAssertDeletedAsync(owner, memoryId);
    }

    [DockerRequiredFact]
    public async Task Backend_Termination_After_Tombstone_Insert_Should_Rollback_Delete_And_Allow_Claim_Retry()
    {
        var (owner, memoryId) = await PrepareMaturedCandidateAsync();
        var dataSource = GetDataSource();
        var pause = await InstallTombstonePauseTriggerAsync(dataSource, memoryId);
        Task<InternalMaturedDeleteBatchResult>? operation = null;
        int? backendPid = null;

        try
        {
            var executor = environment.GetFactory().Services.GetRequiredService<IInternalMaturedDeleteExecutor>();
            operation = executor.ExecuteNextBatchAsync(CancellationToken.None);
            backendPid = await WaitForPausedBackendAsync(pause);
            await TerminateBackendAsync(pause.DataSource, backendPid.Value);

            var failed = await operation.WaitAsync(TimeSpan.FromSeconds(15));
            failed.ScannedCount.Should().Be(1);
            failed.DeletedCount.Should().Be(0);
            failed.CancelledCount.Should().Be(0);
            failed.FailedCount.Should().Be(1);
            failed.TombstoneIds.Should().BeEmpty();
            failed.AuditIds.Should().BeEmpty();
        }
        finally
        {
            if (operation is not null && !operation.IsCompleted)
            {
                try
                {
                    if (backendPid is null)
                    {
                        backendPid = await TryGetPausedBackendAsync(pause);
                    }

                    if (backendPid is not null)
                    {
                        await TerminateBackendAsync(pause.DataSource, backendPid.Value);
                    }
                }
                catch
                {
                }

                try
                {
                    await operation.WaitAsync(TimeSpan.FromSeconds(15));
                }
                catch
                {
                }
            }

            await RemoveTombstonePauseTriggerAsync(pause);
        }

        await AssertRolledBackAsync(owner, memoryId);
        await RetryAndAssertDeletedAsync(owner, memoryId);
    }

    private async Task<(TestOwner Owner, Guid MemoryId)> PrepareMaturedCandidateAsync()
    {
        var owner = await CreateOwnerAsync();
        using var scope = environment.GetFactory().Services.CreateScope();
        UseActor(scope.ServiceProvider, owner.Actor);
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var retention = scope.ServiceProvider.GetRequiredService<IAutonomousRetentionService>();
        var projectId = $"retention-boundary-{Guid.NewGuid():N}";
        var memory = CreateMemory(owner.Actor, projectId);

        db.MemoryItems.Add(memory);
        db.MemoryItemRevisions.Add(new MemoryItemRevision
        {
            MemoryItemId = memory.Id,
            Version = 1,
            Title = memory.Title,
            Content = memory.Content,
            Summary = memory.Summary,
            MetadataJson = memory.MetadataJson,
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.MemoryItemChunks.Add(new MemoryItemChunk
        {
            MemoryItemId = memory.Id,
            ChunkKind = ChunkKind.Document,
            ChunkIndex = 0,
            ChunkText = memory.Content,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(CancellationToken.None);

        await retention.QuarantineAsync(memory.Id, projectId, $"boundary-quarantine-{Guid.NewGuid():N}", CancellationToken.None);
        var state = await db.MemoryRetentionStates.SingleAsync(x => x.ResourceId == memory.Id);
        state.QuarantinedAt = DateTimeOffset.UtcNow.AddDays(-8);
        state.DeleteEligibleAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        state.LifecycleStatus = "Eligible";
        state.ClaimToken = string.Empty;
        state.ClaimedAt = null;
        await db.SaveChangesAsync(CancellationToken.None);

        return (owner, memory.Id);
    }

    private async Task<TestOwner> CreateOwnerAsync()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Slug = $"retention-boundary-{tenantId:N}",
            DisplayName = "Internal matured-delete transaction boundary test tenant",
            Status = TenantStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        });
        db.TenantUsers.Add(new TenantUser
        {
            Id = userId,
            TenantId = tenantId,
            Username = $"retention-boundary-{userId:N}",
            DisplayName = "Internal matured-delete transaction boundary test owner",
            Role = TenantUserRole.Admin,
            Status = TenantUserStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync(CancellationToken.None);

        return new TestOwner(
            new ContextHubRequestActor(
                tenantId,
                userId,
                $"retention-boundary-{userId:N}",
                TenantUserRole.Admin,
                [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite],
                [],
                IsAuthenticated: true),
            tenantId,
            userId);
    }

    private async Task AssertRolledBackAsync(TestOwner owner, Guid memoryId, string? expectedClaimError = null)
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseActor(scope.ServiceProvider, owner.Actor);
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

        (await db.MemoryItems.AnyAsync(x => x.Id == memoryId)).Should().BeTrue();
        (await db.MemoryItemRevisions.AnyAsync(x => x.MemoryItemId == memoryId)).Should().BeTrue();
        (await db.MemoryItemChunks.AnyAsync(x => x.MemoryItemId == memoryId)).Should().BeTrue();
        (await db.ResourceTombstones.AnyAsync(x => x.ResourceId == memoryId)).Should().BeFalse();

        var maturedAudits = await db.SecurityAuditEvents.AsNoTracking()
            .Where(x => x.TenantId == owner.TenantId && x.Outcome == "MaturedHardDelete")
            .ToArrayAsync();
        maturedAudits.Should().NotContain(x => x.DetailsJson.Contains(memoryId.ToString(), StringComparison.OrdinalIgnoreCase));

        var state = await db.MemoryRetentionStates.AsNoTracking().SingleAsync(x => x.ResourceId == memoryId);
        state.LifecycleStatus.Should().Be("Eligible");
        state.ClaimToken.Should().BeEmpty();
        state.ClaimedAt.Should().BeNull();
        state.ClaimLastError.Should().NotBeNullOrWhiteSpace();
        if (expectedClaimError is not null)
        {
            state.ClaimLastError.Should().Be(expectedClaimError);
        }
    }

    private async Task RetryAndAssertDeletedAsync(TestOwner owner, Guid memoryId)
    {
        var executor = environment.GetFactory().Services.GetRequiredService<IInternalMaturedDeleteExecutor>();
        var retry = await executor.ExecuteNextBatchAsync(CancellationToken.None);
        retry.ScannedCount.Should().Be(1);
        retry.DeletedCount.Should().Be(1);
        retry.CancelledCount.Should().Be(0);
        retry.FailedCount.Should().Be(0);
        retry.TombstoneIds.Should().ContainSingle();
        retry.AuditIds.Should().ContainSingle();

        using var scope = environment.GetFactory().Services.CreateScope();
        UseActor(scope.ServiceProvider, owner.Actor);
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await db.MemoryItems.AnyAsync(x => x.Id == memoryId)).Should().BeFalse();
        (await db.MemoryRetentionStates.AnyAsync(x => x.ResourceId == memoryId)).Should().BeFalse();

        var tombstones = await db.ResourceTombstones.AsNoTracking()
            .Where(x => x.ResourceId == memoryId)
            .ToArrayAsync();
        tombstones.Should().ContainSingle();
        tombstones[0].Id.Should().Be(retry.TombstoneIds.Single());
        tombstones[0].AuditId.Should().Be(retry.AuditIds.Single());

        var maturedAudits = await db.SecurityAuditEvents.AsNoTracking()
            .Where(x => x.TenantId == owner.TenantId && x.Outcome == "MaturedHardDelete")
            .ToArrayAsync();
        maturedAudits.Should().ContainSingle(x =>
            x.Id == retry.AuditIds.Single() &&
            x.DetailsJson.Contains(memoryId.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    private NpgsqlDataSource GetDataSource()
        => environment.GetFactory().Services.GetRequiredService<NpgsqlDataSource>();

    private static async Task<PauseTrigger> InstallTombstonePauseTriggerAsync(NpgsqlDataSource dataSource, Guid resourceId)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var functionName = $"test_pause_tombstone_{suffix}";
        var triggerName = $"test_pause_tombstone_{suffix}";
        var marker = $"ctxhub-pause-{suffix}";
        await using var connection = await dataSource.OpenConnectionAsync(CancellationToken.None);

        await ExecuteDdlAsync(connection, $"""
            CREATE OR REPLACE FUNCTION public."{functionName}"()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $function$
            BEGIN
                IF NEW.resource_id = '{resourceId:D}'::uuid THEN
                    PERFORM set_config('application_name', '{marker}', true);
                    PERFORM pg_sleep(30);
                END IF;
                RETURN NEW;
            END;
            $function$;
            """);
        await ExecuteDdlAsync(connection, $"""
            CREATE TRIGGER "{triggerName}"
            AFTER INSERT ON public.resource_tombstones
            FOR EACH ROW EXECUTE FUNCTION public."{functionName}"();
            """);

        return new PauseTrigger(dataSource, triggerName, functionName, marker);
    }

    private static async Task RemoveTombstonePauseTriggerAsync(PauseTrigger pause)
    {
        await using var connection = await pause.DataSource.OpenConnectionAsync(CancellationToken.None);
        await ExecuteDdlAsync(connection, $"DROP TRIGGER IF EXISTS \"{pause.TriggerName}\" ON public.resource_tombstones;");
        await ExecuteDdlAsync(connection, $"DROP FUNCTION IF EXISTS public.\"{pause.FunctionName}\"();");
    }

    private static async Task<int> WaitForPausedBackendAsync(PauseTrigger pause)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var pid = await TryGetPausedBackendAsync(pause);
            if (pid is not null)
            {
                return pid.Value;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        throw new TimeoutException($"Timed out waiting for PostgreSQL trigger marker {pause.Marker}.");
    }

    private static async Task WaitForBackendGoneAsync(PauseTrigger pause)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await TryGetPausedBackendAsync(pause) is null)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        throw new TimeoutException($"Timed out waiting for PostgreSQL trigger marker {pause.Marker} to clear.");
    }

    private static async Task<int?> TryGetPausedBackendAsync(PauseTrigger pause)
    {
        await using var connection = await pause.DataSource.OpenConnectionAsync(CancellationToken.None);
        await using var command = new NpgsqlCommand("""
            SELECT pid
            FROM pg_stat_activity
            WHERE application_name = @marker
              AND state = 'active'
              AND pid <> pg_backend_pid()
            LIMIT 1;
            """, connection);
        command.Parameters.AddWithValue("marker", pause.Marker);
        var value = await command.ExecuteScalarAsync(CancellationToken.None);
        return value is int pid ? pid : null;
    }

    private static async Task TerminateBackendAsync(NpgsqlDataSource dataSource, int pid)
    {
        await using var connection = await dataSource.OpenConnectionAsync(CancellationToken.None);
        await using var command = new NpgsqlCommand("SELECT pg_terminate_backend(@pid);", connection);
        command.Parameters.AddWithValue("pid", pid);
        (await command.ExecuteScalarAsync(CancellationToken.None)).Should().Be(true);
    }

    private static async Task ExecuteDdlAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection)
        {
            CommandTimeout = 10
        };
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static MemoryItem CreateMemory(ContextHubRequestActor actor, string projectId)
        => new()
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = projectId,
            ExternalKey = $"retention-boundary:{Guid.NewGuid():N}",
            Scope = MemoryScope.Project,
            MemoryType = MemoryType.Episode,
            Title = "Internal matured-delete transaction boundary fixture",
            Content = "Disposable transaction-boundary fixture content.",
            Summary = "Synthetic transaction-boundary fixture.",
            Tags = ["machine-generated", "execution-evidence", "synthetic-disposable"],
            SourceType = "tool-execution",
            SourceRef = "integration/internal-matured-delete-transaction-boundary",
            Importance = .1m,
            Confidence = .2m,
            Status = MemoryStatus.Active,
            MetadataJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-90),
            UpdatedAt = DateTimeOffset.UtcNow.AddDays(-90)
        };

    private static void UseActor(IServiceProvider services, ContextHubRequestActor actor)
        => services.GetRequiredService<IRequestActorAccessor>().Current = actor;

    private sealed record PauseTrigger(
        NpgsqlDataSource DataSource,
        string TriggerName,
        string FunctionName,
        string Marker);

    private sealed record TestOwner(ContextHubRequestActor Actor, Guid TenantId, Guid UserId);
}
