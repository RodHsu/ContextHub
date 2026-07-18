# ContextHub MCP Usage Guide

This guide explains how agents and developers should use ContextHub through MCP.

ContextHub is not a prompt dump. It is a structured knowledge system that lets an agent retrieve and write durable context when that context has real future value.

## Endpoints

| Endpoint | Audience | Purpose |
| --- | --- | --- |
| `/mcp` | trusted coding agents and internal automation | Full ContextHub MCP tools |
| `/mcp-chat` | chat agents and custom assistants | Restricted gateway with OAuth/OIDC, allowlists, audit, rate limits, and proposal-gated writes |

Use your own hostname in remote configurations:

```text
https://context-hub.example.com/mcp
https://context-hub.example.com/mcp-chat
```

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
| `conversation_insights_list` | Reviewing staged conversation insights and promotion state |
| `project_hierarchy_set_children` | Maintaining the child repositories managed by one parent ProjectId; this does not change ACLs |
| `project_hierarchy_get_children` | Reading the configured child repositories for one parent ProjectId |
| `discussion_thread_create` | Starting a persistent, participant-scoped discussion hosted by a target project |
| `discussion_threads_list` | Polling discussion threads visible to one participant ProjectId |
| `discussion_thread_get` | Reading one discussion and marking it read for that participant project |
| `discussion_message_create` | Replying as a participant ProjectId with write access |
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

## Cross-Project Discussions

Use discussions for questions and replies that must remain separate from durable knowledge, memory, and conversation-checkpoint promotion. A discussion has one `hostProjectId` that identifies the main repo under discussion and an explicit participant list.

For example, A can open a thread hosted by C with participants A and C. A separate thread hosted by C can contain A, B, and C. Only listed participant projects can list, read, or reply to that thread. The caller needs read access to every participant at creation time and write access only to its sender project, so an agent from A can open a discussion about C without receiving write access to C.

Configure parent-to-child repo structure independently with `project_hierarchy_set_children`. It is organizational metadata only: it never grants token access, copies memories, or silently adds children to a discussion.

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

Expected unauthenticated behavior:

- `/mcp` returns `401`
- `/mcp-chat` returns `401`
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
