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
        StepUpRiskPolicy.Describe(StepUpOperationClass.CredentialRotate).Outcome.Should().Be(StepUpRequirementOutcome.RequiresExternalApproval);
        StepUpRiskPolicy.Describe(StepUpOperationClass.KekDestructiveOperation).Outcome.Should().Be(StepUpRequirementOutcome.Disabled);
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

    private static async Task SeedRulesAsync(MemoryDbContext db, TenantUser user, Guid secretId, string principal, SecretRight right, AuthorizationEffect effect)
    {
        db.SecretPolicies.Add(new SecretPolicy
        {
            TenantId = user.TenantId,
            OwnerUserId = user.Id,
            ProjectId = "ContextHub",
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
        public Task ExecuteAsync(SecretUseContext context, ReadOnlyMemory<byte> material, CancellationToken cancellationToken)
        {
            Captured = material.ToArray();
            return Task.CompletedTask;
        }
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
