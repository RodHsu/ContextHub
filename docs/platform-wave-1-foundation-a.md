# Platform Wave 1 — Foundation A

## Authority and scope

This foundation implements the frozen topology, authorization, revision, and canonical-tag primitives for Work Item `c4d248ab-d392-4b5c-a7eb-7f36ffaf6169`. It does not introduce Foundation B storage, Managed Files, Secrets, a tag-governance dashboard, or any breaking Artifact API change.

## Topology and authorization contract

- `project_hierarchies` remains a multi-parent DAG. `dimension` distinguishes existing discussion relationships from future dimensions.
- Only an edge with `authorization_inheritable = true` participates in authorization. Migration `045` marks every existing edge as `dimension = discussion` and non-inheritable, so hierarchy meaning is never inferred from a project name or an existing relation.
- Mutations use monotonically increasing revisions and reject stale expected revisions. A cycle causes evaluation to fail closed.
- Every legal authorization-inheritable parent path participates. For each requested right, the nearest ancestor with a defined rule on each path is selected. Those path results are unioned, with Deny winning within the inherited tier.
- A target Project rule replaces inherited results for that right. A Resource/File rule replaces the target Project result. Rights without any applicable rule are denied.
- `PolicyDerived` rules are input to every evaluation and are not materialized as effective rights. `ExplicitGrant` is persisted independently. Every decision includes its selected tier, evidence references, and an explanation.
- Break-glass is deliberately not represented as an ordinary rule source. It remains a separate high-risk authority path.

## Correctness model

`ProjectSecurityRevision` separates topology, policy, explicit-grant, and tag revisions. Effective-right cache keys contain the complete revision vector. Mutators must perform expected-revision checks, increment only the affected revision, then precisely invalidate affected project keys. Request-time evaluation rejects invalid revision values and cycles. A full reconciliation may detect drift, repair projections, or prewarm caches; it is never an authorization source.

## Canonical tag base

The persisted base consists of `CanonicalTagDefinition`, `CanonicalTagAlias`, `CanonicalTagRelation`, `CanonicalTagBinding`, and `CanonicalTagSuggestion`.

- Definitions and aliases have normalized, project-scoped uniqueness; concurrent alias collisions fail at the database boundary.
- Suggestions are a separate review object and cannot directly create a canonical definition.
- Resolution supports canonical, alias, normalized lexical, and semantic-candidate scoring.
- Authorized resource keys are applied as a hard filter before binding ranking. Unauthorized bindings are never returned, including through counts.
- Tag results contain no authorization decision and cannot grant a right.

## Production topology migration dry-run

Read-only inventory on 2026-09-21 covered all 27 accessible projects and 18 configured relationships. The graph contains the existing `Vital -> Vital_AirMeet -> component` paths, direct `Vital -> component` paths, and `WJCY -> WJCY_*` relationships. It has no cycle or dangling edge in the accessible project set.

The migration result is deterministic and non-destructive:

- 18 existing relationships remain present.
- 18 become `dimension = discussion`, `authorization_inheritable = false`, `revision = 1`.
- 0 are inferred as authorization-inheritable.
- Existing multi-parent relationships remain intact.
- No project-name heuristic, relation deletion, or authority inference is performed.

Any future decision to make one of those relations authorization-inheritable requires an explicit, revision-checked mutation and cycle validation.
