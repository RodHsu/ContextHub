using System.Data;
using System.Security.Cryptography;
using System.Text;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Memory.Application;

public sealed class StepUpAuthenticationOptions
{
    public const string SectionName = "StepUpAuthentication";
    public int AssertionTtlMinutes { get; set; } = 5;
    public int FailureWindowMinutes { get; set; } = 15;
    public int MaxFailuresPerWindow { get; set; } = 5;
    public string TotpIssuer { get; set; } = "ContextHub";
    public int TotpPeriodSeconds { get; set; } = 30;
    public int TotpAllowedDriftSteps { get; set; } = 1;
    public int TotpEnrollmentMinutes { get; set; } = 10;
    public int RecoveryCodeCount { get; set; } = 10;
    public int WebAuthnChallengeMinutes { get; set; } = 5;
    public string MfaPolicyRevision { get; set; } = "wave4b-v1";
    public string WebAuthnRpId { get; set; } = string.Empty;
    public string WebAuthnRpName { get; set; } = "ContextHub";
    public string[] WebAuthnOrigins { get; set; } = [];

    public TimeSpan NormalizedAssertionTtl => TimeSpan.FromMinutes(Math.Clamp(AssertionTtlMinutes, 1, 15));
    public TimeSpan NormalizedFailureWindow => TimeSpan.FromMinutes(Math.Clamp(FailureWindowMinutes, 1, 60));
    public int NormalizedMaxFailures => Math.Clamp(MaxFailuresPerWindow, 3, 20);
    public int NormalizedTotpPeriodSeconds => Math.Clamp(TotpPeriodSeconds, 30, 60);
    public int NormalizedTotpAllowedDriftSteps => Math.Clamp(TotpAllowedDriftSteps, 0, 1);
    public TimeSpan NormalizedTotpEnrollmentTtl => TimeSpan.FromMinutes(Math.Clamp(TotpEnrollmentMinutes, 5, 10));
    public int NormalizedRecoveryCodeCount => Math.Clamp(RecoveryCodeCount, 8, 12);
    public TimeSpan NormalizedWebAuthnChallengeTtl => TimeSpan.FromMinutes(Math.Clamp(WebAuthnChallengeMinutes, 2, 10));
}

public sealed record PasswordStepUpRequest(
    string Password,
    string Purpose,
    string? ResourceType = null,
    string? ResourceId = null,
    int MaxUses = 1);

public sealed record StepUpAssertionResult(
    Guid AssertionId,
    string Nonce,
    AuthenticationMethod AuthenticationMethod,
    AssuranceLevel AssuranceLevel,
    string Purpose,
    string? ResourceType,
    string? ResourceId,
    int MaxUses,
    DateTimeOffset AuthTime,
    DateTimeOffset ExpiresAt,
    long Revision);

public sealed record StepUpProof(Guid AssertionId, string Nonce, long ExpectedRevision);

public sealed record StepUpAuthorizationResult(
    StepUpRequirementOutcome Outcome,
    AssuranceLevel RequiredAssurance,
    string ReasonCode,
    DateTimeOffset? AssertionExpiresAt = null);

public interface IPasswordCredentialVerifier
{
    bool Verify(string passwordHash, string password);
}

public interface IStepUpAuthenticationService
{
    Task<StepUpAssertionResult> CreatePasswordAssertionAsync(PasswordStepUpRequest request, CancellationToken cancellationToken);
    Task<StepUpAuthorizationResult> AuthorizeAsync(
        StepUpOperationClass operation,
        string purpose,
        string? resourceType,
        string? resourceId,
        StepUpProof? proof,
        CancellationToken cancellationToken);
    Task<StepUpAuthorizationResult> AuthorizeRequiredAssuranceAsync(
        AssuranceLevel requiredAssurance,
        string purpose,
        string? resourceType,
        string? resourceId,
        StepUpProof? proof,
        CancellationToken cancellationToken);
}

public interface IStepUpAssertionIssuer
{
    Task<StepUpAssertionResult> IssueFactorAssertionAsync(
        AuthenticationMethod authenticationMethod,
        string purpose,
        string? resourceType,
        string? resourceId,
        int maxUses,
        DateTimeOffset authTime,
        CancellationToken cancellationToken);
}

