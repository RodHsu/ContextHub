using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Cbor;
using Fido2NetLib.Objects;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Memory.IntegrationTests;

public sealed class MultiFactorWave4BWorkflowTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Completing_one_factor_must_stale_the_opposite_pending_enrollment_without_mutation(bool webAuthnFirst)
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var actorAccessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        var actor = await CreateActorAsync(db);
        actorAccessor.Current = actor;
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        var keys = new TestKeyAuthority();
        var webAuthn = new FakeWebAuthn { UserHandle = actor.UserId!.Value.ToByteArray() };
        var service = CreateRealService(scope.ServiceProvider, db, actorAccessor, clock, keys, webAuthn);
        var totpProof = await SeedAssertionAsync(db, actor, AuthenticationMethod.Password, AssuranceLevel.Aal1,
            "mfa:totp:enroll", "User", actor.UserId.Value.ToString("D"), clock.UtcNow);
        var webAuthnProof = await SeedAssertionAsync(db, actor, AuthenticationMethod.Password, AssuranceLevel.Aal1,
            "mfa:webauthn:register", "User", actor.UserId.Value.ToString("D"), clock.UtcNow);
        var totp = await service.StartTotpEnrollmentAsync(new TotpEnrollmentStartRequest(totpProof.Proof), CancellationToken.None);
        var registration = await service.StartWebAuthnRegistrationAsync(new WebAuthnRegistrationStartRequest(WebAuthnCredentialKind.Passkey, webAuthnProof.Proof), CancellationToken.None);
        var code = ComputeTotp(Base32Decode(totp.Secret), clock.UtcNow.ToUnixTimeSeconds() / 30);

        if (webAuthnFirst)
        {
            await service.CompleteWebAuthnRegistrationAsync(new WebAuthnRegistrationCompleteRequest(registration.CeremonyId, "{}"), CancellationToken.None);
            var beforeEvents = await db.MfaSecurityEvents.CountAsync();
            var stale = () => service.ConfirmTotpEnrollmentAsync(new TotpEnrollmentConfirmRequest(totp.FactorId, code, "secret:rotate"), CancellationToken.None);
            await stale.Should().ThrowAsync<MfaAuthorityStaleException>();
            db.ChangeTracker.Clear();
            (await db.TotpFactors.SingleAsync(x => x.Id == totp.FactorId)).State.Should().Be(MfaFactorState.Pending);
            (await db.MfaRecoveryCodes.CountAsync(x => x.TotpFactorId == totp.FactorId)).Should().Be(0);
            (await db.MfaSecurityEvents.CountAsync()).Should().Be(beforeEvents);
        }
        else
        {
            await service.ConfirmTotpEnrollmentAsync(new TotpEnrollmentConfirmRequest(totp.FactorId, code, "secret:rotate"), CancellationToken.None);
            var beforeCredentials = await db.WebAuthnCredentials.CountAsync(x => x.ActorUserId == actor.UserId);
            var beforeEvents = await db.MfaSecurityEvents.CountAsync();
            var stale = () => service.CompleteWebAuthnRegistrationAsync(new WebAuthnRegistrationCompleteRequest(registration.CeremonyId, "{}"), CancellationToken.None);
            await stale.Should().ThrowAsync<MfaAuthorityStaleException>();
            db.ChangeTracker.Clear();
            (await db.WebAuthnCredentials.CountAsync(x => x.ActorUserId == actor.UserId)).Should().Be(beforeCredentials);
            (await db.WebAuthnCeremonies.SingleAsync(x => x.Id == registration.CeremonyId)).State.Should().Be(WebAuthnCeremonyState.Pending);
            (await db.MfaSecurityEvents.CountAsync()).Should().Be(beforeEvents);
        }
    }

    [DockerRequiredFact]
    public async Task Concurrent_cross_factor_completion_must_serialize_and_allow_exactly_one_authority_mutation()
    {
        ContextHubRequestActor actor;
        TotpEnrollmentStartResult totp;
        WebAuthnCeremonyStartResult registration;
        string code;
        var sharedCredentialId = RandomNumberGenerator.GetBytes(32);
        var keys = new TestKeyAuthority();
        using (var setupScope = environment.GetFactory().Services.CreateScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var actorAccessor = setupScope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
            actor = await CreateActorAsync(db);
            actorAccessor.Current = actor;
            var clock = setupScope.ServiceProvider.GetRequiredService<IClock>();
            var webAuthn = new FakeWebAuthn(sharedCredentialId) { UserHandle = actor.UserId!.Value.ToByteArray() };
            var service = CreateRealService(setupScope.ServiceProvider, db, actorAccessor, clock, keys, webAuthn);
            var totpProof = await SeedAssertionAsync(db, actor, AuthenticationMethod.Password, AssuranceLevel.Aal1,
                "mfa:totp:enroll", "User", actor.UserId.Value.ToString("D"), clock.UtcNow);
            var webAuthnProof = await SeedAssertionAsync(db, actor, AuthenticationMethod.Password, AssuranceLevel.Aal1,
                "mfa:webauthn:register", "User", actor.UserId.Value.ToString("D"), clock.UtcNow);
            totp = await service.StartTotpEnrollmentAsync(new TotpEnrollmentStartRequest(totpProof.Proof), CancellationToken.None);
            registration = await service.StartWebAuthnRegistrationAsync(new WebAuthnRegistrationStartRequest(WebAuthnCredentialKind.Passkey, webAuthnProof.Proof), CancellationToken.None);
            code = ComputeTotp(Base32Decode(totp.Secret), clock.UtcNow.ToUnixTimeSeconds() / 30);
        }

        async Task<Exception?> CompleteTotpAsync()
        {
            try
            {
                using var scope = environment.GetFactory().Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
                var accessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
                accessor.Current = actor;
                var service = CreateRealService(scope.ServiceProvider, db, accessor, scope.ServiceProvider.GetRequiredService<IClock>(), keys,
                    new FakeWebAuthn(sharedCredentialId) { UserHandle = actor.UserId!.Value.ToByteArray() });
                await service.ConfirmTotpEnrollmentAsync(new TotpEnrollmentConfirmRequest(totp.FactorId, code, "secret:rotate"), CancellationToken.None);
                return null;
            }
            catch (Exception exception) { return exception; }
        }

        async Task<Exception?> CompleteWebAuthnAsync()
        {
            try
            {
                using var scope = environment.GetFactory().Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
                var accessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
                accessor.Current = actor;
                var service = CreateRealService(scope.ServiceProvider, db, accessor, scope.ServiceProvider.GetRequiredService<IClock>(), keys,
                    new FakeWebAuthn(sharedCredentialId) { UserHandle = actor.UserId!.Value.ToByteArray() });
                await service.CompleteWebAuthnRegistrationAsync(new WebAuthnRegistrationCompleteRequest(registration.CeremonyId, "{}"), CancellationToken.None);
                return null;
            }
            catch (Exception exception) { return exception; }
        }

        var outcomes = await Task.WhenAll(Task.Run(CompleteTotpAsync), Task.Run(CompleteWebAuthnAsync));
        outcomes.Count(x => x is null).Should().Be(1);
        outcomes.Count(x => x is MfaAuthorityStaleException).Should().Be(1,
            string.Join(" | ", outcomes.Select(x => x is null ? "success" : $"{x.GetType().Name}: {x.Message}")));
        using var verifyScope = environment.GetFactory().Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await verifyDb.MfaAuthorityStates.SingleAsync(x => x.TenantId == actor.TenantId && x.ActorUserId == actor.UserId)).Revision.Should().Be(2);
        var activeFactors = await verifyDb.TotpFactors.CountAsync(x => x.ActorUserId == actor.UserId && x.State == MfaFactorState.Active) +
            await verifyDb.WebAuthnCredentials.CountAsync(x => x.ActorUserId == actor.UserId && x.State == MfaFactorState.Active);
        activeFactors.Should().Be(1);
    }

    [DockerRequiredFact]
    public async Task Policy_revision_escalation_and_old_assertion_replay_must_fail_closed_without_factor_mutation()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var actorAccessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        var actor = await CreateActorAsync(db);
        actorAccessor.Current = actor;
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        var proof = await SeedAssertionAsync(db, actor, AuthenticationMethod.Password, AssuranceLevel.Aal1,
            "mfa:totp:enroll", "User", actor.UserId!.Value.ToString("D"), clock.UtcNow);
        var service = CreateRealService(scope.ServiceProvider, db, actorAccessor, clock, new TestKeyAuthority(), new FakeWebAuthn());
        var enrollment = await service.StartTotpEnrollmentAsync(new TotpEnrollmentStartRequest(proof.Proof), CancellationToken.None);
        var escalatedOptions = Options.Create(new StepUpAuthenticationOptions { MfaPolicyRevision = "wave4b-v2", WebAuthnRpId = "example.test", WebAuthnOrigins = ["https://example.test"] });
        var escalatedAuthority = new MfaAuthorityCoordinator(db, clock, escalatedOptions);
        var escalated = new MultiFactorAuthenticationService(db, actorAccessor, clock,
            scope.ServiceProvider.GetRequiredService<IStepUpAuthenticationService>(), scope.ServiceProvider.GetRequiredService<IStepUpAssertionIssuer>(),
            scope.ServiceProvider.GetRequiredService<IHighAssuranceApprovalVerifier>(), new TestKeyAuthority(), new FakeWebAuthn(), escalatedOptions, escalatedAuthority);
        var code = ComputeTotp(Base32Decode(enrollment.Secret), clock.UtcNow.ToUnixTimeSeconds() / 30);
        var stale = () => escalated.ConfirmTotpEnrollmentAsync(new TotpEnrollmentConfirmRequest(enrollment.FactorId, code, "secret:rotate"), CancellationToken.None);
        await stale.Should().ThrowAsync<MfaAuthorityStaleException>();
        db.ChangeTracker.Clear();
        (await db.TotpFactors.SingleAsync(x => x.Id == enrollment.FactorId)).State.Should().Be(MfaFactorState.Pending);
        (await db.MfaRecoveryCodes.CountAsync(x => x.TotpFactorId == enrollment.FactorId)).Should().Be(0);
        (await db.MfaAuthorityStates.SingleAsync(x => x.ActorUserId == actor.UserId)).PolicyRevision.Should().Be("wave4b-v2");

        var replay = await scope.ServiceProvider.GetRequiredService<IStepUpAuthenticationService>()
            .AuthorizeRequiredAssuranceAsync(AssuranceLevel.Aal1, "mfa:totp:enroll", "User", actor.UserId.Value.ToString("D"),
                new StepUpProof(proof.Assertion.Id, proof.Proof.Nonce, proof.Proof.ExpectedRevision + 1), CancellationToken.None);
        replay.Outcome.Should().Be(StepUpRequirementOutcome.RequiresStepUp);
    }

    [DockerRequiredFact]
    public async Task Recovery_reset_must_invalidate_pending_webauthn_completion()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var accessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        var actor = await CreateActorAsync(db);
        accessor.Current = actor;
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        var service = CreateRealService(scope.ServiceProvider, db, accessor, clock, new TestKeyAuthority(),
            new FakeWebAuthn { UserHandle = actor.UserId!.Value.ToByteArray() });
        var enrollProof = await SeedAssertionAsync(db, actor, AuthenticationMethod.Password, AssuranceLevel.Aal1,
            "mfa:totp:enroll", "User", actor.UserId.Value.ToString("D"), clock.UtcNow);
        var totp = await service.StartTotpEnrollmentAsync(new TotpEnrollmentStartRequest(enrollProof.Proof), CancellationToken.None);
        var confirmed = await service.ConfirmTotpEnrollmentAsync(new TotpEnrollmentConfirmRequest(totp.FactorId,
            ComputeTotp(Base32Decode(totp.Secret), clock.UtcNow.ToUnixTimeSeconds() / 30), "secret:rotate"), CancellationToken.None);
        var webProof = await SeedAssertionAsync(db, actor, AuthenticationMethod.Totp, AssuranceLevel.Aal2,
            "mfa:webauthn:register", "User", actor.UserId.Value.ToString("D"), clock.UtcNow);
        var registration = await service.StartWebAuthnRegistrationAsync(new WebAuthnRegistrationStartRequest(WebAuthnCredentialKind.Passkey, webProof.Proof), CancellationToken.None);
        var resetProof = await SeedAssertionAsync(db, actor, AuthenticationMethod.Totp, AssuranceLevel.Aal2,
            "mfa:recovery-codes:regenerate", "TotpFactor", totp.FactorId.ToString("D"), clock.UtcNow);
        await service.RegenerateRecoveryCodesAsync(new RecoveryCodeRegenerationRequest(totp.FactorId, resetProof.Proof), CancellationToken.None);
        var stale = () => service.CompleteWebAuthnRegistrationAsync(new WebAuthnRegistrationCompleteRequest(registration.CeremonyId, "{}"), CancellationToken.None);
        await stale.Should().ThrowAsync<MfaAuthorityStaleException>();
        db.ChangeTracker.Clear();
        (await db.WebAuthnCredentials.CountAsync(x => x.ActorUserId == actor.UserId)).Should().Be(0);
        (await db.MfaRecoveryCodes.CountAsync(x => x.TotpFactorId == totp.FactorId && x.UsedAt == null)).Should().Be(confirmed.RecoveryCodes.Count);
    }

    [DockerRequiredFact]
    public async Task Factor_revoke_must_invalidate_pending_totp_completion()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var accessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        var actor = await CreateActorAsync(db);
        accessor.Current = actor;
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        var webAuthn = new FakeWebAuthn { UserHandle = actor.UserId!.Value.ToByteArray() };
        var service = CreateRealService(scope.ServiceProvider, db, accessor, clock, new TestKeyAuthority(), webAuthn);
        var webEnrollProof = await SeedAssertionAsync(db, actor, AuthenticationMethod.Password, AssuranceLevel.Aal1,
            "mfa:webauthn:register", "User", actor.UserId.Value.ToString("D"), clock.UtcNow);
        var registration = await service.StartWebAuthnRegistrationAsync(new WebAuthnRegistrationStartRequest(WebAuthnCredentialKind.Passkey, webEnrollProof.Proof), CancellationToken.None);
        var credential = await service.CompleteWebAuthnRegistrationAsync(new WebAuthnRegistrationCompleteRequest(registration.CeremonyId, "{}"), CancellationToken.None);
        var totpProof = await SeedAssertionAsync(db, actor, AuthenticationMethod.Passkey, AssuranceLevel.Aal3,
            "mfa:totp:enroll", "User", actor.UserId.Value.ToString("D"), clock.UtcNow);
        var totp = await service.StartTotpEnrollmentAsync(new TotpEnrollmentStartRequest(totpProof.Proof), CancellationToken.None);
        var removeProof = await SeedAssertionAsync(db, actor, AuthenticationMethod.Passkey, AssuranceLevel.Aal3,
            "mfa:webauthn:remove", "WebAuthnCredential", credential.CredentialId.ToString("D"), clock.UtcNow);
        (await service.RemoveWebAuthnCredentialAsync(new FactorRemovalRequest(credential.CredentialId, removeProof.Proof, null, "revoked"), CancellationToken.None)).Applied.Should().BeTrue();
        var stale = () => service.ConfirmTotpEnrollmentAsync(new TotpEnrollmentConfirmRequest(totp.FactorId,
            ComputeTotp(Base32Decode(totp.Secret), clock.UtcNow.ToUnixTimeSeconds() / 30), "secret:rotate"), CancellationToken.None);
        await stale.Should().ThrowAsync<MfaAuthorityStaleException>();
        db.ChangeTracker.Clear();
        (await db.TotpFactors.SingleAsync(x => x.Id == totp.FactorId)).State.Should().Be(MfaFactorState.Pending);
        (await db.MfaRecoveryCodes.CountAsync(x => x.TotpFactorId == totp.FactorId)).Should().Be(0);
    }

    [DockerRequiredFact]
    public async Task Assurance_policy_must_reject_aal1_for_aal2_and_aal2_for_aal3_with_exact_binding()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var actorAccessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        var actor = await CreateActorAsync(db);
        actorAccessor.Current = actor;
        var service = scope.ServiceProvider.GetRequiredService<IStepUpAuthenticationService>();
        var now = DateTimeOffset.UtcNow;
        var resourceId = Guid.NewGuid().ToString("D");
        var aal1 = await SeedAssertionAsync(db, actor, AuthenticationMethod.Password, AssuranceLevel.Aal1, "credential:rotate", "Secret", resourceId, now);
        (await service.AuthorizeRequiredAssuranceAsync(AssuranceLevel.Aal2, "credential:rotate", "Secret", resourceId, aal1.Proof, CancellationToken.None))
            .Outcome.Should().Be(StepUpRequirementOutcome.RequiresStepUp);
        (await service.AuthorizeRequiredAssuranceAsync(AssuranceLevel.Aal1, "credential:rotate", "Secret", resourceId, aal1.Proof, CancellationToken.None))
            .Outcome.Should().Be(StepUpRequirementOutcome.Allowed);

        var aal2 = await SeedAssertionAsync(db, actor, AuthenticationMethod.Totp, AssuranceLevel.Aal2, "secret:export", "Secret", resourceId, now);
        (await service.AuthorizeRequiredAssuranceAsync(AssuranceLevel.Aal3, "secret:export", "Secret", resourceId, aal2.Proof, CancellationToken.None))
            .Outcome.Should().Be(StepUpRequirementOutcome.RequiresStepUp);
        (await service.AuthorizeRequiredAssuranceAsync(AssuranceLevel.Aal2, "other-purpose", "Secret", resourceId, aal2.Proof, CancellationToken.None))
            .Outcome.Should().Be(StepUpRequirementOutcome.RequiresStepUp);
        (await service.AuthorizeRequiredAssuranceAsync(AssuranceLevel.Aal2, "secret:export", "Secret", Guid.NewGuid().ToString("D"), aal2.Proof, CancellationToken.None))
            .Outcome.Should().Be(StepUpRequirementOutcome.RequiresStepUp);
        actorAccessor.Current = actor with { AuthenticationSessionId = "foreign-session" };
        (await service.AuthorizeRequiredAssuranceAsync(AssuranceLevel.Aal2, "secret:export", "Secret", resourceId, aal2.Proof, CancellationToken.None))
            .Outcome.Should().Be(StepUpRequirementOutcome.RequiresStepUp);
    }

    [DockerRequiredFact]
    public async Task Totp_enrollment_replay_recovery_regeneration_and_encrypted_storage_must_fail_closed()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var actorAccessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        var actor = await CreateActorAsync(db);
        actorAccessor.Current = actor;
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var stepUp = new AllowStepUp(db, actorAccessor, clock);
        var issuer = new CapturingIssuer(clock);
        var keys = new TestKeyAuthority();
        var service = CreateService(db, actorAccessor, clock, stepUp, issuer, keys, new FakeWebAuthn(), new AllowApproval(false));

        var enrollment = await service.StartTotpEnrollmentAsync(new TotpEnrollmentStartRequest(Proof()), CancellationToken.None);
        stepUp.LastRequiredAssurance.Should().Be(AssuranceLevel.Aal1);
        var factor = await db.TotpFactors.SingleAsync(x => x.Id == enrollment.FactorId);
        var version = await db.SecretVersions.SingleAsync(x => x.Id == factor.SeedSecretVersionId);
        var seed = Base32Decode(enrollment.Secret);
        version.Ciphertext.Should().NotEqual(seed);
        version.KeyId.Should().Be("mfa-test-kek");

        var firstCode = ComputeTotp(seed, clock.UtcNow.ToUnixTimeSeconds() / 30);
        var confirmed = await service.ConfirmTotpEnrollmentAsync(new TotpEnrollmentConfirmRequest(factor.Id, firstCode, "secret:rotate", "Secret", Guid.NewGuid().ToString("D")), CancellationToken.None);
        confirmed.Assertion.AssuranceLevel.Should().Be(AssuranceLevel.Aal2);
        confirmed.RecoveryCodes.Should().HaveCount(10).And.OnlyHaveUniqueItems();
        var storedCodes = await db.MfaRecoveryCodes.Where(x => x.TotpFactorId == factor.Id).ToArrayAsync();
        storedCodes.Should().OnlyContain(x => x.CodeHash.Length == 32 && x.Salt.Length == 16 && x.UsedAt == null);
        Encoding.UTF8.GetString(storedCodes[0].CodeHash).Should().NotContain(confirmed.RecoveryCodes[0].Replace("-", string.Empty));

        var replay = () => service.VerifyTotpAsync(new TotpVerificationRequest(firstCode, "secret:rotate"), CancellationToken.None);
        await replay.Should().ThrowAsync<UnauthorizedAccessException>();
        clock.UtcNow = clock.UtcNow.AddSeconds(30);
        var secondCode = ComputeTotp(seed, clock.UtcNow.ToUnixTimeSeconds() / 30);
        (await service.VerifyTotpAsync(new TotpVerificationRequest(secondCode, "secret:rotate"), CancellationToken.None)).AssuranceLevel.Should().Be(AssuranceLevel.Aal2);

        var recovery = confirmed.RecoveryCodes[0];
        (await service.VerifyRecoveryCodeAsync(new RecoveryCodeVerificationRequest(recovery, "secret:rotate"), CancellationToken.None)).AuthenticationMethod.Should().Be(AuthenticationMethod.RecoveryCode);
        var recoveryReplay = () => service.VerifyRecoveryCodeAsync(new RecoveryCodeVerificationRequest(recovery, "secret:rotate"), CancellationToken.None);
        await recoveryReplay.Should().ThrowAsync<UnauthorizedAccessException>();

        var oldUnusedCode = confirmed.RecoveryCodes[1];
        var regenerated = await service.RegenerateRecoveryCodesAsync(new RecoveryCodeRegenerationRequest(factor.Id, Proof()), CancellationToken.None);
        stepUp.LastRequiredAssurance.Should().Be(AssuranceLevel.Aal2);
        regenerated.RecoveryCodes.Should().HaveCount(10).And.OnlyHaveUniqueItems();
        var invalidatedOldCode = () => service.VerifyRecoveryCodeAsync(new RecoveryCodeVerificationRequest(oldUnusedCode, "secret:rotate"), CancellationToken.None);
        await invalidatedOldCode.Should().ThrowAsync<UnauthorizedAccessException>();
        (await service.VerifyRecoveryCodeAsync(new RecoveryCodeVerificationRequest(regenerated.RecoveryCodes[0], "secret:rotate"), CancellationToken.None)).AssuranceLevel.Should().Be(AssuranceLevel.Aal2);

        (await db.MfaSecurityEvents.Where(x => x.ActorUserId == actor.UserId).Select(x => x.Action).ToArrayAsync())
            .Should().Contain([MfaSecurityAction.TotpEnrolled, MfaSecurityAction.TotpReplayRejected, MfaSecurityAction.RecoveryCodeUsed, MfaSecurityAction.FactorReset]);
        CryptographicOperations.ZeroMemory(seed);
    }

    [DockerRequiredFact]
    public async Task WebAuthn_challenge_session_replay_counter_removal_and_agent_boundaries_must_fail_closed()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var actorAccessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        var actor = await CreateActorAsync(db);
        actorAccessor.Current = actor;
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var stepUp = new AllowStepUp(db, actorAccessor, clock);
        var issuer = new CapturingIssuer(clock);
        var keys = new TestKeyAuthority();
        var webAuthn = new FakeWebAuthn { UserHandle = actor.UserId!.Value.ToByteArray() };
        var service = CreateService(db, actorAccessor, clock, stepUp, issuer, keys, webAuthn, new AllowApproval(true));

        var registration = await service.StartWebAuthnRegistrationAsync(new WebAuthnRegistrationStartRequest(WebAuthnCredentialKind.Passkey, Proof()), CancellationToken.None);
        stepUp.LastRequiredAssurance.Should().Be(AssuranceLevel.Aal1);
        var registered = await service.CompleteWebAuthnRegistrationAsync(new WebAuthnRegistrationCompleteRequest(registration.CeremonyId, "{}"), CancellationToken.None);
        registered.Kind.Should().Be(WebAuthnCredentialKind.Passkey);
        var registrationReplay = () => service.CompleteWebAuthnRegistrationAsync(new WebAuthnRegistrationCompleteRequest(registration.CeremonyId, "{}"), CancellationToken.None);
        await registrationReplay.Should().ThrowAsync<UnauthorizedAccessException>();

        _ = await service.StartWebAuthnRegistrationAsync(new WebAuthnRegistrationStartRequest(WebAuthnCredentialKind.RoamingSecurityKey, Proof()), CancellationToken.None);
        stepUp.LastRequiredAssurance.Should().Be(AssuranceLevel.Aal3);

        var authentication = await service.StartWebAuthnAuthenticationAsync(new WebAuthnAuthenticationStartRequest("secret:export", "Secret", Guid.NewGuid().ToString("D")), CancellationToken.None);
        var wrongSession = actorAccessor.Current;
        actorAccessor.Current = wrongSession with { AuthenticationSessionId = "other-session" };
        var crossSession = () => service.CompleteWebAuthnAuthenticationAsync(new WebAuthnAuthenticationCompleteRequest(authentication.CeremonyId, CredentialJson(webAuthn.CredentialId)), CancellationToken.None);
        await crossSession.Should().ThrowAsync<UnauthorizedAccessException>();
        actorAccessor.Current = wrongSession;

        var authentication2 = await service.StartWebAuthnAuthenticationAsync(new WebAuthnAuthenticationStartRequest("secret:export", "Secret", Guid.NewGuid().ToString("D")), CancellationToken.None);
        var assertion = await service.CompleteWebAuthnAuthenticationAsync(new WebAuthnAuthenticationCompleteRequest(authentication2.CeremonyId, CredentialJson(webAuthn.CredentialId)), CancellationToken.None);
        assertion.AssuranceLevel.Should().Be(AssuranceLevel.Aal3);
        assertion.AuthenticationMethod.Should().Be(AuthenticationMethod.Passkey);
        var authReplay = () => service.CompleteWebAuthnAuthenticationAsync(new WebAuthnAuthenticationCompleteRequest(authentication2.CeremonyId, CredentialJson(webAuthn.CredentialId)), CancellationToken.None);
        await authReplay.Should().ThrowAsync<UnauthorizedAccessException>();

        var authentication3 = await service.StartWebAuthnAuthenticationAsync(new WebAuthnAuthenticationStartRequest("secret:export"), CancellationToken.None);
        webAuthn.SignCount = 1;
        var counterReplay = () => service.CompleteWebAuthnAuthenticationAsync(new WebAuthnAuthenticationCompleteRequest(authentication3.CeremonyId, CredentialJson(webAuthn.CredentialId)), CancellationToken.None);
        await counterReplay.Should().ThrowAsync<UnauthorizedAccessException>();

        var removal = await service.RemoveWebAuthnCredentialAsync(new FactorRemovalRequest(registered.CredentialId, null, "approved", "lost authenticator"), CancellationToken.None);
        removal.Applied.Should().BeTrue();
        var unavailable = () => service.StartWebAuthnAuthenticationAsync(new WebAuthnAuthenticationStartRequest("secret:export"), CancellationToken.None);
        await unavailable.Should().ThrowAsync<UnauthorizedAccessException>();

        actorAccessor.Current = wrongSession with { IsInteractiveUser = false, IsServiceActor = true };
        var agent = () => service.StartWebAuthnRegistrationAsync(new WebAuthnRegistrationStartRequest(WebAuthnCredentialKind.Passkey, Proof()), CancellationToken.None);
        await agent.Should().ThrowAsync<UnauthorizedAccessException>();
        (await db.MfaSecurityEvents.Where(x => x.ActorUserId == actor.UserId).Select(x => x.Action).ToArrayAsync())
            .Should().Contain([MfaSecurityAction.WebAuthnRegistered, MfaSecurityAction.WebAuthnAuthenticated, MfaSecurityAction.WebAuthnReplayRejected, MfaSecurityAction.RecoveryApproved]);
    }

    [Fact]
    public void WebAuthn_configuration_requires_exact_https_origin_and_rp_id()
    {
        var invalid = new Fido2WebAuthnCeremonyVerifier(Options.Create(new StepUpAuthenticationOptions
        {
            WebAuthnRpId = "example.test",
            WebAuthnOrigins = ["http://example.test"]
        }));
        var action = () => invalid.CreateAuthenticationOptions([]);
        action.Should().Throw<InvalidOperationException>().WithMessage("WebAuthn relying-party configuration is unavailable.");
    }

    [Theory]
    [InlineData("https://evil.example.test", "example.test")]
    [InlineData("https://example.test", "other.example.test")]
    public async Task WebAuthn_library_verification_must_reject_wrong_origin_or_rp_binding(string clientOrigin, string rpHashInput)
    {
        var verifier = new Fido2WebAuthnCeremonyVerifier(Options.Create(new StepUpAuthenticationOptions
        {
            WebAuthnRpId = "example.test",
            WebAuthnOrigins = ["https://example.test"]
        }));
        var optionsJson = verifier.CreateRegistrationOptions("user", Guid.NewGuid().ToByteArray(), WebAuthnCredentialKind.Platform, []);
        var credentialJson = BuildAttestationJson(optionsJson, clientOrigin, rpHashInput);
        var action = () => verifier.VerifyRegistrationAsync(credentialJson, optionsJson, (_, _) => Task.FromResult(true), CancellationToken.None);
        await action.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task WebAuthn_library_registration_must_accept_exact_origin_rp_challenge_and_uv_binding()
    {
        var verifier = new Fido2WebAuthnCeremonyVerifier(Options.Create(new StepUpAuthenticationOptions
        {
            WebAuthnRpId = "example.test",
            WebAuthnOrigins = ["https://example.test"]
        }));
        var userHandle = Guid.NewGuid().ToByteArray();
        var optionsJson = verifier.CreateRegistrationOptions("user", userHandle, WebAuthnCredentialKind.Platform, []);
        var result = await verifier.VerifyRegistrationAsync(BuildAttestationJson(optionsJson, "https://example.test", "example.test"), optionsJson,
            (_, _) => Task.FromResult(true), CancellationToken.None);
        result.UserHandle.Should().Equal(userHandle);
        result.PublicKey.Should().NotBeEmpty();
    }

    private static MultiFactorAuthenticationService CreateService(MemoryDbContext db, IRequestActorAccessor actorAccessor, IClock clock,
        IStepUpAuthenticationService stepUp, IStepUpAssertionIssuer issuer, ISecretEnvelopeKeyAuthority keys, IWebAuthnCeremonyVerifier webAuthn,
        IHighAssuranceApprovalVerifier approval)
        => new(db, actorAccessor, clock, stepUp, issuer, approval, keys, webAuthn, Options.Create(new StepUpAuthenticationOptions
        {
            TotpIssuer = "ContextHub Test",
            TotpAllowedDriftSteps = 1,
            RecoveryCodeCount = 10,
            WebAuthnRpId = "example.test",
            WebAuthnOrigins = ["https://example.test"]
        }), new PermissiveMfaAuthorityCoordinator(db, clock));

    private static MultiFactorAuthenticationService CreateRealService(IServiceProvider services, MemoryDbContext db,
        IRequestActorAccessor actorAccessor, IClock clock, ISecretEnvelopeKeyAuthority keys, IWebAuthnCeremonyVerifier webAuthn)
        => new(db, actorAccessor, clock, services.GetRequiredService<IStepUpAuthenticationService>(),
            services.GetRequiredService<IStepUpAssertionIssuer>(), services.GetRequiredService<IHighAssuranceApprovalVerifier>(), keys, webAuthn,
            services.GetRequiredService<IOptions<StepUpAuthenticationOptions>>(), services.GetRequiredService<IMfaAuthorityCoordinator>());

    private static async Task<ContextHubRequestActor> CreateActorAsync(MemoryDbContext db)
    {
        var tenant = await db.Tenants.FirstAsync();
        var user = new TenantUser
        {
            TenantId = tenant.Id,
            Username = $"wave4b-{Guid.NewGuid():N}",
            DisplayName = "Wave 4B",
            Role = TenantUserRole.Admin,
            Status = TenantUserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.TenantUsers.Add(user);
        await db.SaveChangesAsync();
        return new ContextHubRequestActor(tenant.Id, user.Id, user.Username, user.Role,
            [SecurityScopes.SecurityManage, SecurityScopes.SecretsRead, SecurityScopes.SecretsUse, SecurityScopes.SecretsManage],
            ["ContextHub"], true, IsInteractiveUser: true, AuthenticationSessionId: $"session-{Guid.NewGuid():N}");
    }

    private static StepUpProof Proof() => new(Guid.NewGuid(), "proof", 1);

    private static async Task<(StepUpAssertion Assertion, StepUpProof Proof)> SeedAssertionAsync(MemoryDbContext db, ContextHubRequestActor actor,
        AuthenticationMethod method, AssuranceLevel assurance, string purpose, string resourceType, string resourceId, DateTimeOffset now)
    {
        var authority = await db.MfaAuthorityStates.SingleOrDefaultAsync(x => x.TenantId == actor.TenantId && x.ActorUserId == actor.UserId);
        if (authority is null)
        {
            authority = new MfaAuthorityState
            {
                TenantId = actor.TenantId!.Value,
                ActorUserId = actor.UserId!.Value,
                PolicyRevision = "wave4b-v1",
                UpdatedAt = now
            };
            db.MfaAuthorityStates.Add(authority);
        }
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var assertion = new StepUpAssertion
        {
            TenantId = actor.TenantId!.Value,
            ActorUserId = actor.UserId!.Value,
            SessionHash = Sha256(actor.AuthenticationSessionId!),
            AuthenticationMethod = method,
            AssuranceLevel = assurance,
            Purpose = purpose,
            ResourceType = resourceType,
            ResourceId = resourceId,
            NonceHash = Sha256(nonce),
            MfaAuthorityRevision = authority.Revision,
            MfaPolicyRevision = authority.PolicyRevision,
            MaxUses = 1,
            AuthTime = now,
            IssuedAt = now,
            ExpiresAt = now.AddMinutes(5)
        };
        db.StepUpAssertions.Add(assertion);
        await db.SaveChangesAsync();
        return (assertion, new StepUpProof(assertion.Id, nonce, assertion.Revision));
    }

    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string CredentialJson(byte[] credentialId) => $$"""{"rawId":"{{Convert.ToBase64String(credentialId).TrimEnd('=').Replace('+', '-').Replace('/', '_')}}"}""";

    private static string BuildAttestationJson(string optionsJson, string origin, string rpHashInput)
    {
        var options = CredentialCreateOptions.FromJson(optionsJson);
        var attestedCredential = AttestedCredentialData.Parse(Convert.FromHexString("000000000000000000000000000000000040FE6A3263BE37D101B12E57CA966C002293E419C8CD0106230BC692E8CC771221F1DB115D410F826BDB98AC642EB1AEB5A803D1DBC147EF371CFDB1CEB048CB2CA5010203262001215820A6D109385AC78E5BF03D1C2E0874BE6DBBA40B4F2A5F2F1182456565534F672822582043E1082AF3135B40609379AC474258AAB397B8861DE441B44E83085D1C6BE0D0"));
        var authData = new AuthenticatorData(SHA256.HashData(Encoding.UTF8.GetBytes(rpHashInput)), AuthenticatorFlags.UP | AuthenticatorFlags.UV | AuthenticatorFlags.AT, 0, attestedCredential).ToByteArray();
        var challenge = Convert.ToBase64String(options.Challenge).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var clientData = JsonSerializer.SerializeToUtf8Bytes(new { type = "webauthn.create", challenge, origin });
        var response = new AuthenticatorAttestationRawResponse
        {
            Type = PublicKeyCredentialType.PublicKey,
            Id = Convert.ToBase64String(attestedCredential.CredentialId).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            RawId = attestedCredential.CredentialId,
            Response = new AuthenticatorAttestationRawResponse.AttestationResponse
            {
                AttestationObject = new CborMap { { "fmt", "none" }, { "attStmt", new CborMap() }, { "authData", authData } }.Encode(),
                ClientDataJson = clientData
            }
        };
        return JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static string ComputeTotp(byte[] seed, long counter)
    {
        Span<byte> counterBytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counterBytes, counter);
        using var hmac = new HMACSHA1(seed);
        var digest = hmac.ComputeHash(counterBytes.ToArray());
        var offset = digest[^1] & 0x0f;
        var binary = ((digest[offset] & 0x7f) << 24) | (digest[offset + 1] << 16) | (digest[offset + 2] << 8) | digest[offset + 3];
        CryptographicOperations.ZeroMemory(digest);
        return (binary % 1_000_000).ToString("D6");
    }

    private static byte[] Base32Decode(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new List<byte>();
        var buffer = 0;
        var bitsLeft = 0;
        foreach (var character in value)
        {
            buffer = (buffer << 5) | alphabet.IndexOf(character);
            bitsLeft += 5;
            if (bitsLeft < 8) continue;
            output.Add((byte)(buffer >> (bitsLeft - 8)));
            bitsLeft -= 8;
        }
        return output.ToArray();
    }

    private sealed class MutableClock(DateTimeOffset value) : IClock { public DateTimeOffset UtcNow { get; set; } = value; }

    private sealed class PermissiveMfaAuthorityCoordinator(MemoryDbContext db, IClock clock) : IMfaAuthorityCoordinator
    {
        private readonly MfaAuthorityState state = new()
        {
            TenantId = Guid.Empty,
            ActorUserId = Guid.Empty,
            Revision = 1,
            PolicyRevision = "wave4b-v1",
            UpdatedAt = clock.UtcNow
        };

        public Task<MfaAuthoritySnapshot> LockCurrentAsync(ContextHubRequestActor actor, CancellationToken cancellationToken)
            => Task.FromResult(new MfaAuthoritySnapshot(state, state.PolicyRevision));

        public async Task<AssuranceLevel> RequiredAssuranceAsync(ContextHubRequestActor actor, AssuranceLevel policyFloor, CancellationToken cancellationToken)
        {
            var strongest = await db.WebAuthnCredentials.AnyAsync(x => x.ActorUserId == actor.UserId && x.State == MfaFactorState.Active, cancellationToken)
                ? AssuranceLevel.Aal3
                : await db.TotpFactors.AnyAsync(x => x.ActorUserId == actor.UserId && x.State == MfaFactorState.Active, cancellationToken)
                    ? AssuranceLevel.Aal2
                    : AssuranceLevel.Aal0;
            return strongest > policyFloor ? strongest : policyFloor;
        }

        public Task ValidatePendingAuthorizationAsync(ContextHubRequestActor actor, MfaAuthoritySnapshot current,
            long authorityRevisionAtStart, string policyRevisionAtStart, Guid assertionId, long assertionRevision,
            AssuranceLevel requiredAssurance, string purpose, string? resourceType, string? resourceId, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public void Advance(MfaAuthoritySnapshot current)
        {
            current.State.Revision++;
            current.State.UpdatedAt = clock.UtcNow;
        }
    }

    private sealed class AllowStepUp(MemoryDbContext db, IRequestActorAccessor actorAccessor, IClock clock) : IStepUpAuthenticationService
    {
        public AssuranceLevel LastRequiredAssurance { get; private set; }
        public Task<StepUpAssertionResult> CreatePasswordAssertionAsync(PasswordStepUpRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StepUpAuthorizationResult> AuthorizeAsync(StepUpOperationClass operation, string purpose, string? resourceType, string? resourceId, StepUpProof? proof, CancellationToken cancellationToken)
            => AuthorizeRequiredAssuranceAsync(StepUpRiskPolicy.Describe(operation).RequiredAssurance, purpose, resourceType, resourceId, proof, cancellationToken);
        public async Task<StepUpAuthorizationResult> AuthorizeRequiredAssuranceAsync(AssuranceLevel requiredAssurance, string purpose, string? resourceType, string? resourceId, StepUpProof? proof, CancellationToken cancellationToken)
        {
            LastRequiredAssurance = requiredAssurance;
            if (proof is not null && !await db.StepUpAssertions.AnyAsync(x => x.Id == proof.AssertionId, cancellationToken))
            {
                var actor = actorAccessor.Current;
                db.StepUpAssertions.Add(new StepUpAssertion
                {
                    Id = proof.AssertionId,
                    TenantId = actor.TenantId!.Value,
                    ActorUserId = actor.UserId!.Value,
                    SessionHash = Sha256(actor.AuthenticationSessionId!),
                    AuthenticationMethod = requiredAssurance switch
                    {
                        AssuranceLevel.Aal3 => AuthenticationMethod.Passkey,
                        AssuranceLevel.Aal2 => AuthenticationMethod.Totp,
                        _ => AuthenticationMethod.Password
                    },
                    AssuranceLevel = requiredAssurance,
                    Purpose = purpose,
                    ResourceType = resourceType,
                    ResourceId = resourceId,
                    NonceHash = Sha256(proof.Nonce),
                    MfaAuthorityRevision = 1,
                    MfaPolicyRevision = "wave4b-v1",
                    State = StepUpAssertionState.Exhausted,
                    MaxUses = 1,
                    UsedCount = 1,
                    Revision = checked(proof.ExpectedRevision + 1),
                    AuthTime = clock.UtcNow,
                    IssuedAt = clock.UtcNow,
                    ExpiresAt = clock.UtcNow.AddMinutes(5)
                });
                await db.SaveChangesAsync(cancellationToken);
            }
            return new StepUpAuthorizationResult(proof is null ? StepUpRequirementOutcome.RequiresStepUp : StepUpRequirementOutcome.Allowed,
                requiredAssurance, proof is null ? "TestStepUpRequired" : "TestAllowed");
        }
    }

    private sealed class CapturingIssuer(IClock clock) : IStepUpAssertionIssuer
    {
        public Task<StepUpAssertionResult> IssueFactorAssertionAsync(AuthenticationMethod authenticationMethod, string purpose, string? resourceType, string? resourceId, int maxUses, DateTimeOffset authTime, CancellationToken cancellationToken)
            => Task.FromResult(new StepUpAssertionResult(Guid.NewGuid(), "nonce", authenticationMethod,
                authenticationMethod is AuthenticationMethod.Totp or AuthenticationMethod.RecoveryCode ? AssuranceLevel.Aal2 : AssuranceLevel.Aal3,
                purpose, resourceType, resourceId, maxUses, authTime, clock.UtcNow.AddMinutes(5), 1));
    }

    private sealed class FakeWebAuthn(byte[]? credentialId = null) : IWebAuthnCeremonyVerifier
    {
        private static readonly byte[] StableChallenge = SHA256.HashData("wave4b-stable-challenge"u8.ToArray());
        public byte[] CredentialId { get; } = credentialId ?? RandomNumberGenerator.GetBytes(32);
        public byte[] UserHandle { get; set; } = [];
        public uint SignCount { get; set; } = 1;
        public string CreateRegistrationOptions(string username, byte[] userHandle, WebAuthnCredentialKind kind, IReadOnlyList<(byte[] Id, IReadOnlyList<string> Transports)> existingCredentials)
            => "{\"challenge\":\"test\"}";
        public Task<WebAuthnRegistrationVerification> VerifyRegistrationAsync(string credentialJson, string optionsJson, Func<byte[], CancellationToken, Task<bool>> isUnique, CancellationToken cancellationToken)
            => Task.FromResult(new WebAuthnRegistrationVerification(CredentialId, RandomNumberGenerator.GetBytes(77), UserHandle, 0, ["Internal", "Hybrid"], true, true, Guid.NewGuid()));
        public string CreateAuthenticationOptions(IReadOnlyList<(byte[] Id, IReadOnlyList<string> Transports)> credentials)
            => "{\"challenge\":\"test\"}";
        public Task<WebAuthnAssertionVerification> VerifyAuthenticationAsync(string credentialJson, string optionsJson, byte[] publicKey, uint storedSignCount, Func<byte[], byte[], CancellationToken, Task<bool>> isUserHandleOwner, CancellationToken cancellationToken)
            => Task.FromResult(new WebAuthnAssertionVerification(CredentialId, SignCount, true));
        public byte[] ReadRegistrationChallenge(string optionsJson) => StableChallenge;
        public byte[] ReadAuthenticationChallenge(string optionsJson) => StableChallenge;
    }

    private sealed class AllowApproval(bool allowed) : IHighAssuranceApprovalVerifier
    {
        public Task<bool> VerifyAsync(string actorId, string approvalReference, string purpose, CancellationToken cancellationToken) => Task.FromResult(allowed);
    }

    private sealed class TestKeyAuthority : ISecretEnvelopeKeyAuthority
    {
        private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
        public WrappedSecretKey Wrap(Guid secretId, Guid versionId, ReadOnlySpan<byte> plaintextKey)
        {
            var nonce = RandomNumberGenerator.GetBytes(12); var ciphertext = new byte[plaintextKey.Length]; var tag = new byte[16];
            using var aes = new AesGcm(key, 16); aes.Encrypt(nonce, plaintextKey, ciphertext, tag, Aad(secretId, versionId));
            return new WrappedSecretKey("mfa-test-kek", ciphertext, nonce, tag);
        }
        public byte[] Unwrap(Guid secretId, Guid versionId, WrappedSecretKey wrappedKey)
        {
            var plaintext = new byte[wrappedKey.Ciphertext.Length];
            using var aes = new AesGcm(key, 16); aes.Decrypt(wrappedKey.Nonce, wrappedKey.Ciphertext, wrappedKey.Tag, plaintext, Aad(secretId, versionId));
            return plaintext;
        }
        public WrappedSecretKey Rewrap(Guid secretId, Guid versionId, WrappedSecretKey wrappedKey) => wrappedKey;
        private static byte[] Aad(Guid secretId, Guid versionId) => Encoding.UTF8.GetBytes($"contexthub-secret-kek-v1|{secretId:D}|{versionId:D}|mfa-test-kek");
    }
}
