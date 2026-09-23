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

    public TimeSpan NormalizedAssertionTtl => TimeSpan.FromMinutes(Math.Clamp(AssertionTtlMinutes, 1, 15));
    public TimeSpan NormalizedFailureWindow => TimeSpan.FromMinutes(Math.Clamp(FailureWindowMinutes, 1, 60));
    public int NormalizedMaxFailures => Math.Clamp(MaxFailuresPerWindow, 3, 20);
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
}

public sealed class StepUpAuthenticationService(
    IApplicationDbContext dbContext,
    IRequestActorAccessor actorAccessor,
    IPasswordCredentialVerifier passwordVerifier,
    IClock clock,
    IOptions<StepUpAuthenticationOptions> options) : IStepUpAuthenticationService
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

        await using var transaction = await dbContext.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
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
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureAuthenticatedUser(actor);
        var requirement = StepUpRiskPolicy.Describe(operation);
        if (requirement.Outcome is StepUpRequirementOutcome.Disabled or StepUpRequirementOutcome.RequiresExternalApproval)
        {
            return requirement;
        }

        if (!actor.IsInteractiveUser || proof is null)
        {
            return requirement with { Outcome = StepUpRequirementOutcome.RequiresStepUp, ReasonCode = "HumanStepUpRequired" };
        }

        var normalizedPurpose = RequireText(purpose, nameof(purpose), 200);
        ValidateResourceBinding(resourceType, resourceId);
        await using var transaction = await dbContext.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
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
            assertion.AssuranceLevel < requirement.RequiredAssurance)
        {
            return requirement with { Outcome = StepUpRequirementOutcome.RequiresStepUp, ReasonCode = "StepUpAssertionInvalid" };
        }

        assertion.UsedCount++;
        assertion.Revision++;
        if (assertion.UsedCount >= assertion.MaxUses) assertion.State = StepUpAssertionState.Exhausted;
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new StepUpAuthorizationResult(StepUpRequirementOutcome.Allowed, requirement.RequiredAssurance, "StepUpSatisfied", assertion.ExpiresAt);
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

            StepUpOperationClass.CredentialRotate or
            StepUpOperationClass.CredentialRevoke or
            StepUpOperationClass.SecretReveal or
            StepUpOperationClass.RestrictedRelease or
            StepUpOperationClass.SensitiveAuthorizationChange
                => new(StepUpRequirementOutcome.RequiresExternalApproval, AssuranceLevel.Aal2, "Aal2Unavailable"),

            StepUpOperationClass.SecretExport or
            StepUpOperationClass.BreakGlassReveal or
            StepUpOperationClass.KekDestructiveOperation or
            StepUpOperationClass.SshCaDestructiveOperation or
            StepUpOperationClass.SecurityBoundaryDisable
                => new(StepUpRequirementOutcome.Disabled, AssuranceLevel.Aal3, "Aal3Unavailable"),

            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };
}
