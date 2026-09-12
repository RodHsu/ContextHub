using System.Text;
using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Memory.UnitTests;

public sealed class MemoryTransferScoreContractTests
{
    [Fact]
    public async Task Preview_preflights_every_item_and_rejects_an_invalid_score_after_a_valid_item()
    {
        await using var db = CreateDbContext();
        var memoryService = new CountingMemoryService();
        var service = CreateService(db, memoryService);
        var request = new MemoryImportRequest(CreatePackageWithValidItemBeforeInvalidItem());

        var exception = (await ((Func<Task>)(() => service.PreviewImportAsync(request, CancellationToken.None)))
                .Should().ThrowAsync<MemoryScoreValidationException>())
            .Which;

        exception.Field.Should().Be("confidence");
        exception.NumericValue.Should().Be(95m);
        memoryService.UpsertCalls.Should().Be(0);
    }

    [Fact]
    public async Task Apply_preflights_the_complete_batch_before_any_create_or_overwrite()
    {
        await using var db = CreateDbContext();
        var memoryService = new CountingMemoryService();
        var service = CreateService(db, memoryService);
        var request = new MemoryImportRequest(
            CreatePackageWithValidItemBeforeInvalidItem(),
            ForceOverwrite: true);

        var exception = (await ((Func<Task>)(() => service.ApplyImportAsync(request, CancellationToken.None)))
                .Should().ThrowAsync<MemoryScoreValidationException>())
            .Which;

        exception.Field.Should().Be("confidence");
        exception.NumericValue.Should().Be(95m);
        memoryService.UpsertCalls.Should().Be(0, "neither a create nor an overwrite may start before full-batch score validation completes");
    }

    private static MemoryTransferService CreateService(MemoryDbContext db, IMemoryService memoryService)
        => new(
            db,
            memoryService,
            new UnusedRuntimeConfigurationAccessor(),
            new RequestActorAccessor { Current = ContextHubRequestActor.Unrestricted });

    private static MemoryDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MemoryDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1")
            .Options;
        return new MemoryDbContext(options);
    }

    private static string CreatePackageWithValidItemBeforeInvalidItem()
    {
        var now = DateTimeOffset.UtcNow;
        var bundle = new
        {
            version = 1,
            @namespace = "numeric-contract-test",
            exportedAtUtc = now,
            items = new[]
            {
                CreateTransferItem("valid-first", 0.95m, 0.95m, now),
                CreateTransferItem("invalid-second", 0.95m, 95m, now)
            }
        };
        var payloadJson = JsonSerializer.Serialize(bundle);
        var package = new
        {
            version = 1,
            format = "contexthub-memory-transfer",
            encrypted = false,
            algorithm = "none",
            payloadBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson)),
            saltBase64 = (string?)null,
            nonceBase64 = (string?)null
        };
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(package)));
    }

    private static object CreateTransferItem(
        string externalKey,
        decimal importance,
        decimal confidence,
        DateTimeOffset timestamp)
        => new
        {
            projectId = "numeric-contract-test",
            externalKey,
            scope = MemoryScope.Project,
            memoryType = MemoryType.Fact,
            title = externalKey,
            content = externalKey,
            summary = externalKey,
            sourceType = "unit-test",
            sourceRef = externalKey,
            tags = Array.Empty<string>(),
            importance,
            confidence,
            metadataJson = "{}",
            isReadOnly = false,
            status = MemoryStatus.Active,
            createdAt = timestamp,
            updatedAt = timestamp
        };

    private sealed class UnusedRuntimeConfigurationAccessor : IRuntimeConfigurationAccessor
    {
        public RuntimeConfigurationResult Current => throw new InvalidOperationException("Not used by import.");
    }

    private sealed class CountingMemoryService : IMemoryService
    {
        public int UpsertCalls { get; private set; }

        public Task<MemoryDocument> UpsertAsync(MemoryUpsertRequest request, CancellationToken cancellationToken)
        {
            UpsertCalls++;
            throw new InvalidOperationException("Import persistence started before full-batch validation.");
        }

        public Task<MemoryDocument> UpdateAsync(MemoryUpdateRequest request, CancellationToken cancellationToken) => throw Unused();
        public Task<MemoryDocument> ArchiveAsync(MemoryArchiveRequest request, CancellationToken cancellationToken) => throw Unused();
        public Task<MemoryDocument> MoveAsync(MemoryMoveRequest request, CancellationToken cancellationToken) => throw Unused();
        public Task<MemoryDeleteResult> DeleteAsync(MemoryDeleteRequest request, CancellationToken cancellationToken) => throw Unused();
        public Task<ProjectCleanupPreviewResult> PreviewProjectCleanupAsync(ProjectCleanupPreviewRequest request, CancellationToken cancellationToken) => throw Unused();
        public Task<ProjectCleanupApplyResult> ApplyProjectCleanupAsync(ProjectCleanupApplyRequest request, CancellationToken cancellationToken) => throw Unused();
        public Task<MemoryDocument?> GetAsync(Guid id, CancellationToken cancellationToken) => throw Unused();
        public Task<IReadOnlyList<MemorySearchHit>> SearchAsync(MemorySearchRequest request, CancellationToken cancellationToken) => throw Unused();
        public Task<WorkingContextResult> BuildWorkingContextAsync(WorkingContextRequest request, CancellationToken cancellationToken) => throw Unused();
        public Task<EnqueueReindexResult> EnqueueReindexAsync(EnqueueReindexRequest request, CancellationToken cancellationToken) => throw Unused();
        public Task<EnqueueSummaryRefreshResult> EnqueueSummaryRefreshAsync(EnqueueSummaryRefreshRequest request, CancellationToken cancellationToken) => throw Unused();
        public Task<JobResult?> GetJobAsync(Guid id, CancellationToken cancellationToken) => throw Unused();
        public Task<MemoryDocument> PromoteLogSliceAsync(PromoteLogSliceRequest request, CancellationToken cancellationToken) => throw Unused();
        public Task<UserPreferenceResult> UpsertUserPreferenceAsync(UserPreferenceUpsertRequest request, CancellationToken cancellationToken) => throw Unused();
        public Task<IReadOnlyList<UserPreferenceResult>> ListUserPreferencesAsync(UserPreferenceListRequest request, CancellationToken cancellationToken) => throw Unused();
        public Task<UserPreferenceResult> ArchiveUserPreferenceAsync(UserPreferenceArchiveRequest request, CancellationToken cancellationToken) => throw Unused();

        private static Exception Unused() => new InvalidOperationException("Not used by this test.");
    }
}
