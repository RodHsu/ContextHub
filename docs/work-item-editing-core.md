# Work item editing core — staged source

This change adds the actual Application command/aggregate core and PostgreSQL store. It is a source foundation, not a completed public editor cutover. Existing REST/MCP/proposal/governance writers are not routed through it in this phase. Do not deploy this phase alone or infer end-to-end CAS from the additional read revision. All writer adapters, public contract/host acceptance and database acceptance are separate required gates before rollout.

## Intent and original version

WorkItemEditCommand requires an explicit regular project, target, positive original-read revision, bounded request identity, operation and typed patch. Missing original revision rejects; the server does not fill it with a current version. The production read DTO preserves an omitted revision as unknown/null; only an actual mapped row supplies the database baseline. Migration baseline1 is not reconstructed history.

The normalized WorkItemEditIntent/v1 fingerprint binds canonical project identity, work item, original revision, operation, field presence, leaf identities/order and effective values. RequestId is a separate scoped ledger key. DueAt has explicit Keep/Clear/Set intent. The same scoped key with a different fingerprint conflicts. Read-only receipt recovery requires the pending intent's original fingerprint/version; it cannot mistake an old result for a different reused-key intent.

## Aggregate and commit

The production aggregate helper validates a candidate before changing tracked objects. It preserves stable child identities, advances revision/time once per real logical edit and retains no-op revision/time. Completed uncheck or a new incomplete leaf requires explicit reopen. Reopen changes Completed to InProgress; before/after history retains the previous completion status/time. Valid metadata/text/reorder/removal do not automatically reopen a completed work item. No earned units or progress baseline is recalculated here.

The PostgreSQL store uses a fresh context and one ReadCommitted transaction, with a transaction-scoped advisory lock keyed by tenant+work item. A query after a contended lock sees a fresh snapshot, so a duplicate can find the first committed receipt. Scope is tenant plus owner or existing service-actor scope and canonical project; the Application service checks current actor scopes/project before dispatch. The store checks the receipt fingerprint before comparing the original revision. Parent revision is an EF concurrency token; new child IDs are explicitly Added/INSERT rather than left to relationship discovery, and removed children are explicitly deleted in the same transaction.

A changed aggregate, immutable before/after history, immutable scoped receipt and transactional outbox event are saved/committed together. A no-op persists only its receipt. An apply result is returned after this store's commit completes. Cancellation/transport failure at commit is unknown until exact same-request recovery; it does not cause automatic replay. Receipt result is the original committed result, not current-state or signed-proof evidence.

Current ambient transactions are explicitly rejected by this foundation. Proposal/governance composition must establish one commit owner or an explicit staged-result protocol in the adapter phase; nesting this store and calling a staged receipt committed would be incorrect. This dependency is not bypassed by using a second independent commit for a proposal.

## Migration and validation boundaries

Embedded migration059 is additive: positive parent revision, scoped unique receipt key, unique work-item revision history, step/identity/hash checks and append-only history/receipt triggers. Apply it only through the normal approved migration path. EnsureCreated/model metadata does not install the SQL append-only triggers and is not equivalent acceptance. The source change has not executed this migration or any database query.

Pure-helper, actor-dispatch, actual EF model/query translation and change-tracker tests can run without a connection. They do not establish PostgreSQL advisory-lock behavior, uniqueness/collision isolation, rollback/commit cancellation or trigger enforcement. Real database transaction/concurrency/restart evidence remains required and unexecuted under the current resource gate.

## Rollout and rollback

First finish all writer adapters and legacy rejection/read-reload flow, then verify schemas/host publication, migration readiness and actual database invariants. Existing immutable governance snapshot/execute/Natural gates are not weakened; old snapshots without an original version cannot be upgraded by reading latest. No new surface is enabled by this source package.

If rollout is incomplete, keep versioned edit entry points unavailable. Rollback preserves additive revision/history/receipt data and uses compatible writers or read-only editing; it must not drop evidence or return to blind overwrites after guarded edits have occurred. A destructive down migration is deliberately not supplied.
