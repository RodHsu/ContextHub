using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Memory.Application;

public sealed record TotpEnrollmentStartRequest(StepUpProof StepUp);
public sealed record TotpEnrollmentStartResult(Guid FactorId, string Secret, string OtpAuthUri, DateTimeOffset ExpiresAt);
public sealed record TotpEnrollmentConfirmRequest(Guid FactorId, string Code, string Purpose, string? ResourceType = null, string? ResourceId = null, int MaxUses = 1);
public sealed record TotpEnrollmentConfirmResult(StepUpAssertionResult Assertion, IReadOnlyList<string> RecoveryCodes);
public sealed record TotpVerificationRequest(string Code, string Purpose, string? ResourceType = null, string? ResourceId = null, int MaxUses = 1);
public sealed record RecoveryCodeVerificationRequest(string RecoveryCode, string Purpose, string? ResourceType = null, string? ResourceId = null, int MaxUses = 1);
public sealed record RecoveryCodeRegenerationRequest(Guid FactorId, StepUpProof StepUp);
public sealed record RecoveryCodeRegenerationResult(Guid FactorId, IReadOnlyList<string> RecoveryCodes);
public sealed record WebAuthnRegistrationStartRequest(WebAuthnCredentialKind Kind, StepUpProof StepUp);
public sealed record WebAuthnCeremonyStartResult(Guid CeremonyId, string OptionsJson, DateTimeOffset ExpiresAt);
public sealed record WebAuthnRegistrationCompleteRequest(Guid CeremonyId, string CredentialJson);
public sealed record WebAuthnRegistrationResult(Guid CredentialId, WebAuthnCredentialKind Kind, IReadOnlyList<string> Transports, uint SignCount, bool IsBackupEligible, bool IsBackedUp);
public sealed record WebAuthnAuthenticationStartRequest(string Purpose, string? ResourceType = null, string? ResourceId = null);
public sealed record WebAuthnAuthenticationCompleteRequest(Guid CeremonyId, string CredentialJson, int MaxUses = 1);
public sealed record FactorRemovalRequest(Guid FactorId, StepUpProof? StepUp, string? ExternalApprovalReference, string Reason);
public sealed record FactorMutationResult(bool Applied, StepUpRequirementOutcome Outcome, AssuranceLevel RequiredAssurance, string ReasonCode);

public sealed record WebAuthnRegistrationVerification(
    byte[] CredentialId,
    byte[] PublicKey,
    byte[] UserHandle,
    uint SignCount,
    IReadOnlyList<string> Transports,
    bool IsBackupEligible,
    bool IsBackedUp,
    Guid AaGuid);

public sealed record WebAuthnAssertionVerification(byte[] CredentialId, uint SignCount, bool IsBackedUp);

public interface IWebAuthnCeremonyVerifier
{
    string CreateRegistrationOptions(string username, byte[] userHandle, WebAuthnCredentialKind kind, IReadOnlyList<(byte[] Id, IReadOnlyList<string> Transports)> existingCredentials);
    Task<WebAuthnRegistrationVerification> VerifyRegistrationAsync(string credentialJson, string optionsJson, Func<byte[], CancellationToken, Task<bool>> isUnique, CancellationToken cancellationToken);
    string CreateAuthenticationOptions(IReadOnlyList<(byte[] Id, IReadOnlyList<string> Transports)> credentials);
    Task<WebAuthnAssertionVerification> VerifyAuthenticationAsync(
        string credentialJson,
        string optionsJson,
        byte[] publicKey,
        uint storedSignCount,
        Func<byte[], byte[], CancellationToken, Task<bool>> isUserHandleOwner,
        CancellationToken cancellationToken);
    byte[] ReadRegistrationChallenge(string optionsJson);
    byte[] ReadAuthenticationChallenge(string optionsJson);
}

public interface IMultiFactorAuthenticationService
{
    Task<TotpEnrollmentStartResult> StartTotpEnrollmentAsync(TotpEnrollmentStartRequest request, CancellationToken cancellationToken);
    Task<TotpEnrollmentConfirmResult> ConfirmTotpEnrollmentAsync(TotpEnrollmentConfirmRequest request, CancellationToken cancellationToken);
    Task<StepUpAssertionResult> VerifyTotpAsync(TotpVerificationRequest request, CancellationToken cancellationToken);
    Task<StepUpAssertionResult> VerifyRecoveryCodeAsync(RecoveryCodeVerificationRequest request, CancellationToken cancellationToken);
    Task<RecoveryCodeRegenerationResult> RegenerateRecoveryCodesAsync(RecoveryCodeRegenerationRequest request, CancellationToken cancellationToken);
    Task<WebAuthnCeremonyStartResult> StartWebAuthnRegistrationAsync(WebAuthnRegistrationStartRequest request, CancellationToken cancellationToken);
    Task<WebAuthnRegistrationResult> CompleteWebAuthnRegistrationAsync(WebAuthnRegistrationCompleteRequest request, CancellationToken cancellationToken);
    Task<WebAuthnCeremonyStartResult> StartWebAuthnAuthenticationAsync(WebAuthnAuthenticationStartRequest request, CancellationToken cancellationToken);
    Task<StepUpAssertionResult> CompleteWebAuthnAuthenticationAsync(WebAuthnAuthenticationCompleteRequest request, CancellationToken cancellationToken);
    Task<FactorMutationResult> RemoveTotpFactorAsync(FactorRemovalRequest request, CancellationToken cancellationToken);
    Task<FactorMutationResult> RemoveWebAuthnCredentialAsync(FactorRemovalRequest request, CancellationToken cancellationToken);
}

