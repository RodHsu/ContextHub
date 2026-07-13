# Repo Onboarding Guide

This guide explains how to connect a repository to ContextHub so coding agents can recover working context across conversations.

It is for maintainers adopting ContextHub in another repo. For MCP tool behavior, see [mcp-usage-guide.md](mcp-usage-guide.md).

## Flow

```text
Choose ProjectId
  -> start or connect to ContextHub
  -> configure MCP client
  -> add repo AGENTS.md
  -> begin tasks with build_working_context
  -> search memory/logs when needed
  -> close useful work with conversation_ingest
  -> save durable decisions/facts/preferences only when stable
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
- the team knows when to use `conversation_ingest` versus durable memory writes

## Multi-Repo Context

Use `includedProjectIds` only when the task truly needs another repo.

Examples:

- app repo referencing shared library decisions
- deployment repo referencing service repo release behavior
- docs repo summarizing product architecture

Write cross-repo conclusions to the repo most affected by the decision, or to a shared project only when the conclusion is genuinely shared.
