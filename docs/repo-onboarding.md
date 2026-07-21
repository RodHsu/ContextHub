# Repo Onboarding Guide

This guide explains how to connect a repository to ContextHub so coding agents can recover working context across conversations.

It is for maintainers adopting ContextHub in another repo. For MCP tool behavior, see [mcp-usage-guide.md](mcp-usage-guide.md).

## Flow

```text
Choose ProjectId
  -> start or connect to ContextHub
  -> configure MCP client
  -> add repo AGENTS.md
  -> initialize durable project information
  -> begin tasks with build_working_context
  -> route each write to knowledge, shared summary, work item, discussion, or checkpoint
  -> close useful work with conversation_ingest
  -> save only verified, reusable knowledge
```

## Prerequisites

- A running ContextHub deployment
- A stable MCP endpoint, such as `http://localhost:8092/mcp` or `https://context-hub.example.com/mcp`
- A client that supports Streamable HTTP MCP
- A bearer token or equivalent auth mechanism when the endpoint is not local-only

## Start ContextHub Locally

From the ContextHub repo:

```powershell
Copy-Item .env.example .env
docker compose up -d --build
```

Check:

- `http://localhost:8092/health/ready`
- `http://localhost:8092/api/status`
- `http://localhost:8091/login`

## Configure MCP Client

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

Remote example:

```toml
[mcp_servers.contexthub]
enabled = true
url = "https://context-hub.example.com/mcp"
bearer_token_env_var = "CONTEXTHUB_MCP_TOKEN"
```

Keep tokens out of committed files.

## Choose ProjectId

Every repo needs one stable `ProjectId`.

Rules:

1. If the repo already specifies a `ProjectId`, use that value.
2. Otherwise, use the repo root directory name unchanged.
3. Do not change the value later unless you intentionally migrate or rebuild existing knowledge.

All ContextHub MCP calls for that repo should pass the same `projectId`.

## Initialize Project Information

Before the first substantial task, create a concise, durable description for the repository. It is fixed project background, not an activity log or a task list.

```text
project_information_upsert(
  projectId = "<RepoName>",
  displayName = "<Human-readable repository name>",
  description = "Purpose, key boundaries, and stable operating context."
)
```

Then verify the client can read it and that it appears in task context:

```text
project_information_get(projectId = "<RepoName>")
build_working_context(projectId = "<RepoName>", query = "Initialize repository working context")
```

Use a normal repository `ProjectId` for project information and work items. Do not create work items under `shared` or `user`.

## Add AGENTS.md

Place `AGENTS.md` at the repo root.

Minimum template:

```md
# Agent Notes

## ContextHub

- ProjectId: `<RepoName>`
- Before substantive analysis, code changes, or tests, call `build_working_context(projectId = <RepoName>, query = <task>)`.
- All ContextHub reads and writes must pass `projectId = <RepoName>`.
- Use `includedProjectIds` only when a task explicitly needs cross-repo context.
- At task close, call `conversation_ingest` when the work has reusable value.
- Save stable decisions, facts, artifacts, and preferences only when they are reusable.
- Do not write secrets, tokens, private keys, full raw logs, or large diffs.
- Route unfinished actions to `project_work_item_*`; do not use durable memory or checkpoints as task trackers.
- Use `discussion_thread_*` only for unresolved coordination that genuinely needs at least two project participants.
```

Recommended additions:

- build, test, lint commands
- local startup commands
- release or deployment checks
- documentation update rules
- security boundaries
- data and artifact locations

## Start A Task

Use a task-shaped query:

```text
build_working_context(
  projectId = "<RepoName>",
  query = "Add API contract documentation for the new memory search filters."
)
```

If the result is thin, use `memory_search` with a narrower topic.

## During Work

Use ContextHub when you need durable context:

| Need | Tool |
| --- | --- |
| Recover project context | `build_working_context` |
| Find prior decisions or facts | `memory_search` |
| Read a known memory item | `memory_get` |
| Search runtime facts | `log_search` |
| Read log details | `log_read` |
| Queue vector rebuild | `enqueue_reindex` |

Do not query ContextHub on every message. Query when it changes the quality of a decision, implementation, or diagnosis.

## Route Context to the Right Store

ContextHub has separate stores because searchable content is not automatically appropriate for durable knowledge. Decide the destination before writing:

| Content | Destination | Rules |
| --- | --- | --- |
| Confirmed, reusable conclusion for one repo | Current repo `ProjectId` durable memory | Save only verified Facts, Decisions, Artifacts, Preferences, or concise summaries. Exclude reminders, unconfirmed options, and task lists. |
| Confirmed, decontextualized conclusion reusable by multiple repos | `ProjectId = shared` summary layer | Write only material with long-term value beyond one repo. Read with `useSummaryLayer = true` or `queryMode = SummaryOnly` when needed. |
| Unfinished action, blocker, owner follow-up, or verification | `project_work_item_*` for the responsible normal repo `ProjectId` | This is the only action-tracking store. Use status, priority, due date, tags, and checklist as appropriate. |
| Unresolved coordination between at least two repos | `discussion_thread_*` | Use the primary receiving repo as `hostProjectId`, list only required participants, and use the actual speaking repo as `senderProjectId`. |
| Recoverable conversation progress | `conversation_ingest` with the current repo `ProjectId` | Keep it concise. It may refer to a work item or discussion, but cannot replace either record. |

For a one-way reference to another repository, use the current `projectId` with explicit `includedProjectIds`; do not create a discussion merely to retrieve context. A confirmed discussion outcome belongs in the affected project's knowledge or, when genuinely reusable and decontextualized, in the shared summary layer. Create resulting actions as work items in the repo that owns the work, not automatically in the discussion host project.

### Write Decision

```text
Confirmed, reusable for this repo?         -> durable memory under this ProjectId
Confirmed, reusable across multiple repos? -> shared summary layer
Unfinished action requiring tracking?      -> project work item in responsible ProjectId
Needs two or more repos to coordinate?     -> participant-scoped discussion
Need only task recovery?                   -> conversation checkpoint
```

## Close A Task

Use `conversation_ingest` for a concise checkpoint when the work should be recoverable later.

Good checkpoint content:

- task goal
- files changed or likely changed
- key decisions
- verification performed
- open follow-ups
- blockers

Use `memory_upsert` or `memory_update` only for stable reusable knowledge, such as accepted architecture decisions or verified incident root causes.

## Validation Checklist

Onboarding is complete when:

- the MCP client can list ContextHub tools
- `build_working_context(projectId = <RepoName>)` returns repo rules or useful context
- `memory_search` can find a known decision or fact
- the repo has `AGENTS.md` with a stable `ProjectId`
- project information has been created and is returned by `build_working_context`
- the team knows when to use durable memory, shared summary, work items, discussions, and `conversation_ingest`

## Multi-Repo Context

Use `includedProjectIds` only when the task truly needs another repo.

Examples:

- app repo referencing shared library decisions
- deployment repo referencing service repo release behavior
- docs repo summarizing product architecture

Write cross-repo conclusions to the repo most affected by the decision, or to the shared summary layer only when the conclusion is genuinely shared and does not retain a repo-specific owner, schedule, task, or sensitive detail.
