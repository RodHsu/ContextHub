using System.Security.Cryptography;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Memory.IntegrationTests;

public sealed class ManagedFileCreateReplayTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Create_replays_only_in_its_current_canonical_project_or_unassigned_scope()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<MemoryDbContext>();
        var accessor = services.GetRequiredService<IRequestActorAccessor>();
        var actor = await GetBootstrapActorAsync(db);
        var projectA = $"replay-a-{Guid.NewGuid():N}";
        var projectB = $"replay-b-{Guid.NewGuid():N}";
        accessor.Current = actor with { AllowedProjectIds = [projectA, projectB] };
        var objectA = AddReadyObject(db, actor, projectA);
        var objectB = AddReadyObject(db, actor, projectB);
        var unassignedObject = AddReadyObject(db, actor, projectA);
        await db.SaveChangesAsync();
        var files = services.GetRequiredService<IManagedFileService>();
        var request = Request(objectA, projectA, Guid.NewGuid().ToString("N"));

        var first = await files.CreateAsync(request, default);
        first.Outcome.Should().Be(ManagedFileCreateOutcome.Created);
        var alias = await files.CreateAsync(request with { ProjectId = projectA.ToUpperInvariant() }, default);
        alias.Outcome.Should().Be(ManagedFileCreateOutcome.Created);
        alias.FileId.Should().Be(first.FileId);
        alias.FileVersionId.Should().Be(first.FileVersionId);
        alias.BinaryReused.Should().BeTrue();

        accessor.Current = actor with { AllowedProjectIds = [projectB] };
        var adjacent = await files.CreateAsync(request with { ProjectId = projectB, ManagedObjectId = objectB.Id }, default);
        adjacent.Outcome.Should().Be(ManagedFileCreateOutcome.Created);
        adjacent.FileId.Should().NotBe(first.FileId!.Value);
        adjacent.FileVersionId.Should().NotBe(first.FileVersionId!.Value);
        (await db.FileAssets.AsNoTracking().SingleAsync(asset => asset.Id == adjacent.FileId)).ProjectId.Should().Be(projectB);
        Func<Task> denied = () => files.CreateAsync(request, default);
        await denied.Should().ThrowAsync<UnauthorizedAccessException>();

        accessor.Current = actor with { AllowedProjectIds = [projectA, projectB] };
        var unassignedRequest = request with { ProjectId = null, ManagedObjectId = unassignedObject.Id };
        var unassigned = await files.CreateAsync(unassignedRequest, default);
        unassigned.Outcome.Should().Be(ManagedFileCreateOutcome.Created);
        unassigned.FileId.Should().NotBe(first.FileId!.Value).And.NotBe(adjacent.FileId!.Value);
        var draft = await db.FileAssets.AsNoTracking().SingleAsync(asset => asset.Id == unassigned.FileId);
        draft.ProjectId.Should().BeNull();
        draft.State.Should().Be(FileAssetState.Unassigned);
        var draftReplay = await files.CreateAsync(unassignedRequest with { ProjectId = "  " }, default);
        draftReplay.FileVersionId.Should().Be(unassigned.FileVersionId);
        (await db.FileAccessEvents.CountAsync(row => row.ReasonCode == "Created:" + request.IdempotencyKey)).Should().Be(3);
    }

    [DockerRequiredFact]
    public async Task Tenant_admin_cannot_replay_another_uploaders_creation_receipt()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<MemoryDbContext>();
        var accessor = services.GetRequiredService<IRequestActorAccessor>();
        var admin = await GetBootstrapActorAsync(db);
        admin.IsAdmin.Should().BeTrue();
        var projectId = $"replay-owner-{Guid.NewGuid():N}";
        var memberUser = new TenantUser
        {
            TenantId = admin.TenantId!.Value,
            Username = $"replay-member-{Guid.NewGuid():N}",
            DisplayName = "Replay Member",
            Email = $"{Guid.NewGuid():N}@example.test",
            PasswordHash = "synthetic-fixture-only",
            Role = TenantUserRole.Member,
            Status = TenantUserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.TenantUsers.Add(memberUser);
        await db.SaveChangesAsync();
        var member = admin with
        {
            UserId = memberUser.Id,
            Username = memberUser.Username,
            Role = memberUser.Role,
            AllowedProjectIds = [projectId]
        };
        var memberObject = AddReadyObject(db, member, projectId);
        var adminObject = AddReadyObject(db, admin, projectId);
        await db.SaveChangesAsync();
        var request = Request(memberObject, projectId, Guid.NewGuid().ToString("N"));
        var files = services.GetRequiredService<IManagedFileService>();

        accessor.Current = member;
        var memberResult = await files.CreateAsync(request, default);
        accessor.Current = admin;
        var adminResult = await files.CreateAsync(request with { ManagedObjectId = adminObject.Id }, default);
        adminResult.Outcome.Should().Be(ManagedFileCreateOutcome.Created);
        adminResult.FileId.Should().NotBe(memberResult.FileId!.Value);
        adminResult.FileVersionId.Should().NotBe(memberResult.FileVersionId!.Value);
        (await db.FileAssets.AsNoTracking().SingleAsync(asset => asset.Id == adminResult.FileId))
            .OwnerUserId.Should().Be(admin.UserId);
    }

    [DockerRequiredFact]
    public async Task Request_lock_serializes_alias_retries_even_with_different_filenames()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var actor = await GetBootstrapActorAsync(db);
        var projectId = $"replay-race-{Guid.NewGuid():N}";
        var managedObject = AddReadyObject(db, actor, projectId);
        await db.SaveChangesAsync();
        var request = Request(managedObject, projectId, Guid.NewGuid().ToString("N"));

        var results = await Task.WhenAll(CreateAsync(request), CreateAsync(request with
        {
            ProjectId = projectId.ToUpperInvariant(),
            FileName = "other-name.txt"
        }));

        results.Should().OnlyContain(result => result.Outcome == ManagedFileCreateOutcome.Created);
        results.Select(result => result.FileVersionId).Distinct().Should().ContainSingle();
        (await db.FileAccessEvents.CountAsync(row => row.ReasonCode == "Created:" + request.IdempotencyKey)).Should().Be(1);
        (await db.FileVersions.CountAsync(version => version.ManagedObjectId == managedObject.Id)).Should().Be(1);

        async Task<ManagedFileCreateResult> CreateAsync(CreateManagedFileRequest command)
        {
            using var child = environment.GetFactory().Services.CreateScope();
            child.ServiceProvider.GetRequiredService<IRequestActorAccessor>().Current = actor;
            return await child.ServiceProvider.GetRequiredService<IManagedFileService>().CreateAsync(command, default);
        }
    }

    [DockerRequiredFact]
    public async Task Replay_rechecks_the_current_asset_scope_and_live_version()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<MemoryDbContext>();
        var actor = await GetBootstrapActorAsync(db);
        services.GetRequiredService<IRequestActorAccessor>().Current = actor;
        var projectId = $"replay-drift-{Guid.NewGuid():N}";
        var managedObject = AddReadyObject(db, actor, projectId);
        await db.SaveChangesAsync();
        var request = Request(managedObject, projectId, Guid.NewGuid().ToString("N"));
        var files = services.GetRequiredService<IManagedFileService>();
        var result = await files.CreateAsync(request, default);
        var asset = await db.FileAssets.SingleAsync(row => row.Id == result.FileId);
        var version = await db.FileVersions.SingleAsync(row => row.Id == result.FileVersionId);
        Func<Task> replay = () => files.CreateAsync(request, default);

        asset.ProjectId = projectId + "-adjacent";
        await db.SaveChangesAsync();
        await replay.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("File version is not available.");

        asset.ProjectId = projectId;
        version.Lifecycle = FileVersionLifecycle.LogicalDeleted;
        await db.SaveChangesAsync();
        await replay.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("File version is not available.");

        var receipt = await db.FileAccessEvents.SingleAsync(row => row.ReasonCode == "Created:" + request.IdempotencyKey);
        receipt.FileVersionId = null;
        await db.SaveChangesAsync();
        await replay.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("File version is not available.");
        (await db.FileAccessEvents.CountAsync(row => row.ReasonCode == "Created:" + request.IdempotencyKey)).Should().Be(1);
    }

    private static CreateManagedFileRequest Request(ManagedObject managedObject, string projectId, string key)
        => new(managedObject.Id, "fixture.txt", "text/plain", "replay fixture", key, projectId, ManagedFileCreateIntent.CreateNewFile);

    private static ManagedObject AddReadyObject(MemoryDbContext db, ContextHubRequestActor actor, string projectId)
    {
        var now = DateTimeOffset.UtcNow;
        var managedObject = new ManagedObject
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = projectId,
            State = ManagedObjectState.Ready,
            StorageId = RandomHash(),
            PlaintextLength = 10,
            ChunkSize = 65536,
            ChunkCount = 1,
            KeyId = "replay-fixture-key",
            WrappedDek = new byte[32],
            WrapNonce = new byte[12],
            WrapTag = new byte[16],
            PlaintextSha256 = RandomHash(),
            StagedUntil = now.AddHours(1),
            CreatedAt = now,
            UpdatedAt = now
        };
        db.ManagedObjects.Add(managedObject);
        return managedObject;
    }

    private static string RandomHash() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    private static async Task<ContextHubRequestActor> GetBootstrapActorAsync(MemoryDbContext db)
    {
        var user = await db.TenantUsers.SingleAsync(row => row.Username == "contract-test-admin");
        return new(user.TenantId, user.Id, user.Username, user.Role, [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite], [], IsAuthenticated: true);
    }
}
