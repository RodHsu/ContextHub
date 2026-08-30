# ContextHub MCP Usage Guide

This guide explains how agents and developers should use ContextHub through MCP.

ContextHub is not a prompt dump. It is a structured knowledge system that lets an agent retrieve and write durable context when that context has real future value.

## Endpoints

| Endpoint | Audience | Purpose |
| --- | --- | --- |
| `/mcp` | trusted coding agents and internal automation | Full ContextHub MCP tools |
| `/mcp-chat` | chat agents and custom assistants | Restricted gateway with OAuth/OIDC, allowlists, audit, rate limits, and proposal-gated writes |
| `/mcp-automation` | Scheduled Governance Automation only | Four-tool least-privilege gateway with full server-side review, fixed reversible execution, and receipt recovery; no irreversible deletion authority |

Use your own hostname in remote configurations:

```text
https://context-hub.example.com/mcp
https://context-hub.example.com/mcp-chat
```

Scheduled Governance uses a separate connector URL and must never fall back to the general connector:

```text
https://context-hub.example.com/mcp-automation
```

Its OAuth token must include `governance:scheduled`. The connector exposes only `scheduled_governance_contract_get`, `scheduled_governance_review`, `scheduled_governance_execute`, and `scheduled_governance_run_get`. Call review with a fresh `governanceRunId`; obey the returned server decision. Call execute only for `ReversibleExecutionRequired`, replay the exact request after an unknown outcome, and use receipt read-back before retrying. `NoOpConverged` is a valid successful run with zero mutation. `HumanDecisionOnly` and `CoverageIncomplete` stop unattended execution. Never switch to `/mcp-chat`, explicit ProjectIds, REST, DB, or admin endpoints to bypass a stop decision.

## Protocol Lifecycle

ContextHub targets MCP `2026-07-28` on both endpoints. The HTTP transport is stateless:

- modern clients may call `server/discover` and then send self-describing requests
- every modern request carries the protocol version and client capabilities in `_meta`
- HTTP requests carry `MCP-Protocol-Version` and `Mcp-Method`; named calls such as `tools/call` also carry `Mcp-Name`
- the server does not issue or require `Mcp-Session-Id`
- a response may still stream within one request; stateless does not mean that response streaming is disabled

The same endpoints retain legacy Streamable HTTP compatibility. A legacy client may send `initialize` and then call tools, but it must not require a server-issued session id. Clients whose implementation treats `Mcp-Session-Id` as mandatory must upgrade or use the maintained ContextHub stdio bridge, which negotiates modern MCP first and falls back to legacy initialization when needed.

Application-level ContextHub conversation sessions are durable product data and are independent of the removed MCP transport-session concept.

The specification's twelve-month retention rule applies to entries in its deprecated-features registry; it is not a blanket guarantee that the removed transport-session lifecycle remains supported for twelve months. ContextHub's legacy fallback is therefore an explicit migration runway. Remove it only after managed-client inventory and legacy probes show no remaining dependency.

### Protocol Validation

The automated suite covers modern discovery, per-request metadata and routing headers, version mismatch rejection, stateless tool calls, legacy initialization without a transport session, bridge fallback, retry safety, and authentication on every request.

See the complete requirement mapping and security evidence in [MCP 2026-07-28 compliance](mcp-2026-07-28-compliance.md).

The official conformance package can additionally exercise the isolated stateless lifecycle. Run it only against a local endpoint with authentication deliberately disabled; never expose an unauthenticated conformance target publicly:

```powershell
npx --yes @modelcontextprotocol/conformance@0.2.0-alpha.11 server `
  --url http://localhost:8092/mcp `
  --scenario server-stateless `
  --spec-version 2026-07-28
```

Pin the package version in CI and review upgrades. The full conformance requirements suite includes fixture-specific tools and content types that are intended for SDK conformance servers, so ContextHub uses the applicable transport scenario plus its own public-contract black-box tests.

### Protocol Negotiation

Both endpoints use the same stateless Streamable HTTP protocol boundary and support the legacy initialize era as well as MCP `2026-07-28` per-request metadata. A `2026-07-28` request must carry matching protocol declarations in the `MCP-Protocol-Version` header and `_meta/io.modelcontextprotocol/protocolVersion`, plus per-request client capabilities. Header, initialize, and metadata version conflicts fail closed.

