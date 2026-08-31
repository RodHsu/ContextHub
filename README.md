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

Do not treat a running container as a complete readiness signal. For agent usage, verify health, `/api/status`, MCP `server/discover`, and a stateless MCP `tools/list` call. Also keep one legacy `initialize` compatibility probe until all managed clients have migrated.

## MCP Tools

The main MCP endpoint is:

```text
POST /mcp
```

ContextHub targets MCP `2026-07-28`: requests are stateless and self-describing, and the server does not issue `Mcp-Session-Id`. Modern clients use `server/discover` or call tools directly with per-request metadata. Legacy Streamable HTTP clients may still use `initialize`, but must tolerate a server that does not create a transport session.

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

It is separate from `/mcp` and is intended for OAuth/OIDC-authenticated clients, project allowlists, rate limits, audit trails, restricted tool discovery, proposal-gated durable knowledge writes, and direct project work-item lifecycle operations. Scheduled governance uses `knowledge_review` with a stable `governanceRunId`, then calls the server-side `governance_batch_execute` tool with the returned snapshot token and continuation cursor. A null `projectIds` value resolves the actor's complete durable-governance scope independently of the UI project list, including authorized hidden/archived/default project rows plus Shared exactly once; explicit project IDs remain exact. Project Information is protected non-retrieval system metadata, so an empty description is valid and does not produce ordinary invalid-memory or reindex findings. Coverage publishes an authoritative-versus-covered count invariant. The unified compact plan covers Project Information, hierarchy, Project/Shared knowledge, preferences, artifacts, discussions, work items, conversation insights, Suggested Actions, proposals, typed retention, and bounded server-side log partitions. Low-value machine evidence is first archived into a durable quarantine state. Every later review recomputes hit, link, authority, replacement, hold, dependency, Work Item, Discussion, project-relationship, and policy evidence; a changed blocker clears `DeleteEligibleAt` and restarts grace only after eligibility returns. Scheduled direct hard-delete remains forbidden. Scheduled matured-delete is a separate administrator capability and applies only after typed grace, immediate eligibility revalidation, atomic content/chunk/vector/revision removal, minimal immutable tombstone creation, audit, and resource read-back. Exact replay returns the original tombstone/audit references. The executor persists an actor-scoped plan, applies bounded operations, returns per-item audit references, and continues a backlog across later scheduled runs without treating a page-size threshold as a stop condition. Evidence-versioned semantic items automatically reopen after related evidence or policy changes; reversible candidates above the configured confidence threshold can resolve autonomously, while Critical, legal/privacy, protected destruction, external authorization, and ambiguous equal-authority cases remain human decisions. DisplayName, OAuth, ACL, ordinary business Work Items, Discussion history, Decision, Fact, formal Artifact, audit/security evidence, and governance acceptance evidence are protected from general short retention. Callers must review every candidate page, execute bounded batches, follow `nextCursor`, and perform same-run re-review. Completion requires every surface `coverageComplete=true`, every `hasMore=false`, and `governanceActionableCount=0`; governed exceptions return `ConvergedWithExceptions`. The full Codex/agent MCP endpoint remains `/mcp`.

Project workspace `DisplayName` is an interactive Dashboard-owned field. New projects and blank legacy values fall back to `ProjectId`; MCP, ChatGPT, scheduled/background governance, cleanup, retention, insights, suggested actions, and hierarchy synchronization can update project descriptions or lifecycle data but cannot change `DisplayName`.

Scheduled ChatGPT governance connects to a separate least-privilege resource:

```text
/mcp-automation
```

This surface publishes exactly four tools: `scheduled_governance_contract_get`, `scheduled_governance_review`, `scheduled_governance_execute`, and `scheduled_governance_run_get`. Review resolves the actor's full authorized durable scope server-side and returns an immutable snapshot, count invariant, and one fixed decision (`NoOpConverged`, `ReversibleExecutionRequired`, `HumanDecisionOnly`, or `CoverageIncomplete`). Execute accepts no ProjectIds, action list, risk policy, hard-delete, matured-delete, dry-run, or execution-mode controls; it can run only the server's fixed low-risk reversible policy with snapshot/cursor binding and authorization revalidation. Irreversible matured retention remains exclusively owned by the internal Worker. General `/mcp` and `/mcp-chat` capabilities are unchanged.

### ChatGPT App connections

Register the two resources as separate ChatGPT Apps. Do not replace the general connection with the governance connection, and do not use `/mcp-chat` as a fallback for scheduled-governance acceptance.

| Field | General App | Scheduled Governance App |
| --- | --- | --- |
| Name | `ContextHub` | `ContextHub Governance` |
| Description | `ContextHub project knowledge, collaboration, and governed write workflows.` | `Least-privilege scheduled governance for ContextHub with reversible bounded execution only.` |
| MCP server URL | `https://context-hub.example.com/mcp-chat` | `https://context-hub.example.com/mcp-automation` |
| Authentication | OAuth with CIMD | OAuth with CIMD |
| Expected tool surface | The restricted general ChatGPT catalog | Exactly the four `scheduled_governance_*` tools listed above |

Both MCP initialization responses publish the ContextHub title, description, website, and same-origin `/favicon.svg` icon. A client may cache connection metadata, so refresh or reconnect the App after a server release. If the connection is later packaged as an installable ChatGPT/Codex plugin, also put a PNG icon and logo under the plugin's `assets/` folder and declare `interface.composerIcon` and `interface.logo` in `.codex-plugin/plugin.json`; MCP `serverInfo.icons` and install-surface manifest assets are complementary.

The linked identity comes from the ContextHub tenant user that actually signs in during OAuth, not from the user's ChatGPT account. ContextHub publishes that knowledge-base account as stable `sub`, `preferred_username`, and `name` claims. It publishes `email` only when the stored value is not a reserved placeholder such as `admin@example.com`; a placeholder must never override the connected knowledge-base username in the ChatGPT UI. An administrator can correct an existing user through the authenticated `PATCH /api/security/users/{userId}` application endpoint; do not update PostgreSQL directly. Disconnect and authorize both Apps again after an identity change because an existing OAuth connection can retain earlier claims.

Verification ownership is split deliberately:

- Codex verifies source changes, focused and full regression, Docker/HTTP/OAuth/MCP security simulations, deployment, and production metadata read-back.
- The user performs the one-time ChatGPT UI installation or OAuth authorization when the host requires direct interaction, then confirms that the icon and linked account label are correct.
- GPT, with the `ContextHub Governance` App connected, verifies that discovery returns exactly four tools and performs controlled acceptance with a fresh `governanceRunId`. Only after that pass may the existing four-hour governance Automation be migrated; manual runs do not count toward the six-natural-schedule reliability gate.

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

The overview metric `memoryItems` is an instance-wide `memory_items` row inventory, not an actor-scoped governance count. Its API contract includes ownership, project, scope, type, and status partitions plus separate tombstone/revision/chunk/vector/insight counts; the UI labels it `全 Instance 記憶資料列` to keep it distinct from `durableMemoryCoverage.totalCount`.

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
- Compose routes Docker API access through isolated, repo-built policy proxies based on a digest-pinned Alpine image; do not publish their port or replace them with direct socket mounts.
- Never commit `.env`, tokens, password hashes, OAuth secrets, signing keys, or private keys.

The current MCP requirement matrix and security review are documented in [MCP 2026-07-28 compliance](docs/mcp-2026-07-28-compliance.md).
