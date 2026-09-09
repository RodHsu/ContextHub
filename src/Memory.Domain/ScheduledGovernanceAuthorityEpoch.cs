using System.Security.Cryptography;
using System.Text;

namespace Memory.Domain;

/// <summary>
/// The identity scope for a server-owned scheduled-governance authority
/// epoch chain. Epochs are never shared across tenants, owners, or runtime
/// environments.
/// </summary>
public sealed record ScheduledGovernanceAuthorityEpochScope(
    Guid TenantId,
    Guid OwnerUserId,
    string Environment);

/// <summary>
/// Server-side compare-and-advance request for the authority epoch ledger.
/// The expected predecessor is intentionally required for every advance after
/// the first row so two independent writers cannot silently fork a chain.
/// The digest fields are opaque, server-computed binding values. This internal
/// ledger enforces monotonicity and non-reuse only; it does not authenticate a
/// platform signature or establish cryptographic authority.
/// </summary>
public sealed record ScheduledGovernanceAuthorityEpochAdvanceRequest(
    Guid TenantId,
    Guid OwnerUserId,
    string Environment,
    string ConfigurationDigest,
    string AuthorityEpochDigest,
    string? ExpectedPreviousAuthorityEpochDigest = null,
    long? ExpectedPreviousGeneration = null);

/// <summary>
/// Outcome of a durable authority epoch operation. Only <see cref="Advanced"/>
/// and <see cref="ReplayCurrent"/> are current, accepted authority. A
/// historical replay is reported separately so an old digest cannot revive a
/// reliability streak merely because the request is byte-for-byte repeated.
/// </summary>
public enum ScheduledGovernanceAuthorityEpochAdvanceStatus
{
    Invalid,
    Advanced,
    ReplayCurrent,
    ReplayHistorical,
    Conflict,
    Unavailable
}

/// <summary>
/// Durable, append-only authority epoch row. The database constrains this
/// entity to one contiguous predecessor chain per tenant/owner/environment.
/// </summary>
public sealed class ScheduledGovernanceAuthorityEpoch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid OwnerUserId { get; set; }
    public string Environment { get; set; } = string.Empty;
    public string ConfigurationDigest { get; set; } = string.Empty;
    public string AuthorityEpochDigest { get; set; } = string.Empty;
    public long Generation { get; set; }
    public string? PreviousAuthorityEpochDigest { get; set; }
    public long? PreviousGeneration { get; set; }
    public string RequestHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}

/// <summary>
/// Result returned by the internal epoch ledger boundary. Callers must use
/// <see cref="IsCurrent"/> when binding a reliability projection; a non-null
/// row alone is not proof that the requested epoch is still current.
/// </summary>
public sealed record ScheduledGovernanceAuthorityEpochAdvanceResult(
    ScheduledGovernanceAuthorityEpochAdvanceStatus Status,
    ScheduledGovernanceAuthorityEpoch? Epoch,
    string Reason)
{
    public bool IsReplay => Status is
        ScheduledGovernanceAuthorityEpochAdvanceStatus.ReplayCurrent or
        ScheduledGovernanceAuthorityEpochAdvanceStatus.ReplayHistorical;

    public bool IsCurrent => Status is
        ScheduledGovernanceAuthorityEpochAdvanceStatus.Advanced or
        ScheduledGovernanceAuthorityEpochAdvanceStatus.ReplayCurrent;

    public bool Accepted => IsCurrent;
}

/// <summary>
/// Internal persistence boundary for the server-owned monotonic authority
/// epoch chain. It is deliberately not an HTTP or MCP contract.
/// </summary>
public interface IScheduledGovernanceAuthorityEpochLedger
{
    Task<ScheduledGovernanceAuthorityEpochAdvanceResult> AdvanceAsync(
        ScheduledGovernanceAuthorityEpochAdvanceRequest request,
        CancellationToken cancellationToken = default);

    Task<ScheduledGovernanceAuthorityEpoch?> GetCurrentAsync(
        ScheduledGovernanceAuthorityEpochScope scope,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Canonical, non-secret request identity used to make an exact replay
/// idempotent while retaining all tenant/owner/environment/configuration
/// bindings in the digest.
/// </summary>
public static class ScheduledGovernanceAuthorityEpochContract
{
    public const int DigestLength = 64;
    public const int MaximumEnvironmentLength = 64;

    public static string ComputeRequestHash(
        ScheduledGovernanceAuthorityEpochAdvanceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fields = new[]
        {
            "scheduled-governance-authority-epoch-advance-v1",
            request.TenantId.ToString("D"),
            request.OwnerUserId.ToString("D"),
            request.Environment,
            request.ConfigurationDigest,
            request.AuthorityEpochDigest,
            request.ExpectedPreviousAuthorityEpochDigest ?? string.Empty,
            request.ExpectedPreviousGeneration?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
        };
        var canonical = string.Concat(fields.Select(value => $"{value.Length}:{value}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    public static bool IsSha256Digest(string? value)
        => value is { Length: DigestLength } && value.All(IsLowerHex);

    public static bool IsValidEnvironment(string? value)
        => value is { Length: > 0 and <= MaximumEnvironmentLength } &&
           string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
           value.All(character => character is >= '!' and <= '~');

    private static bool IsLowerHex(char value)
        => value is >= '0' and <= '9' or >= 'a' and <= 'f';
}