During the ChatGPT transition period, ContextHub normalizes one version-limited compatibility shape: when the effective protocol is a supported pre-`2026-07-28` revision, the transport adapter removes only the three newer reserved per-request metadata fields before the validated MCP core receives the request. It also discards a stale legacy `Mcp-Session-Id` after a stateless server upgrade. Ordinary `_meta` fields are preserved, and a request that actually declares conflicting protocol versions is rejected rather than rewritten.

## Core Principles

### Read Context Before Acting

At the start of a new task, call `build_working_context` with an explicit `projectId` and a task-shaped query.

Before a project has useful task history, create its durable description with `project_information_upsert`. The returned `projectInformation` section is always loaded by `build_working_context` before task-specific retrieval, so it provides stable project intent and boundaries without coupling unrelated discussion threads together. Read it directly with `project_information_get` when a client needs only the project background.

Good query:

```text
Update public repo documentation for open-source release readiness.
```

Poor query:

```text
docs
```

### Use Explicit Project IDs

Every repo should have one stable `ProjectId`.

Resolution order:

1. Use the value specified by repo rules or existing project documentation.
2. If none exists, use the repo root directory name.
3. Once established, do not change it without a migration plan.

Avoid writing real work to `default`.

### Write Durable Knowledge Only

Write back:

- accepted architecture decisions
- stable repo rules
- confirmed incident summaries
- verified deployment or test conclusions
- explicit long-term user preferences

Do not write:

- secrets, tokens, private keys, password hashes
- full raw logs
- full raw diffs
- temporary guesses
- brainstorming that has not been accepted
- data that can be trivially read from source code without interpretation

## Common Tools

