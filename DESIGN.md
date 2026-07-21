# ContextHub Design Baseline

This document defines the public product design baseline for ContextHub. It covers the dashboard experience, responsive behavior, UI states, local QA expectations, and release verification principles.

For service boundaries and data flow, see [docs/architecture.md](docs/architecture.md). For the route and feature inventory, see [docs/design/context-hub-feature-inventory.md](docs/design/context-hub-feature-inventory.md).

## Product Positioning

ContextHub is an operations and knowledge console for coding-agent memory systems. It should feel quiet, dense, precise, and production-focused.

It is not a marketing landing page. The first screen after login should help an operator answer:

- Is the system healthy?
- Are memories, logs, jobs, and telemetry current?
- Is any queue, proposal, or governance item waiting for review?
- Which project is in scope, and are its discussions or work items waiting for action?
- Can I inspect or act without losing context?

## Design Source

The active public design baseline is `Quiet Signal`.

Related files:

- [Quiet Signal UI baseline](docs/design/context-hub-quiet-signal-vnext.md)
- [Feature inventory](docs/design/context-hub-feature-inventory.md)
- [Mockups](docs/design/mockups/quiet-signal/)

Internal design-tool sessions, private project IDs, and one-off implementation QA notes should stay outside tracked public documentation.

## Visual System

- Personality: quiet, precise, trustworthy, fast
- Avoid: neon cyberpunk, terminal-heavy decoration, marketing hero layouts, decorative card walls
- Canvas: deep neutral background
- Surfaces: restrained graphite-like panels
- Primary accent: teal, reserved for primary actions, selected state, and focus
- Status colors: success, warning, danger, and info must be paired with text or icons
- Typography: sans-serif UI font for product text; monospace only for IDs, JSON, logs, timestamps, code, and tokens
- Radius: keep containers at 8px or less unless a component requires otherwise
- Shadows: use only for overlays such as menus, drawers, and dialogs

## App Shell

```text
Desktop >= 1440
  expanded sidebar
  dense content grid
  master-detail allowed

Medium desktop 1025-1365
  icon rail or compact sidebar
  secondary columns collapse into drawers

Tablet 768-1024
  icon rail or overlay navigation
  tables become priority-column lists or master-detail route transitions

Mobile < 768
  top app bar
  overlay navigation
  single-column workflows
```

Rules:

- The document and page body must not horizontally scroll.
- Tables, JSON, code, payloads, diffs, and log readers may scroll only inside named frames.
- A component should own at most one scroll axis unless it is a dedicated reader.
- Technical values must be selectable and copyable.
- Sticky actions must not cover content without safe-area spacing.
- Navigation must compress predictably; it must not become a full-width desktop sidebar on narrow screens.

## Common Component Contracts

| Component | Contract |
| --- | --- |
| `AppShell` | Shared navigation model across sidebar, rail, and drawer states |
| `PageHeader` | Title, context, freshness, and primary action; no hero treatment |
| `MetricStrip` | Compact grouped metrics with dividers, not large KPI card walls |
| `DataGrid` | Sticky header, predictable row height, column priority, local overflow |
| `MasterDetail` | Desktop split view; tablet/mobile drawer or full-width detail |
| `ContentReader` | Selectable text, copy action, wrap toggle, visible scrollbar, bounded height |
| `FormSection` | Label above control, inline validation, dirty state, disabled explanation |
| `ProjectScope` | A visible project identifier, its durable description, and lifecycle state; project scope must not be inferred from a generic list selection |
| `DiscussionWorkspace` | Participant-scoped thread list and reader; clearly distinguish discussion from durable knowledge and from project work items |
| `WorkItemWorkspace` | Filterable project work list, execution checklist, status, and completion guard; completion remains disabled until every checklist item is complete |
| `RefreshState` | Preserve current data while refresh is running; update client-local timestamp on success |
| `DestructiveDialog` | Object name, impact preview, consequence, and typed confirmation for high-risk actions |
| `AsyncState` | Loading, stale, empty, error, retry, disabled, and success states preserve layout shape |

## Dashboard Route Baseline

| Area | Routes |
| --- | --- |
| Operations | `/`, `/monitoring`, `/runtime`, `/logs`, `/jobs`, `/performance` |
| Knowledge work | `/graph`, `/memories`, `/retention`, `/sources` |
| Project collaboration | `/project-information`, `/project-work-items`, `/discussions` |
| Governance | `/inbox`, `/governance`, `/evaluation`, `/chatgpt-proposals`, `/mcp-tools` |
| Administration | `/preferences`, `/storage`, `/security`, `/settings`, `/account/tokens` |
| Operations detail | `/connectivity` |
| Boundaries | `/login`, `/forbidden`, `/not-found`, `/Error` |

Every route should have:

- loading state
- empty or no-results state where applicable
- stale or refresh state where applicable
- error state with actionable recovery
- keyboard focus visibility
- copy affordance for technical values
- no body-level horizontal overflow

## Dense Admin Layout

- Preserve high information density, but do not make panels visually compete for attention.
- Merge related columns before shrinking text or forcing unreadable wrapping.
- Keep forms compact and action-oriented.
- Give ambiguous runtime and telemetry metrics an `InfoPopover`.
- Ensure popovers, menus, drawers, and dialogs are not clipped by panel overflow.

## Graph Explorer

- The graph canvas is the primary surface; side panels are support surfaces.
- Initial render should optimize readability before trying to fill all whitespace.
- Small graphs should not be over-zoomed because the baseline canvas is large.
- Empty states must distinguish no data, no matching filters, stale index, and load failure.
- The graph is for relationship exploration, not a replacement for the memories table.

## Test Artifacts

Allowed locations:

- System temporary directories
- Existing repo paths explicitly intended for artifacts
- `.agent/local/` for local-only evidence and notes
- `docs/` only for curated public assets such as approved mockups

Do not scatter screenshots, databases, caches, exports, or one-off QA files in the repo root.

## Browser QA

For UI changes, use screenshot or browser tests across representative viewports:

- Desktop wide
- Desktop compact
- Tablet landscape and portrait
- Mobile narrow
- App-browser constrained width where relevant

Check:

- no overlapping text or controls
- no unexpected horizontal page scroll
- readers and tables scroll inside their own frames
- dialogs and drawers fit the viewport
- loading, empty, error, stale, disabled, and focus states are usable
- reduced-motion behavior remains acceptable

## Deployment Verification

A deployment is not verified just because containers are running. At minimum, verify:

```text
GET /health/ready for dashboard and mcp-server
GET /api/status
GET /api/dashboard/monitoring
Dashboard /login
MCP initialize and tools/list
```

Monitoring data should not remain indefinitely in unavailable or zero-refresh states after a healthy deployment. If the dashboard loads but monitoring sections remain unavailable, verify that all services were updated together and that the dashboard and `mcp-server` versions are compatible.

## Current Priorities

Worth doing now:

- Keep public docs product-oriented and generic.
- Keep deployment examples provider-agnostic unless a provider-specific doc is explicitly scoped.
- Preserve design contracts in browser tests.
- Keep chat-agent writes proposal-gated.
- Keep runtime and telemetry values explainable in the UI.

Usually defer:

- Marketing-first website treatment inside the dashboard.
- One-off private incident notes in public docs.
- Tool-specific project/session IDs in public design docs.
- New services that duplicate existing application use cases.
