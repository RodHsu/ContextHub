# Platform Wave 7A breaking cutover

Wave 7A is the release-candidate cutover from legacy Artifact/ObjectRef exchange to ContextHub logical Managed Files. It does not deploy or mutate Production, and it does not claim Production E2E or external-host acceptance.

## Contract boundary

- Artifact exchange supports only `Summary`, `Snippet`, and `FileReference`.
- A `FileReference` carries both `FileId` and `FileVersionId`. ContextHub revalidates the active file, project ownership, version relationship, actor scope, and current metadata authorization before publish or read.
- `managed_file_register`, `managed_files_list`, and `managed_files_search` are the high-level MCP replacements. Callers never receive provider identity, storage locator, direct URL, managed-object identity, wrapped key, or other crypto material.
- Artifact metadata recursively rejects provider/storage locator property names. Persisted legacy rows that cannot satisfy the logical contract are not returned.
- The General catalog is `2026-09-27-v8`. AgentExecution and Skills retain their existing surfaces. Scheduled Governance remains an independent exactly-four-tool surface.

## Re-inventoried legacy consumers

The Wave 7A baseline was scanned again; the earlier zero-unmanaged-consumer conclusion was not reused as an assumption. Active repository consumers existed in the Application contracts/service, ChatGPT proposal service, direct and restricted MCP tools, canonical catalog, governance projection, Dashboard catalog text, storage adapter/configuration, and API/ChatGPT/integration/unit tests. Wave 7A removes or replaces each of those dependencies. Documentation from earlier waves is retained only as marked historical evidence.

Unknown external cached clients cannot be proven absent from repository evidence. The catalog bump is therefore deliberately breaking and requires external-host reacceptance in the later authorized release wave.

## Migration 051

`051_legacy_artifact_managed_file_cutover.sql` runs transactionally and keeps rollback-only evidence in the restricted `audit.legacy_artifact_cutover_mappings` table.

- An existing logical ID pair is accepted only when the version belongs to that project file.
- A legacy content hash maps only when exactly one same-owner, same-tenant, same-project FileVersion matches.
- More than one candidate aborts the migration. No arbitrary winner is selected.
- Mapped rows become logical `FileReference` records and provider locator data is removed.
- Legacy external, malformed, or unmapped references are archived rather than guessed.
- Active Summary/Snippet metadata is recursively stripped of provider locator fields.
- A database constraint prevents unsafe active artifact metadata from being written after cutover.

Replay is idempotent through `schema_migrations`; the rehearsal applies migrations through 050, seeds production-shaped legacy rows, applies 051, verifies public opacity and audit evidence, rehearses rollback in a transaction, and replays cleanly. A separate rehearsal proves an ambiguous mapping aborts and rolls back every 051 mutation.

## Release and rollback runbook

Before any later authorized deployment:

1. Confirm the candidate commit is contained in `origin/main`, and record the immutable application image digest.
2. Take and verify a restorable PostgreSQL backup. Independently verify managed-object ciphertext and every still-required KEK version; database-only recovery is insufficient.
3. Run the same legacy inventory query against the target database. Stop on ambiguous mapping, active unmanaged consumer, provider leakage, or missing rollback authority.
4. Quiesce writes, apply migration 051 transactionally, then validate the constraint, mapping counts, archived counts, catalog version, and provider-opacity probes before reopening writes.

Rollback must use the paired pre-cutover application image and database backup. If a transaction-local data restoration is required for rehearsal, first remove the post-cutover public-contract constraint, restore `old_metadata_text` and `old_status` from the restricted audit table, and restore the previous schema/application as one coordinated operation. Never expose the audit table through API, MCP, UI, logs, telemetry, or agents. If ciphertext, KEK, database, image, or mapping evidence is incomplete, stop rather than attempt a partial rollback.

## Wave 7A acceptance

- Migration upgrade, replay, ambiguity failure, and rollback rehearsal pass against real PostgreSQL.
- API and MCP black-box responses contain only logical IDs and approved metadata.
- General catalog/schema parity passes; legacy tools are absent.
- Dashboard inventory totals and grouped dimensions are collected from one repeatable-read database snapshot.
- AgentExecution claim preserves bounded aging and skill revalidation while loading full queue payloads only for capability-matched candidates.
- Scheduled Governance remains exactly four tools; AgentExecution and Skills regression suites remain present.
- Full solution tests complete with zero failed and zero skipped, formatting and Compose configuration pass, and security review has no unresolved blocker.

These checks establish only a release candidate. Production deployment, Production database/runtime/catalog mutation, Production E2E, and external-host acceptance require a separately authorized wave.
