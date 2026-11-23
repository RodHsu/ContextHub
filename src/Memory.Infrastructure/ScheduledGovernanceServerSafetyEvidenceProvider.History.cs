using Memory.Application;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Memory.Infrastructure;

public sealed partial class ScheduledGovernanceServerSafetyEvidenceProvider
{
    // Bound the number of complete receipt chains kept in memory at once.
    private const int HistoryBatchSize = 16;

    public async Task<IReadOnlyDictionary<ScheduledGovernanceServerSafetyEvidenceQuery, ScheduledGovernanceServerSafetyEvidenceSnapshot?>> GetHistoryAsync(
        IReadOnlyList<ScheduledGovernanceServerSafetyEvidenceQuery> queries,
        CancellationToken cancellationToken)
    {
        var results = queries.Distinct().ToDictionary(x => x, _ => (ScheduledGovernanceServerSafetyEvidenceSnapshot?)null);
        var scopes = results.Keys.Where(CanRead).GroupBy(x => (x.TenantId, x.OwnerUserId)).ToArray();
        foreach (var scope in scopes)
        {
            foreach (var batch in scope.Chunk(HistoryBatchSize))
            {
                await ReadScopeBatchAsync(scope.Key.TenantId, scope.Key.OwnerUserId, batch, results, cancellationToken);
            }
        }

        return results;
    }

    private async Task ReadScopeBatchAsync(
        Guid tenantId,
        Guid ownerUserId,
        ScheduledGovernanceServerSafetyEvidenceQuery[] batch,
        Dictionary<ScheduledGovernanceServerSafetyEvidenceQuery, ScheduledGovernanceServerSafetyEvidenceSnapshot?> results,
        CancellationToken cancellationToken)
    {
        var source = await dbContext.GovernanceRunReceipts.FromSqlRaw(
            ReadHistorySql,
            new NpgsqlParameter<Guid>("tenant", tenantId),
            new NpgsqlParameter<Guid>("owner", ownerUserId),
            new NpgsqlParameter<string[]>("run_ids", batch.Select(x => x.GovernanceRunId.Trim()).ToArray()),
            new NpgsqlParameter<Guid[]>("receipt_ids", batch.Select(x => x.ReceiptId).ToArray()),
            new NpgsqlParameter<int>("event_limit", EventReadLimit))
            .AsNoTracking()
            .ToArrayAsync(cancellationToken);

        var receipts = source.DistinctBy(row => row.Id).ToDictionary(row => row.Id);
        var histories = receipts.Values.GroupBy(row => row.GovernanceRunId, StringComparer.Ordinal)
            .ToDictionary(
                run => run.Key,
                run => run.OrderBy(row => row.EventSequence).Take(EventReadLimit).ToArray(),
                StringComparer.Ordinal);
        var observed = batch.Where(x =>
            receipts.TryGetValue(x.ReceiptId, out var row) &&
            string.Equals(row.GovernanceRunId, x.GovernanceRunId.Trim(), StringComparison.Ordinal)).ToArray();
        var needsFacts = observed.Select(x => receipts[x.ReceiptId])
            .DistinctBy(row => row.Id)
            .Where(row => histories[row.GovernanceRunId] is { Length: < EventReadLimit } events &&
                          events[^1].EventSequence == row.EventSequence &&
                          IsCurrentCompletedScheduledReceipt(row))
            .ToArray();

        Dictionary<long, HistoryFacts> facts = needsFacts.Length == 0
            ? []
            : await dbContext.Database.SqlQueryRaw<HistoryFacts>(
                    ReadHistoryFactsSql,
                    new NpgsqlParameter<Guid>("tenant", tenantId),
                    new NpgsqlParameter<Guid>("owner", ownerUserId),
                    new NpgsqlParameter<string[]>("run_ids", needsFacts.Select(row => row.GovernanceRunId).ToArray()),
                    new NpgsqlParameter<long[]>("sequences", needsFacts.Select(row => row.EventSequence).ToArray()),
                    new NpgsqlParameter<bool[]>("check_mutation", needsFacts.Select(row =>
                        HasNoReceiptMutationSignals(histories[row.GovernanceRunId])).ToArray()))
                .ToDictionaryAsync(row => row.EventSequence, cancellationToken);

        foreach (var query in observed)
        {
            var row = receipts[query.ReceiptId];
            var current = facts.GetValueOrDefault(row.EventSequence);
            results[query] = await EvaluateAsync(
                row, histories[row.GovernanceRunId], row.GovernanceRunId,
                () => Task.FromResult(current?.HasMutationSignal ?? true),
                () => Task.FromResult(current?.HasNewerReceipt ?? true));
        }
    }

    private sealed record HistoryFacts(long EventSequence, bool HasMutationSignal, bool HasNewerReceipt);

    // Keep the observed row even if it is outside the first 1,001 events. Its sequence is needed
    // for the same Unknown result as the single-run reader when the per-run event cap is exceeded.
    private const string ReadHistorySql = """
        WITH observed AS (
            SELECT r.*
            FROM unnest(@run_ids, @receipt_ids) AS requested(run_id, receipt_id)
            JOIN governance_run_receipts r ON r.id = requested.receipt_id AND r.governance_run_id = requested.run_id
            WHERE r.tenant_id = @tenant AND r.owner_user_id = @owner
        ),
        observed_runs AS (SELECT DISTINCT governance_run_id FROM observed)
        SELECT e.*
        FROM observed_runs requested
        CROSS JOIN LATERAL (
            SELECT r.*
            FROM governance_run_receipts r
            WHERE r.tenant_id = @tenant AND r.owner_user_id = @owner AND r.governance_run_id = requested.governance_run_id
            ORDER BY r.event_sequence
            LIMIT @event_limit
        ) e
        UNION ALL
        SELECT * FROM observed
        """;

    // Read the newer-event guard with the mutation facts so the guard covers the same database
    // snapshot. Batch and audit evidence retain the single-run reader's tenant-wide run binding.
    private const string ReadHistoryFactsSql = """
        SELECT requested.seq AS "EventSequence",
            CASE WHEN requested.check_mutation THEN
                EXISTS (
                    SELECT 1 FROM governance_batch_runs b
                    WHERE b.tenant_id = @tenant AND b.governance_run_id = requested.run_id
                ) OR EXISTS (
                    SELECT 1 FROM security_audit_events a
                    WHERE a.tenant_id = @tenant AND a.details_json @> jsonb_build_object('governanceRunId', requested.run_id)
                )
                ELSE FALSE END AS "HasMutationSignal",
            EXISTS (
                SELECT 1 FROM governance_run_receipts r
                WHERE r.tenant_id = @tenant AND r.owner_user_id = @owner AND r.governance_run_id = requested.run_id
                    AND r.event_sequence > requested.seq
            ) AS "HasNewerReceipt"
        FROM unnest(@run_ids, @sequences, @check_mutation) AS requested(run_id, seq, check_mutation)
        """;
}
