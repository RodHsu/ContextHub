# Immutable memory manifest

The authenticated general MCP tools `memory_manifest_read` and `memory_manifest_verify` share the same application service as `POST /api/memories/manifest/read` and `POST /api/memories/manifest/verify`. They require `memory:read` and the existing actor, tenant, owner and project authorization. The dedicated Scheduled Governance catalog remains exactly four tools.

Start with an explicit non-default `projectId` and a caller-generated UUID `requestId`. By default the service freezes all authorized governance projects, including hidden, archived, default and legacy projects, plus Shared exactly once. User-project preferences stay on their independent surface. Set `allAuthorizedProjects=false` to freeze the context project and explicit `includedProjectIds`, plus Shared. The same resolver and durable grants apply to both modes.

```json
{
  "projectId": "ExampleProject",
  "requestId": "11111111-1111-4111-8111-111111111111",
  "pageSize": 100,
  "allAuthorizedProjects": true
}
```

The first request materializes the authorized metadata projection in one database statement and persists it in the existing snapshot JSON envelope. No schema migration or new permission is required. The reserved storage key begins with `metadata-manifest:` and exceeds the existing 128-character governance Run ID limit; it identifies a read snapshot, **not a governance run**. This also makes legacy release/compatible rollback APIs reject it before execution, even when they ignore the new JSON member. New governance readers/executors and tracker exclusions additionally reject the manifest namespace/envelope. No review, Analyze, retention classification, finding/proposal creation, execution, archive, merge, delete or indexing is invoked. Existing snapshot retention policy remains unchanged; cursor expiry does not delete data. Formal compatible rollback/replay acceptance remains a separate release gate.

The response includes `snapshotId`, creation and fixed 24-hour expiry, scope contract version, opaque actor/access scope identity, authoritative total, unique count, count invariant, partition counts, position, bounded items, `endOfSnapshot` and `nextCursor`. Pages contain at most 100 records. Entries contain ID, ProjectId, type, scope, status, version, update time, read-only state, available revision ID/identity, classification, a bounded source-type classification and source-reference presence. They omit title, content, summary, raw source references, raw metadata, provider locators, payload hashes and logs. Unknown source types return `Other`; project-information records distinguish body-bearing system metadata and non-retrieval system metadata.

Continue with the same request fields, `snapshotId` and `nextCursor`. Keep `pageSize` unchanged. The authenticated cursor binds snapshot, position and page size; persisted membership and signing state survive application restart. Retry the **same** `requestId` after an uncertain freeze response, and replay the same cursor after a page timeout. A retry never silently creates a replacement snapshot. Inserts, updates, archival and moves after materialization cannot change its membership, versions, partitions or count.

Every page and verification rechecks current project grants and actor/access identity, including durable grant IDs, permission flags and update times. Revocation followed by restoration or grant recreation invalidates the earlier access identity. Revocation, different tenant/owner, context/scope contract changes, expiry, malformed/tampered cursor, or count invariant failure returns an explicit failure. Freeze refuses a DbContext with unsaved changes (`manifest-context-not-clean`), preserving those changes instead of committing or clearing them. REST exposes manifest failures as HTTP 409 with a stable error code; missing authentication/read scope remains the existing authentication/authorization failure. Expired requests require a new explicit request UUID and reconciliation as a new snapshot.

To verify up to 100 selected members without fetching bodies:

```json
{
  "projectId": "ExampleProject",
  "snapshotId": "22222222-2222-4222-8222-222222222222",
  "memoryIds": ["33333333-3333-4333-8333-333333333333"]
}
```

Verification returns `Unchanged`, `Changed`, or `RemovedOrMoved` and the available current version. This is a metadata comparison, not a body hash attestation. Revision identity uses the existing revision ID when available and the memory ID/version contract; no sensitive payload digest is produced. A subsequent allowed `memory_get` must independently compare its actual returned version with the manifest entry because a write can occur after verification. Do not count a mismatching current body as review of a frozen version. The existing version/history work owns historical body reads.

Check the stop/cancellation registry **before** any body request. Neither manifest operation fetches a body, retries a stopped body request, or resumes a terminal governance run. For reconciliation, preserve separate unchanged/reusable, changed, new, removed, unread and stopped sets. Review reuse needs established ID/version/revision evidence; a manifest alone does not complete content review.

A historical snapshot without saved members returns `legacy-manifest-unavailable`; live memory listing or export cannot reconstruct its original membership. Enumerate every page of a **new** snapshot, prove unique IDs equal the authoritative total and independently reconcile each partition. Repository tests and server responses are separate from published connector catalog and fresh ChatGPT-host full-page acceptance. None of these results substitutes for NaturalSchedule provenance or controlled Production acceptance.