| Tool | Use When |
| --- | --- |
| `describe_context_hub` | The agent is first discovering what the MCP server does |
| `build_working_context` | Starting a task or recovering project context |
| `memory_search` | Looking for existing facts, decisions, artifacts, or episodes |
| `memory_get` | Reading one known memory item in full |
| `memory_upsert` | Creating or replacing stable durable knowledge |
| `memory_update` | Correcting or extending existing durable knowledge |
| `memory_archive` | Archiving or restoring one memory item while preserving the item |
| `memory_restore` | Restoring one archived memory item |
| `memory_move` | Moving one memory item to another ProjectId after access and duplicate-key checks |
| `memory_delete` | Permanently deleting one confirmed low-value or already-migrated memory item |
| `project_cleanup_preview` | Previewing safe cleanup candidates for one ProjectId before bulk cleanup |
| `project_cleanup_apply` | Archiving or deleting selected safe cleanup candidates after preview |
| `conversation_ingest` | Saving a concise checkpoint for future task continuity |
| `conversation_sessions_list` | Auditing staged conversation sessions |
| `conversation_insights_list` | Reviewing staged conversation insights and promotion state with offset pagination |
| `conversation_insight_status` / `conversation_insight_retry` / `conversation_insight_skip` / `conversation_insight_set_disposition` | Inspecting and converging a staged insight. `set_disposition` records audited `Deferred`, `RequiresUserDecision`, or `HostBlocked` exceptions; exception states are not automatically retried and may be manually retried later. |
| `project_information_get` | Reading fixed project background before task-specific retrieval |
| `project_information_upsert` | Creating or correcting the durable description for one ProjectId; agent and background callers cannot change the Dashboard-owned `DisplayName` |
| `project_information_update_lifecycle` | Hiding, unhiding, archiving, or restoring a project on trusted `/mcp`; archiving excludes it from default retrieval |
| `project_hierarchy_set_children` | Maintaining the child repositories managed by one parent ProjectId; this does not change ACLs; available on `/mcp` and `/mcp-chat` |
| `project_hierarchy_get_children` | Reading the configured child repositories for one parent ProjectId; available on `/mcp` and `/mcp-chat` |
| `discussion_thread_create` | Starting a persistent, participant-scoped discussion hosted by a target project; available on `/mcp` and `/mcp-chat` |
| `discussion_threads_list` | Polling discussion threads visible to one participant ProjectId; available on `/mcp` and `/mcp-chat` |
| `discussion_thread_get` | Reading one discussion for a participant project without changing its read cursor; available on `/mcp` and `/mcp-chat` |
| `discussion_thread_close` | Closing a discussion when the caller has write access to its `hostProjectId`; closed history is retained and new replies are rejected; available on `/mcp` and `/mcp-chat` |
| `discussion_thread_archive` / `discussion_thread_restore` | Hiding or restoring a discussion without changing its `Open` or `Closed` status; archived discussions reject mutations and are excluded from default lists; available on `/mcp` and `/mcp-chat` |
| `discussion_message_create` | Posting to an open discussion as an authorized participant; available on `/mcp` and `/mcp-chat` |
| `project_work_item_create` | Creating a user-managed project task with optional tags, priority, due date, and checklist; available on `/mcp` and `/mcp-chat` |
| `project_work_item_update` | Updating a project work item; `Completed` requires every checklist item to be completed first |
| `project_work_item_checklist_update` | Completing or reopening one checklist item so guarded work items can progress to `Completed` |
| `project_work_item_archive` / `project_work_item_restore` | Hiding or restoring a work item without changing its business status; archived work items reject mutations and are excluded from default lists |
| `project_work_items_list` | Listing user-managed work items for one ProjectId; work items are not governance suggested actions |
| `knowledge_review` | Reading an actor-scoped full-coverage governance snapshot through `/mcp-chat`. With `projectIds=null`, the Application-layer scope resolver includes every authorized durable project containing actor-owned rows, including hidden/archived/default, and adds Shared exactly once; an explicit list remains exact. Coverage returns authoritative and covered durable counts, the enforced count invariant, system-metadata/non-retrieval counts, and the resolved project set. Empty Project Information descriptions are valid protected non-retrieval metadata and do not create ordinary invalid-memory or reindex findings. The compact plan covers Project Information, hierarchy, durable memory, preferences, artifacts, discussions, work items, conversation insights, Suggested Actions, proposals, and server-side log partitions. Every surface returns independent totals, scanned/candidate/actionable/exception counts, `hasMore`, and `coverageComplete`; raw log rows and large memory bodies remain server-side. Callers retain one `governanceRunId`, execute archive-first/proposal-first actions, and re-review until `Converged` or `ConvergedWithExceptions`. |
| `governance_contract_get` | Reading the canonical `governance_batch_execute` contract version, schema hash, published catalog version, request fields, and action values shared by `/mcp` and `/mcp-chat`. |
| `governance_batch_execute` | Executing one bounded administrator-only governance batch against a `knowledge_review` snapshot. A versioned cursor is bound to actor, tenant, project scope, execution policy, logical position, and expiry rather than one physical snapshot ID. After same-run re-review, the prior `nextCursor` safely maps to the new snapshot and the durable execution ledger excludes already terminal items. Each completed batch advances the next re-review generation; a repeated re-review without another completed batch replays the same immutable generation. Exact execution replay returns the original result and audit IDs even after newer generations exist. Scheduled direct hard-delete is always rejected. External `MaturedDelete` also fails closed as `HostBlockedMaturedDelete`; the policy-bound ContextHub Worker consumes only the server-side matured queue and revalidates each item before the existing retention service deletes it. Reversible semantic candidates may auto-resolve only above `semanticAutoResolutionConfidenceThreshold` with deterministic authority read-back. Cursor failures return stable codes such as `CursorExpired`, `CursorActorMismatch`, `CursorScopeMismatch`, `CursorPolicyMismatch`, `ReReviewRequired`, and `ReplayPayloadMismatch`. |
| `governance_run_get` / `governance_runs_list` | Reading the latest immutable cumulative receipt for one governance run or a bounded actor/project-scoped run list. A batch receipt exposes received/executed status, request/replay identity hashes, result counters, audit IDs, latest snapshot generation, cursor, re-review state, convergence, and stop reason, allowing timeout/502 outcome recovery before an exact replay. Review-only, schema mismatch, host-blocked, stopped/failed execution, and internal retention outcomes remain auditable without returning governed request content. Tenant, owner, and current project ACL checks are reapplied on every read. |
| `governance_tombstone_get` | Reading the minimal immutable tombstone for a permanently deleted resource. The response contains hashes, classification, policy, reason, replacement, governance-run, and audit references but never the original content. |
| `governance_finding_set_disposition` | Auditing a durable-memory finding as `Deferred`, `RequiresUserDecision`, or `HostBlocked`. The persisted exception remains in coverage but is excluded from `actionableItemCount`; equivalent pending suggested actions are superseded. |
| `governance_finding_reopen` | Reopening a durable-memory finding exception for an explicit retry while incrementing its persisted retry count. |
| `daily_memory_review` | Reading a compatibility daily review through `/mcp-chat`; scheduled governance should use `knowledge_review` |
| `chatgpt_governance_proposal_create` | Creating one proposal-gated governance operation per `governanceRunId` and operation payload; retries return the existing proposal |
| `log_search` | Searching recent runtime events |
| `log_read` | Reading a specific log slice or event |
| `promote_log_slice_to_memory` | Turning a verified incident into durable knowledge |
| `user_preference_list` | Inspecting explicit long-term preferences |
| `user_preference_upsert` | Saving a confirmed preference |
| `user_preference_archive` | Retiring a stale preference |
| `project_artifact_publish` | Publishing a project-scoped artifact summary, snippet, file reference, or external object pointer |
| `project_artifact_upload_object` | Uploading managed artifact content and publishing only the expiring object pointer |
| `project_artifacts_list` | Listing project-scoped artifact exchange records |
| `project_artifacts_search` | Searching project-scoped artifact exchange records |
| `project_artifact_get` | Reading one project-scoped artifact exchange record by memory id |
| `project_artifacts_prune_expired_objects` | Pruning expired managed artifact objects and archiving their exchange records |
| `enqueue_reindex` | Rebuilding vectors after embedding model/profile changes |
| `enqueue_summary_refresh` | Rebuilding the read-only shared summary layer |
| `maintenance_status` | Checking maintenance state before long-running work |
| `maintenance_lease_heartbeat` | Reporting active agent work so maintenance can wait |
| `maintenance_lease_complete` | Completing an agent maintenance lease |
| `chatgpt_proposals_list` | Listing ChatGPT gateway write proposals |
| `chatgpt_proposal_approve` | Approving and applying a pending ChatGPT write proposal |
| `chatgpt_proposal_reject` | Rejecting a pending ChatGPT write proposal |

