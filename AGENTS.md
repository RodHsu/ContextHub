# ContextHub Agent Notes

This file contains repository-specific instructions that are safe to keep in the public repo. Personal workflow notes, private deployment details, one-off logs, and local TODOs belong under `.agent/`, which is ignored by Git.

## Project

- Name: `ContextHub`
- Stack: .NET 10, ASP.NET Core Minimal APIs, Blazor Web App, PostgreSQL + pgvector, Redis, Docker Compose, ONNX embedding service
- Main service endpoints:
  - MCP: `/mcp`
  - Restricted chat-agent MCP gateway: `/mcp-chat`
  - REST: `/api/*`
  - Health: `/health/live`, `/health/ready`
  - Performance probe: `POST /api/performance/measure`

## ContextHub Usage

- The canonical `ProjectId` for this repository is `ContextHub`.
- Before substantive analysis, code changes, or tests, load working context with `build_working_context(projectId = ContextHub, query = <task>)` when the ContextHub MCP tools are available.
- All ContextHub reads and writes for this repo must pass `projectId = ContextHub`; do not use `default`.
- Use `includedProjectIds` only when a task explicitly needs cross-repository context.
- At a useful checkpoint or task close, write a concise `conversation_ingest` checkpoint when the work has reusable value.
- Store only stable, reusable decisions, facts, artifacts, or preferences. Do not write secrets, full logs, private keys, tokens, or large raw diffs.

## Architecture Rules

- Do not add a separate `memory-api` service.
- REST and MCP must share the same `Memory.Application` use cases.
- `memory_item_chunks` are the retrieval unit; do not embed an entire large document as a single vector unit.
- Runtime logs are DB-first and should not depend on large physical log files.
- Redis is for cache, cache invalidation, dashboard snapshots, locks, and job signals. It is not a durable data source.
- The default production embedding path is the internal ONNX `embedding-service`.
- Local `dotnet test` must continue using deterministic providers instead of requiring model downloads.
- Changing embedding model, dimensions, or profile requires a reindex.

## Development Commands

```powershell
dotnet test ContextHub.slnx
dotnet format ContextHub.slnx --verify-no-changes
docker compose config --quiet
docker compose up -d --build
docker compose down
```

Run the focused tests that cover the touched code first. Escalate to the full solution when changes affect shared infrastructure, cross-module behavior, database schema/migrations, authentication, authorization, build/deploy flow, or broad refactoring.

## Testing Rules

- Any code change must run tests and `dotnet format ContextHub.slnx --verify-no-changes`.
- Documentation-only or comment-only changes do not require tests or linter.
- Container integration tests may skip when Docker is unavailable.
- `McpProtocolTests` must keep real Streamable HTTP black-box coverage.
- For code changes touching public contracts, include API/MCP contract tests where practical.

## Documentation Rules

- Public documentation should describe product behavior, architecture, local development, generic deployment, and reusable design rules.
- Private hostnames, personal machine paths, deployment credentials, Portainer targets, one-off incident notes, and local QA artifacts belong in `.agent/`.
- Prefer relative Markdown links in public docs.
- Use placeholder hostnames such as `context-hub.example.com` in public deployment examples.
- Keep `.env.example` free of real secrets. Required secret fields should be empty placeholders.

## Files To Check

- Architecture and product behavior: `README.md`, `docs/architecture.md`
- Public design rules: `DESIGN.md`, `docs/design/*`
- MCP usage: `docs/mcp-usage-guide.md`
- Onboarding: `docs/repo-onboarding.md`
- Reverse proxy and edge policy: `docs/context-hub-public-nginx.md`, `docs/context-hub-cloudflare-rules.md`