public sealed class Fido2WebAuthnCeremonyVerifier(IOptions<StepUpAuthenticationOptions> options) : IWebAuthnCeremonyVerifier
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string CreateRegistrationOptions(string username, byte[] userHandle, WebAuthnCredentialKind kind, IReadOnlyList<(byte[] Id, IReadOnlyList<string> Transports)> existingCredentials)
    {
        var selection = kind switch
        {
            WebAuthnCredentialKind.Platform => new AuthenticatorSelection
            {
                AuthenticatorAttachment = AuthenticatorAttachment.Platform,
                ResidentKey = ResidentKeyRequirement.Preferred,
                UserVerification = UserVerificationRequirement.Required
            },
            WebAuthnCredentialKind.RoamingSecurityKey => new AuthenticatorSelection
            {
                AuthenticatorAttachment = AuthenticatorAttachment.CrossPlatform,
                ResidentKey = ResidentKeyRequirement.Discouraged,
                UserVerification = UserVerificationRequirement.Required
            },
            WebAuthnCredentialKind.Passkey => new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Required,
                UserVerification = UserVerificationRequirement.Required
            },
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
        var ceremony = CreateFido2();
        var result = ceremony.RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Id = userHandle, Name = username, DisplayName = username },
            ExcludeCredentials = existingCredentials.Select(x => new PublicKeyCredentialDescriptor(PublicKeyCredentialType.PublicKey, x.Id, ParseTransports(x.Transports))).ToArray(),
            AuthenticatorSelection = selection,
            AttestationPreference = AttestationConveyancePreference.None
        });
        return result.ToJson();
    }

    public async Task<WebAuthnRegistrationVerification> VerifyRegistrationAsync(
        string credentialJson,
        string optionsJson,
        Func<byte[], CancellationToken, Task<bool>> isUnique,
        CancellationToken cancellationToken)
    {
        var response = JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(credentialJson, JsonOptions)
            ?? throw new UnauthorizedAccessException("WebAuthn registration response is invalid.");
        var result = await CreateFido2().MakeNewCredentialAsync(new MakeNewCredentialParams
        {
            AttestationResponse = response,
            OriginalOptions = CredentialCreateOptions.FromJson(optionsJson),
            IsCredentialIdUniqueToUserCallback = async (args, token) => await isUnique(args.CredentialId, token)
        }, cancellationToken);
        return new WebAuthnRegistrationVerification(result.Id, result.PublicKey, result.User.Id, result.SignCount,
            result.Transports?.Select(x => x.ToString()).ToArray() ?? [], result.IsBackupEligible, result.IsBackedUp, result.AaGuid);
    }

    public string CreateAuthenticationOptions(IReadOnlyList<(byte[] Id, IReadOnlyList<string> Transports)> credentials)
    {
        var result = CreateFido2().GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = credentials.Select(x => new PublicKeyCredentialDescriptor(PublicKeyCredentialType.PublicKey, x.Id, ParseTransports(x.Transports))).ToArray(),
            UserVerification = UserVerificationRequirement.Required
        });
        return result.ToJson();
    }

    public async Task<WebAuthnAssertionVerification> VerifyAuthenticationAsync(
        string credentialJson,
        string optionsJson,
        byte[] publicKey,
        uint storedSignCount,
        Func<byte[], byte[], CancellationToken, Task<bool>> isUserHandleOwner,
        CancellationToken cancellationToken)
    {
        var response = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(credentialJson, JsonOptions)
            ?? throw new UnauthorizedAccessException("WebAuthn authentication response is invalid.");
        var result = await CreateFido2().MakeAssertionAsync(new MakeAssertionParams
        {
            AssertionResponse = response,
            OriginalOptions = AssertionOptions.FromJson(optionsJson),
            StoredPublicKey = publicKey,
            StoredSignatureCounter = storedSignCount,
            IsUserHandleOwnerOfCredentialIdCallback = async (args, token) => await isUserHandleOwner(args.UserHandle, args.CredentialId, token)
        }, cancellationToken);
        return new WebAuthnAssertionVerification(result.CredentialId, result.SignCount, result.IsBackedUp);
    }

    public byte[] ReadRegistrationChallenge(string optionsJson) => CredentialCreateOptions.FromJson(optionsJson).Challenge;
    public byte[] ReadAuthenticationChallenge(string optionsJson) => AssertionOptions.FromJson(optionsJson).Challenge;

    private Fido2 CreateFido2()
    {
        var value = options.Value;
        var rpId = value.WebAuthnRpId.Trim();
        var origins = value.WebAuthnOrigins.Select(x => x.Trim()).Where(x => x.Length > 0).ToHashSet(StringComparer.Ordinal);
        if (rpId.Length == 0 || origins.Count == 0 || origins.Any(x => !Uri.TryCreate(x, UriKind.Absolute, out var uri) || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("WebAuthn relying-party configuration is unavailable.");
        return new Fido2(new Fido2Configuration
        {
            RPID = rpId,
            RPName = string.IsNullOrWhiteSpace(value.WebAuthnRpName) ? "ContextHub" : value.WebAuthnRpName.Trim(),
            Origins = origins,
            ChallengeSize = 32,
            TimestampDriftTolerance = 0,
            Timeout = checked((uint)value.NormalizedWebAuthnChallengeTtl.TotalMilliseconds)
        }, null!);
    }

    private static AuthenticatorTransport[] ParseTransports(IReadOnlyList<string> transports)
        => transports.Select(x => Enum.TryParse<AuthenticatorTransport>(x, true, out var value) ? value : (AuthenticatorTransport?)null)
            .Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
}

public sealed class MultiFactorAuthenticationService(
    IApplicationDbContext dbContext,
    IRequestActorAccessor actorAccessor,
    IClock clock,
    IStepUpAuthenticationService stepUp,
    IStepUpAssertionIssuer assertionIssuer,
    IHighAssuranceApprovalVerifier externalApproval,
    ISecretEnvelopeKeyAuthority keyAuthority,
    IWebAuthnCeremonyVerifier webAuthn,
    IOptions<StepUpAuthenticationOptions> options,
    IMfaAuthorityCoordinator mfaAuthority) : IMultiFactorAuthenticationService
{
    private const int TotpSeedBytes = 20;
    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int RecoveryIterations = 100_000;

    public async Task<TotpEnrollmentStartResult> StartTotpEnrollmentAsync(TotpEnrollmentStartRequest request, CancellationToken cancellationToken)
    {
        var actor = RequireInteractiveActor();
        await using var transaction = await dbContext.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var authority = await mfaAuthority.LockCurrentAsync(actor, cancellationToken);
        var enrollmentAssurance = await mfaAuthority.RequiredAssuranceAsync(actor, StepUpRiskPolicy.Describe(StepUpOperationClass.MfaFactorEnroll).RequiredAssurance, cancellationToken);
        var authorization = await stepUp.AuthorizeRequiredAssuranceAsync(enrollmentAssurance, "mfa:totp:enroll", "User", actor.UserId!.Value.ToString("D"), request.StepUp, cancellationToken);
        if (authorization.Outcome != StepUpRequirementOutcome.Allowed)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            EnsureAllowed(authorization);
        }
        await dbContext.AcquireTransactionLockAsync($"mfa-totp:{actor.TenantId:D}:{actor.UserId:D}", cancellationToken);
        var now = clock.UtcNow;
        var activeExists = await dbContext.TotpFactors.AnyAsync(x => x.TenantId == actor.TenantId && x.ActorUserId == actor.UserId && x.State == MfaFactorState.Active, cancellationToken);
        if (activeExists) throw new InvalidOperationException("An active TOTP factor already exists.");
        var stalePending = await dbContext.TotpFactors.Where(x => x.TenantId == actor.TenantId && x.ActorUserId == actor.UserId && x.State == MfaFactorState.Pending).ToArrayAsync(cancellationToken);
        foreach (var stale in stalePending)
        {
            stale.State = MfaFactorState.Revoked;
            stale.RemovedAt = now;
            stale.Revision++;
        }

        var seed = RandomNumberGenerator.GetBytes(TotpSeedBytes);
        try
        {
            var (secret, version) = CreateEncryptedTotpSecret(actor, seed, now);
            var factor = new TotpFactor
            {
                TenantId = actor.TenantId!.Value,
                ActorUserId = actor.UserId!.Value,
                SeedSecretId = secret.Id,
                SeedSecretVersionId = version.Id,
                Issuer = NormalizeIssuer(options.Value.TotpIssuer),
                AccountName = actor.Username,
                AuthorityRevisionAtStart = authority.State.Revision,
                PolicyRevisionAtStart = authority.PolicyRevision,
                RequiredAssuranceAtStart = enrollmentAssurance,
                AuthorizationAssertionId = request.StepUp.AssertionId,
                AuthorizationAssertionRevision = checked(request.StepUp.ExpectedRevision + 1),
                CreatedAt = now,
                ExpiresAt = now + options.Value.NormalizedTotpEnrollmentTtl
            };
            await dbContext.Secrets.AddAsync(secret, cancellationToken);
            await dbContext.SecretVersions.AddAsync(version, cancellationToken);
            secret.CurrentVersionId = version.Id;
            await dbContext.TotpFactors.AddAsync(factor, cancellationToken);
            await AddEventAsync(actor, factor.Id, MfaSecurityAction.TotpEnrollmentStarted, null, null, "mfa:totp:enroll", null, true, "EnrollmentStarted", cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            var encoded = Base32Encode(seed);
            var uri = $"otpauth://totp/{Uri.EscapeDataString(factor.Issuer)}:{Uri.EscapeDataString(factor.AccountName)}?secret={encoded}&issuer={Uri.EscapeDataString(factor.Issuer)}&algorithm=SHA1&digits=6&period={options.Value.NormalizedTotpPeriodSeconds}";
            return new TotpEnrollmentStartResult(factor.Id, encoded, uri, factor.ExpiresAt);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    public async Task<TotpEnrollmentConfirmResult> ConfirmTotpEnrollmentAsync(TotpEnrollmentConfirmRequest request, CancellationToken cancellationToken)
    {
        var actor = RequireInteractiveActor();
        ValidateBinding(request.Purpose, request.ResourceType, request.ResourceId, request.MaxUses);
        await using var transaction = await dbContext.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var authority = await mfaAuthority.LockCurrentAsync(actor, cancellationToken);
        await dbContext.AcquireTransactionLockAsync($"mfa-totp-factor:{request.FactorId:D}", cancellationToken);
        var factor = await dbContext.TotpFactors.SingleOrDefaultAsync(x => x.Id == request.FactorId && x.TenantId == actor.TenantId && x.ActorUserId == actor.UserId, cancellationToken)
            ?? throw new UnauthorizedAccessException("TOTP enrollment is unavailable.");
        var now = clock.UtcNow;
        if (factor.State != MfaFactorState.Pending || factor.ExpiresAt <= now) throw new UnauthorizedAccessException("TOTP enrollment is unavailable.");
        var currentRequiredAssurance = await mfaAuthority.RequiredAssuranceAsync(actor,
            StepUpRiskPolicy.Describe(StepUpOperationClass.MfaFactorEnroll).RequiredAssurance, cancellationToken);
        try
        {
            await mfaAuthority.ValidatePendingAuthorizationAsync(actor, authority,
                factor.AuthorityRevisionAtStart, factor.PolicyRevisionAtStart, factor.AuthorizationAssertionId,
                factor.AuthorizationAssertionRevision, currentRequiredAssurance, "mfa:totp:enroll", "User",
                actor.UserId!.Value.ToString("D"), cancellationToken);
        }
        catch (MfaAuthorityStaleException)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw;
        }
        var counter = await VerifyTotpCodeAsync(factor, request.Code, now, cancellationToken);
        if (counter is null) throw new UnauthorizedAccessException("TOTP verification failed.");
        factor.State = MfaFactorState.Active;
        factor.LastAcceptedCounter = counter;
        factor.ConfirmedAt = now;
        factor.Revision++;
        var recoveryCodes = CreateRecoveryCodes(factor.Id, now, options.Value.NormalizedRecoveryCodeCount);
        await dbContext.MfaRecoveryCodes.AddRangeAsync(recoveryCodes.Entities, cancellationToken);
        await AddEventAsync(actor, factor.Id, MfaSecurityAction.TotpEnrolled, AuthenticationMethod.Totp, AssuranceLevel.Aal2,
            request.Purpose, Resource(request.ResourceType, request.ResourceId), true, "TotpEnrolled", cancellationToken);
        mfaAuthority.Advance(authority);
        var assertion = await assertionIssuer.IssueFactorAssertionAsync(AuthenticationMethod.Totp, request.Purpose, request.ResourceType, request.ResourceId, request.MaxUses, now, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new TotpEnrollmentConfirmResult(assertion, recoveryCodes.Plaintext);
    }

    public async Task<StepUpAssertionResult> VerifyTotpAsync(TotpVerificationRequest request, CancellationToken cancellationToken)
    {
        var actor = RequireInteractiveActor();
        ValidateBinding(request.Purpose, request.ResourceType, request.ResourceId, request.MaxUses);
        await using var transaction = await dbContext.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        _ = await mfaAuthority.LockCurrentAsync(actor, cancellationToken);
        await dbContext.AcquireTransactionLockAsync($"mfa-totp:{actor.TenantId:D}:{actor.UserId:D}", cancellationToken);
        var factor = await dbContext.TotpFactors.SingleOrDefaultAsync(x => x.TenantId == actor.TenantId && x.ActorUserId == actor.UserId && x.State == MfaFactorState.Active, cancellationToken)
            ?? throw new UnauthorizedAccessException("TOTP verification failed.");
        var now = clock.UtcNow;
        var counter = await VerifyTotpCodeAsync(factor, request.Code, now, cancellationToken);
        if (counter is null)
        {
            await AddEventAsync(actor, factor.Id, MfaSecurityAction.TotpReplayRejected, AuthenticationMethod.Totp, AssuranceLevel.Aal2,
                request.Purpose, Resource(request.ResourceType, request.ResourceId), false, "InvalidOrReplayedCode", cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw new UnauthorizedAccessException("TOTP verification failed.");
        }
        factor.LastAcceptedCounter = counter;
        factor.Revision++;
        await AddEventAsync(actor, factor.Id, MfaSecurityAction.TotpVerified, AuthenticationMethod.Totp, AssuranceLevel.Aal2,
            request.Purpose, Resource(request.ResourceType, request.ResourceId), true, "TotpVerified", cancellationToken);
        var assertion = await assertionIssuer.IssueFactorAssertionAsync(AuthenticationMethod.Totp, request.Purpose, request.ResourceType, request.ResourceId, request.MaxUses, now, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return assertion;
    }

    public async Task<StepUpAssertionResult> VerifyRecoveryCodeAsync(RecoveryCodeVerificationRequest request, CancellationToken cancellationToken)
    {
        var actor = RequireInteractiveActor();
        ValidateBinding(request.Purpose, request.ResourceType, request.ResourceId, request.MaxUses);
        var normalized = NormalizeRecoveryCode(request.RecoveryCode);
        await using var transaction = await dbContext.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var authority = await mfaAuthority.LockCurrentAsync(actor, cancellationToken);
        await dbContext.AcquireTransactionLockAsync($"mfa-recovery:{actor.TenantId:D}:{actor.UserId:D}", cancellationToken);
        var factor = await dbContext.TotpFactors.SingleOrDefaultAsync(x => x.TenantId == actor.TenantId && x.ActorUserId == actor.UserId && x.State == MfaFactorState.Active, cancellationToken)
            ?? throw new UnauthorizedAccessException("Recovery verification failed.");
        var codes = await dbContext.MfaRecoveryCodes.Where(x => x.TotpFactorId == factor.Id && x.UsedAt == null).ToArrayAsync(cancellationToken);
        MfaRecoveryCode? matched = null;
        foreach (var candidate in codes)
        {
            var hash = HashRecoveryCode(normalized, candidate.Salt);
            try
            {
                if (CryptographicOperations.FixedTimeEquals(hash, candidate.CodeHash)) matched = candidate;
            }
            finally { CryptographicOperations.ZeroMemory(hash); }
        }
        var now = clock.UtcNow;
        if (matched is null)
        {
            await AddEventAsync(actor, factor.Id, MfaSecurityAction.RecoveryCodeReplayRejected, AuthenticationMethod.RecoveryCode, AssuranceLevel.Aal2,
                request.Purpose, Resource(request.ResourceType, request.ResourceId), false, "InvalidOrUsedRecoveryCode", cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw new UnauthorizedAccessException("Recovery verification failed.");
        }
        matched.UsedAt = now;
        await AddEventAsync(actor, factor.Id, MfaSecurityAction.RecoveryCodeUsed, AuthenticationMethod.RecoveryCode, AssuranceLevel.Aal2,
            request.Purpose, Resource(request.ResourceType, request.ResourceId), true, "RecoveryCodeUsed", cancellationToken);
        mfaAuthority.Advance(authority);
        var assertion = await assertionIssuer.IssueFactorAssertionAsync(AuthenticationMethod.RecoveryCode, request.Purpose, request.ResourceType, request.ResourceId, request.MaxUses, now, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return assertion;
    }

    public async Task<RecoveryCodeRegenerationResult> RegenerateRecoveryCodesAsync(RecoveryCodeRegenerationRequest request, CancellationToken cancellationToken)
    {
        var actor = RequireInteractiveActor();
        await using var transaction = await dbContext.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var authority = await mfaAuthority.LockCurrentAsync(actor, cancellationToken);
        var requiredAssurance = await mfaAuthority.RequiredAssuranceAsync(actor, AssuranceLevel.Aal2, cancellationToken);
        var authorization = await stepUp.AuthorizeRequiredAssuranceAsync(requiredAssurance, "mfa:recovery-codes:regenerate", "TotpFactor", request.FactorId.ToString("D"), request.StepUp, cancellationToken);
        if (authorization.Outcome != StepUpRequirementOutcome.Allowed)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            EnsureAllowed(authorization);
        }
        await dbContext.AcquireTransactionLockAsync($"mfa-totp-factor:{request.FactorId:D}", cancellationToken);
        var factor = await dbContext.TotpFactors.SingleOrDefaultAsync(x => x.Id == request.FactorId && x.TenantId == actor.TenantId && x.ActorUserId == actor.UserId, cancellationToken)
            ?? throw new UnauthorizedAccessException("MFA factor is unavailable.");
        if (factor.State != MfaFactorState.Active) throw new UnauthorizedAccessException("MFA factor is unavailable.");
        var now = clock.UtcNow;
        var activeCodes = await dbContext.MfaRecoveryCodes.Where(x => x.TotpFactorId == factor.Id && x.UsedAt == null).ToArrayAsync(cancellationToken);
        foreach (var code in activeCodes) code.UsedAt = now;
        var replacements = CreateRecoveryCodes(factor.Id, now, options.Value.NormalizedRecoveryCodeCount);
        await dbContext.MfaRecoveryCodes.AddRangeAsync(replacements.Entities, cancellationToken);
        factor.Revision++;
        await AddEventAsync(actor, factor.Id, MfaSecurityAction.FactorReset, AuthenticationMethod.RecoveryCode, AssuranceLevel.Aal2,
            "mfa:recovery-codes:regenerate", factor.Id.ToString("D"), true, "RecoveryCodesRegenerated", cancellationToken);
        mfaAuthority.Advance(authority);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new RecoveryCodeRegenerationResult(factor.Id, replacements.Plaintext);
    }

    public async Task<WebAuthnCeremonyStartResult> StartWebAuthnRegistrationAsync(WebAuthnRegistrationStartRequest request, CancellationToken cancellationToken)
    {
        var actor = RequireInteractiveActor();
        await using var transaction = await dbContext.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var authority = await mfaAuthority.LockCurrentAsync(actor, cancellationToken);
        var enrollmentAssurance = await mfaAuthority.RequiredAssuranceAsync(actor,
            StepUpRiskPolicy.Describe(StepUpOperationClass.MfaFactorEnroll).RequiredAssurance, cancellationToken);
        var authorization = await stepUp.AuthorizeRequiredAssuranceAsync(enrollmentAssurance, "mfa:webauthn:register", "User", actor.UserId!.Value.ToString("D"), request.StepUp, cancellationToken);
        if (authorization.Outcome != StepUpRequirementOutcome.Allowed)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            EnsureAllowed(authorization);
        }
        var credentials = await dbContext.WebAuthnCredentials.AsNoTracking()
            .Where(x => x.TenantId == actor.TenantId && x.ActorUserId == actor.UserId && x.State == MfaFactorState.Active)
            .Select(x => new { x.CredentialId, x.Transports }).ToArrayAsync(cancellationToken);
        var optionsJson = webAuthn.CreateRegistrationOptions(actor.Username, actor.UserId.Value.ToByteArray(), request.Kind,
            credentials.Select(x => (x.CredentialId, (IReadOnlyList<string>)ParseTransportList(x.Transports))).ToArray());
        var result = await CreateCeremonyAsync(actor, WebAuthnCeremonyKind.Registration, "mfa:webauthn:register", "User", actor.UserId.Value.ToString("D"), optionsJson,
            webAuthn.ReadRegistrationChallenge(optionsJson), authority, enrollmentAssurance, request.StepUp, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<WebAuthnRegistrationResult> CompleteWebAuthnRegistrationAsync(WebAuthnRegistrationCompleteRequest request, CancellationToken cancellationToken)
    {
        var actor = RequireInteractiveActor();
        await using var transaction = await dbContext.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var authority = await mfaAuthority.LockCurrentAsync(actor, cancellationToken);
        await dbContext.AcquireTransactionLockAsync($"webauthn-ceremony:{request.CeremonyId:D}", cancellationToken);
        var ceremony = await RequireCeremonyAsync(actor, request.CeremonyId, WebAuthnCeremonyKind.Registration, cancellationToken);
        var currentRequiredAssurance = await mfaAuthority.RequiredAssuranceAsync(actor,
            StepUpRiskPolicy.Describe(StepUpOperationClass.MfaFactorEnroll).RequiredAssurance, cancellationToken);
        try
        {
            if (ceremony.AuthorizationAssertionId is null || ceremony.AuthorizationAssertionRevision is null)
                throw new MfaAuthorityStaleException(currentRequiredAssurance, "MfaEnrollmentAuthorizationMissing");
            await mfaAuthority.ValidatePendingAuthorizationAsync(actor, authority,
                ceremony.AuthorityRevisionAtStart, ceremony.PolicyRevisionAtStart, ceremony.AuthorizationAssertionId.Value,
                ceremony.AuthorizationAssertionRevision.Value, currentRequiredAssurance, ceremony.Purpose,
                ceremony.ResourceType, ceremony.ResourceId, cancellationToken);
        }
        catch (MfaAuthorityStaleException)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw;
        }
        try
        {
            var verified = await webAuthn.VerifyRegistrationAsync(request.CredentialJson, ceremony.OptionsJson,
                async (credentialId, token) => !await dbContext.WebAuthnCredentials.AnyAsync(x => x.CredentialId == credentialId, token), cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(verified.UserHandle, actor.UserId!.Value.ToByteArray())) throw new UnauthorizedAccessException("WebAuthn user binding failed.");
            var transports = verified.Transports.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var kind = InferCredentialKind(verified.IsBackupEligible, transports);
            var now = clock.UtcNow;
            var credential = new WebAuthnCredential
            {
                TenantId = actor.TenantId!.Value,
                ActorUserId = actor.UserId.Value,
                CredentialId = verified.CredentialId,
                PublicKey = verified.PublicKey,
                UserHandle = verified.UserHandle,
                SignCount = verified.SignCount,
                Transports = string.Join(',', transports),
                Kind = kind,
                UserVerificationRequired = true,
                IsBackupEligible = verified.IsBackupEligible,
                IsBackedUp = verified.IsBackedUp,
                AaGuid = verified.AaGuid,
                CreatedAt = now,
                LastUsedAt = now
            };
            ceremony.State = WebAuthnCeremonyState.Used;
            ceremony.UsedAt = now;
            await dbContext.WebAuthnCredentials.AddAsync(credential, cancellationToken);
            await AddEventAsync(actor, credential.Id, MfaSecurityAction.WebAuthnRegistered, MethodFor(kind), AssuranceLevel.Aal3,
                ceremony.Purpose, Resource(ceremony.ResourceType, ceremony.ResourceId), true, "WebAuthnRegistered", cancellationToken);
            mfaAuthority.Advance(authority);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new WebAuthnRegistrationResult(credential.Id, credential.Kind, transports, credential.SignCount, credential.IsBackupEligible, credential.IsBackedUp);
        }
        catch
        {
            ceremony.State = WebAuthnCeremonyState.Failed;
            ceremony.UsedAt = clock.UtcNow;
            await AddEventAsync(actor, null, MfaSecurityAction.WebAuthnReplayRejected, null, AssuranceLevel.Aal3,
                ceremony.Purpose, Resource(ceremony.ResourceType, ceremony.ResourceId), false, "RegistrationVerificationFailed", cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw new UnauthorizedAccessException("WebAuthn registration failed.");
        }
    }

    public async Task<WebAuthnCeremonyStartResult> StartWebAuthnAuthenticationAsync(WebAuthnAuthenticationStartRequest request, CancellationToken cancellationToken)
    {
        var actor = RequireInteractiveActor();
        ValidateBinding(request.Purpose, request.ResourceType, request.ResourceId, 1);
        await using var transaction = await dbContext.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var authority = await mfaAuthority.LockCurrentAsync(actor, cancellationToken);
        var credentials = await dbContext.WebAuthnCredentials.AsNoTracking()
            .Where(x => x.TenantId == actor.TenantId && x.ActorUserId == actor.UserId && x.State == MfaFactorState.Active)
            .Select(x => new { x.CredentialId, x.Transports }).ToArrayAsync(cancellationToken);
        if (credentials.Length == 0) throw new UnauthorizedAccessException("WebAuthn authentication is unavailable.");
        var optionsJson = webAuthn.CreateAuthenticationOptions(credentials.Select(x => (x.CredentialId, (IReadOnlyList<string>)ParseTransportList(x.Transports))).ToArray());
        var result = await CreateCeremonyAsync(actor, WebAuthnCeremonyKind.Authentication, request.Purpose, request.ResourceType, request.ResourceId, optionsJson,
            webAuthn.ReadAuthenticationChallenge(optionsJson), authority, AssuranceLevel.Aal3, null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<StepUpAssertionResult> CompleteWebAuthnAuthenticationAsync(WebAuthnAuthenticationCompleteRequest request, CancellationToken cancellationToken)
    {
        var actor = RequireInteractiveActor();
        if (request.MaxUses is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(request.MaxUses));
        await using var transaction = await dbContext.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var authority = await mfaAuthority.LockCurrentAsync(actor, cancellationToken);
        await dbContext.AcquireTransactionLockAsync($"webauthn-ceremony:{request.CeremonyId:D}", cancellationToken);
        var ceremony = await RequireCeremonyAsync(actor, request.CeremonyId, WebAuthnCeremonyKind.Authentication, cancellationToken);
        if (ceremony.AuthorityRevisionAtStart != authority.State.Revision ||
            !string.Equals(ceremony.PolicyRevisionAtStart, authority.PolicyRevision, StringComparison.Ordinal))
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw new MfaAuthorityStaleException(AssuranceLevel.Aal3, "MfaAuthorityRevisionChanged");
        }
        try
        {
            var rawId = ReadRawId(request.CredentialJson);
            await dbContext.AcquireTransactionLockAsync($"webauthn-credential:{Convert.ToHexString(rawId)}", cancellationToken);
            var credential = await dbContext.WebAuthnCredentials.SingleOrDefaultAsync(x => x.CredentialId == rawId && x.TenantId == actor.TenantId && x.ActorUserId == actor.UserId, cancellationToken)
                ?? throw new UnauthorizedAccessException("WebAuthn credential is unavailable.");
            if (credential.State != MfaFactorState.Active || !credential.UserVerificationRequired) throw new UnauthorizedAccessException("WebAuthn credential is unavailable.");
            var verified = await webAuthn.VerifyAuthenticationAsync(request.CredentialJson, ceremony.OptionsJson, credential.PublicKey, credential.SignCount,
                async (userHandle, credentialId, token) => await dbContext.WebAuthnCredentials.AnyAsync(x => x.TenantId == actor.TenantId && x.ActorUserId == actor.UserId &&
                    x.State == MfaFactorState.Active && x.CredentialId == credentialId && x.UserHandle == userHandle, token), cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(verified.CredentialId, credential.CredentialId)) throw new UnauthorizedAccessException("WebAuthn credential binding failed.");
            if (credential.SignCount > 0 && verified.SignCount <= credential.SignCount) throw new UnauthorizedAccessException("WebAuthn signature counter did not advance.");
            var now = clock.UtcNow;
            credential.SignCount = Math.Max(credential.SignCount, verified.SignCount);
            credential.IsBackedUp = verified.IsBackedUp;
            credential.LastUsedAt = now;
            credential.Revision++;
            ceremony.State = WebAuthnCeremonyState.Used;
            ceremony.UsedAt = now;
            await AddEventAsync(actor, credential.Id, MfaSecurityAction.WebAuthnAuthenticated, MethodFor(credential.Kind), AssuranceLevel.Aal3,
                ceremony.Purpose, Resource(ceremony.ResourceType, ceremony.ResourceId), true, "WebAuthnAuthenticated", cancellationToken);
            var assertion = await assertionIssuer.IssueFactorAssertionAsync(MethodFor(credential.Kind), ceremony.Purpose, ceremony.ResourceType, ceremony.ResourceId, request.MaxUses, now, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return assertion;
        }
        catch
        {
            ceremony.State = WebAuthnCeremonyState.Failed;
            ceremony.UsedAt = clock.UtcNow;
            await AddEventAsync(actor, null, MfaSecurityAction.WebAuthnReplayRejected, null, AssuranceLevel.Aal3,
                ceremony.Purpose, Resource(ceremony.ResourceType, ceremony.ResourceId), false, "AuthenticationVerificationFailed", cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw new UnauthorizedAccessException("WebAuthn authentication failed.");
        }
    }

    public Task<FactorMutationResult> RemoveTotpFactorAsync(FactorRemovalRequest request, CancellationToken cancellationToken)
        => RemoveFactorAsync(request, AssuranceLevel.Aal2, true, cancellationToken);

    public Task<FactorMutationResult> RemoveWebAuthnCredentialAsync(FactorRemovalRequest request, CancellationToken cancellationToken)
        => RemoveFactorAsync(request, AssuranceLevel.Aal3, false, cancellationToken);

    private async Task<FactorMutationResult> RemoveFactorAsync(FactorRemovalRequest request, AssuranceLevel requiredAssurance, bool totp, CancellationToken cancellationToken)
    {
        var actor = RequireInteractiveActor();
        var purpose = totp ? "mfa:totp:remove" : "mfa:webauthn:remove";
        await using var transaction = await dbContext.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var authority = await mfaAuthority.LockCurrentAsync(actor, cancellationToken);
        requiredAssurance = await mfaAuthority.RequiredAssuranceAsync(actor, requiredAssurance, cancellationToken);
        var decision = await stepUp.AuthorizeRequiredAssuranceAsync(requiredAssurance, purpose, totp ? "TotpFactor" : "WebAuthnCredential", request.FactorId.ToString("D"), request.StepUp, cancellationToken);
        if (decision.Outcome != StepUpRequirementOutcome.Allowed)
        {
            var approved = !string.IsNullOrWhiteSpace(request.ExternalApprovalReference) &&
                await externalApproval.VerifyAsync(actor.UserId!.Value.ToString("D"), request.ExternalApprovalReference, purpose, cancellationToken);
            if (!approved)
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new FactorMutationResult(false, StepUpRequirementOutcome.RequiresExternalApproval, requiredAssurance, "ExternalRecoveryApprovalRequired");
            }
        }
        await dbContext.AcquireTransactionLockAsync($"mfa-factor:{request.FactorId:D}", cancellationToken);
        var now = clock.UtcNow;
        if (totp)
        {
            var factor = await dbContext.TotpFactors.SingleOrDefaultAsync(x => x.Id == request.FactorId && x.TenantId == actor.TenantId && x.ActorUserId == actor.UserId, cancellationToken)
                ?? throw new UnauthorizedAccessException("MFA factor is unavailable.");
            if (factor.State != MfaFactorState.Active) throw new UnauthorizedAccessException("MFA factor is unavailable.");
            factor.State = MfaFactorState.Removed;
            factor.RemovedAt = now;
            factor.Revision++;
        }
        else
        {
            var credential = await dbContext.WebAuthnCredentials.SingleOrDefaultAsync(x => x.Id == request.FactorId && x.TenantId == actor.TenantId && x.ActorUserId == actor.UserId, cancellationToken)
                ?? throw new UnauthorizedAccessException("MFA factor is unavailable.");
            if (credential.State != MfaFactorState.Active) throw new UnauthorizedAccessException("MFA factor is unavailable.");
            credential.State = MfaFactorState.Removed;
            credential.RemovedAt = now;
            credential.Revision++;
        }
        await AddEventAsync(actor, request.FactorId, decision.Outcome == StepUpRequirementOutcome.Allowed ? MfaSecurityAction.FactorRemoved : MfaSecurityAction.RecoveryApproved,
            null, requiredAssurance, purpose, request.FactorId.ToString("D"), true, RequireText(request.Reason, nameof(request.Reason), 500), cancellationToken);
        mfaAuthority.Advance(authority);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new FactorMutationResult(true, StepUpRequirementOutcome.Allowed, requiredAssurance, "FactorRemoved");
    }

    private async Task<WebAuthnCeremonyStartResult> CreateCeremonyAsync(ContextHubRequestActor actor, WebAuthnCeremonyKind kind, string purpose,
        string? resourceType, string? resourceId, string optionsJson, byte[] challenge, MfaAuthoritySnapshot authority,
        AssuranceLevel requiredAssurance, StepUpProof? authorizationProof, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var ceremony = new WebAuthnCeremony
        {
            TenantId = actor.TenantId!.Value,
            ActorUserId = actor.UserId!.Value,
            SessionHash = Hash(RequireSession(actor)),
            Kind = kind,
            Purpose = RequireText(purpose, nameof(purpose), 200),
            ResourceType = NormalizeOptional(resourceType),
            ResourceId = NormalizeOptional(resourceId),
            OptionsJson = optionsJson,
            ChallengeHash = Hash(challenge),
            AuthorityRevisionAtStart = authority.State.Revision,
            PolicyRevisionAtStart = authority.PolicyRevision,
            RequiredAssuranceAtStart = requiredAssurance,
            AuthorizationAssertionId = authorizationProof?.AssertionId,
            AuthorizationAssertionRevision = authorizationProof is null ? null : checked(authorizationProof.ExpectedRevision + 1),
            CreatedAt = now,
            ExpiresAt = now + options.Value.NormalizedWebAuthnChallengeTtl
        };
        ValidateResourceBinding(ceremony.ResourceType, ceremony.ResourceId);
        await dbContext.WebAuthnCeremonies.AddAsync(ceremony, cancellationToken);
        await AddEventAsync(actor, null, kind == WebAuthnCeremonyKind.Registration ? MfaSecurityAction.WebAuthnRegistrationStarted : MfaSecurityAction.WebAuthnAuthenticationStarted,
            null, AssuranceLevel.Aal3, ceremony.Purpose, Resource(resourceType, resourceId), true, "CeremonyStarted", cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new WebAuthnCeremonyStartResult(ceremony.Id, ceremony.OptionsJson, ceremony.ExpiresAt);
    }

    private async Task<WebAuthnCeremony> RequireCeremonyAsync(ContextHubRequestActor actor, Guid ceremonyId, WebAuthnCeremonyKind expectedKind, CancellationToken cancellationToken)
    {
        var ceremony = await dbContext.WebAuthnCeremonies.SingleOrDefaultAsync(x => x.Id == ceremonyId && x.TenantId == actor.TenantId && x.ActorUserId == actor.UserId, cancellationToken)
            ?? throw new UnauthorizedAccessException("WebAuthn ceremony is unavailable.");
        if (ceremony.Kind != expectedKind || ceremony.State != WebAuthnCeremonyState.Pending || ceremony.ExpiresAt <= clock.UtcNow ||
            !FixedEquals(ceremony.SessionHash, Hash(RequireSession(actor))))
            throw new UnauthorizedAccessException("WebAuthn ceremony is unavailable.");
        var challenge = expectedKind == WebAuthnCeremonyKind.Registration ? webAuthn.ReadRegistrationChallenge(ceremony.OptionsJson) : webAuthn.ReadAuthenticationChallenge(ceremony.OptionsJson);
        if (!FixedEquals(ceremony.ChallengeHash, Hash(challenge))) throw new UnauthorizedAccessException("WebAuthn ceremony binding failed.");
        return ceremony;
    }

    private async Task<long?> VerifyTotpCodeAsync(TotpFactor factor, string code, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var normalized = NormalizeTotpCode(code);
        var version = await dbContext.SecretVersions.AsNoTracking().SingleAsync(x => x.Id == factor.SeedSecretVersionId && x.SecretId == factor.SeedSecretId && x.State == SecretVersionState.Active, cancellationToken);
        var seed = Decrypt(version, factor.SeedSecretId);
        try
        {
            var currentCounter = now.ToUnixTimeSeconds() / options.Value.NormalizedTotpPeriodSeconds;
            long? matched = null;
            for (var offset = -options.Value.NormalizedTotpAllowedDriftSteps; offset <= options.Value.NormalizedTotpAllowedDriftSteps; offset++)
            {
                var counter = currentCounter + offset;
                if (counter < 0 || counter <= factor.LastAcceptedCounter) continue;
                if (FixedEquals(ComputeTotp(seed, counter), normalized)) matched = counter;
            }
            return matched;
        }
        finally { CryptographicOperations.ZeroMemory(seed); }
    }

    private (Secret Secret, SecretVersion Version) CreateEncryptedTotpSecret(ContextHubRequestActor actor, ReadOnlySpan<byte> seed, DateTimeOffset now)
    {
        var secret = new Secret
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = "ContextHub",
            Name = $"MFA TOTP {Guid.NewGuid():N}",
            NormalizedName = $"mfa-totp-{Guid.NewGuid():N}",
            Kind = SecretKind.TotpSeed,
            CreatedAt = now,
            UpdatedAt = now
        };
        var versionId = Guid.NewGuid();
        var dek = RandomNumberGenerator.GetBytes(KeyBytes);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[seed.Length];
        var tag = new byte[TagBytes];
        try
        {
            using var aes = new AesGcm(dek, TagBytes);
            aes.Encrypt(nonce, seed, ciphertext, tag, Aad(secret.Id, versionId));
            var wrapped = keyAuthority.Wrap(secret.Id, versionId, dek);
            return (secret, new SecretVersion
            {
                Id = versionId,
                SecretId = secret.Id,
                VersionNumber = 1,
                KeyId = wrapped.KeyId,
                WrappedDek = wrapped.Ciphertext,
                WrapNonce = wrapped.Nonce,
                WrapTag = wrapped.Tag,
                Ciphertext = ciphertext,
                CiphertextNonce = nonce,
                CiphertextTag = tag,
                CiphertextSha256 = Convert.ToHexString(SHA256.HashData(ciphertext)).ToLowerInvariant(),
                PlaintextLength = seed.Length,
                CreatedAt = now
            });
        }
        finally { CryptographicOperations.ZeroMemory(dek); }
    }

    private byte[] Decrypt(SecretVersion version, Guid secretId)
    {
        var dek = keyAuthority.Unwrap(secretId, version.Id, new WrappedSecretKey(version.KeyId, version.WrappedDek, version.WrapNonce, version.WrapTag));
        var plaintext = new byte[version.PlaintextLength];
        try
        {
            using var aes = new AesGcm(dek, TagBytes);
            aes.Decrypt(version.CiphertextNonce, version.Ciphertext, version.CiphertextTag, plaintext, Aad(secretId, version.Id));
            return plaintext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new CryptographicException("Factor material could not be authenticated.");
        }
        finally { CryptographicOperations.ZeroMemory(dek); }
    }

    private static (IReadOnlyList<MfaRecoveryCode> Entities, IReadOnlyList<string> Plaintext) CreateRecoveryCodes(Guid factorId, DateTimeOffset now, int count)
    {
        var entities = new List<MfaRecoveryCode>();
        var plaintext = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var bytes = RandomNumberGenerator.GetBytes(10);
            try
            {
                var code = Convert.ToHexString(bytes).ToLowerInvariant();
                var salt = RandomNumberGenerator.GetBytes(16);
                entities.Add(new MfaRecoveryCode { TotpFactorId = factorId, Salt = salt, CodeHash = HashRecoveryCode(code, salt), CreatedAt = now });
                plaintext.Add($"{code[..5]}-{code[5..10]}-{code[10..15]}-{code[15..]}");
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        return (entities, plaintext);
    }

    private static byte[] HashRecoveryCode(string normalizedCode, byte[] salt)
        => Rfc2898DeriveBytes.Pbkdf2(normalizedCode, salt, RecoveryIterations, HashAlgorithmName.SHA256, 32);

    private static string ComputeTotp(byte[] seed, long counter)
    {
        Span<byte> counterBytes = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counterBytes, counter);
        using var hmac = new HMACSHA1(seed);
        var digest = hmac.ComputeHash(counterBytes.ToArray());
        try
        {
            var offset = digest[^1] & 0x0f;
            var binary = ((digest[offset] & 0x7f) << 24) | (digest[offset + 1] << 16) | (digest[offset + 2] << 8) | digest[offset + 3];
            return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        }
        finally { CryptographicOperations.ZeroMemory(digest); }
    }

    private static byte[] ReadRawId(string credentialJson)
    {
        using var document = JsonDocument.Parse(credentialJson);
        if (!document.RootElement.TryGetProperty("rawId", out var rawId) || rawId.ValueKind != JsonValueKind.String)
            throw new UnauthorizedAccessException("WebAuthn credential identifier is missing.");
        var value = rawId.GetString() ?? string.Empty;
        return Base64UrlDecode(value);
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };
        try { return Convert.FromBase64String(padded); }
        catch (FormatException) { throw new UnauthorizedAccessException("WebAuthn credential identifier is invalid."); }
    }

    private static WebAuthnCredentialKind InferCredentialKind(bool backupEligible, IReadOnlyList<string> transports)
        => backupEligible ? WebAuthnCredentialKind.Passkey
            : transports.Any(x => x.Equals("Usb", StringComparison.OrdinalIgnoreCase) || x.Equals("Nfc", StringComparison.OrdinalIgnoreCase) || x.Equals("Ble", StringComparison.OrdinalIgnoreCase))
                ? WebAuthnCredentialKind.RoamingSecurityKey : WebAuthnCredentialKind.Platform;

    private static AuthenticationMethod MethodFor(WebAuthnCredentialKind kind) => kind switch
    {
        WebAuthnCredentialKind.Platform => AuthenticationMethod.WebAuthnPlatform,
        WebAuthnCredentialKind.RoamingSecurityKey => AuthenticationMethod.WebAuthnSecurityKey,
        WebAuthnCredentialKind.Passkey => AuthenticationMethod.Passkey,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    private static IReadOnlyList<string> ParseTransportList(string value)
        => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private async Task AddEventAsync(ContextHubRequestActor actor, Guid? factorId, MfaSecurityAction action, AuthenticationMethod? method,
        AssuranceLevel? assurance, string purpose, string? resource, bool succeeded, string reason, CancellationToken cancellationToken)
        => await dbContext.MfaSecurityEvents.AddAsync(new MfaSecurityEvent
        {
            TenantId = actor.TenantId!.Value,
            ActorUserId = actor.UserId!.Value,
            FactorId = factorId,
            Action = action,
            AuthenticationMethod = method,
            AssuranceLevel = assurance,
            Purpose = RequireText(purpose, nameof(purpose), 200),
            ResourceHash = Hash(resource ?? string.Empty),
            ReasonCode = RequireText(reason, nameof(reason), 500),
            Succeeded = succeeded,
            CreatedAt = clock.UtcNow
        }, cancellationToken);

    private ContextHubRequestActor RequireInteractiveActor()
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureAuthenticatedUser(actor);
        if (!actor.IsInteractiveUser || actor.TenantId is null || actor.UserId is null) throw new UnauthorizedAccessException("Human MFA requires an authorized interactive session.");
        _ = RequireSession(actor);
        return actor;
    }

    private static void EnsureAllowed(StepUpAuthorizationResult decision)
    {
        if (decision.Outcome != StepUpRequirementOutcome.Allowed) throw new StepUpRequiredException(decision);
    }

    private static void ValidateBinding(string purpose, string? resourceType, string? resourceId, int maxUses)
    {
        _ = RequireText(purpose, nameof(purpose), 200);
        ValidateResourceBinding(resourceType, resourceId);
        if (maxUses is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(maxUses));
    }

    private static void ValidateResourceBinding(string? resourceType, string? resourceId)
    {
        if (string.IsNullOrWhiteSpace(resourceType) != string.IsNullOrWhiteSpace(resourceId)) throw new ArgumentException("ResourceType and ResourceId must be supplied together.");
    }

    private static string Resource(string? type, string? id) => type is null ? string.Empty : $"{type}:{id}";
    private static string RequireSession(ContextHubRequestActor actor) => string.IsNullOrWhiteSpace(actor.AuthenticationSessionId) ? throw new UnauthorizedAccessException("The authenticated session cannot perform MFA.") : actor.AuthenticationSessionId.Trim();
    private static string NormalizeIssuer(string value) => RequireText(value, nameof(value), 200);
    private static string NormalizeTotpCode(string code) => code?.Trim() is { Length: 6 } value && value.All(char.IsAsciiDigit) ? value : throw new UnauthorizedAccessException("TOTP verification failed.");
    private static string NormalizeRecoveryCode(string code)
    {
        var normalized = new string((code ?? string.Empty).Where(char.IsAsciiHexDigit).Select(char.ToLowerInvariant).ToArray());
        return normalized.Length == 20 ? normalized : throw new UnauthorizedAccessException("Recovery verification failed.");
    }
    private static string RequireText(string value, string name, int max)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0 || normalized.Length > max) throw new ArgumentException($"{name} is required and must not exceed {max} characters.", name);
        return normalized;
    }
    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static byte[] Aad(Guid secretId, Guid versionId) => Encoding.UTF8.GetBytes($"contexthub-secret-v1|{secretId:D}|{versionId:D}");
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    private static bool FixedEquals(string left, string right) => CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));
    private static string Base32Encode(ReadOnlySpan<byte> data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new StringBuilder((data.Length * 8 + 4) / 5);
        var buffer = 0;
        var bitsLeft = 0;
        foreach (var value in data)
        {
            buffer = (buffer << 8) | value;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                output.Append(alphabet[(buffer >> (bitsLeft - 5)) & 31]);
                bitsLeft -= 5;
            }
        }
        if (bitsLeft > 0) output.Append(alphabet[(buffer << (5 - bitsLeft)) & 31]);
        return output.ToString();
    }
}
