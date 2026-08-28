# ContextHub

ContextHub is a self-hosted knowledge system for coding agents. It provides long-term memory, explicit user preferences, runtime logs, retrieval telemetry, background reindexing, and MCP tools through a single Docker Compose deployment.

The goal is to keep durable context outside the prompt while still letting agents retrieve relevant project facts, decisions, artifacts, logs, and preferences on demand.

## Highlights

- MCP endpoint and REST APIs served from the same application use cases
- PostgreSQL + pgvector as the durable source of truth
- Redis for cache, cache invalidation, job signaling, and dashboard snapshots
- Local ONNX embedding service, with no external embedding API required at runtime
- Blazor dashboard for memory, logs, jobs, telemetry, security, storage, and runtime status
- Explicit user preference management through MCP tools and REST APIs
- Durable project information, explicit project hierarchy, and participant-scoped cross-project discussions
- Project work items with checklists, ownership boundaries, and completion guards
- Non-destructive knowledge review that keeps knowledge, discussions, work items, and governance actions distinct
- Canonical versioned governance tool contracts, auditable run receipts, and policy-bound internal matured deletion
- DB-first runtime logs that can be searched and promoted into durable memory
- Retrieval telemetry and maintenance APIs for retention, summaries, and reclaim workflows
- Chat-agent gateway for restricted MCP access with OAuth/OIDC and proposal-gated writes

## Repository Layout

| Path | Purpose |
| --- | --- |
| `src/Memory.Domain` | Shared domain enums and models |
| `src/Memory.Application` | Use cases for memory, logs, context building, governance, and telemetry |
| `src/Memory.Infrastructure` | PostgreSQL, pgvector, Redis, embeddings, logging, and storage adapters |
| `src/Memory.McpServer` | MCP endpoint, REST API, health checks, and performance probe |
| `src/Memory.ChatGptGateway` | Restricted chat-agent MCP gateway and OAuth/OIDC flow |
| `src/Memory.Dashboard` | Blazor dashboard |
| `src/Memory.EmbeddingServer` | ONNX embedding HTTP service |
| `src/Memory.Worker` | Durable background job worker |
| `tests/*` | Unit, integration, MCP protocol, dashboard, and compose smoke tests |
| `tools/*` | Local diagnostics and MCP bridge tooling |
| `docs/*` | Architecture, usage, deployment, onboarding, and design documentation |

## Runtime Topology

```text
Agent / REST Client / Dashboard User
  -> mcp-server (/mcp, /api/*, /health/*)
      -> Memory.Application
      -> PostgreSQL + pgvector
      -> Redis
      -> embedding-service

worker
  -> PostgreSQL jobs
  -> embedding-service
  -> Redis signal

dashboard
  -> mcp-server REST APIs
  -> optional read-only Docker socket for local compose metrics

chatgpt-gateway
  -> restricted /mcp-chat gateway
  -> mcp-server REST APIs
```

## Services

| Service | Responsibility | Default local binding |
| --- | --- | --- |
| `mcp-server` | MCP, REST, health, performance probe, memory and log APIs | `127.0.0.1:8092` |
| `dashboard` | Web UI for operations and knowledge governance | `127.0.0.1:8091` |
| `chatgpt-gateway` | Restricted MCP gateway for chat-agent integrations | `127.0.0.1:8094` |
| `embedding-service` | ONNX tokenizer and embedding inference | internal only |
| `worker` | Reindexing and durable background jobs | internal only |
| `postgres` | Durable memory, logs, jobs, telemetry, security records | `127.0.0.1:5432` |
| `redis` | Cache, snapshot cache, and job signals | `127.0.0.1:6379` |

## Quick Start

Prerequisites:

- Docker Engine or Docker Desktop
- Docker Compose
- .NET 10 SDK for local development and tests

Create your environment file:

```powershell
Copy-Item .env.example .env
```

Before starting a shared or public-facing deployment, set at least:

- `CONTEXTHUB_SECRET_KEY`
- `CONTEXTHUB_SECURITY_BOOTSTRAP_TOKEN`
- `DASHBOARD_API_TOKEN`
- `DASHBOARD_ADMIN_USERNAME`
- `DASHBOARD_ADMIN_PASSWORD_HASH`

