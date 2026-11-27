using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Memory.IntegrationTests;

public sealed class SecretWave4AWorkflowTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Reconciliation_clears_abandoned_uses_only_after_exhausted_leases_expire()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var now = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var createdAt = now.AddMinutes(-20);
        var projectId = $"reconciliation-{Guid.NewGuid():N}";
        var secret = new Secret
        {
            ProjectId = projectId,
            Name = projectId,
            NormalizedName = projectId,
            Kind = SecretKind.ApiToken,
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        };
        db.Secrets.Add(secret);
        await db.SaveChangesAsync();

        var expiredExhausted = Lease(SecretLeaseState.Exhausted, now.AddMinutes(-1), activeUses: 2);
        var boundaryExhausted = Lease(SecretLeaseState.Exhausted, now);
        var freshExhausted = Lease(SecretLeaseState.Exhausted, now.AddMinutes(10));
        var clearedExhausted = Lease(SecretLeaseState.Exhausted, now, activeUses: 0);
        var expiredActive = Lease(SecretLeaseState.Active, now);
        var freshActive = Lease(SecretLeaseState.Active, now.AddMinutes(10));
        var revoked = Lease(SecretLeaseState.Revoked, now);
        SecretLease[] leases = [expiredExhausted, boundaryExhausted, freshExhausted, clearedExhausted, expiredActive, freshActive, revoked];
        db.SecretLeases.AddRange(leases);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var clock = new ReconciliationClock(now);
        var service = new SecretReconciliationService(db, clock);
        (await service.RunAsync(CancellationToken.None)).ExpiredLeases.Should().Be(1);

        expiredActive.State = SecretLeaseState.Expired;
        foreach (var lease in new[] { expiredExhausted, boundaryExhausted, expiredActive })
        {
            lease.ActiveUses = 0;
            lease.UpdatedAt = now;
        }
        (await ReadLeasesAsync()).Should().BeEquivalentTo(leases);

        clock.UtcNow = now.AddMinutes(1);
        (await service.RunAsync(CancellationToken.None)).ExpiredLeases.Should().Be(0);
        (await ReadLeasesAsync()).Should().BeEquivalentTo(leases);

        SecretLease Lease(SecretLeaseState state, DateTimeOffset expiresAt, int activeUses = 1)
            => new()
            {
                SecretId = secret.Id,
                SecretVersionId = Guid.NewGuid(),
                ProjectId = projectId,
                ActorId = "reconciliation-fixture",
                Kind = SecretLeaseKind.Use,
                Purpose = "reconciliation-fixture",
                Target = "reconciliation-fixture",
                CapabilityHash = new string('0', 64),
                AuthorityRevision = 3,
                Revision = 7,
                MaxUses = 2,
                UsedCount = state == SecretLeaseState.Exhausted ? 2 : 1,
                MaxConcurrency = 2,
                ActiveUses = activeUses,
                State = state,
                ExpiresAt = expiresAt,
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
                RevokedAt = state == SecretLeaseState.Revoked ? createdAt : null
            };

        Task<SecretLease[]> ReadLeasesAsync() => db.SecretLeases.AsNoTracking()
            .Where(lease => lease.SecretId == secret.Id).ToArrayAsync();
    }

    private sealed class ReconciliationClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    [DockerRequiredFact]
    public async Task Password_step_up_must_bind_session_purpose_resource_and_block_agent_or_replay()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var tenant = await db.Tenants.SingleAsync(x => x.Slug == "contract-tests");
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<object>>();
        var user = new TenantUser
        {
            TenantId = tenant.Id,
            Username = $"step-up-{Guid.NewGuid():N}",
            DisplayName = "Step-up test",
            PasswordHash = hasher.HashPassword(new object(), "correct-horse-battery-staple"),
            Role = TenantUserRole.Admin,
            Status = TenantUserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.TenantUsers.Add(user);
        await db.SaveChangesAsync();
        var actorAccessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        actorAccessor.Current = Actor(user, interactive: true) with { AuthenticationSessionId = "browser-session" };
        var service = scope.ServiceProvider.GetRequiredService<IStepUpAuthenticationService>();

        var assertion = await service.CreatePasswordAssertionAsync(new PasswordStepUpRequest(
            "correct-horse-battery-staple", "secret:create", "Project", "ContextHub"), CancellationToken.None);
        actorAccessor.Current = actorAccessor.Current with { AuthenticationSessionId = "other-session" };
        var foreignSession = await service.AuthorizeAsync(StepUpOperationClass.SecretCreate, "secret:create", "Project", "ContextHub",
            new StepUpProof(assertion.AssertionId, assertion.Nonce, assertion.Revision), CancellationToken.None);
        foreignSession.Outcome.Should().Be(StepUpRequirementOutcome.RequiresStepUp);

        actorAccessor.Current = actorAccessor.Current with { AuthenticationSessionId = "browser-session" };
        var wrongPurpose = await service.AuthorizeAsync(StepUpOperationClass.SecretCreate, "secret:lease:create", "Project", "ContextHub",
            new StepUpProof(assertion.AssertionId, assertion.Nonce, assertion.Revision), CancellationToken.None);
        wrongPurpose.Outcome.Should().Be(StepUpRequirementOutcome.RequiresStepUp);
        var wrongResource = await service.AuthorizeAsync(StepUpOperationClass.SecretCreate, "secret:create", "Project", "Other",
            new StepUpProof(assertion.AssertionId, assertion.Nonce, assertion.Revision), CancellationToken.None);
        wrongResource.Outcome.Should().Be(StepUpRequirementOutcome.RequiresStepUp);
        var accepted = await service.AuthorizeAsync(StepUpOperationClass.SecretCreate, "secret:create", "Project", "ContextHub",
            new StepUpProof(assertion.AssertionId, assertion.Nonce, assertion.Revision), CancellationToken.None);
        accepted.Outcome.Should().Be(StepUpRequirementOutcome.Allowed);
        var replay = await service.AuthorizeAsync(StepUpOperationClass.SecretCreate, "secret:create", "Project", "ContextHub",
            new StepUpProof(assertion.AssertionId, assertion.Nonce, assertion.Revision), CancellationToken.None);
        replay.Outcome.Should().Be(StepUpRequirementOutcome.RequiresStepUp);

        actorAccessor.Current = Actor(user, interactive: false);
        var agentAttempt = () => service.CreatePasswordAssertionAsync(new PasswordStepUpRequest(
            "correct-horse-battery-staple", "secret:create", "Project", "ContextHub"), CancellationToken.None);
        await agentAttempt.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*interactive session*");

        actorAccessor.Current = Actor(user, interactive: true) with { AuthenticationSessionId = "brute-force-session" };
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var invalidPassword = () => service.CreatePasswordAssertionAsync(new PasswordStepUpRequest(
                "wrong-password", "secret:create", "Project", "ContextHub"), CancellationToken.None);
            await invalidPassword.Should().ThrowAsync<UnauthorizedAccessException>();
        }
        var lockedCorrectPassword = () => service.CreatePasswordAssertionAsync(new PasswordStepUpRequest(
            "correct-horse-battery-staple", "secret:create", "Project", "ContextHub"), CancellationToken.None);
        await lockedCorrectPassword.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*locked*");
        StepUpRiskPolicy.Describe(StepUpOperationClass.CredentialRotate).Outcome.Should().Be(StepUpRequirementOutcome.RequiresStepUp);
        StepUpRiskPolicy.Describe(StepUpOperationClass.KekDestructiveOperation).Outcome.Should().Be(StepUpRequirementOutcome.RequiresStepUp);
    }

    [DockerRequiredFact]
    public async Task Secret_envelope_use_signer_replay_deny_and_recovery_paths_must_fail_closed()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var actorAccessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        var user = await db.TenantUsers.SingleAsync(x => x.Username == "contract-test-admin");
        actorAccessor.Current = Actor(user, interactive: true);
        var keys = new TestSecretKeyAuthority();
        var service = CreateSecretService(db, actorAccessor, keys, new AllowApproval());
        var proof = Proof();

        var secret = await service.CreateAsync(new CreateSecretRequest("ContextHub", $"api-{Guid.NewGuid():N}", SecretKind.ApiToken, proof), CancellationToken.None);
        var material = Encoding.UTF8.GetBytes("wave-4a-sensitive-material");
        var version = await service.AddVersionAsync(new AddSecretVersionRequest(secret.Id, material, secret.Revision, "version-1", proof), CancellationToken.None);
        CryptographicOperations.ZeroMemory(material);
        var principal = user.Id.ToString("D");
        await SeedRulesAsync(db, user, secret.Id, principal, SecretRight.Use, AuthorizationEffect.Allow);

        var lease = await service.CreateLeaseAsync(new CreateSecretLeaseRequest(secret.Id, SecretLeaseKind.Use, "oauth:exchange",
            "https://api.example.test/token", "lease-1", version.SecretRevision, 1, 1, TimeSpan.FromMinutes(5), null, proof), CancellationToken.None);
        var consumer = new CapturingConsumer();
        var use = await service.UseAsync(new SecretUseRequest(lease.LeaseId, lease.Capability, lease.Revision, "use-1",
            "oauth:exchange", "https://api.example.test/token"), consumer, CancellationToken.None);
        Encoding.UTF8.GetString(consumer.Captured).Should().Be("wave-4a-sensitive-material");
        CryptographicOperations.ZeroMemory(consumer.Captured);
        use.State.Should().Be(SecretLeaseState.Exhausted);
        var replay = () => service.UseAsync(new SecretUseRequest(lease.LeaseId, lease.Capability, lease.Revision, "use-1",
            "oauth:exchange", "https://api.example.test/token"), new CapturingConsumer(), CancellationToken.None);
        await replay.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("Secret resource is unavailable.");

        var persisted = await db.SecretVersions.AsNoTracking().SingleAsync(x => x.Id == version.VersionId);
        Encoding.UTF8.GetString(persisted.Ciphertext).Should().NotContain("wave-4a-sensitive-material");
        (await db.SecretAccessEvents.Where(x => x.SecretId == secret.Id).Select(x => x.Purpose + x.ReasonCode + x.TargetHash).ToArrayAsync())
            .Should().NotContain(x => x.Contains("sensitive-material", StringComparison.Ordinal));

        var denySecret = await service.CreateAsync(new CreateSecretRequest("ContextHub", $"deny-{Guid.NewGuid():N}", SecretKind.ApiToken, proof), CancellationToken.None);
        var denyMaterial = Encoding.UTF8.GetBytes("denied");
        var denyVersion = await service.AddVersionAsync(new AddSecretVersionRequest(denySecret.Id, denyMaterial, denySecret.Revision, "deny-version", proof), CancellationToken.None);
        CryptographicOperations.ZeroMemory(denyMaterial);
        await SeedRulesAsync(db, user, denySecret.Id, principal, SecretRight.Use, AuthorizationEffect.Deny);
        var denied = () => service.CreateLeaseAsync(new CreateSecretLeaseRequest(denySecret.Id, SecretLeaseKind.Use, "test", "target", "deny-lease",
            denyVersion.SecretRevision, 1, 1, null, null, proof), CancellationToken.None);
        await denied.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("Secret resource is unavailable.");

        await SeedRulesAsync(db, user, secret.Id, principal, SecretRight.Rotate, AuthorizationEffect.Allow);
        keys.CurrentKeyId = "kek-v2";
        var rewrapped = await service.RewrapCurrentVersionAsync(secret.Id, version.SecretRevision, "approved", "rewrap-1", CancellationToken.None);
        rewrapped.Applied.Should().BeTrue();
        var rewrappedRevision = rewrapped.Revision ?? throw new InvalidOperationException("Rewrap did not return a revision.");
        keys.Remove("kek-v1");
        var staleHistorical = () => keys.Unwrap(secret.Id, version.VersionId, new WrappedSecretKey("kek-v1", persisted.WrappedDek, persisted.WrapNonce, persisted.WrapTag));
        staleHistorical.Should().Throw<CryptographicException>();

        using var rsa = RSA.Create(2048);
        var signerSecret = await service.CreateAsync(new CreateSecretRequest("ContextHub", $"ssh-{Guid.NewGuid():N}", SecretKind.SshPrivateKeyPkcs8, proof), CancellationToken.None);
        var privateKey = rsa.ExportPkcs8PrivateKey();
        var signerVersion = await service.AddVersionAsync(new AddSecretVersionRequest(signerSecret.Id, privateKey, signerSecret.Revision, "signer-version", proof), CancellationToken.None);
        CryptographicOperations.ZeroMemory(privateKey);
        await SeedRulesAsync(db, user, signerSecret.Id, principal, SecretRight.SshSign, AuthorizationEffect.Allow);
        await SeedRulesAsync(db, user, signerSecret.Id, principal, SecretRight.Use, AuthorizationEffect.Allow);
        var target = SecretTargetBinding.Ssh("host.example.test", 22, "deploy");
        var signerLease = await service.CreateLeaseAsync(new CreateSecretLeaseRequest(signerSecret.Id, SecretLeaseKind.SshSigner, "ssh:authenticate", target,
            "signer-lease", signerVersion.SecretRevision, 1, 1, null, null, proof), CancellationToken.None);
        var genericSigningLease = () => service.CreateLeaseAsync(new CreateSecretLeaseRequest(signerSecret.Id, SecretLeaseKind.Use, "ssh:authenticate", target,
            "generic-signing-lease", signerVersion.SecretRevision, 1, 1, null, null, proof), CancellationToken.None);
        await genericSigningLease.Should().ThrowAsync<UnauthorizedAccessException>();
        var challenge = RandomNumberGenerator.GetBytes(32);
        var wrongTarget = () => service.SignSshAuthenticationAsync(new SshSignerRequest(signerLease.LeaseId, signerLease.Capability, signerLease.Revision,
            "sign-wrong-target", "ssh-session", "other.example.test", 22, "deploy", challenge), CancellationToken.None);
        await wrongTarget.Should().ThrowAsync<UnauthorizedAccessException>();
        var signed = await service.SignSshAuthenticationAsync(new SshSignerRequest(signerLease.LeaseId, signerLease.Capability, signerLease.Revision,
            "sign-1", "ssh-session", "host.example.test", 22, "deploy", challenge), CancellationToken.None);
        var payload = SecretTargetBinding.BuildSshAuthenticationPayload("ssh-session", "host.example.test", 22, "deploy", challenge);
        rsa.VerifyData(payload, signed.Signature, HashAlgorithmName.SHA512, RSASignaturePadding.Pkcs1).Should().BeTrue();
        CryptographicOperations.ZeroMemory(challenge);
        CryptographicOperations.ZeroMemory(payload);
        CryptographicOperations.ZeroMemory(signed.Signature);

        await SeedRulesAsync(db, user, secret.Id, principal, SecretRight.Revoke, AuthorizationEffect.Deny);
        var deniedRevoke = () => service.RevokeAsync(secret.Id, rewrappedRevision, "approved", "revoke-denied", CancellationToken.None);
        await deniedRevoke.Should().ThrowAsync<UnauthorizedAccessException>();
        var rotatePolicy = await db.SecretPolicies.SingleAsync(x => x.SecretId == secret.Id && x.PrincipalId == principal && x.Right == SecretRight.Rotate);
        rotatePolicy.Effect = AuthorizationEffect.Deny;
        rotatePolicy.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        var deniedRewrap = () => service.RewrapCurrentVersionAsync(secret.Id, rewrappedRevision, "approved", "rewrap-denied", CancellationToken.None);
        await deniedRewrap.Should().ThrowAsync<UnauthorizedAccessException>();

        actorAccessor.Current = actorAccessor.Current with { AllowedProjectIds = ["OtherProject"] };
        var outOfProject = () => service.CreateLeaseAsync(new CreateSecretLeaseRequest(secret.Id, SecretLeaseKind.Use, "oauth:exchange",
            "https://api.example.test/token", "cross-project", rewrappedRevision, 1, 1, null, null, proof), CancellationToken.None);
        await outOfProject.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("Secret resource is unavailable.");
    }

    [DockerRequiredFact]
    public async Task Failed_unwrap_releases_the_active_slot_without_refunding_the_use()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var actorAccessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        var user = await db.TenantUsers.SingleAsync(x => x.Username == "contract-test-admin");
        actorAccessor.Current = Actor(user, interactive: true);
        var keys = new TestSecretKeyAuthority();
        var service = CreateSecretService(db, actorAccessor, keys, new AllowApproval());
        var proof = Proof();
        var secret = await service.CreateAsync(new("ContextHub", $"unwrap-recovery-{Guid.NewGuid():N}", SecretKind.Opaque, proof), default);
        var testMaterial = RandomNumberGenerator.GetBytes(16);
        var version = await service.AddVersionAsync(new(secret.Id, testMaterial, secret.Revision, "version-1", proof), default);
        CryptographicOperations.ZeroMemory(testMaterial);
        await SeedRulesAsync(db, user, secret.Id, user.Id.ToString("D"), SecretRight.Use, AuthorizationEffect.Allow);

        const string purpose = "test:lease-recovery";
        const string target = "https://example.test/recovery";
        var lease = await service.CreateLeaseAsync(new(secret.Id, SecretLeaseKind.Use, purpose, target,
            "lease-1", version.SecretRevision, 2, 1, TimeSpan.FromMinutes(5), null, proof), default);
        var first = new SecretUseRequest(lease.LeaseId, lease.Capability, lease.Revision, "use-unwrap-failure", purpose, target);
        var consumer = new CapturingConsumer();
        keys.FailNextUnwrap = true;
        var fail = () => service.UseAsync(first, consumer, default);
        await fail.Should().ThrowAsync<CryptographicException>();
        consumer.Calls.Should().Be(0);

        var committed = await db.SecretLeases.AsNoTracking().SingleAsync(x => x.Id == lease.LeaseId);
        committed.ActiveUses.Should().Be(0);
        committed.UsedCount.Should().Be(1);
        committed.Revision.Should().Be(lease.Revision + 1);
        committed.State.Should().Be(SecretLeaseState.Active);
        var useAudits = db.SecretAccessEvents.Where(x => x.LeaseId == lease.LeaseId &&
            x.Operation == SecretAccessOperation.Use && x.Allowed && x.ReasonCode == "UseStarted");
        (await useAudits.Select(x => x.RequestId).ToArrayAsync()).Should().Equal(first.RequestId);

        var next = first with { ExpectedRevision = committed.Revision };
        var replay = () => service.UseAsync(next, consumer, default);
        await replay.Should().ThrowAsync<UnauthorizedAccessException>();
        consumer.Calls.Should().Be(0);

        const string recoveryRequestId = "use-after-recovery";
        var recovered = await service.UseAsync(next with { RequestId = recoveryRequestId }, consumer, default);
        consumer.Calls.Should().Be(1);
        consumer.Captured.Should().HaveCount(testMaterial.Length);
        CryptographicOperations.ZeroMemory(consumer.Captured);
        recovered.UsedCount.Should().Be(2);
        recovered.Revision.Should().Be(committed.Revision + 1);
        recovered.State.Should().Be(SecretLeaseState.Exhausted);
        (await db.SecretLeases.AsNoTracking().SingleAsync(x => x.Id == lease.LeaseId)).ActiveUses.Should().Be(0);
        (await useAudits.OrderBy(x => x.RequestId).Select(x => x.RequestId).ToArrayAsync())
            .Should().Equal(recoveryRequestId, first.RequestId);
    }

    [DockerRequiredFact]
    public async Task Canceled_secret_consumer_releases_the_slot_without_refunding_its_use()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var accessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        var user = await db.TenantUsers.SingleAsync(item => item.Username == "contract-test-admin");
        accessor.Current = Actor(user, interactive: true);
        var service = CreateSecretService(db, accessor, new TestSecretKeyAuthority(), new AllowApproval());
        var proof = Proof();
        var secret = await service.CreateAsync(new("ContextHub", $"canceled-use-{Guid.NewGuid():N}", SecretKind.Opaque, proof), CancellationToken.None);
        var material = RandomNumberGenerator.GetBytes(16);
        SecretVersionResult version;
        try
        {
            version = await service.AddVersionAsync(new(secret.Id, material, secret.Revision, "version", proof), CancellationToken.None);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
        await SeedRulesAsync(db, user, secret.Id, user.Id.ToString("D"), SecretRight.Use, AuthorizationEffect.Allow);

        const string purpose = "test:lease-cancellation";
        const string target = "https://example.test/cancellation";
        var lease = await service.CreateLeaseAsync(new(secret.Id, SecretLeaseKind.Use, purpose, target,
            "lease", version.SecretRevision, 2, 1, TimeSpan.FromMinutes(5), null, proof), CancellationToken.None);
        var first = new SecretUseRequest(lease.LeaseId, lease.Capability, lease.Revision, "canceled-use", purpose, target);
        using var cancellation = new CancellationTokenSource();
        var expected = new OperationCanceledException(cancellation.Token);
        var consumer = new CancelingConsumer(cancellation, expected);
        var use = () => service.UseAsync(first, consumer, cancellation.Token);
        var canceled = await use.Should().ThrowAsync<OperationCanceledException>();
        canceled.Which.Should().BeSameAs(expected);

        var released = await db.SecretLeases.AsNoTracking().SingleAsync(item => item.Id == lease.LeaseId);
        released.ActiveUses.Should().Be(0);
        released.UsedCount.Should().Be(1);
        released.Revision.Should().Be(lease.Revision + 1);
        released.State.Should().Be(SecretLeaseState.Active);
        var useAudits = db.SecretAccessEvents.AsNoTracking().Where(item =>
            item.LeaseId == lease.LeaseId && item.Operation == SecretAccessOperation.Use && item.Allowed && item.ReasonCode == "UseStarted");
        (await useAudits.Select(item => item.RequestId).ToArrayAsync()).Should().Equal(first.RequestId);

        var second = first with { ExpectedRevision = released.Revision, RequestId = "next-use" };
        var nextConsumer = new GatedConsumer();
        nextConsumer.Finish();
        var result = await service.UseAsync(second, nextConsumer, CancellationToken.None);
        nextConsumer.Entered.IsCompletedSuccessfully.Should().BeTrue();
        result.Should().Be(new SecretUseResult(lease.Revision + 2, 2, 0, SecretLeaseState.Exhausted));
        var finished = await db.SecretLeases.AsNoTracking().SingleAsync(item => item.Id == lease.LeaseId);
        finished.ActiveUses.Should().Be(0);
        finished.UsedCount.Should().Be(2);
        finished.Revision.Should().Be(lease.Revision + 2);
        (await useAudits.Select(item => item.RequestId).ToArrayAsync()).Should().BeEquivalentTo(new[] { first.RequestId, second.RequestId });
    }

    [DockerRequiredFact]
    public async Task Concurrent_consumers_release_only_their_own_active_lease_slot()
    {
        var factory = environment.GetFactory();
        var keys = new TestSecretKeyAuthority();
        const string purpose = "test:lease-concurrency";
        const string target = "https://example.test/concurrency";
        ContextHubRequestActor actor;
        SecretLeaseResult lease;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = deadline.Token;
        using (var setup = factory.Services.CreateScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var accessor = setup.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
            var user = await db.TenantUsers.SingleAsync(item => item.Username == "contract-test-admin", cancellationToken);
            actor = Actor(user, interactive: true);
            accessor.Current = actor;
            var service = CreateSecretService(db, accessor, keys, new AllowApproval());
            var proof = Proof();
            var secret = await service.CreateAsync(new("ContextHub", $"concurrent-use-{Guid.NewGuid():N}", SecretKind.Opaque, proof), cancellationToken);
            var material = RandomNumberGenerator.GetBytes(16);
            SecretVersionResult version;
            try
            {
                version = await service.AddVersionAsync(new(secret.Id, material, secret.Revision, "version", proof), cancellationToken);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(material);
            }
            await SeedRulesAsync(db, user, secret.Id, user.Id.ToString("D"), SecretRight.Use, AuthorizationEffect.Allow);
            lease = await service.CreateLeaseAsync(new(secret.Id, SecretLeaseKind.Use, purpose, target,
                "lease", version.SecretRevision, 2, 2, TimeSpan.FromMinutes(5), null, proof), cancellationToken);
        }

        using var firstScope = factory.Services.CreateScope();
        using var secondScope = factory.Services.CreateScope();
        using var verificationScope = factory.Services.CreateScope();
        var firstDb = firstScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var secondDb = secondScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var firstAccessor = firstScope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        var secondAccessor = secondScope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        firstAccessor.Current = actor;
        secondAccessor.Current = actor;
        var firstService = CreateSecretService(firstDb, firstAccessor, keys, new AllowApproval());
        var secondService = CreateSecretService(secondDb, secondAccessor, keys, new AllowApproval());
        var firstConsumer = new GatedConsumer();
        var secondConsumer = new GatedConsumer();
        var firstRequest = new SecretUseRequest(lease.LeaseId, lease.Capability, lease.Revision, "first-use", purpose, target);
        var secondRequest = firstRequest with { ExpectedRevision = lease.Revision + 1, RequestId = "second-use" };

        var firstUse = firstService.UseAsync(firstRequest, firstConsumer, cancellationToken);
        Task<SecretUseResult>? secondUse = null;
        try
        {
            await firstConsumer.Entered.WaitAsync(cancellationToken);
            secondUse = secondService.UseAsync(secondRequest, secondConsumer, cancellationToken);
            await secondConsumer.Entered.WaitAsync(cancellationToken);

            var bothActive = await ReadLeaseAsync();
            bothActive.ActiveUses.Should().Be(2);
            bothActive.UsedCount.Should().Be(2);

            firstConsumer.Finish();
            (await firstUse).Should().Be(new SecretUseResult(lease.Revision + 1, 1, 1, SecretLeaseState.Active));
            secondUse.IsCompleted.Should().BeFalse();
            var afterFirst = await ReadLeaseAsync();
            afterFirst.ActiveUses.Should().Be(1);
            afterFirst.UsedCount.Should().Be(2);
            afterFirst.Revision.Should().Be(lease.Revision + 2);
            afterFirst.State.Should().Be(SecretLeaseState.Exhausted);
            var tracked = firstDb.SecretLeases.Local.Single(item => item.Id == lease.LeaseId);
            tracked.ActiveUses.Should().Be(afterFirst.ActiveUses);
            tracked.Revision.Should().Be(afterFirst.Revision);

            secondConsumer.Finish();
            (await secondUse).Should().Be(new SecretUseResult(lease.Revision + 2, 2, 0, SecretLeaseState.Exhausted));
            var afterSecond = await ReadLeaseAsync();
            afterSecond.ActiveUses.Should().Be(0);
            afterSecond.UsedCount.Should().Be(2);
            afterSecond.Revision.Should().Be(lease.Revision + 2);
            afterSecond.State.Should().Be(SecretLeaseState.Exhausted);
            var audits = await verificationDb.SecretAccessEvents.AsNoTracking()
                .Where(item => item.LeaseId == lease.LeaseId && item.Operation == SecretAccessOperation.Use && item.Allowed && item.ReasonCode == "UseStarted")
                .Select(item => item.RequestId).ToArrayAsync(cancellationToken);
            audits.Should().BeEquivalentTo(new[] { firstRequest.RequestId, secondRequest.RequestId });
        }
        finally
        {
            firstConsumer.Finish();
            secondConsumer.Finish();
            await Task.WhenAll(firstUse, secondUse ?? Task.CompletedTask);
        }

        Task<SecretLease> ReadLeaseAsync() => verificationDb.SecretLeases.AsNoTracking()
            .SingleAsync(item => item.Id == lease.LeaseId, cancellationToken);
    }

    [DockerRequiredFact]
    public async Task Ssh_certificate_issue_proactive_renew_max_lifetime_actor_binding_and_krl_must_fail_closed()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var actorAccessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        var user = await db.TenantUsers.SingleAsync(x => x.Username == "contract-test-admin");
        actorAccessor.Current = Actor(user, interactive: true);
        var secretService = CreateSecretService(db, actorAccessor, new TestSecretKeyAuthority(), new AllowApproval());
        var ca = await secretService.CreateAsync(new CreateSecretRequest("ContextHub", $"ca-{Guid.NewGuid():N}", SecretKind.SshCertificateAuthorityReference, Proof()), CancellationToken.None);
        var principal = user.Id.ToString("D");
        await SeedRulesAsync(db, user, ca.Id, principal, SecretRight.SshIssue, AuthorizationEffect.Allow);
        await SeedRulesAsync(db, user, ca.Id, principal, SecretRight.Revoke, AuthorizationEffect.Allow);
        var service = new SshCertificateService(db, actorAccessor, new SystemClock(), new AllowFoundationStore(), new AllowStepUp(),
            new AllowApproval(), new TestSshCertificateIssuer(), Options.Create(new SecretManagementOptions
            {
                SshCertificateMinutes = 1,
                SshRenewalWindowSeconds = 300,
                SshMaxSessionMinutes = 5,
                SshMaxRenewals = 1
            }));

        var issued = await service.IssueAsync(new IssueSshCertificateRequest(ca.Id, TestPublicKey, "host.example.test", 22, "deploy",
            "deployment", "issue-1", ca.Revision, null, Proof()), CancellationToken.None);
        issued.RenewalEligibleAt.Should().BeBefore(DateTimeOffset.UtcNow);
        var originalActor = actorAccessor.Current;
        actorAccessor.Current = originalActor with { UserId = Guid.NewGuid(), IsServiceActor = true, IsInteractiveUser = false };
        var foreignActorRenewal = () => service.RenewAsync(new RenewSshCertificateRequest(issued.CertificateLeaseId, issued.RenewalCapability,
            issued.Revision, "foreign-renew"), CancellationToken.None);
        await foreignActorRenewal.Should().ThrowAsync<UnauthorizedAccessException>();
        actorAccessor.Current = originalActor;
        var renewed = await service.RenewAsync(new RenewSshCertificateRequest(issued.CertificateLeaseId, issued.RenewalCapability,
            issued.Revision, "renew-1"), CancellationToken.None);
        renewed.Certificate.Should().NotBe(issued.Certificate);
        renewed.ValidBefore.Should().BeOnOrBefore(renewed.MaxSessionExpiresAt);

        var beyondMaximum = () => service.RenewAsync(new RenewSshCertificateRequest(renewed.CertificateLeaseId, renewed.RenewalCapability,
            renewed.Revision, "renew-2"), CancellationToken.None);
        await beyondMaximum.Should().ThrowAsync<UnauthorizedAccessException>();

        var revoked = await service.RevokeAsync(renewed.CertificateLeaseId, renewed.Revision, "approved", "compromised", CancellationToken.None);
        revoked.ReasonCode.Should().Be("RevokedKrlPending");
        (await db.SshRevocationRecords.SingleAsync(x => x.SshCertificateLeaseId == renewed.CertificateLeaseId)).KrlRequired.Should().BeTrue();
        var caRevoked = await secretService.RevokeAsync(ca.Id, ca.Revision, "approved", "ca-revoke", CancellationToken.None);
        caRevoked.Applied.Should().BeTrue();
        (await db.SshCertificateLeases.Where(x => x.CaSecretId == ca.Id).Select(x => x.State).ToArrayAsync())
            .Should().OnlyContain(x => x == SshCertificateState.Revoked);
        (await db.SshRevocationRecords.CountAsync(x => x.ProjectId == "ContextHub" &&
            (x.SshCertificateLeaseId == issued.CertificateLeaseId || x.SshCertificateLeaseId == renewed.CertificateLeaseId))).Should().Be(2);
    }

    private static SecretManagementService CreateSecretService(MemoryDbContext db, IRequestActorAccessor actorAccessor, ISecretEnvelopeKeyAuthority keys, IHighAssuranceApprovalVerifier approval)
        => new(db, actorAccessor, new SystemClock(), new AllowFoundationStore(), new AllowStepUp(), approval, keys, new RsaSshBoundSigner(),
            Options.Create(new SecretManagementOptions()));

    [DockerRequiredFact]
    public async Task Secret_project_grants_use_frozen_unicode_identity_and_preserve_owner_isolation()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var accessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        var user = await db.TenantUsers.SingleAsync(x => x.Username == "contract-test-admin");
        var actor = Actor(user, interactive: true);
        accessor.Current = actor;
        var service = CreateSecretService(db, accessor, new TestSecretKeyAuthority(), new AllowApproval());
        var suffix = "-" + Guid.NewGuid().ToString("N");
        var created = await service.CreateAsync(new("µ" + suffix, "identity-control", SecretKind.Opaque, Proof()), default);
        foreach (var alias in new[] { "µ", "μ", "Μ" })
        {
            accessor.Current = actor with { AllowedProjectIds = [alias + suffix] };
            var items = await service.ListAsync("Μ" + suffix, default);
            items.Should().ContainSingle().Which.Id.Should().Be(created.Id);
            items[0].ProjectId.Should().Be("µ" + suffix);
        }
        foreach (var foreign in new[] { actor with { UserId = Guid.NewGuid() }, actor with { TenantId = Guid.NewGuid() } })
        {
            accessor.Current = foreign with { AllowedProjectIds = ["Μ" + suffix] };
            (await service.ListAsync("Μ" + suffix, default)).Should().BeEmpty();
        }
        accessor.Current = actor with { AllowedProjectIds = ["M" + suffix] };
        var denied = () => service.ListAsync("Μ" + suffix, default);
        await denied.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [DockerRequiredFact]
    public async Task Ssh_project_grants_use_frozen_unicode_identity_for_issue_and_revoke()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var accessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        var user = await db.TenantUsers.SingleAsync(x => x.Username == "contract-test-admin");
        var actor = Actor(user, interactive: true);
        accessor.Current = actor;
        var secretService = CreateSecretService(db, accessor, new TestSecretKeyAuthority(), new AllowApproval());
        var suffix = "-" + Guid.NewGuid().ToString("N");
        var project = "µ" + suffix;
        var ca = await secretService.CreateAsync(new(project, "identity-ca", SecretKind.SshCertificateAuthorityReference, Proof()), default);
        foreach (var right in new[] { SecretRight.SshIssue, SecretRight.Revoke })
            await SeedRulesAsync(db, user, ca.Id, user.Id.ToString("D"), right, AuthorizationEffect.Allow, project);
        var service = new SshCertificateService(db, accessor, new SystemClock(), new AllowFoundationStore(), new AllowStepUp(),
            new AllowApproval(), new TestSshCertificateIssuer(), Options.Create(new SecretManagementOptions()));
        accessor.Current = actor with { AllowedProjectIds = ["Μ" + suffix] };
        var issued = await service.IssueAsync(new(ca.Id, TestPublicKey, "host.example.test", 22, "deploy",
            "identity-control", "identity-issue", ca.Revision, null, Proof()), default);
        foreach (var foreign in new[] { actor with { UserId = Guid.NewGuid() }, actor with { TenantId = Guid.NewGuid() },
                     actor with { AllowedProjectIds = ["M" + suffix] } })
        {
            accessor.Current = foreign;
            var denied = () => service.RevokeAsync(issued.CertificateLeaseId, issued.Revision, "approved", "identity-control", default);
            await denied.Should().ThrowAsync<UnauthorizedAccessException>();
            (await db.SshCertificateLeases.AsNoTracking().SingleAsync(x => x.Id == issued.CertificateLeaseId)).State.Should().Be(SshCertificateState.Active);
        }
        accessor.Current = actor with { AllowedProjectIds = ["μ" + suffix] };
        (await service.RevokeAsync(issued.CertificateLeaseId, issued.Revision, "approved", "identity-control", default)).Applied.Should().BeTrue();
        (await db.SshCertificateLeases.AsNoTracking().SingleAsync(x => x.Id == issued.CertificateLeaseId)).ProjectId.Should().Be(project);
    }

    private static async Task SeedRulesAsync(MemoryDbContext db, TenantUser user, Guid secretId, string principal, SecretRight right, AuthorizationEffect effect, string projectId = "ContextHub")
    {
        db.SecretPolicies.Add(new SecretPolicy
        {
            TenantId = user.TenantId,
            OwnerUserId = user.Id,
            ProjectId = projectId,
            SecretId = secretId,
            PrincipalId = principal,
            Right = right,
            Effect = effect,
            EvidenceRef = "wave4a-test",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private static ContextHubRequestActor Actor(TenantUser user, bool interactive)
        => new(user.TenantId, user.Id, user.Username, user.Role,
            [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite, SecurityScopes.SecurityManage, SecurityScopes.SecretsRead, SecurityScopes.SecretsUse, SecurityScopes.SecretsManage],
            [], true, IsInteractiveUser: interactive);

    private static StepUpProof Proof() => new(Guid.NewGuid(), "test-proof", 1);

    private sealed class AllowStepUp : IStepUpAuthenticationService
    {
        public Task<StepUpAssertionResult> CreatePasswordAssertionAsync(PasswordStepUpRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StepUpAuthorizationResult> AuthorizeAsync(StepUpOperationClass operation, string purpose, string? resourceType, string? resourceId, StepUpProof? proof, CancellationToken cancellationToken)
            => Task.FromResult(new StepUpAuthorizationResult(StepUpRequirementOutcome.Allowed, AssuranceLevel.Aal1, "Test"));
        public Task<StepUpAuthorizationResult> AuthorizeRequiredAssuranceAsync(AssuranceLevel requiredAssurance, string purpose, string? resourceType, string? resourceId, StepUpProof? proof, CancellationToken cancellationToken)
            => Task.FromResult(new StepUpAuthorizationResult(StepUpRequirementOutcome.Allowed, requiredAssurance, "Test"));
    }

    private sealed class AllowApproval : IHighAssuranceApprovalVerifier
    {
        public Task<bool> VerifyAsync(string actorId, string approvalReference, string purpose, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class AllowFoundationStore : IPlatformFoundationStore
    {
        public Task<EffectiveRightsResult> EvaluateAsync(string projectId, string principalId, IReadOnlyList<string> rights, string? resourceType, string? resourceId, CancellationToken cancellationToken)
            => Task.FromResult(new EffectiveRightsResult(projectId, principalId, new SecurityRevisionVector(1, 1, 1, 1),
                rights.Select(x => new EffectiveRightDecision(x, true, "Test", ["test"], "test")).ToArray()));
        public Task<AuthorizationTopologyEdge> UpsertTopologyEdgeAsync(TopologyEdgeUpsertRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AuthorizationRule> UpsertPolicyAsync(AuthorizationRuleUpsertRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AuthorizationRule> UpsertExplicitGrantAsync(AuthorizationRuleUpsertRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CanonicalTagSuggestion> SuggestTagAsync(CanonicalTagSuggestionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class CapturingConsumer : ISecretMaterialConsumer
    {
        public byte[] Captured { get; private set; } = [];
        public int Calls { get; private set; }
        public Task ExecuteAsync(SecretUseContext context, ReadOnlyMemory<byte> material, CancellationToken cancellationToken)
        {
            Calls++;
            Captured = material.ToArray();
            return Task.CompletedTask;
        }
    }

    private sealed class CancelingConsumer(CancellationTokenSource request, OperationCanceledException failure) : ISecretMaterialConsumer
    {
        public Task ExecuteAsync(SecretUseContext context, ReadOnlyMemory<byte> material, CancellationToken cancellationToken)
        {
            request.Cancel();
            return Task.FromException(failure);
        }
    }

    private sealed class GatedConsumer : ISecretMaterialConsumer
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _finish = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public Task ExecuteAsync(SecretUseContext context, ReadOnlyMemory<byte> material, CancellationToken cancellationToken)
        {
            _entered.SetResult();
            return _finish.Task.WaitAsync(cancellationToken);
        }

        public void Finish() => _finish.TrySetResult();
    }

    private const string TestPublicKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIEZha2VQdWJsaWNLZXlGb3JXYXZlNEFUZXN0";

    private sealed class TestSshCertificateIssuer : ISshCertificateIssuer
    {
        private int sequence;
        public Task<SshCertificateIssueArtifact> IssueAsync(SshCertificateIssueContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = Interlocked.Increment(ref sequence);
            return Task.FromResult(new SshCertificateIssueArtifact($"ssh-ed25519-cert-v01@openssh.com test-{current}", $"SHA256:test-{current}"));
        }
    }

    private sealed class TestSecretKeyAuthority : ISecretEnvelopeKeyAuthority
    {
        private readonly Dictionary<string, byte[]> keys = new()
        {
            ["kek-v1"] = RandomNumberGenerator.GetBytes(32),
            ["kek-v2"] = RandomNumberGenerator.GetBytes(32)
        };

        public string CurrentKeyId { get; set; } = "kek-v1";
        public bool FailNextUnwrap { get; set; }
        public void Remove(string keyId) { if (keys.Remove(keyId, out var key)) CryptographicOperations.ZeroMemory(key); }

        public WrappedSecretKey Wrap(Guid secretId, Guid versionId, ReadOnlySpan<byte> plaintextKey)
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var ciphertext = new byte[plaintextKey.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(keys[CurrentKeyId], 16);
            aes.Encrypt(nonce, plaintextKey, ciphertext, tag, Aad(secretId, versionId, CurrentKeyId));
            return new(CurrentKeyId, ciphertext, nonce, tag);
        }

        public byte[] Unwrap(Guid secretId, Guid versionId, WrappedSecretKey wrappedKey)
        {
            if (FailNextUnwrap)
            {
                FailNextUnwrap = false;
                throw new CryptographicException("Test key provider is temporarily unavailable.");
            }

            if (!keys.TryGetValue(wrappedKey.KeyId, out var key)) throw new CryptographicException("Missing historical KEK.");
            var plaintext = new byte[wrappedKey.Ciphertext.Length];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(wrappedKey.Nonce, wrappedKey.Ciphertext, wrappedKey.Tag, plaintext, Aad(secretId, versionId, wrappedKey.KeyId));
            return plaintext;
        }

        public WrappedSecretKey Rewrap(Guid secretId, Guid versionId, WrappedSecretKey wrappedKey)
        {
            var plaintext = Unwrap(secretId, versionId, wrappedKey);
            try { return Wrap(secretId, versionId, plaintext); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }

        private static byte[] Aad(Guid secretId, Guid versionId, string keyId) => Encoding.UTF8.GetBytes($"test|{secretId:D}|{versionId:D}|{keyId}");
    }
}
