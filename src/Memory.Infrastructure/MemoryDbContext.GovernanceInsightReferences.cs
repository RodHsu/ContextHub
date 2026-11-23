using Memory.Application;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Memory.Infrastructure;

public sealed partial class MemoryDbContext : IGovernanceInsightReferenceReader
{
    public async Task<IReadOnlyDictionary<Guid, GovernanceInsightReferenceWatermarks>> ReadInsightReferenceWatermarksAsync(
        Guid tenantId, Guid ownerUserId, Guid[] insightIds, CancellationToken cancellationToken)
    {
        return await Database.SqlQueryRaw<GovernanceInsightReferenceWatermarks>(
            InsightReferenceWatermarksSql,
            new NpgsqlParameter<Guid>("tenant", tenantId),
            new NpgsqlParameter<Guid>("owner", ownerUserId),
            new NpgsqlParameter<Guid[]>("subjects", insightIds))
            .ToDictionaryAsync(x => x.InsightId, cancellationToken);
    }

    // A canonical UUID substring spans exactly four consecutive hyphens. Inspect these windows once
    // per field, without word boundaries, case folding, or consumption of an overlapping match.
    // Only the eight aggregate timestamps are returned, and project/owner scope comes from stored rows.
    private const string InsightReferenceWatermarksSql = """
        WITH subjects AS (
            SELECT id, id::text COLLATE "C" AS canonical_id, public.project_identity_key(project_id) AS project_key
            FROM conversation_insights
            WHERE tenant_id = @tenant AND owner_user_id = @owner AND id = ANY(@subjects)
        ),
        projects AS (SELECT DISTINCT project_key FROM subjects),
        work_items AS (
            SELECT w.id, p.project_key, w.updated_at, w.title, w.description
            FROM project_work_items w
            JOIN projects p ON p.project_key = public.project_identity_key(w.project_id)
            WHERE w.tenant_id = @tenant AND w.owner_user_id = @owner
        ),
        threads AS (
            SELECT t.id, p.project_key, t.updated_at, t.title
            FROM discussion_threads t
            JOIN projects p ON p.project_key = public.project_identity_key(t.host_project_id)
            WHERE t.tenant_id = @tenant AND t.owner_user_id = @owner
        ),
        reference_text(project_key, insight_source_id, kind, at, parent_kind, parent_at, content) AS (
            SELECT w.project_key, NULL::uuid, 'work_item', w.updated_at, NULL::text, NULL::timestamptz, field.content
            FROM work_items w
            CROSS JOIN LATERAL (VALUES (w.title), (w.description)) AS field(content)
            UNION ALL
            SELECT w.project_key, NULL, 'checklist', c.updated_at, 'work_item', w.updated_at, c.content
            FROM project_work_item_checklist_items c JOIN work_items w ON w.id = c.work_item_id
            UNION ALL
            SELECT t.project_key, NULL, 'discussion', t.updated_at, NULL, NULL, t.title
            FROM threads t
            UNION ALL
            SELECT t.project_key, NULL, 'message', m.created_at, 'discussion', t.updated_at, m.content
            FROM discussion_messages m JOIN threads t ON t.id = m.thread_id
            UNION ALL
            SELECT p.project_key, NULL, 'action', a.updated_at, NULL, NULL, a.dedup_key
            FROM suggested_actions a
            JOIN projects p ON p.project_key = public.project_identity_key(a.project_id)
            WHERE a.tenant_id = @tenant AND a.owner_user_id = @owner
            UNION ALL
            SELECT p.project_key, i.id, 'insight', i.updated_at, NULL, NULL, field.content
            FROM conversation_insights i
            JOIN projects p ON p.project_key = public.project_identity_key(i.project_id)
            CROSS JOIN LATERAL (VALUES (i.title), (i.content), (i.summary), (i.dedup_key)) AS field(content)
            WHERE i.tenant_id = @tenant AND i.owner_user_id = @owner
        ),
        reference_parts AS MATERIALIZED (
            SELECT project_key, insight_source_id, kind, at, parent_kind, parent_at,
                string_to_array(content, '-') AS parts
            FROM reference_text
            WHERE content COLLATE "C" LIKE '%-%-%-%-%'
        ),
        reference_windows AS MATERIALIZED (
            SELECT r.project_key, r.insight_source_id, r.kind, r.at, r.parent_kind, r.parent_at,
                right(r.parts[pos.n], 8) || '-' || r.parts[pos.n + 1] || '-' || r.parts[pos.n + 2]
                || '-' || r.parts[pos.n + 3] || '-' || left(r.parts[pos.n + 4], 12) AS token
            FROM reference_parts r
            CROSS JOIN LATERAL generate_series(1, cardinality(r.parts) - 4) AS pos(n)
            WHERE length(r.parts[pos.n]) >= 8 AND length(r.parts[pos.n + 1]) = 4
                AND length(r.parts[pos.n + 2]) = 4 AND length(r.parts[pos.n + 3]) = 4
                AND length(r.parts[pos.n + 4]) >= 12
        ),
        reference_watermarks AS (
            SELECT s.id, stamp.kind, max(stamp.at) AS at
            FROM reference_windows r
            JOIN subjects s ON s.project_key = r.project_key AND s.canonical_id = r.token COLLATE "C"
                AND r.insight_source_id IS DISTINCT FROM s.id
            CROSS JOIN LATERAL (VALUES (r.kind, r.at), (r.parent_kind, r.parent_at)) AS stamp(kind, at)
            WHERE stamp.kind IS NOT NULL
            GROUP BY s.id, stamp.kind
        ),
        project_watermarks AS (
            SELECT p.project_key, 'project_information' AS kind, max(m.updated_at) AS at
            FROM memory_items m
            JOIN projects p ON p.project_key = public.project_identity_key(m.project_id)
            WHERE m.tenant_id = @tenant AND m.owner_user_id = @owner AND m.external_key = 'system:project-information'
            GROUP BY p.project_key
            UNION ALL
            SELECT p.project_key, 'hierarchy', max(h.updated_at)
            FROM project_hierarchies h
            JOIN projects p ON p.project_key = public.project_identity_key(h.parent_project_id)
                OR p.project_key = public.project_identity_key(h.child_project_id)
            WHERE h.tenant_id = @tenant AND h.owner_user_id = @owner
            GROUP BY p.project_key
        ),
        watermarks AS (
            SELECT id, kind, at FROM reference_watermarks
            UNION ALL
            SELECT s.id, p.kind, p.at FROM subjects s JOIN project_watermarks p USING (project_key)
        )
        SELECT s.id AS "InsightId",
            max(w.at) FILTER (WHERE w.kind = 'project_information') AS "ProjectInformation",
            max(w.at) FILTER (WHERE w.kind = 'work_item') AS "WorkItem",
            max(w.at) FILTER (WHERE w.kind = 'checklist') AS "Checklist",
            max(w.at) FILTER (WHERE w.kind = 'discussion') AS "Discussion",
            max(w.at) FILTER (WHERE w.kind = 'message') AS "Message",
            max(w.at) FILTER (WHERE w.kind = 'action') AS "Action",
            max(w.at) FILTER (WHERE w.kind = 'insight') AS "Insight",
            max(w.at) FILTER (WHERE w.kind = 'hierarchy') AS "Hierarchy"
        FROM subjects s LEFT JOIN watermarks w ON w.id = s.id
        GROUP BY s.id
        """;
}