Start the stack:

```powershell
docker compose up -d --build
```

Stop while preserving data:

```powershell
docker compose down
```

Remove containers, database volume, Redis data, and embedding model cache:

```powershell
docker compose down -v
```

## Health Checks

Local compose defaults:

- Dashboard: `http://localhost:8091/login`
- Dashboard health: `http://localhost:8091/health/ready`
- MCP server health: `http://localhost:8092/health/ready`
- API status: `http://localhost:8092/api/status`
- MCP endpoint: `http://localhost:8092/mcp`
- Restricted chat-agent MCP endpoint: `http://localhost:8094/mcp`

Do not treat a running container as a complete readiness signal. For agent usage, verify health, `/api/status`, MCP `initialize`, and MCP `tools/list`.

## MCP Tools

The main MCP endpoint is:

```text
POST/GET /mcp
```

Primary tools:

- `describe_context_hub`
- `build_working_context`
- `memory_search`
- `memory_get`
- `memory_upsert`
- `memory_update`
- `conversation_ingest`
- `project_information_get` / `project_information_upsert`
- `discussion_thread_create` / `discussion_threads_list` / `discussion_thread_get` / `discussion_thread_close` / `discussion_message_create`
- `project_hierarchy_set_children`
- `project_work_item_create` / `project_work_item_update` / `project_work_items_list`
- `daily_memory_review`
- `log_search`
- `log_read`
- `promote_log_slice_to_memory`
- `user_preference_list`
- `user_preference_upsert`
- `user_preference_archive`
- `enqueue_reindex`

When using ContextHub with a repo, agents should first resolve an explicit `projectId`, then call `build_working_context(projectId=..., query=...)`. Avoid falling back to a shared `default` project for real work.

## Remote MCP Configuration

For clients that support Streamable HTTP MCP:

```toml
[mcp_servers.contexthub]
enabled = true
url = "https://your-context-hub.example.com/mcp"
bearer_token_env_var = "CONTEXTHUB_MCP_TOKEN"
```

Keep tokens in environment variables or a secret manager. Do not commit bearer tokens, OAuth client secrets, signing keys, or private keys.

For local compose:

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

## Chat-Agent Gateway

`Memory.ChatGptGateway` exposes a restricted MCP surface for chat agents:

```text
/mcp-chat
```

It is separate from `/mcp` and is intended for OAuth/OIDC-authenticated clients, project allowlists, rate limits, audit trails, restricted tool discovery, proposal-gated durable knowledge writes, and direct project work-item lifecycle operations. Scheduled governance uses `knowledge_review` with a stable `governanceRunId`, then calls the server-side `governance_batch_execute` tool with the returned snapshot token and continuation cursor. The unified compact plan covers Project Information, hierarchy, Project/Shared knowledge, preferences, artifacts, discussions, work items, conversation insights, Suggested Actions, proposals, typed retention, and bounded server-side log partitions. Low-value machine evidence is first archived into a durable quarantine state. Every later review recomputes hit, link, authority, replacement, hold, dependency, Work Item, Discussion, project-relationship, and policy evidence; a changed blocker clears `DeleteEligibleAt` and restarts grace only after eligibility returns. Scheduled direct hard-delete remains forbidden. Scheduled matured-delete is a separate administrator capability and applies only after typed grace, immediate eligibility revalidation, atomic content/chunk/vector/revision removal, minimal immutable tombstone creation, audit, and resource read-back. Exact replay returns the original tombstone/audit references. The executor persists an actor-scoped plan, applies bounded operations, returns per-item audit references, and continues a backlog across later scheduled runs without treating a page-size threshold as a stop condition. Evidence-versioned semantic items automatically reopen after related evidence or policy changes; reversible candidates above the configured confidence threshold can resolve autonomously, while Critical, legal/privacy, protected destruction, external authorization, and ambiguous equal-authority cases remain human decisions. DisplayName, OAuth, ACL, ordinary business Work Items, Discussion history, Decision, Fact, formal Artifact, audit/security evidence, and governance acceptance evidence are protected from general short retention. Callers must review every candidate page, execute bounded batches, follow `nextCursor`, and perform same-run re-review. Completion requires every surface `coverageComplete=true`, every `hasMore=false`, and `governanceActionableCount=0`; governed exceptions return `ConvergedWithExceptions`. The full Codex/agent MCP endpoint remains `/mcp`.

