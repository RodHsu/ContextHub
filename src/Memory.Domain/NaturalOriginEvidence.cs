namespace Memory.Domain;

/// <summary>
/// The two independently produced, normalized provenance records that may
/// support a scheduled-governance reliability decision.
/// </summary>
public enum NaturalOriginEvidenceKind
{
    PlatformAttestation,
    ControlPlaneAudit
}

/// <summary>
/// The non-secret binding that a verified natural-origin record must carry.
/// Digest properties are SHA-256 values represented as lower-case hexadecimal;
/// raw tokens, prompts, and personally identifying payloads are deliberately
/// not part of this value object.
/// Project scope is represented by the digest of the server-resolved scope so
/// the trust contract does not hard-code or disclose project identifiers.
/// </summary>
public sealed record NaturalOriginEvidenceBinding(
    Guid TenantId,
    Guid OwnerUserId,
    string ProjectScopeHash,
    string Audience,
    string ActorBindingHash,
    string TaskBindingHash,
    string AutomationBindingHash,
    string GovernanceRunIdHash,
    string SlotIdHash,
    DateTimeOffset ExpectedAtUtc,
    string RequestIdentityHash,
    string DispatchIdentityHash,
    Guid ReceiptId,
    string ReceiptEventKeyHash,
    string ScheduleDigest,
    string ConfigurationDigest,
    string ToolContractVersion,
    string SchemaHash,
    string PublishedCatalogVersion,
    string RuntimeIdentityHash);

/// <summary>
/// A verified, append-only natural-origin evidence record. This is a storage
/// entity, not a caller-facing ingestion contract; only trusted server-side
/// verification code should construct one for the internal store.
/// </summary>
public sealed class NaturalOriginEvidenceLedgerEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public NaturalOriginEvidenceKind EvidenceKind { get; set; }
    public string Issuer { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public string KeyId { get; set; } = string.Empty;
    public string Algorithm { get; set; } = string.Empty;
    public string EvidenceVersion { get; set; } = string.Empty;
    public string JtiHash { get; set; } = string.Empty;
    public string SourceSystem { get; set; } = string.Empty;
    public string SourceEventIdHash { get; set; } = string.Empty;
    public long? SourceSequence { get; set; }
    public Guid TenantId { get; set; }
    public Guid OwnerUserId { get; set; }
    public string ProjectScopeHash { get; set; } = string.Empty;
    public string TriggerKind { get; set; } = "NaturalSchedule";
    public string Audience { get; set; } = string.Empty;
    public string ActorBindingHash { get; set; } = string.Empty;
    public string TaskBindingHash { get; set; } = string.Empty;
    public string AutomationBindingHash { get; set; } = string.Empty;
    public string GovernanceRunIdHash { get; set; } = string.Empty;
    public string SlotIdHash { get; set; } = string.Empty;
    public DateTimeOffset ExpectedAtUtc { get; set; }
    public DateTimeOffset IssuedAtUtc { get; set; }
    public DateTimeOffset ObservedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public string ScheduleDigest { get; set; } = string.Empty;
    public string ConfigurationDigest { get; set; } = string.Empty;
    public string RequestIdentityHash { get; set; } = string.Empty;
    public string DispatchIdentityHash { get; set; } = string.Empty;
    public Guid ReceiptId { get; set; }
    public string ReceiptEventKeyHash { get; set; } = string.Empty;
    public string SignatureDigest { get; set; } = string.Empty;
    public string TenantBindingHash { get; set; } = string.Empty;
    public string ToolContractVersion { get; set; } = string.Empty;
    public string SchemaHash { get; set; } = string.Empty;
    public string PublishedCatalogVersion { get; set; } = string.Empty;
    public string RuntimeIdentityHash { get; set; } = string.Empty;
    public string VerificationStatus { get; set; } = "Verified";
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed record NaturalOriginEvidenceAppendResult(
    NaturalOriginEvidenceLedgerEntry Entry,
    bool IsReplay);

/// <summary>
/// Internal persistence boundary for already verified evidence. It is
/// intentionally not an HTTP/MCP ingestion contract.
/// </summary>
public interface INaturalOriginEvidenceStore
{
    Task<NaturalOriginEvidenceAppendResult> AppendAsync(
        NaturalOriginEvidenceLedgerEntry entry,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NaturalOriginEvidenceLedgerEntry>> ListForRunAsync(
        Guid tenantId,
        Guid ownerUserId,
        string governanceRunId,
        CancellationToken cancellationToken = default);
}
