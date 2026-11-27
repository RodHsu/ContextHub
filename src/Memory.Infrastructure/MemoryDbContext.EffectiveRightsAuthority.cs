using Memory.Application;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Memory.Infrastructure;

public sealed partial class MemoryDbContext
{
    public async Task<EffectiveRightsAuthoritySnapshot> ReadEffectiveRightsAuthorityAsync(
        string projectId, string principalId, IReadOnlyList<string> rights, Guid? tenantId, Guid? ownerUserId,
        CancellationToken cancellationToken)
    {
        var rows = await Database.SqlQueryRaw<EffectiveRightsAuthorityRow>(
            EffectiveRightsAuthoritySql,
            new NpgsqlParameter<string>("project", projectId),
            new NpgsqlParameter<string>("principal", principalId),
            new NpgsqlParameter<string[]>("rights", rights.ToArray()),
            new NpgsqlParameter("tenant", NpgsqlDbType.Uuid) { Value = (object?)tenantId ?? DBNull.Value },
            new NpgsqlParameter("owner", NpgsqlDbType.Uuid) { Value = (object?)ownerUserId ?? DBNull.Value })
            .ToArrayAsync(cancellationToken);

        var edges = rows.Where(row => row.Kind == "Edge")
            .Select(row => new AuthorizationTopologyEdge(row.ProjectId, row.ChildProjectId!, row.Dimension!, true, row.Revision))
            .ToArray();
        var rules = rows.Where(row => row.Kind is "Policy" or "Grant")
            .Select(row => new AuthorizationRule(
                row.ProjectId, row.PrincipalId!, row.Right!, Enum.Parse<AuthorizationEffect>(row.Effect!),
                row.Kind == "Policy" ? AuthorizationRuleSource.PolicyDerived : AuthorizationRuleSource.ExplicitGrant,
                row.EvidenceRef!, row.ResourceType, row.ResourceId))
            .ToArray();
        var revisionRows = rows.Where(row => row.Kind == "Revision").ToArray();
        var revisions = new SecurityRevisionVector(
            revisionRows.Sum(row => row.TopologyRevision), revisionRows.Sum(row => row.PolicyRevision),
            revisionRows.Sum(row => row.GrantRevision), revisionRows.Sum(row => row.TagRevision));
        return new EffectiveRightsAuthoritySnapshot(edges, rules, revisions);
    }

    // UNION deduplicates ancestor keys so traversal terminates on a cycle. Return all reachable
    // inheritable edges, including closing edges, for the evaluator's existing fail-closed check.
    private const string EffectiveRightsAuthoritySql = """
        WITH RECURSIVE scoped_edges AS (
            SELECT parent_project_id, child_project_id, dimension, revision,
                   public.project_identity_key(parent_project_id) AS parent_key,
                   public.project_identity_key(child_project_id) AS child_key
            FROM project_hierarchies
            WHERE authorization_inheritable
              AND (@tenant IS NULL OR (tenant_id = @tenant AND (@owner IS NULL OR owner_user_id = @owner)))
        ),
        ancestors(project_key) AS (
            SELECT public.project_identity_key(@project)
            UNION
            SELECT edge.parent_key
            FROM scoped_edges edge
            JOIN ancestors ancestor ON edge.child_key = ancestor.project_key
        ),
        rules AS (
            SELECT 'Policy' AS kind, project_id, principal_id, right_key, effect, evidence_ref,
                   resource_type, resource_id, tenant_id, owner_user_id
            FROM project_authorization_policies
            UNION ALL
            SELECT 'Grant', project_id, principal_id, right_key, effect, evidence_ref,
                   resource_type, resource_id, tenant_id, owner_user_id
            FROM project_explicit_grants
        )
        SELECT 'Edge' AS "Kind", edge.parent_project_id AS "ProjectId",
               edge.child_project_id AS "ChildProjectId", edge.dimension AS "Dimension", edge.revision AS "Revision",
               NULL::text AS "PrincipalId", NULL::text AS "Right", NULL::text AS "Effect", NULL::text AS "EvidenceRef",
               NULL::text AS "ResourceType", NULL::text AS "ResourceId", 0::bigint AS "TopologyRevision",
               0::bigint AS "PolicyRevision", 0::bigint AS "GrantRevision", 0::bigint AS "TagRevision"
        FROM scoped_edges edge
        JOIN ancestors ancestor ON edge.child_key = ancestor.project_key
        UNION ALL
        SELECT rule.kind, rule.project_id, NULL, NULL, 0,
               rule.principal_id, rule.right_key, rule.effect, rule.evidence_ref, rule.resource_type, rule.resource_id,
               0, 0, 0, 0
        FROM rules rule
        JOIN ancestors ancestor ON public.project_identity_key(rule.project_id) = ancestor.project_key
        WHERE (@tenant IS NULL OR (rule.tenant_id = @tenant AND (@owner IS NULL OR rule.owner_user_id = @owner)))
          AND (rule.principal_id = @principal OR rule.principal_id = '*')
          AND rule.right_key = ANY (@rights)
        UNION ALL
        SELECT 'Revision', revision.project_id, NULL, NULL, 0, NULL, NULL, NULL, NULL, NULL, NULL,
               revision.topology_revision, revision.policy_revision, revision.grant_revision, revision.tag_revision
        FROM project_security_revisions revision
        JOIN ancestors ancestor ON public.project_identity_key(revision.project_id) = ancestor.project_key
        WHERE (@tenant IS NULL OR (revision.tenant_id = @tenant AND (@owner IS NULL OR revision.owner_user_id = @owner)))
        """;

    private sealed class EffectiveRightsAuthorityRow
    {
        public string Kind { get; set; } = string.Empty;
        public string ProjectId { get; set; } = string.Empty;
        public string? ChildProjectId { get; set; }
        public string? Dimension { get; set; }
        public long Revision { get; set; }
        public string? PrincipalId { get; set; }
        public string? Right { get; set; }
        public string? Effect { get; set; }
        public string? EvidenceRef { get; set; }
        public string? ResourceType { get; set; }
        public string? ResourceId { get; set; }
        public long TopologyRevision { get; set; }
        public long PolicyRevision { get; set; }
        public long GrantRevision { get; set; }
        public long TagRevision { get; set; }
    }
}
