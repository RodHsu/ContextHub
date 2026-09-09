using System.Text.Json;
using Memory.Application;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Memory.Infrastructure;

/// <summary>
/// Projects already-verified append-only A/B evidence into the application
/// reliability contract. The provider rebinds every record to the immutable
/// receipt and authenticated actor; a ledger row is never accepted merely
/// because its verification-status column says Verified.
/// </summary>
public sealed class ScheduledGovernanceReliabilityEvidenceProvider(
    MemoryDbContext dbContext,
    INaturalOriginEvidenceStore evidenceStore) : IScheduledGovernanceReliabilityEvidenceProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ScheduledGovernanceReliabilityEvidenceSnapshot?> GetAsync(
        ScheduledGovernanceReliabilityEvidenceQuery query,
        CancellationToken cancellationToken)
    {
        var rows = await evidenceStore.ListForRunAsync(
            query.TenantId,
            query.OwnerUserId,
            query.GovernanceRunId,
            cancellationToken);
        if (rows.Count == 0)
        {
            return null;
        }

        var receipt = await dbContext.GovernanceRunReceipts
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.Id == query.ReceiptId &&
                       row.TenantId == query.TenantId &&
                       row.OwnerUserId == query.OwnerUserId &&
                       row.GovernanceRunId == query.GovernanceRunId,
                cancellationToken);
        var attestationRows = rows
            .Where(row => row.EvidenceKind == NaturalOriginEvidenceKind.PlatformAttestation)
            .ToArray();
        var auditRows = rows
            .Where(row => row.EvidenceKind == NaturalOriginEvidenceKind.ControlPlaneAudit)
            .ToArray();

        var attestation = ProjectAttestation(attestationRows, query, receipt);
        var audit = ProjectAudit(auditRows, query, receipt);
        return new ScheduledGovernanceReliabilityEvidenceSnapshot(attestation, audit);
    }

    private static ScheduledGovernancePlatformAttestationEvidence? ProjectAttestation(
        IReadOnlyList<NaturalOriginEvidenceLedgerEntry> rows,
        ScheduledGovernanceReliabilityEvidenceQuery query,
        GovernanceRunReceipt? receipt)
    {
        if (rows.Count == 0)
        {
            return null;
        }

        var status = ResolveStatus(rows, query, receipt);
        var row = rows[0];
        var verified = status == ScheduledGovernanceEvidenceVerificationStatus.Verified;
        return new ScheduledGovernancePlatformAttestationEvidence(
            status,
            SignatureValid: verified,
            ReplaySafe: verified,
            row.Issuer,
            row.Environment,
            row.KeyId,
            ToBinding(row, query.GovernanceRunId),
            row.IssuedAtUtc,
            row.ExpiresAtUtc);
    }

    private static ScheduledGovernanceControlPlaneAuditEvidence? ProjectAudit(
        IReadOnlyList<NaturalOriginEvidenceLedgerEntry> rows,
        ScheduledGovernanceReliabilityEvidenceQuery query,
        GovernanceRunReceipt? receipt)
    {
        if (rows.Count == 0)
        {
            return null;
        }

        var status = ResolveStatus(rows, query, receipt);
        var row = rows[0];
        var verified = status == ScheduledGovernanceEvidenceVerificationStatus.Verified;
        return new ScheduledGovernanceControlPlaneAuditEvidence(
            status,
            SourceAuthenticated: verified,
            ImmutableEvent: verified,
            ReplaySafe: verified,
            row.SourceSystem,
            row.SourceEventIdHash,
            row.SourceSequence ?? -1,
            ToBinding(row, query.GovernanceRunId));
    }

    private static ScheduledGovernanceEvidenceVerificationStatus ResolveStatus(
        IReadOnlyList<NaturalOriginEvidenceLedgerEntry> rows,
        ScheduledGovernanceReliabilityEvidenceQuery query,
        GovernanceRunReceipt? receipt)
    {
        if (rows.Count != 1)
        {
            return ScheduledGovernanceEvidenceVerificationStatus.Ambiguous;
        }

        var row = rows[0];
        return receipt is not null &&
               EntryMatchesQuery(row, query) &&
               ReceiptBindingMatches(receipt, row, query)
            ? ScheduledGovernanceEvidenceVerificationStatus.Verified
            : ScheduledGovernanceEvidenceVerificationStatus.Invalid;
    }

    private static bool EntryMatchesQuery(
        NaturalOriginEvidenceLedgerEntry row,
        ScheduledGovernanceReliabilityEvidenceQuery query)
        => row.TenantId == query.TenantId &&
           row.OwnerUserId == query.OwnerUserId &&
           row.ReceiptId == query.ReceiptId &&
           string.Equals(
               row.GovernanceRunIdHash,
               ScheduledGovernanceReliabilityEvidenceContract.ComputeOpaqueHash(query.GovernanceRunId),
               StringComparison.Ordinal) &&
           string.Equals(row.RequestIdentityHash, query.RequestIdentityHash, StringComparison.Ordinal) &&
           string.Equals(row.ProjectScopeHash, query.ProjectScopeHash, StringComparison.Ordinal) &&
           string.Equals(
               row.ActorBindingHash,
               ScheduledGovernanceReliabilityEvidenceContract.ComputeActorBindingHash(
                   query.TenantId,
                   query.OwnerUserId),
               StringComparison.Ordinal) &&
           string.Equals(
               row.TenantBindingHash,
               ScheduledGovernanceReliabilityEvidenceContract.ComputeActorBindingHash(
                   query.TenantId,
                   query.OwnerUserId),
               StringComparison.Ordinal) &&
           string.Equals(
               row.Audience,
               ScheduledGovernanceReliabilityEvidenceContract.ResourceAudience,
               StringComparison.Ordinal) &&
           string.Equals(
               row.TriggerKind,
               ScheduledGovernanceReliabilityEvidenceContract.NaturalScheduleTrigger,
               StringComparison.Ordinal) &&
           string.Equals(row.VerificationStatus, "Verified", StringComparison.Ordinal);

    private static bool ReceiptBindingMatches(
        GovernanceRunReceipt receipt,
        NaturalOriginEvidenceLedgerEntry row,
        ScheduledGovernanceReliabilityEvidenceQuery query)
    {
        IReadOnlyList<string> projectIds;
        try
        {
            projectIds = JsonSerializer.Deserialize<string[]>(receipt.ProjectIdsJson, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return false;
        }

        return string.Equals(
                   ScheduledGovernanceReliabilityEvidenceContract.ComputeProjectScopeHash(projectIds),
                   query.ProjectScopeHash,
                   StringComparison.Ordinal) &&
               !string.IsNullOrWhiteSpace(receipt.EventKey) &&
               string.Equals(
                   row.ReceiptEventKeyHash,
                   ScheduledGovernanceReliabilityEvidenceContract.ComputeOpaqueHash(receipt.EventKey),
                   StringComparison.Ordinal);
    }

    private static ScheduledGovernanceReliabilityEvidenceBinding ToBinding(
        NaturalOriginEvidenceLedgerEntry row,
        string governanceRunId)
        => new(
            row.TenantId,
            row.OwnerUserId,
            governanceRunId,
            row.ReceiptId,
            row.ProjectScopeHash,
            row.Environment,
            row.ExpectedAtUtc,
            row.SlotIdHash,
            row.TriggerKind,
            row.Audience,
            row.RequestIdentityHash,
            row.DispatchIdentityHash,
            row.ActorBindingHash,
            row.TaskBindingHash,
            row.AutomationBindingHash,
            row.ScheduleDigest,
            row.ConfigurationDigest,
            row.ToolContractVersion,
            row.SchemaHash,
            row.PublishedCatalogVersion,
            row.RuntimeIdentityHash);
}
