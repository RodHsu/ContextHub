# WorkItem proposal dispatch and recovery — staged source

This package connects the existing WorkItem archive/restore proposal path to original-read edits. It does not complete the governance executor's immutable WorkItem preconditions, Memory CAS, structural checklist UI or durable Dashboard recovery custody. Do not deploy the incomplete SafeEditing chain alone.

New WorkItem proposals require the caller's positive original revision. A supplied request identity is preserved; a missing identity is assigned only during creation and saved in the canonical payload/content. Governance-run creation derives a stable identity from tenant, actor, run and canonical original intent, retaining creation deduplication. Approve never reads a current revision to fill an old payload or generates a replacement identity. A legacy proposal missing its original version/key remains Pending and fails validation instead of becoming Failed or silently overwriting data.

## Separate commit owners

The coordinator uses a fresh PostgreSQL context, ReadCommitted and a transaction advisory lock scoped to tenant+proposal and an authorized proposal row lock before reading its intent. The row lock also serializes ordinary insight writers during marker creation; afterward the SQL guard freezes the original identity, tool, tags and payload. Proposal authority remains strict tenant+owner, including service actors. The actual target separately uses existing WorkItem owner/service-actor/project authority. Initial target lookup selects only its identity, not a revision. The current Application actor/scopes/project remain mandatory; recovery additionally requires existing MemoryRead and current resource authority.

The first approved attempt commits an immutable original-intent `Dispatched` marker before returning permission to invoke the edit core. Concurrent or later approvals only recover that same request. The core continues to own its revision/history/receipt/outbox transaction. The coordinator rejects ambient transaction composition; it never returns an outer staged result as a committed edit.

Finalize starts another owned transaction and reads the actual immutable core receipt by actor/project/target/key/hash/receipt ID. It binds the original proposal payload and marker, verifies the complete canonical receipt rather than trusting a passed result DTO, then commits proposal Applied status, its lifecycle audit and the terminal coordination row together. Repeated finalization validates the same receipt and does not add another audit or mutation. This is an explicit recoverable workflow, not one atomic transaction spanning proposal and core.

A definite core conflict is recorded as a terminal conflict while the proposal remains Pending; repeated approval cannot dispatch again. Once the marker exists, generic transport/cancellation/finalization errors preserve the original request and uncertain result. A commit followed by a failed proposal save can be recovered by reading the original receipt and finalizing it without another apply.

**Recovery limitation:** if the marker commits and the process stops before calling the core, receipt lookup can remain empty indefinitely. Absence is not authoritative proof that the edit never arrived. This conservative version does not redispatch or reset the marker, so it does not promise unattended completion of that failure window. It requires further authoritative diagnosis/approved recovery; a new request must not masquerade as the old receipt.

Reject uses the same lock. An undispatched or definitively conflicted Pending proposal may be rejected with its original note/history. A dispatched uncertain attempt cannot be relabeled Rejected. No ordinary business WorkItem is changed by rejection.

## Boundaries and rollout

REST/MCP decisions retain the existing request shape. Known edit conflicts expose safe machine codes; private authorization/resource errors are opaque, and unconfirmed outcomes use a generic error without SQL or stored payload details. No new role, tool, public receipt/proof schema or Scheduled Governance gate is introduced.

Migration060 is additive source only. The coordination table fixes original scope/version/key/hash and terminal state, prevents evidence deletion/reset, and retains RESTRICT links to proposal, WorkItem and core receipt. Existing proposals are not backfilled from latest data. Rollback disables new dispatch/finalization entry points while retaining coordination/history/receipts; dropping evidence or returning to blind writers is not a rollback strategy. Retention/export and protected system-metadata coverage of this new table still need explicit integration acceptance.

Source tests exercise the real proposal service/workflow and HTTP/MCP boundaries using controlled dependencies, and inspect the actual EF model/query helpers and migration source. These do not execute PostgreSQL locks, constraints/triggers, interruption recovery, process races or migrations. EnsureCreated/model metadata is not equivalent to the SQL guard. Real database and host/Production acceptance remain unexecuted.

The governance WorkItem executor is a separate dependency: old review snapshots lack immutable original WorkItem revisions and must not gain them from execution-time reads. This package does not add or activate the absent `CompletedHistoricalWorkItem` candidate or weaken coverage, fixed-executor, snapshot, NaturalSchedule or five-state evidence requirements.
