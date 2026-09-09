using System.Buffers.Binary;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Memory.Infrastructure;

/// <summary>
/// Durable server-owned compare-and-advance boundary for scheduled-governance
/// authority epochs. The ledger is deliberately independent from the
/// reliability projection: a caller must prove that the epoch is current
/// before using it as evidence.
/// </summary>
public sealed class ScheduledGovernanceAuthorityEpochLedger(
    MemoryDbContext dbContext,
    TimeProvider timeProvider) : IScheduledGovernanceAuthorityEpochLedger
{
    private const string AdvisoryLockPurpose = "scheduled-governance-authority-epoch-v1";
    internal const int MaximumChainLength = 4_096;

    public async Task<ScheduledGovernanceAuthorityEpochAdvanceResult> AdvanceAsync(
        ScheduledGovernanceAuthorityEpochAdvanceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryNormalize(request, out var normalized, out var invalidReason))
        {
            return new(
                ScheduledGovernanceAuthorityEpochAdvanceStatus.Invalid,
                null,
                invalidReason);
        }

        if (HasAmbientTransaction())
        {
            // Advanced means committed to the durable ledger. Joining an
            // ambient transaction would allow a caller to observe success and
            // later roll the row back, so this boundary deliberately refuses
            // that ambiguous lifecycle.
            return new(
                ScheduledGovernanceAuthorityEpochAdvanceStatus.Unavailable,
                null,
                "Authority epoch advances cannot join an ambient transaction.");
        }

        var ownsTransaction = dbContext.Database.CurrentTransaction is null;
        IDbContextTransaction? transaction = null;

        try
        {
            if (ownsTransaction)
            {
                transaction = await dbContext.Database.BeginTransactionAsync(
                    IsolationLevel.ReadCommitted,
                    cancellationToken);
            }

            await dbContext.Database.ExecuteSqlRawAsync(
                "SELECT pg_advisory_xact_lock({0});",
                [ComputeAdvisoryLockKey(ToScope(normalized))],
                cancellationToken);

            var chain = await dbContext.ScheduledGovernanceAuthorityEpochs
                .AsNoTracking()
                .Where(row => row.TenantId == normalized.TenantId &&
                              row.OwnerUserId == normalized.OwnerUserId &&
                              row.Environment == normalized.Environment)
                .OrderBy(row => row.Generation)
                .Take(MaximumChainLength + 1)
                .ToArrayAsync(cancellationToken);

            if (!IsValidChain(chain, ToScope(normalized)))
            {
                return await FinishAsync(
                    transaction,
                    new(
                        ScheduledGovernanceAuthorityEpochAdvanceStatus.Unavailable,
                        null,
                        "The authority epoch ledger is inconsistent; the request was rejected."),
                    cancellationToken);
            }

            var existing = chain.SingleOrDefault(row =>
                row.AuthorityEpochDigest == normalized.AuthorityEpochDigest);
            if (existing is not null)
            {
                var replayRequest = WithEffectivePreviousGeneration(normalized, existing.PreviousGeneration);
                var replayRequestHash = ScheduledGovernanceAuthorityEpochContract.ComputeRequestHash(replayRequest);
                if (!IsExactReplay(existing, replayRequest, replayRequestHash))
                {
                    return await FinishAsync(
                        transaction,
                        new(
                            ScheduledGovernanceAuthorityEpochAdvanceStatus.Conflict,
                            null,
                            "The authority epoch digest is already bound to a different request."),
                        cancellationToken);
                }

                var current = chain[^1];
                var replayStatus = existing.Generation == current.Generation
                    ? ScheduledGovernanceAuthorityEpochAdvanceStatus.ReplayCurrent
                    : ScheduledGovernanceAuthorityEpochAdvanceStatus.ReplayHistorical;
                return await FinishAsync(
                    transaction,
                    new(replayStatus, existing, replayStatus ==
                        ScheduledGovernanceAuthorityEpochAdvanceStatus.ReplayCurrent
                        ? "The current authority epoch request was replayed exactly."
                        : "A historical authority epoch request was replayed; it is not current authority."),
                    cancellationToken);
            }

            if (!HasCapacityForAppend(chain.Length))
            {
                return await FinishAsync(
                    transaction,
                    new(
                        ScheduledGovernanceAuthorityEpochAdvanceStatus.Unavailable,
                        null,
                        "The authority epoch chain reached its bounded history limit; the request was rejected."),
                    cancellationToken);
            }

            var previous = chain.Length == 0 ? null : chain[^1];
            if (previous is null)
            {
                if (normalized.ExpectedPreviousAuthorityEpochDigest is not null ||
                    normalized.ExpectedPreviousGeneration is not null)
                {
                    return await FinishAsync(
                        transaction,
                        new(
                            ScheduledGovernanceAuthorityEpochAdvanceStatus.Conflict,
                            null,
                            "An initial authority epoch cannot name a predecessor."),
                        cancellationToken);
                }
            }
            else
            {
                if (!string.Equals(
                        normalized.ExpectedPreviousAuthorityEpochDigest,
                        previous.AuthorityEpochDigest,
                        StringComparison.Ordinal) ||
                    (normalized.ExpectedPreviousGeneration.HasValue &&
                     normalized.ExpectedPreviousGeneration.Value != previous.Generation))
                {
                    return await FinishAsync(
                        transaction,
                        new(
                            ScheduledGovernanceAuthorityEpochAdvanceStatus.Conflict,
                            null,
                            "The expected predecessor is not the current authority epoch."),
                        cancellationToken);
                }
            }

            if (previous is not null && previous.Generation == long.MaxValue)
            {
                return await FinishAsync(
                    transaction,
                    new(
                        ScheduledGovernanceAuthorityEpochAdvanceStatus.Unavailable,
                        null,
                        "The authority epoch generation is exhausted; the request was rejected."),
                    cancellationToken);
            }

            var persistedRequest = WithEffectivePreviousGeneration(
                normalized,
                previous?.Generation);
            var requestHash = ScheduledGovernanceAuthorityEpochContract.ComputeRequestHash(persistedRequest);
            var epoch = new ScheduledGovernanceAuthorityEpoch
            {
                TenantId = normalized.TenantId,
                OwnerUserId = normalized.OwnerUserId,
                Environment = normalized.Environment,
                ConfigurationDigest = normalized.ConfigurationDigest,
                AuthorityEpochDigest = normalized.AuthorityEpochDigest,
                Generation = previous is null ? 1 : previous.Generation + 1,
                PreviousAuthorityEpochDigest = previous?.AuthorityEpochDigest,
                PreviousGeneration = previous?.Generation,
                RequestHash = requestHash,
                CreatedAtUtc = ToPostgresTimestamp(timeProvider.GetUtcNow())
            };

            dbContext.ScheduledGovernanceAuthorityEpochs.Add(epoch);
            await dbContext.SaveChangesAsync(cancellationToken);

            return await FinishAsync(
                transaction,
                new(
                    ScheduledGovernanceAuthorityEpochAdvanceStatus.Advanced,
                    epoch,
                    "The authority epoch was durably advanced."),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DbUpdateException exception) when (ownsTransaction && IsConflict(exception))
        {
            dbContext.ChangeTracker.Clear();
            return new(
                ScheduledGovernanceAuthorityEpochAdvanceStatus.Conflict,
                null,
                "The authority epoch advance conflicted with another durable ledger operation.");
        }
        catch (PostgresException exception) when (ownsTransaction && IsConflict(exception))
        {
            dbContext.ChangeTracker.Clear();
            return new(
                ScheduledGovernanceAuthorityEpochAdvanceStatus.Conflict,
                null,
                "The authority epoch advance conflicted with another durable ledger operation.");
        }
        catch (Exception) when (ownsTransaction)
        {
            dbContext.ChangeTracker.Clear();
            return new(
                ScheduledGovernanceAuthorityEpochAdvanceStatus.Unavailable,
                null,
                "The authority epoch ledger was unavailable; the request was rejected.");
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    public async Task<ScheduledGovernanceAuthorityEpoch?> GetCurrentAsync(
        ScheduledGovernanceAuthorityEpochScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!TryNormalizeScope(scope, out var normalizedScope, out var invalidReason))
        {
            throw new ArgumentException(invalidReason, nameof(scope));
        }

        if (HasAmbientTransaction())
        {
            throw new InvalidOperationException(
                "Authority epoch reads cannot join an ambient transaction.");
        }

        var ownsTransaction = dbContext.Database.CurrentTransaction is null;
        IDbContextTransaction? transaction = null;
        try
        {
            if (ownsTransaction)
            {
                transaction = await dbContext.Database.BeginTransactionAsync(
                    IsolationLevel.ReadCommitted,
                    cancellationToken);
            }

            await dbContext.Database.ExecuteSqlRawAsync(
                "SELECT pg_advisory_xact_lock({0});",
                [ComputeAdvisoryLockKey(normalizedScope)],
                cancellationToken);

            var chain = await dbContext.ScheduledGovernanceAuthorityEpochs
                .AsNoTracking()
                .Where(row => row.TenantId == normalizedScope.TenantId &&
                              row.OwnerUserId == normalizedScope.OwnerUserId &&
                              row.Environment == normalizedScope.Environment)
                .OrderBy(row => row.Generation)
                .Take(MaximumChainLength + 1)
                .ToArrayAsync(cancellationToken);
            if (!IsValidChain(chain, normalizedScope))
            {
                throw new InvalidOperationException(
                    "The authority epoch ledger is inconsistent; current authority is unavailable.");
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return chain.Length == 0 ? null : chain[^1];
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    private static async Task<ScheduledGovernanceAuthorityEpochAdvanceResult> FinishAsync(
        IDbContextTransaction? transaction,
        ScheduledGovernanceAuthorityEpochAdvanceResult result,
        CancellationToken cancellationToken)
    {
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return result;
    }

    private static bool TryNormalize(
        ScheduledGovernanceAuthorityEpochAdvanceRequest request,
        out ScheduledGovernanceAuthorityEpochAdvanceRequest normalized,
        out string reason)
    {
        normalized = request;
        if (request.TenantId == Guid.Empty || request.OwnerUserId == Guid.Empty)
        {
            reason = "Tenant and owner identifiers are required.";
            return false;
        }

        if (!ScheduledGovernanceAuthorityEpochContract.IsValidEnvironment(request.Environment))
        {
            reason = "Environment must be a trimmed printable ASCII identifier of at most 64 characters.";
            return false;
        }

        if (!ScheduledGovernanceAuthorityEpochContract.IsSha256Digest(request.ConfigurationDigest) ||
            !ScheduledGovernanceAuthorityEpochContract.IsSha256Digest(request.AuthorityEpochDigest))
        {
            reason = "Configuration and authority epoch digests must be lower-case SHA-256 values.";
            return false;
        }

        if (request.ExpectedPreviousAuthorityEpochDigest is { Length: 0 } ||
            (request.ExpectedPreviousAuthorityEpochDigest is not null &&
             !ScheduledGovernanceAuthorityEpochContract.IsSha256Digest(
                 request.ExpectedPreviousAuthorityEpochDigest)))
        {
            reason = "The expected predecessor digest must be null or a lower-case SHA-256 value.";
            return false;
        }

        if (request.ExpectedPreviousGeneration is <= 0)
        {
            reason = "The expected predecessor generation must be positive when supplied.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool TryNormalizeScope(
        ScheduledGovernanceAuthorityEpochScope scope,
        out ScheduledGovernanceAuthorityEpochScope normalized,
        out string reason)
    {
        normalized = scope;
        if (scope.TenantId == Guid.Empty || scope.OwnerUserId == Guid.Empty)
        {
            reason = "Tenant and owner identifiers are required.";
            return false;
        }

        if (!ScheduledGovernanceAuthorityEpochContract.IsValidEnvironment(scope.Environment))
        {
            reason = "Environment must be a trimmed printable ASCII identifier of at most 64 characters.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool IsExactReplay(
        ScheduledGovernanceAuthorityEpoch existing,
        ScheduledGovernanceAuthorityEpochAdvanceRequest request,
        string requestHash)
        => existing.TenantId == request.TenantId &&
           existing.OwnerUserId == request.OwnerUserId &&
           existing.Environment == request.Environment &&
           existing.ConfigurationDigest == request.ConfigurationDigest &&
           existing.AuthorityEpochDigest == request.AuthorityEpochDigest &&
           existing.PreviousAuthorityEpochDigest == request.ExpectedPreviousAuthorityEpochDigest &&
           existing.PreviousGeneration == request.ExpectedPreviousGeneration &&
           existing.RequestHash == requestHash;

    private static ScheduledGovernanceAuthorityEpochAdvanceRequest WithEffectivePreviousGeneration(
        ScheduledGovernanceAuthorityEpochAdvanceRequest request,
        long? previousGeneration)
        => request.ExpectedPreviousGeneration.HasValue ||
           request.ExpectedPreviousAuthorityEpochDigest is null
            ? request
            : request with { ExpectedPreviousGeneration = previousGeneration };

    private static ScheduledGovernanceAuthorityEpochScope ToScope(
        ScheduledGovernanceAuthorityEpochAdvanceRequest request)
        => new(request.TenantId, request.OwnerUserId, request.Environment);

    private static bool IsValidChain(
        IReadOnlyList<ScheduledGovernanceAuthorityEpoch> chain,
        ScheduledGovernanceAuthorityEpochScope scope)
    {
        if (chain.Count > MaximumChainLength)
        {
            return false;
        }

        var digests = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < chain.Count; index++)
        {
            var row = chain[index];
            if (row.Id == Guid.Empty ||
                row.TenantId != scope.TenantId ||
                row.OwnerUserId != scope.OwnerUserId ||
                row.Environment != scope.Environment ||
                row.Generation != index + 1 ||
                !ScheduledGovernanceAuthorityEpochContract.IsSha256Digest(row.ConfigurationDigest) ||
                !ScheduledGovernanceAuthorityEpochContract.IsSha256Digest(row.AuthorityEpochDigest) ||
                !ScheduledGovernanceAuthorityEpochContract.IsSha256Digest(row.RequestHash) ||
                !digests.Add(row.AuthorityEpochDigest))
            {
                return false;
            }

            if (index == 0)
            {
                if (row.PreviousAuthorityEpochDigest is not null || row.PreviousGeneration is not null)
                {
                    return false;
                }
            }
            else
            {
                var previous = chain[index - 1];
                if (!string.Equals(
                        row.PreviousAuthorityEpochDigest,
                        previous.AuthorityEpochDigest,
                        StringComparison.Ordinal) ||
                    row.PreviousGeneration != previous.Generation)
                {
                    return false;
                }
            }

            var persistedRequest = new ScheduledGovernanceAuthorityEpochAdvanceRequest(
                row.TenantId,
                row.OwnerUserId,
                row.Environment,
                row.ConfigurationDigest,
                row.AuthorityEpochDigest,
                row.PreviousAuthorityEpochDigest,
                row.PreviousGeneration);
            if (!string.Equals(
                    row.RequestHash,
                    ScheduledGovernanceAuthorityEpochContract.ComputeRequestHash(persistedRequest),
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private bool HasAmbientTransaction()
        => dbContext.Database.CurrentTransaction is not null ||
           System.Transactions.Transaction.Current is not null;

    internal static bool HasCapacityForAppend(int chainLength)
        => chainLength >= 0 && chainLength < MaximumChainLength;

    private static long ComputeAdvisoryLockKey(ScheduledGovernanceAuthorityEpochScope scope)
    {
        var canonical = string.Concat(
            AdvisoryLockPurpose,
            "|",
            scope.TenantId.ToString("D"),
            "|",
            scope.OwnerUserId.ToString("D"),
            "|",
            scope.Environment);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return BinaryPrimitives.ReadInt64BigEndian(hash.AsSpan(0, sizeof(long)));
    }

    private static DateTimeOffset ToPostgresTimestamp(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % 10), TimeSpan.Zero);
    }

    private static bool IsConflict(DbUpdateException exception)
        => exception.GetBaseException() is PostgresException postgres && IsConflict(postgres);

    private static bool IsConflict(PostgresException exception)
        => exception.SqlState is PostgresErrorCodes.UniqueViolation or
            PostgresErrorCodes.ForeignKeyViolation or
            PostgresErrorCodes.CheckViolation or
            PostgresErrorCodes.SerializationFailure or
            PostgresErrorCodes.DeadlockDetected;
}