Project workspace `DisplayName` is an interactive Dashboard-owned field. New projects and blank legacy values fall back to `ProjectId`; MCP, ChatGPT, scheduled/background governance, cleanup, retention, insights, suggested actions, and hierarchy synchronization can update project descriptions or lifecycle data but cannot change `DisplayName`.

## Embedding Profiles

Most deployments should change only:

```env
EMBEDDING_PROFILE=compact
```

Supported profiles:

| Profile | Model | Dimensions | Token limit | Use case |
| --- | --- | ---: | ---: | --- |
| `compact` | `intfloat/multilingual-e5-small` | 384 | 512 | Lower memory local development |
| `balanced` | `intfloat/multilingual-e5-base` | 768 | 512 | Higher retrieval quality |

Advanced overrides are available through:

- `EMBEDDING_MODEL_ID`
- `EMBEDDING_DIMENSIONS`
- `EMBEDDING_MAX_TOKENS`
- `EMBEDDING_INFERENCE_THREADS`
- `EMBEDDING_BATCH_SIZE`

After changing the model, dimensions, or profile, run `enqueue_reindex` so existing chunks receive vectors in the new embedding space.

## Dashboard

The dashboard is a Blazor Web App that reads business data from `mcp-server` REST APIs. It does not directly mutate PostgreSQL outside the established application use cases.

Key routes:

- `/`
- `/monitoring`
- `/runtime`
- `/memories`
- `/graph`
- `/sources`
- `/retention`
- `/project-information`
- `/project-work-items`
- `/discussions`
- `/inbox`
- `/chatgpt-proposals`
- `/governance`
- `/evaluation`
- `/preferences`
- `/logs`
- `/jobs`
- `/storage`
- `/security`
- `/performance`
- `/settings`
- `/account/tokens`
- `/connectivity`
- `/mcp-tools`

## Development

Run the default verification gates:

```powershell
dotnet test ContextHub.slnx
dotnet format ContextHub.slnx --verify-no-changes
docker compose config --quiet
```

Optional release compose validation:

```powershell
docker compose -f docker-compose.release.yml config --quiet
```

Notes:

- Unit tests use deterministic providers and do not require model downloads.
- Container-backed tests skip when Docker is unavailable.
- Compose smoke tests require `RUN_COMPOSE_SMOKE_TESTS=1`.
- Dashboard browser tests use Playwright and cover responsive routes.

## Documentation

- [Architecture](docs/architecture.md)
- [MCP usage guide](docs/mcp-usage-guide.md)
- [Agent connectivity telemetry](docs/agent-connectivity-telemetry.md)
- [Repo onboarding guide](docs/repo-onboarding.md) — initial `ProjectId`／project information setup and data-routing rules
- [Public Nginx proxy guide](docs/context-hub-public-nginx.md)
- [Cloudflare edge rules](docs/context-hub-cloudflare-rules.md)
- [Design baseline](DESIGN.md)
- [Feature inventory](docs/design/context-hub-feature-inventory.md)
- [Quiet Signal UI baseline](docs/design/context-hub-quiet-signal-vnext.md)

## Security

- Bind local service ports to `127.0.0.1` unless you intentionally expose them through a reverse proxy.
- Keep PostgreSQL, Redis, and `embedding-service` off the public internet.
- Put public deployments behind HTTPS and a reverse proxy.
- Keep `/mcp`, `/mcp-chat`, `/api/*`, `/_blazor*`, `/health*`, `/login*`, and `/account*` uncached.
- Use bearer tokens or OAuth/OIDC for external agent access.
- Treat Docker socket access as privileged, even when mounted read-only.
- Never commit `.env`, tokens, password hashes, OAuth secrets, signing keys, or private keys.
