using System.Security.Cryptography;
using System.Text;

namespace Memory.Application;

/// <summary>
/// The verification state of a server-side reliability evidence source.
/// Reliability qualification accepts only <see cref="Verified"/>. Every
/// other state is deliberately fail-closed and remains non-qualifying.
/// </summary>
public enum ScheduledGovernanceEvidenceVerificationStatus
{
    Missing,
    Unverified,
    Verified,
    Invalid,
    Ambiguous,
    Unavailable,
    ReplayRejected
}

/// <summary>
/// Fixed reliability boundary values. These values describe the dedicated
/// automation surface; they are not caller-selectable request fields.
/// </summary>
public static class ScheduledGovernanceReliabilityEvidenceContract
{
    public const string ResourceAudience = "/mcp-automation";
    public const string NaturalScheduleTrigger = "NaturalSchedule";
    public static readonly TimeSpan MaximumAttestationLifetime = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Produces the canonical digest of the server-resolved project scope.
    /// Empty scopes remain empty so a missing receipt scope cannot accidentally
    /// match a proof. The digest is used for exact binding without embedding a
    /// project identifier in the reliability gate.
    /// </summary>
    public static string ComputeProjectScopeHash(IEnumerable<string>? projectIds)
    {
        if (projectIds is null)
        {
            return string.Empty;
        }

        var canonical = projectIds
            .Where(projectId => !string.IsNullOrWhiteSpace(projectId))
            .Select(projectId => projectId.Trim())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (canonical.Length == 0)
        {
            return string.Empty;
        }

        return ComputeFieldSequenceHash(canonical);
    }

    public static string ComputeActorBindingHash(Guid tenantId, Guid ownerUserId)
        => tenantId == Guid.Empty || ownerUserId == Guid.Empty
            ? string.Empty
            : ComputeFieldSequenceHash(
                [tenantId.ToString("D"), ownerUserId.ToString("D")]);

    public static string ComputeRuntimeIdentityHash(ScheduledGovernanceRuntimeIdentity? identity)
        => identity is null
            ? string.Empty
            : ComputeFieldSequenceHash(
                [
                    identity.ServiceName,
                    identity.BuildVersion,
                    identity.BuildTimestampUtc.ToUniversalTime().ToString("O"),
                    identity.DerivedIdentity
                ]);

    public static string ComputeOpaqueHash(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : ComputeFieldSequenceHash([value.Trim()]);

    public static string ComputeReviewRequestIdentityHash(
        string? governanceRunId,
        bool isReReview = false)
        => string.IsNullOrWhiteSpace(governanceRunId)
            ? string.Empty
            : ComputeFieldSequenceHash(
                [
                    "scheduled-governance-review-request-v2",
                    ScheduledGovernanceContract.ReviewToolName,
                    ResourceAudience,
                    governanceRunId.Trim(),
                    isReReview ? "true" : "false",
                    "projectIds:server-resolved",
                    "limitPerSection:200",
                    "offset:0"
                 ]);

    public static string ComputeDispatchIdentityHash(
        Guid receiptId,
        string? receiptEventKey,
        string? requestIdentityHash)
        => receiptId == Guid.Empty ||
           string.IsNullOrWhiteSpace(receiptEventKey) ||
           string.IsNullOrWhiteSpace(requestIdentityHash)
            ? string.Empty
            : ComputeFieldSequenceHash(
                [
                    "scheduled-governance-dispatch-v1",
                    ResourceAudience,
                    receiptId.ToString("D"),
                    ComputeOpaqueHash(receiptEventKey),
                    requestIdentityHash.Trim()
                ]);

    private static string ComputeFieldSequenceHash(IEnumerable<string> fields)
    {
        var canonical = string.Concat(fields.Select(value => $"{value.Length}:{value}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }
}

/// <summary>
/// Server-derived lookup identity. Implementations must scope the lookup by
/// all fields and must not accept evidence supplied in an MCP or REST body.
/// </summary>
public sealed record ScheduledGovernanceReliabilityEvidenceQuery(
    Guid TenantId,
    Guid OwnerUserId,
    Guid ReceiptId,
    string GovernanceRunId,
    string RequestIdentityHash,
    string ProjectScopeHash);

/// <summary>
/// Canonical identity shared by both trusted natural-origin evidence
/// sources. The application compares every field from A and B against the
/// immutable receipt and the authenticated actor before allowing a run to
/// qualify.
/// </summary>
public sealed record ScheduledGovernanceReliabilityEvidenceBinding(
    Guid TenantId,
    Guid OwnerUserId,
    string GovernanceRunId,
    Guid ReceiptId,
    string ProjectScopeHash,
    string Environment,
    DateTimeOffset? ExpectedAtUtc,
    string ScheduleSlotId,
    string TriggerType,
    string ResourceAudience,
    string RequestIdentityHash,
    string DispatchIdentityHash,
    string ActorBindingHash,
    string TaskBindingHash,
    string AutomationBindingHash,
    string ScheduleDigest,
    string ConfigurationDigest,
    string ToolContractVersion,
    string SchemaHash,
    string PublishedCatalogVersion,
    string RuntimeIdentityHash);

/// <summary>
/// Platform-signed scheduler provenance after signature, key, freshness,
/// replay and claim validation have completed in the trusted provider.
/// Raw envelopes and tokens must never cross this boundary.
/// </summary>
public sealed record ScheduledGovernancePlatformAttestationEvidence(
    ScheduledGovernanceEvidenceVerificationStatus VerificationStatus,
    bool SignatureValid,
    bool ReplaySafe,
    string Issuer,
    string Environment,
    string KeyId,
    ScheduledGovernanceReliabilityEvidenceBinding Binding,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Immutable control-plane scheduler audit provenance after source
/// authentication, schema validation, correlation and replay checks have
/// completed in the trusted provider.
/// </summary>
public sealed record ScheduledGovernanceControlPlaneAuditEvidence(
    ScheduledGovernanceEvidenceVerificationStatus VerificationStatus,
    bool SourceAuthenticated,
    bool ImmutableEvent,
    bool ReplaySafe,
    string SourceSystem,
    string AuditEventHash,
    long Sequence,
    ScheduledGovernanceReliabilityEvidenceBinding Binding);

/// <summary>
/// The only natural-origin proof accepted by the reliability projection. A
/// run qualifies only when both A and B are independently present, verified,
/// replay-safe and exactly bound. Timing is only a consistency check.
/// </summary>
public sealed record ScheduledGovernanceReliabilityEvidenceSnapshot(
    ScheduledGovernancePlatformAttestationEvidence? PlatformAttestation,
    ScheduledGovernanceControlPlaneAuditEvidence? ControlPlaneAudit);

/// <summary>
/// Internal application boundary for trusted, server-derived A/B evidence.
/// The implementation owns cryptographic verification and authoritative
/// control-plane access; callers cannot populate this evidence through the
/// scheduled MCP contract.
/// </summary>
public interface IScheduledGovernanceReliabilityEvidenceProvider
{
    Task<ScheduledGovernanceReliabilityEvidenceSnapshot?> GetAsync(
        ScheduledGovernanceReliabilityEvidenceQuery query,
        CancellationToken cancellationToken);
}