The Dashboard also exposes this catalog in Traditional Chinese at `/mcp-tools`.

The catalog summary is generated from the same canonical policy used by `/mcp-chat`, rather than a second configuration allowlist. In v1.1.86 it reports 66 backend `/mcp` tools and 65 ChatGPT App-facing tools, together with query/read, mutation, delete-capable, and proposal-gated counts. Production smoke validates the final host-compatible projection, including complete non-destructive read-only annotations for `governance_contract_get`, `governance_run_get`, and `governance_runs_list`. App-facing validation accepts JSON Schema outputs rooted at either `object` or `array`, while rejecting scalar roots that the connector cannot project as structured tool results.

## Cross-Project Discussions

Use discussions for questions and replies that must remain separate from durable knowledge, memory, and conversation-checkpoint promotion. A discussion has one `hostProjectId` that identifies the main repo under discussion and an explicit participant list.

For example, A can open a thread hosted by C with participants A and C. A separate thread hosted by C can contain A, B, and C. Only listed participant projects can list, read, or reply to that thread. The caller needs read access to every participant at creation time and write access only to its sender project, so an agent from A can open a discussion about C without receiving write access to C.

Configure parent-to-child repo structure independently with `project_hierarchy_set_children`. It is organizational metadata only: it never grants token access, copies memories, or silently adds children to a discussion.

For `discussion_thread_create`, `discussion_message_create`, and `project_hierarchy_set_children`, pass the contract fields inside the MCP tool's `request` argument, as shown by its tool schema. The same contract is available through both `/mcp` and the OAuth-protected `/mcp-chat` gateway.

Only the host project can close a discussion: `discussion_thread_close` accepts the thread id, validates the caller's write access to that thread's `hostProjectId`, retains the entire discussion history, and prevents further replies.

Archive is a separate lifecycle dimension from close. `discussion_thread_archive` hides the thread from default lists and rejects replies, read-cursor updates, and close mutations until `discussion_thread_restore` is called. Restore preserves the prior `Open` or `Closed` status. Pass `includeArchived: true` to list archived history explicitly.

## Project Information and Work Items

Project information is the durable, fixed background for a `ProjectId`; it is not a scratchpad. Create or update it only when the project purpose, boundaries, or stable operating description is known. `build_working_context` returns that information separately from task-retrieved knowledge.

Use project work items for actionable, user-managed follow-up. They support `Pending`, `InProgress`, `Blocked`, `Completed`, and `Cancelled` states, plus tags, priority, optional due date, and an ordered checklist. Use `project_work_item_checklist_update` to complete checklist entries; a work item can move to `Completed` only when every checklist item is complete. Do not use work items to store an architecture decision or use a discussion thread as a substitute for an auditable task.

Archive is also separate from the work-item status. `project_work_item_archive` hides an item from default lists and freezes updates and checklist mutations until restore; `project_work_item_restore` returns it with the same status it had before archival. Use `includeArchived: true` only for history or restore workflows.