public sealed record MfaAuthoritySnapshot(MfaAuthorityState State, string PolicyRevision);

public sealed class MfaAuthorityStaleException(AssuranceLevel requiredAssurance, string reasonCode)
    : UnauthorizedAccessException("The MFA authority changed. Restart the operation and complete current step-up authentication.")
{
    public AssuranceLevel RequiredAssurance { get; } = requiredAssurance;
    public string ReasonCode { get; } = reasonCode;
}

public interface IMfaAuthorityCoordinator
{
    Task<MfaAuthoritySnapshot> LockCurrentAsync(ContextHubRequestActor actor, CancellationToken cancellationToken);
    Task<AssuranceLevel> RequiredAssuranceAsync(ContextHubRequestActor actor, AssuranceLevel policyFloor, CancellationToken cancellationToken);
    Task ValidatePendingAuthorizationAsync(
        ContextHubRequestActor actor,
        MfaAuthoritySnapshot current,
        long authorityRevisionAtStart,
        string policyRevisionAtStart,
        Guid assertionId,
        long assertionRevision,
        AssuranceLevel requiredAssurance,
        string purpose,
        string? resourceType,
        string? resourceId,
        CancellationToken cancellationToken);
    void Advance(MfaAuthoritySnapshot current);
}

public sealed class MfaAuthorityCoordinator(
    IApplicationDbContext dbContext,
    IClock clock,
    IOptions<StepUpAuthenticationOptions> options) : IMfaAuthorityCoordinator
{
    public async Task<MfaAuthoritySnapshot> LockCurrentAsync(ContextHubRequestActor actor, CancellationToken cancellationToken)
    {
        if (actor.TenantId is null || actor.UserId is null) throw new UnauthorizedAccessException("MFA authority requires an authenticated user.");
        await dbContext.AcquireTransactionLockAsync(AuthorityLockKey(actor.TenantId.Value, actor.UserId.Value), cancellationToken);
        var policyRevision = NormalizePolicyRevision(options.Value.MfaPolicyRevision);
        var state = await dbContext.MfaAuthorityStates.SingleOrDefaultAsync(
            x => x.TenantId == actor.TenantId && x.ActorUserId == actor.UserId,
            cancellationToken);
        if (state is null)
        {
            state = new MfaAuthorityState
            {
                TenantId = actor.TenantId.Value,
                ActorUserId = actor.UserId.Value,
                PolicyRevision = policyRevision,
                UpdatedAt = clock.UtcNow
            };
            await dbContext.MfaAuthorityStates.AddAsync(state, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        else if (!string.Equals(state.PolicyRevision, policyRevision, StringComparison.Ordinal))
        {
            state.PolicyRevision = policyRevision;
            state.Revision = checked(state.Revision + 1);
            state.UpdatedAt = clock.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return new MfaAuthoritySnapshot(state, policyRevision);
    }

    public async Task<AssuranceLevel> RequiredAssuranceAsync(
        ContextHubRequestActor actor,
        AssuranceLevel policyFloor,
        CancellationToken cancellationToken)
    {
        var strongest = AssuranceLevel.Aal0;
        if (await dbContext.WebAuthnCredentials.AnyAsync(
                x => x.TenantId == actor.TenantId && x.ActorUserId == actor.UserId && x.State == MfaFactorState.Active,
                cancellationToken))
            strongest = AssuranceLevel.Aal3;
        else if (await dbContext.TotpFactors.AnyAsync(
                     x => x.TenantId == actor.TenantId && x.ActorUserId == actor.UserId && x.State == MfaFactorState.Active,
                     cancellationToken))
            strongest = AssuranceLevel.Aal2;
        return strongest > policyFloor ? strongest : policyFloor;
    }

    public async Task ValidatePendingAuthorizationAsync(
        ContextHubRequestActor actor,
        MfaAuthoritySnapshot current,
        long authorityRevisionAtStart,
        string policyRevisionAtStart,
        Guid assertionId,
        long assertionRevision,
        AssuranceLevel requiredAssurance,
        string purpose,
        string? resourceType,
        string? resourceId,
        CancellationToken cancellationToken)
    {
        if (authorityRevisionAtStart != current.State.Revision ||
            !string.Equals(policyRevisionAtStart, current.PolicyRevision, StringComparison.Ordinal))
            throw new MfaAuthorityStaleException(requiredAssurance, "MfaAuthorityRevisionChanged");

        var assertion = await dbContext.StepUpAssertions.SingleOrDefaultAsync(x => x.Id == assertionId, cancellationToken);
        var now = clock.UtcNow;
        if (assertion is null || assertion.TenantId != actor.TenantId || assertion.ActorUserId != actor.UserId ||
            assertion.Revision != assertionRevision || assertion.UsedCount <= 0 || assertion.RevokedAt is not null ||
            assertion.State is not (StepUpAssertionState.Active or StepUpAssertionState.Exhausted) || assertion.ExpiresAt <= now ||
            assertion.MfaAuthorityRevision != current.State.Revision ||
            !string.Equals(assertion.MfaPolicyRevision, current.PolicyRevision, StringComparison.Ordinal) ||
            assertion.AssuranceLevel < requiredAssurance ||
            !FixedEquals(assertion.SessionHash, Hash(RequireAuthenticationSession(actor))) ||
            !string.Equals(assertion.Purpose, NormalizeRequired(purpose), StringComparison.Ordinal) ||
            !string.Equals(assertion.ResourceType, NormalizeOptional(resourceType), StringComparison.Ordinal) ||
            !string.Equals(assertion.ResourceId, NormalizeOptional(resourceId), StringComparison.Ordinal))
            throw new MfaAuthorityStaleException(requiredAssurance, "MfaEnrollmentAuthorizationStale");
    }

    public void Advance(MfaAuthoritySnapshot current)
    {
        current.State.Revision = checked(current.State.Revision + 1);
        current.State.UpdatedAt = clock.UtcNow;
    }

    private static string AuthorityLockKey(Guid tenantId, Guid userId) => $"mfa-authority:{tenantId:D}:{userId:D}";
    private static string NormalizePolicyRevision(string value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is 0 or > 200) throw new InvalidOperationException("MFA policy revision must contain 1 to 200 characters.");
        return normalized;
    }
    private static string NormalizeRequired(string value) => value?.Trim() ?? string.Empty;
    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string RequireAuthenticationSession(ContextHubRequestActor actor)
    {
        var sessionId = actor.AuthenticationSessionId?.Trim() ?? string.Empty;
        if (sessionId.Length is 0 or > 512) throw new UnauthorizedAccessException("Human MFA requires an authenticated session.");
        return sessionId;
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static bool FixedEquals(string left, string right)
        => CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));
}

public sealed class StepUpAuthenticationService(
    IApplicationDbContext dbContext,
    IRequestActorAccessor actorAccessor,
    IPasswordCredentialVerifier passwordVerifier,
    IClock clock,
    IOptions<StepUpAuthenticationOptions> options,
    IMfaAuthorityCoordinator mfaAuthority) : IStepUpAuthenticationService, IStepUpAssertionIssuer
{
    public async Task<StepUpAssertionResult> CreatePasswordAssertionAsync(PasswordStepUpRequest request, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureAuthenticatedUser(actor);
        if (!actor.IsInteractiveUser)
        {
            throw new UnauthorizedAccessException("Human step-up is available only through an authorized interactive session.");
        }

        var sessionId = RequireAuthenticationSession(actor);
        var purpose = RequireText(request.Purpose, nameof(request.Purpose), 200);
        ValidateResourceBinding(request.ResourceType, request.ResourceId);
        if (request.MaxUses is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(request.MaxUses));
        if (string.IsNullOrEmpty(request.Password)) throw new UnauthorizedAccessException("Password verification failed.");

        await using var transaction = await dbContext.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var authority = await mfaAuthority.LockCurrentAsync(actor, cancellationToken);
        await dbContext.AcquireTransactionLockAsync($"step-up:{actor.TenantId:D}:{actor.UserId:D}", cancellationToken);
        var now = clock.UtcNow;
        var failureWindow = now - options.Value.NormalizedFailureWindow;
        var failedAttempts = await dbContext.StepUpAuthenticationAttempts
            .CountAsync(x => x.TenantId == actor.TenantId && x.ActorUserId == actor.UserId && !x.Succeeded && x.CreatedAt >= failureWindow, cancellationToken);
        if (failedAttempts >= options.Value.NormalizedMaxFailures)
        {
            await RecordAttemptAsync(actor, sessionId, false, "RateLimited", cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw new UnauthorizedAccessException("Password verification is temporarily locked.");
        }

        var user = await dbContext.TenantUsers.SingleOrDefaultAsync(
            x => x.Id == actor.UserId && x.TenantId == actor.TenantId && x.Status == TenantUserStatus.Active,
            cancellationToken);
        var verified = user is not null &&
            !string.IsNullOrWhiteSpace(user.PasswordHash) &&
            passwordVerifier.Verify(user.PasswordHash, request.Password);
        await RecordAttemptAsync(actor, sessionId, verified, verified ? "Verified" : "InvalidCredential", cancellationToken);
        if (!verified)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw new UnauthorizedAccessException("Password verification failed.");
        }

        var nonceBytes = RandomNumberGenerator.GetBytes(32);
        var nonce = Convert.ToBase64String(nonceBytes);
        CryptographicOperations.ZeroMemory(nonceBytes);
        var assertion = new StepUpAssertion
        {
            TenantId = actor.TenantId!.Value,
            ActorUserId = actor.UserId!.Value,
            SessionHash = Hash(sessionId),
            Purpose = purpose,
            ResourceType = NormalizeOptional(request.ResourceType),
            ResourceId = NormalizeOptional(request.ResourceId),
            NonceHash = Hash(nonce),
            MfaAuthorityRevision = authority.State.Revision,
            MfaPolicyRevision = authority.PolicyRevision,
            MaxUses = request.MaxUses,
            AuthTime = now,
            IssuedAt = now,
            ExpiresAt = now + options.Value.NormalizedAssertionTtl
        };
        await dbContext.StepUpAssertions.AddAsync(assertion, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new StepUpAssertionResult(assertion.Id, nonce, assertion.AuthenticationMethod, assertion.AssuranceLevel, assertion.Purpose,
            assertion.ResourceType, assertion.ResourceId, assertion.MaxUses, assertion.AuthTime, assertion.ExpiresAt, assertion.Revision);
    }

    public async Task<StepUpAuthorizationResult> AuthorizeAsync(
        StepUpOperationClass operation,
        string purpose,
        string? resourceType,
        string? resourceId,
        StepUpProof? proof,
        CancellationToken cancellationToken)
    {
        var requirement = StepUpRiskPolicy.Describe(operation);
        return await AuthorizeRequirementAsync(requirement, purpose, resourceType, resourceId, proof, cancellationToken);
    }

    public Task<StepUpAuthorizationResult> AuthorizeRequiredAssuranceAsync(
        AssuranceLevel requiredAssurance,
        string purpose,
        string? resourceType,
        string? resourceId,
        StepUpProof? proof,
        CancellationToken cancellationToken)
    {
        if (requiredAssurance is AssuranceLevel.Aal0) return Task.FromResult(new StepUpAuthorizationResult(StepUpRequirementOutcome.Allowed, requiredAssurance, "SessionSatisfied"));
        return AuthorizeRequirementAsync(new StepUpAuthorizationResult(StepUpRequirementOutcome.RequiresStepUp, requiredAssurance, $"{requiredAssurance}Required"),
            purpose, resourceType, resourceId, proof, cancellationToken);
    }

    public async Task<StepUpAssertionResult> IssueFactorAssertionAsync(
        AuthenticationMethod authenticationMethod,
        string purpose,
        string? resourceType,
        string? resourceId,
        int maxUses,
        DateTimeOffset authTime,
        CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureAuthenticatedUser(actor);
        if (!actor.IsInteractiveUser) throw new UnauthorizedAccessException("Human step-up is available only through an authorized interactive session.");
        var assurance = authenticationMethod switch
        {
            AuthenticationMethod.Totp or AuthenticationMethod.RecoveryCode => AssuranceLevel.Aal2,
            AuthenticationMethod.WebAuthnPlatform or AuthenticationMethod.WebAuthnSecurityKey or AuthenticationMethod.Passkey => AssuranceLevel.Aal3,
            _ => throw new ArgumentOutOfRangeException(nameof(authenticationMethod), authenticationMethod, "Only verified MFA factors can use this issuer.")
        };
        if (maxUses is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(maxUses));
        var now = clock.UtcNow;
        if (authTime > now || authTime < now - TimeSpan.FromMinutes(2)) throw new UnauthorizedAccessException("Factor verification is no longer fresh.");
        var normalizedPurpose = RequireText(purpose, nameof(purpose), 200);
        ValidateResourceBinding(resourceType, resourceId);
        await using var transaction = await dbContext.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var authority = await mfaAuthority.LockCurrentAsync(actor, cancellationToken);
        var assertion = CreateAssertion(actor, RequireAuthenticationSession(actor), authenticationMethod, assurance,
            normalizedPurpose, resourceType, resourceId, maxUses, authTime, now, authority);
        await dbContext.StepUpAssertions.AddAsync(assertion.Entity, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return assertion.Result;
    }

    private async Task<StepUpAuthorizationResult> AuthorizeRequirementAsync(
        StepUpAuthorizationResult requirement,
        string purpose,
        string? resourceType,
        string? resourceId,
        StepUpProof? proof,
        CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureAuthenticatedUser(actor);
        if (requirement.Outcome is StepUpRequirementOutcome.Disabled or StepUpRequirementOutcome.RequiresExternalApproval) return requirement;

        if (!actor.IsInteractiveUser || proof is null)
        {
            return requirement with { Outcome = StepUpRequirementOutcome.RequiresStepUp, ReasonCode = "HumanStepUpRequired" };
        }

        var normalizedPurpose = RequireText(purpose, nameof(purpose), 200);
        ValidateResourceBinding(resourceType, resourceId);
        await using var transaction = await dbContext.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var authority = await mfaAuthority.LockCurrentAsync(actor, cancellationToken);
        await dbContext.AcquireTransactionLockAsync($"step-up-assertion:{proof.AssertionId:D}", cancellationToken);
        var assertion = await dbContext.StepUpAssertions.SingleOrDefaultAsync(x => x.Id == proof.AssertionId, cancellationToken);
        var now = clock.UtcNow;
        if (assertion is null || assertion.TenantId != actor.TenantId || assertion.ActorUserId != actor.UserId ||
            !FixedEquals(assertion.NonceHash, Hash(proof.Nonce)) || assertion.Revision != proof.ExpectedRevision ||
            assertion.State != StepUpAssertionState.Active || assertion.ExpiresAt <= now ||
            !FixedEquals(assertion.SessionHash, Hash(RequireAuthenticationSession(actor))) ||
            !string.Equals(assertion.Purpose, normalizedPurpose, StringComparison.Ordinal) ||
            !string.Equals(assertion.ResourceType, NormalizeOptional(resourceType), StringComparison.Ordinal) ||
            !string.Equals(assertion.ResourceId, NormalizeOptional(resourceId), StringComparison.Ordinal) ||
            assertion.MfaAuthorityRevision != authority.State.Revision ||
            !string.Equals(assertion.MfaPolicyRevision, authority.PolicyRevision, StringComparison.Ordinal) ||
            assertion.AssuranceLevel < requirement.RequiredAssurance)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return requirement with { Outcome = StepUpRequirementOutcome.RequiresStepUp, ReasonCode = "StepUpAssertionInvalid" };
        }

        assertion.UsedCount++;
        assertion.Revision++;
        if (assertion.UsedCount >= assertion.MaxUses) assertion.State = StepUpAssertionState.Exhausted;
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new StepUpAuthorizationResult(StepUpRequirementOutcome.Allowed, requirement.RequiredAssurance, "StepUpSatisfied", assertion.ExpiresAt);
    }

    private (StepUpAssertion Entity, StepUpAssertionResult Result) CreateAssertion(
        ContextHubRequestActor actor,
        string sessionId,
        AuthenticationMethod authenticationMethod,
        AssuranceLevel assuranceLevel,
        string purpose,
        string? resourceType,
        string? resourceId,
        int maxUses,
        DateTimeOffset authTime,
        DateTimeOffset now,
        MfaAuthoritySnapshot authority)
    {
        var nonceBytes = RandomNumberGenerator.GetBytes(32);
        var nonce = Convert.ToBase64String(nonceBytes);
        CryptographicOperations.ZeroMemory(nonceBytes);
        var assertion = new StepUpAssertion
        {
            TenantId = actor.TenantId!.Value,
            ActorUserId = actor.UserId!.Value,
            SessionHash = Hash(sessionId),
            AuthenticationMethod = authenticationMethod,
            AssuranceLevel = assuranceLevel,
            Purpose = purpose,
            ResourceType = NormalizeOptional(resourceType),
            ResourceId = NormalizeOptional(resourceId),
            NonceHash = Hash(nonce),
            MfaAuthorityRevision = authority.State.Revision,
            MfaPolicyRevision = authority.PolicyRevision,
            MaxUses = maxUses,
            AuthTime = authTime,
            IssuedAt = now,
            ExpiresAt = now + options.Value.NormalizedAssertionTtl
        };
        return (assertion, new StepUpAssertionResult(assertion.Id, nonce, authenticationMethod, assuranceLevel, assertion.Purpose,
            assertion.ResourceType, assertion.ResourceId, assertion.MaxUses, assertion.AuthTime, assertion.ExpiresAt, assertion.Revision));
    }

    private async Task RecordAttemptAsync(ContextHubRequestActor actor, string sessionId, bool succeeded, string reasonCode, CancellationToken cancellationToken)
        => await dbContext.StepUpAuthenticationAttempts.AddAsync(new StepUpAuthenticationAttempt
        {
            TenantId = actor.TenantId!.Value,
            ActorUserId = actor.UserId!.Value,
            SessionHash = Hash(sessionId),
            Succeeded = succeeded,
            ReasonCode = reasonCode,
            CreatedAt = clock.UtcNow
        }, cancellationToken);

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool FixedEquals(string left, string right)
        => CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));

    private static string RequireAuthenticationSession(ContextHubRequestActor actor)
    {
        var sessionId = actor.AuthenticationSessionId?.Trim() ?? string.Empty;
        if (sessionId.Length is 0 or > 512)
            throw new UnauthorizedAccessException("The authenticated session cannot perform password step-up.");
        return sessionId;
    }

    private static void ValidateResourceBinding(string? resourceType, string? resourceId)
    {
        if ((string.IsNullOrWhiteSpace(resourceType)) != (string.IsNullOrWhiteSpace(resourceId)))
            throw new ArgumentException("ResourceType and ResourceId must be supplied together.");
    }

    private static string RequireText(string value, string name, int maximumLength)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0 || normalized.Length > maximumLength) throw new ArgumentException($"{name} is required and must not exceed {maximumLength} characters.", name);
        return normalized;
    }

    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public static class StepUpRiskPolicy
{
    public static StepUpAuthorizationResult Describe(StepUpOperationClass operation)
        => operation switch
        {
            StepUpOperationClass.SecretCreate or
            StepUpOperationClass.SecretVersionCreate or
            StepUpOperationClass.SecretUseLeaseCreate or
            StepUpOperationClass.SshCertificateIssue
                => new(StepUpRequirementOutcome.RequiresStepUp, AssuranceLevel.Aal1, "FreshPasswordRequired"),

            StepUpOperationClass.MfaFactorEnroll
                => new(StepUpRequirementOutcome.RequiresStepUp, AssuranceLevel.Aal1, "FreshPasswordRequired"),

            StepUpOperationClass.CredentialRotate or
            StepUpOperationClass.CredentialRevoke or
            StepUpOperationClass.SecretReveal or
            StepUpOperationClass.RestrictedRelease or
            StepUpOperationClass.SensitiveAuthorizationChange
                => new(StepUpRequirementOutcome.RequiresStepUp, AssuranceLevel.Aal2, "Aal2Required"),

            StepUpOperationClass.MfaFactorRemove
                => new(StepUpRequirementOutcome.RequiresStepUp, AssuranceLevel.Aal2, "Aal2Required"),

            StepUpOperationClass.SecretExport or
            StepUpOperationClass.BreakGlassReveal or
            StepUpOperationClass.KekDestructiveOperation or
            StepUpOperationClass.SshCaDestructiveOperation or
            StepUpOperationClass.SecurityBoundaryDisable
                => new(StepUpRequirementOutcome.RequiresStepUp, AssuranceLevel.Aal3, "Aal3Required"),

            StepUpOperationClass.MfaRecovery
                => new(StepUpRequirementOutcome.RequiresExternalApproval, AssuranceLevel.Aal3, "ExternalRecoveryApprovalRequired"),

            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };
}
