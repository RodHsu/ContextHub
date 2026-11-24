# Conversation exception adjudication

Owner/Admin users can resolve historical conversation exceptions through the general REST or MCP surface. This workflow changes the exception disposition only; it never executes a proposal or deletes, merges, or rewrites its target memory. Scheduled Governance keeps its separate four-tool catalog and cannot call adjudication, including with an admin role.

## Preview and apply

1. Call `conversation_adjudication_preview` with an explicit `projectId`, `offset` and `limit` (maximum 200), or `GET /api/conversations/adjudication-preview`. Preview is read-only. Each page and its scoped counts use a single database snapshot.
2. Inspect the insight with `conversation_insight_status` and any linked resources. The preview returns an `evidenceToken`, semantic `evidenceFingerprint`, classification, and whether reconciliation or reopening is available.
3. Call `conversation_insight_adjudicate` or `POST /api/conversations/adjudications` with `projectId`, `insightId`, a new UUID `operationId`, `action`, `expectedEvidenceToken`, a nonempty `reason` and `governanceRunId`. A run ID is an audit correlation label; it does not grant execution authority.
4. Retain the returned receipt and read a fresh preview. An identical retry with the same operation ID returns the original receipt with `isReplay=true`. Reusing the ID with a different request fails. The service retries the identical request once after PostgreSQL confirms serialization failure in a transaction the service owns. If the conflict still reaches the caller, reuse the identical request. Refresh the preview before making a different decision.

| Action | Requirement | Result |
| --- | --- | --- |
| `ResolveSkipped` | Non-proposal `RequiresUserDecision`, `Deferred`, or `Failed` exception | `Skipped`, `ResolvedSkipped` |
| `RejectProposal` | Proposal-backed exception in one of those states | `Skipped`, `ResolvedRejected`; target unchanged |
| `Reconcile` | Open exception with a verifiable terminal resource link | `Skipped` with an evidence-specific outcome |
| `Reopen` | Prior terminal adjudication and changed semantic evidence | `RequiresUserDecision`; never automatically `Pending` |

REST follows the server's standard enum serialization. Clients using numeric enums can use `ResolveSkipped=0`, `RejectProposal=1`, `Reconcile=2`, and `Reopen=3`. MCP clients should follow their freshly published request schema.

Reconciliation accepts an already archived target when a `memory_archive` or `memory_delete` proposal binds it through `metadata.payload.id/projectId` to the same tenant, owner, and project. An archive proposal records `ResolvedAlreadyArchived` when `archived` is true or omitted. The same tool with `archived=false` requests restoration and needs separate evidence. A delete proposal records `ResolvedWithoutDelete`. Both receipts record the exception disposition and leave the target untouched. A rejected or applied linked proposal requires both `metadata.proposalId` and an exact `sourceRef=chatgpt-proposal:<UUID>`; applied proposals additionally require the matching promoted memory. Free-text references, approximate title matches, and self-references remain manual decisions. Existing pending proposals continue through the ordinary proposal workflow.

The apply transaction protects the bound memory and linked proposal rows until the receipt commits. If another operation changes those rows after preview, obtain a fresh preview before deciding again.

Preview, apply and receipt lookup each own their transaction and require a clean DbContext. This keeps unrelated pending writes with their caller.

An exact linked proposal with `Failed` status appears as `ProposalApplyFailed` when there is no independent proof for reconciliation. Approval can record a failure after releasing control to the target operation, so verify the target outcome before making a decision. An exact link to a `HostBlocked` proposal blocks adjudication of the referring exception, including explicit skip and reopen.

HostBlocked, legal/security holds, and malformed protection metadata fail closed. Read and write require the corresponding memory scope, an actual Owner/Admin role, and the same tenant, owner, and authorized project. Service actors and scheduled-scoped actors are rejected. Existing skip, host, deletion, score and retention policies remain unchanged.

Preview and receipt lookup check the current durable project read grant. Adjudication and exact replay check the current durable read and write grants, including when an existing token still lists the project.

## Evidence and accounting

If the apply response is lost, look up the receipt using the original complete adjudication request. Send it as the body of `POST /api/conversations/adjudications/receipt-lookup` or as `receiptRequest` on `conversation_adjudication_preview` for the same project. The lookup uses Owner/Admin and `memory:read` permission. It checks the exact operation, insight and original request within the current tenant, owner and project. Use `receiptLookup.found` on MCP or `found` on REST to check for a matching committed receipt. Treat a missing result as an unresolved outcome. The receipt describes the disposition at the time of the decision. Use the current preview or insight status for the present disposition.

Migration 052 adds an append-only `conversation_adjudications` ledger. The disposition update, receipt and security audit event commit in one serializable transaction. The ledger stores hashes, actor, reason, prior/result status and run correlation. A trigger rejects updates/deletes, and the insight foreign key prevents deletion of retained adjudication evidence. Migration does not relabel existing data. Reverting application code leaves the additive ledger in place; do not drop it to roll back a release.

Dry-run preview supports historical backfill without automatic mutation. Apply decisions individually with their preview tokens; ambiguous links stay unresolved. `unresolvedHumanDecisionCount` counts current RequiresUserDecision records; `resolvedHumanDecisionCount` counts terminal records with adjudication evidence. Reopening removes the record from the resolved count. Manually adjudicated records are excluded from automatic exception-to-Pending reopening. After an explicit promotion retry, a `Pending` record with a retained receipt remains visible for accounting. Its linked evidence can still be classified, and the preview sets `canReconcile` to `false` until the record returns to an open governance exception status.

For a historical inventory, set `includeInventoryToken=true` on the first page. Send the returned `inventoryToken` as `expectedInventoryToken` on each following page, keeping the project and optional `governanceRunId` fixed. Stop at `hasMore=false`. A changed candidate set, linked evidence, disposition, or scoped counter invalidates the token. REST returns HTTP 409 and MCP returns a tool error. Restart from offset zero if this happens. The token covers the full inventory, so requesting it adds work to each page. Ordinary previews can omit it. Applying a decision still requires the individual candidate's `evidenceToken`.

An optional preview `governanceRunId` scopes `newlyResolvedCount` and `reopenedCount` to committed ledger transitions in that run. Omitting it counts all retained adjudication history. `unchangedCount` counts current candidates with no transition in that interval; inventory counts always describe current state. A record resolved and subsequently reopened contributes one event to each transition count, and only to the current unresolved inventory. Exact retries never add events.

General `knowledge_review` also exposes current `unresolvedHumanDecisionCount` and `resolvedHumanDecisionCount`, plus `newlyResolvedHumanDecisionCount` and `reopenedHumanDecisionCount` for its run ID. Receipt exception deltas continue to report unchanged exceptions. Resolved ledger records are absent from the active exception inventory.

Review exception identity uses the underlying finding/insight ID, excluding refresh timestamps. Legacy receipt keys are normalized during comparison without rewriting old receipts. Semantic changes appear as escalations, while timestamp-only refreshes remain unchanged. Execution cursors remain version-bound. Unknown actions such as `NeedsReview` count as human decisions, not executable actions. A general execution candidate still requires the executor's current policy checks; this count is not Scheduled execution authorization.

This source publishes general catalog `2026-11-22-v13` with 89 tools. Deployment and fresh authenticated host acceptance are separate from local tests.
