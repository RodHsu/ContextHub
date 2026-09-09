using Memory.Application;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Memory.Infrastructure;

/// <summary>
/// Durable append-only store for trusted natural-origin evidence. Signature
/// verification and control-plane authentication belong to the caller that
/// owns this internal boundary; this store refuses anything that is not
/// already normalized and marked Verified.
/// </summary>
public sealed class NaturalOriginEvidenceStore(MemoryDbContext dbContext, TimeProvider timeProvider)
    : INaturalOriginEvidenceStore
{
    private const string ExpectedAudience = "/mcp-automation";
    private const string ExpectedTriggerKind = "NaturalSchedule";
    private const string ExpectedVerificationStatus = "Verified";
    private const int DigestLength = 64;
    private static readonly TimeSpan MaximumEvidenceLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaximumClockSkew = TimeSpan.FromMinutes(2);

    public async Task<NaturalOriginEvidenceAppendResult> AppendAsync(
        NaturalOriginEvidenceLedgerEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        CanonicalizeTimestamps(entry);
        Validate(entry, timeProvider.GetUtcNow());

        var existing = await dbContext.NaturalOriginEvidenceLedgerEntries
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Issuer == entry.Issuer && x.JtiHash == entry.JtiHash, cancellationToken);
        if (existing is not null)
        {
            EnsureExactReplay(existing, entry);
            return new NaturalOriginEvidenceAppendResult(existing, IsReplay: true);
        }

        entry.CreatedAtUtc = timeProvider.GetUtcNow();
        dbContext.NaturalOriginEvidenceLedgerEntries.Add(entry);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return new NaturalOriginEvidenceAppendResult(entry, IsReplay: false);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            dbContext.Entry(entry).State = EntityState.Detached;
            var raced = await dbContext.NaturalOriginEvidenceLedgerEntries
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.Issuer == entry.Issuer && x.JtiHash == entry.JtiHash, cancellationToken);
            if (raced is null)
            {
                throw;
            }

            EnsureExactReplay(raced, entry);
            return new NaturalOriginEvidenceAppendResult(raced, IsReplay: true);
        }
    }

    public async Task<IReadOnlyList<NaturalOriginEvidenceLedgerEntry>> ListForRunAsync(
        Guid tenantId,
        Guid ownerUserId,
        string governanceRunId,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty || ownerUserId == Guid.Empty)
        {
            throw new ArgumentException("Tenant and owner identifiers are required.");
        }

        if (string.IsNullOrWhiteSpace(governanceRunId))
        {
            throw new ArgumentException("GovernanceRunId is required.", nameof(governanceRunId));
        }

        var governanceRunIdHash = ScheduledGovernanceReliabilityEvidenceContract
            .ComputeOpaqueHash(governanceRunId);
        return await dbContext.NaturalOriginEvidenceLedgerEntries
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId &&
                        x.OwnerUserId == ownerUserId &&
                        x.GovernanceRunIdHash == governanceRunIdHash)
            .OrderBy(x => x.ExpectedAtUtc)
            .ThenBy(x => x.EvidenceKind)
            .ThenBy(x => x.CreatedAtUtc)
            .ToArrayAsync(cancellationToken);
    }

    private static void Validate(NaturalOriginEvidenceLedgerEntry entry, DateTimeOffset nowUtc)
    {
        if (entry.Id == Guid.Empty || entry.TenantId == Guid.Empty || entry.OwnerUserId == Guid.Empty || entry.ReceiptId == Guid.Empty)
        {
            throw new InvalidOperationException("Natural-origin evidence requires non-empty identity bindings.");
        }

        if (!Enum.IsDefined(entry.EvidenceKind) ||
            string.IsNullOrWhiteSpace(entry.Issuer) ||
            string.IsNullOrWhiteSpace(entry.Environment) ||
            string.IsNullOrWhiteSpace(entry.KeyId) ||
            string.IsNullOrWhiteSpace(entry.Algorithm) ||
            string.IsNullOrWhiteSpace(entry.EvidenceVersion) ||
            string.IsNullOrWhiteSpace(entry.JtiHash) ||
            string.IsNullOrWhiteSpace(entry.SourceSystem) ||
            string.IsNullOrWhiteSpace(entry.ToolContractVersion) ||
            string.IsNullOrWhiteSpace(entry.PublishedCatalogVersion))
        {
            throw new InvalidOperationException("Natural-origin evidence is missing a required binding.");
        }

        EnsureBoundedIdentifier(entry.Issuer, 256);
        EnsureBoundedIdentifier(entry.Environment, 64);
        EnsureBoundedIdentifier(entry.KeyId, 128);
        EnsureBoundedIdentifier(entry.Algorithm, 32);
        EnsureBoundedIdentifier(entry.EvidenceVersion, 64);
        EnsureBoundedIdentifier(entry.SourceSystem, 128);
        EnsureBoundedIdentifier(entry.ToolContractVersion, 32);
        EnsureBoundedIdentifier(entry.PublishedCatalogVersion, 128);

        if (!string.Equals(entry.Audience, ExpectedAudience, StringComparison.Ordinal) ||
            !string.Equals(entry.TriggerKind, ExpectedTriggerKind, StringComparison.Ordinal) ||
            !string.Equals(entry.VerificationStatus, ExpectedVerificationStatus, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Natural-origin evidence has an unsupported audience, trigger, or verification status.");
        }

        foreach (var digest in new[]
                 {
                     entry.ActorBindingHash,
                     entry.ProjectScopeHash,
                     entry.JtiHash,
                     entry.SourceEventIdHash,
                     entry.TaskBindingHash,
                     entry.AutomationBindingHash,
                     entry.GovernanceRunIdHash,
                     entry.SlotIdHash,
                     entry.ScheduleDigest,
                     entry.ConfigurationDigest,
                     entry.RequestIdentityHash,
                     entry.DispatchIdentityHash,
                     entry.ReceiptEventKeyHash,
                     entry.SignatureDigest,
                     entry.TenantBindingHash,
                     entry.SchemaHash,
                     entry.RuntimeIdentityHash
                 })
        {
            if (!IsSha256Digest(digest))
            {
                throw new InvalidOperationException("Natural-origin evidence contains a missing or invalid digest.");
            }
        }

        if (entry.ExpiresAtUtc <= entry.IssuedAtUtc ||
            entry.ExpiresAtUtc - entry.IssuedAtUtc > MaximumEvidenceLifetime ||
            entry.ObservedAtUtc < entry.IssuedAtUtc ||
            entry.ObservedAtUtc > entry.ExpiresAtUtc ||
            entry.IssuedAtUtc > nowUtc + MaximumClockSkew ||
            entry.ObservedAtUtc > nowUtc + MaximumClockSkew)
        {
            throw new InvalidOperationException("Natural-origin evidence has an invalid validity interval.");
        }

        if (entry.ExpiresAtUtc <= nowUtc)
        {
            throw new InvalidOperationException("Expired natural-origin evidence cannot be persisted.");
        }

        if (entry.EvidenceKind == NaturalOriginEvidenceKind.ControlPlaneAudit &&
            entry.SourceSequence is null or < 0)
        {
            throw new InvalidOperationException("Control-plane evidence requires a non-negative source sequence.");
        }

        if (entry.EvidenceKind == NaturalOriginEvidenceKind.PlatformAttestation &&
            entry.SourceSequence is not null)
        {
            throw new InvalidOperationException("Platform attestation cannot carry a control-plane source sequence.");
        }
    }

    private static void EnsureExactReplay(
        NaturalOriginEvidenceLedgerEntry existing,
        NaturalOriginEvidenceLedgerEntry incoming)
    {
        if (existing.EvidenceKind != incoming.EvidenceKind ||
            existing.Environment != incoming.Environment ||
            existing.KeyId != incoming.KeyId ||
            existing.Algorithm != incoming.Algorithm ||
            existing.EvidenceVersion != incoming.EvidenceVersion ||
            existing.SourceSystem != incoming.SourceSystem ||
            existing.SourceEventIdHash != incoming.SourceEventIdHash ||
            existing.SourceSequence != incoming.SourceSequence ||
            existing.TenantId != incoming.TenantId ||
            existing.OwnerUserId != incoming.OwnerUserId ||
            existing.ProjectScopeHash != incoming.ProjectScopeHash ||
            existing.TriggerKind != incoming.TriggerKind ||
            existing.Audience != incoming.Audience ||
            existing.ActorBindingHash != incoming.ActorBindingHash ||
            existing.TaskBindingHash != incoming.TaskBindingHash ||
            existing.AutomationBindingHash != incoming.AutomationBindingHash ||
            existing.GovernanceRunIdHash != incoming.GovernanceRunIdHash ||
            existing.SlotIdHash != incoming.SlotIdHash ||
            existing.ExpectedAtUtc != incoming.ExpectedAtUtc ||
            existing.IssuedAtUtc != incoming.IssuedAtUtc ||
            existing.ObservedAtUtc != incoming.ObservedAtUtc ||
            existing.ExpiresAtUtc != incoming.ExpiresAtUtc ||
            existing.RequestIdentityHash != incoming.RequestIdentityHash ||
            existing.DispatchIdentityHash != incoming.DispatchIdentityHash ||
            existing.ReceiptId != incoming.ReceiptId ||
            existing.ReceiptEventKeyHash != incoming.ReceiptEventKeyHash ||
            existing.ScheduleDigest != incoming.ScheduleDigest ||
            existing.ConfigurationDigest != incoming.ConfigurationDigest ||
            existing.SignatureDigest != incoming.SignatureDigest ||
            existing.TenantBindingHash != incoming.TenantBindingHash ||
            existing.ToolContractVersion != incoming.ToolContractVersion ||
            existing.SchemaHash != incoming.SchemaHash ||
            existing.PublishedCatalogVersion != incoming.PublishedCatalogVersion ||
            existing.RuntimeIdentityHash != incoming.RuntimeIdentityHash)
        {
            throw new InvalidOperationException("Natural-origin evidence replay binding mismatch.");
        }
    }

    private static void CanonicalizeTimestamps(NaturalOriginEvidenceLedgerEntry entry)
    {
        entry.IssuedAtUtc = ToPostgresTimestamp(entry.IssuedAtUtc);
        entry.ObservedAtUtc = ToPostgresTimestamp(entry.ObservedAtUtc);
        entry.ExpectedAtUtc = ToPostgresTimestamp(entry.ExpectedAtUtc);
        entry.ExpiresAtUtc = ToPostgresTimestamp(entry.ExpiresAtUtc);
    }

    private static DateTimeOffset ToPostgresTimestamp(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % 10), TimeSpan.Zero);
    }

    private static bool IsSha256Digest(string? value)
        => value is { Length: DigestLength } && value.All(IsLowerHex);

    private static void EnsureBoundedIdentifier(string value, int maximumLength)
    {
        if (value.Length > maximumLength ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(character => character is < '!' or > '~'))
        {
            throw new InvalidOperationException("Natural-origin evidence contains an invalid identifier.");
        }
    }

    private static bool IsLowerHex(char value)
        => value is >= '0' and <= '9' or >= 'a' and <= 'f';

    private static bool IsUniqueViolation(DbUpdateException exception)
        => exception.InnerException is Npgsql.PostgresException { SqlState: "23505" };
}