```text
Stable project background -> project_information_*
Question, proposal, or reply -> discussion_thread_* / discussion_message_create
Action with an owner or checklist -> project_work_item_*
Reusable verified fact or decision -> memory_upsert / memory_update
Automated governance candidate -> suggested-action or proposal workflow
```

## Standard Task Lifecycle

```text
New task
  -> build_working_context(projectId, query)
  -> optionally memory_search/log_search for gaps
  -> implement or decide
  -> verify
  -> conversation_ingest checkpoint if reusable
  -> memory_upsert/update only for stable reusable facts or decisions
```

## First MCP Connection

If an agent does not yet know ContextHub's purpose:

```text
describe_context_hub()
resolve projectId
build_working_context(projectId=..., query=...)
```

`describe_context_hub` can be called without `projectId`. It should only bootstrap understanding. Real project reads and writes should use an explicit `projectId`.

## Remote Client Configuration

Generic Streamable HTTP MCP configuration:

```toml
[mcp_servers.contexthub]
enabled = true
url = "https://context-hub.example.com/mcp"
bearer_token_env_var = "CONTEXTHUB_MCP_TOKEN"
```

Local compose example:

```json
{
  "servers": {
    "contextHub": {
      "type": "http",
      "url": "http://localhost:8092/mcp"
    }
  }
}
```

When using bearer tokens:

- store tokens in environment variables or a secret manager
- do not commit tokens to config files
- rotate tokens when sharing access broadly

## Chat-Agent Gateway

For chat-agent integrations, use:

```text
https://context-hub.example.com/mcp-chat
```

The gateway should expose a restricted tool set and should not allow direct durable writes without proposal review.

For remote knowledge-governance automation, `/mcp-chat` provides actor-scoped `projects_list`,
retention classification, conversation-insight and suggested-action reads, and global preference
reads. It never returns `default` from project discovery. Archive, suggested-action accept/dismiss,
and other durable changes create a pending proposal; the existing approval path rechecks scope and
project access before applying it.

OAuth/OIDC discovery endpoints:

```text
/.well-known/oauth-protected-resource/mcp-chat
/.well-known/oauth-authorization-server/mcp-chat
/.well-known/openid-configuration/mcp-chat
/oauth/chat/authorize
/oauth/chat/token
/userinfo
```

Recommended OAuth scopes:

```text
openid profile email offline_access
```

## Diagnostics

Raw full MCP diagnostic:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-contexthub-mcp.ps1 -Endpoint https://context-hub.example.com/mcp
```

Chat-agent gateway diagnostic:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-contexthub-mcp-chat.ps1 -Endpoint https://context-hub.example.com/mcp-chat
```

Authorized chat-agent simulation:

```powershell
$env:CONTEXTHUB_MCP_CHAT_TOKEN = "<access-token>"
powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-contexthub-mcp-chat.ps1 -Endpoint https://context-hub.example.com/mcp-chat -RequireAuthorizationToken
```

Scheduled Governance least-privilege catalog and controlled read-only review:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-contexthub-mcp-chat.ps1 `
  -Surface Automation `
  -Endpoint https://context-hub.example.com/mcp-automation `
  -ResourceMetadataUrl https://context-hub.example.com/.well-known/oauth-protected-resource/mcp-automation `
  -AuthorizationServerMetadataUrl https://context-hub.example.com/.well-known/oauth-authorization-server/mcp-automation `
  -OpenIdConfigurationUrl https://context-hub.example.com/.well-known/openid-configuration/mcp-automation `
  -RequireAuthorizationToken `
  -RunControlledReview
```

Expected unauthenticated behavior:

- `/mcp` returns `401`
- `/mcp-chat` returns `401`
- `/mcp-automation` returns `401`
- neither endpoint returns a browser challenge page
- dynamic responses are not cached

## Operational Checks

ContextHub is useful only when these remain true:

- `build_working_context` returns relevant structured facts, decisions, artifacts, preferences, logs, and citations.
- `memory_search` can find known project decisions.
- `user_preference_list` returns explicit preferences when configured.
- `log_search` can find recent runtime facts.
- write operations create jobs that complete.
- model/profile changes are followed by `enqueue_reindex`.
- `/api/performance/measure` behaves consistently enough to serve as a local performance probe.

## Common Misuse

- Writing every conversation into memory
- Writing user preferences as generic memory
- Starting implementation before reading working context
- Using vague one-word queries
- Forgetting to reindex after embedding changes
- Treating health checks as a substitute for search quality and job status
- Exposing `/mcp` to untrusted chat agents instead of using `/mcp-chat`
