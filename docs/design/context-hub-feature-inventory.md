# ContextHub Feature Inventory

> Public dashboard information architecture and page/function inventory.

Design source: [Quiet Signal UI baseline](context-hub-quiet-signal-vnext.md).

## Labels

| Label | Meaning |
| --- | --- |
| Page | Directly routable page or boundary screen |
| Function | User-visible workspace, table, form, action, reader, control, or shared component |
| State | Loading, empty, error, stale, disabled, pending, applied, and similar states |
| Guardrail | Confirmation, disabled reason, permission denial, secret reveal, audit trail, or other safety control |

## Site Tree

```text
ContextHub Dashboard
├── Shared App Shell
├── Overview /
├── Projects & Work
│   ├── Project Information /project-information
│   ├── Project Topology /project-tree
│   ├── Project Work Items /project-work-items
│   └── Discussions /discussions
├── Files & Knowledge
│   ├── Memories /memories
│   ├── Graph /graph
│   ├── Sources /sources
│   └── Retention /retention
├── Agents
│   ├── Agent Executions /agent-executions
│   ├── Agent Skills /skills
│   └── Connectivity /connectivity
├── Governance & Security
│   ├── Inbox /inbox
│   ├── Governance /governance
│   ├── ChatGPT Proposals /chatgpt-proposals
│   ├── Evaluation /evaluation
│   └── Security /security
├── Operations
│   ├── Operations Center /operations
│   ├── Logs /logs
│   ├── Performance /performance
│   ├── Storage /storage
│   ├── MCP Tools /mcp-tools
│   └── Settings /settings
├── Personal
│   ├── Preferences /preferences
│   └── Tokens /account/tokens
├── Operations drill-down routes
│   ├── Monitoring /monitoring
│   ├── Runtime /runtime
│   └── Jobs /jobs
└── Boundary Pages
    ├── /login
    ├── /forbidden
    ├── /not-found
    └── /Error
```

## Shared Shell

| Type | Name | Scope | Notes |
| --- | --- | --- | --- |
| Function | App shell | All routes | Sidebar, topbar, content scroll host, layout contract |
| Function | Navigation groups | All routes | Overview, Projects & Work, Files & Knowledge, Agents, Governance & Security, Operations, Personal |
| Function | Responsive navigation | All routes | Expanded sidebar, icon rail, overlay drawer |
| Function | Account chrome | All routes | User identity, theme switcher, logout |
| Function | Theme switching | All routes | Dark/light mode and persistence |
| Function | Refresh status | Most dashboard pages | Last updated, refresh state, polling interval |
| Function | Client-local time | All routes | User-facing timestamps default to local time |
| Function | Toast | All routes | Success, error, undo where safe |
| Function | Info popover | All routes | Metric definitions, disabled reasons, risk explanations |
| State | App loading | All routes | Route progress, skeleton panels, table skeletons |
| Guardrail | Scroll ownership | All routes | Body no horizontal scroll; local overflow only |
| Guardrail | Selectability | All routes | Technical values selectable; chrome generally not selectable |

## Operations

| Route | Page Purpose | Key Functions | Required States / Guardrails |
| --- | --- | --- | --- |
| `/` | Concise action-oriented summary | At most three high-value metrics, health exceptions, latest errors, human decisions, active work | loading, error, stale, degraded, empty activity |
| `/operations` | Canonical platform operations | Authority revision, projection lag, incremental/full reconciliation, AgentExecution and logical storage health | authority versus projection distinction, stale, degraded, fail-closed operation |
| `/monitoring` | Runtime snapshot and metrics | Time range controls, refresh cadence, charts, service table, detail drawer | no snapshot, stale, failed refresh |
| `/runtime` | Effective config and service status | Service cards, dependency map, settings view, detail drawer | degraded, permission denied, secret redaction |
| `/logs` | Search and inspect runtime logs | Query bar, severity filter, log table, log reader, copy line | empty, validation error, selectable log text |
| `/jobs` | Inspect and operate background jobs | Job table, detail drawer, retry, cancel, copy result | failed retry, destructive confirmation |
| `/performance` | Run and inspect performance probes | Probe form, metrics, charts, payload reader, retry | validation error, measurement failed |
| `/connectivity` | Inspect agent-to-ContextHub MCP connectivity telemetry | Active policy, recent observations, summaries, and retention context | no sampled observations, stale summary, telemetry disabled |

## Files & Knowledge

| Route | Page Purpose | Key Functions | Required States / Guardrails |
| --- | --- | --- | --- |
| `/graph` | Explore memory relationships | Filter rail, graph canvas, layer toggles, zoom controls, node drawer, reindex action | empty, stale index, reduced-motion fallback |
| `/memories` | Search and inspect memory items | Filters, memory table, detail drawer, content reader, revisions, chunks | no results, conflict, copyable readers |
| `/retention` | Review retention candidates and cleanup decisions | Run summary, generate list, preview delete, apply delete, candidate table, decision panel | busy, blocked, destructive confirmation |
| `/sources` | Manage source inventory and sync status | Source table, detail drawer, trigger sync, pause/resume, failed diagnostics | secret redaction, unhealthy source gating |
| `/project-work-items` | Execute user-managed work for one project | Project and status filters, work list, detail reader, ordered checklist, completion action | checklist completion guard, blocked/cancelled distinction, empty result |

## Projects & Work

| Route | Page Purpose | Key Functions | Required States / Guardrails |
| --- | --- | --- | --- |
| `/project-information` | Inspect and maintain durable project background | Project selector, description editor, lifecycle controls, project hierarchy | hidden/archived distinction, lifecycle confirmation, no implicit scope change |
| `/discussions` | Read and manage participant-scoped cross-project threads | Project filter, thread list, message reader, create/reply actions, unread state | participant access boundary, closed thread, empty inbox, discussion not presented as knowledge |

## Governance & Security

| Route | Page Purpose | Key Functions | Required States / Guardrails |
| --- | --- | --- | --- |
| `/inbox` | Triage incoming items and processing queue | Queue, preview pane, batch actions, processing state | pending distinct from processed, retry failed |
| `/governance` | Manage policy rules, exceptions, and approvals | Policy table, exception drawer, approval workflow, warnings | permission denied, publish guard |
| `/evaluation` | Review evaluation runs and regressions | Run selector, scorecards, failure table, evidence drawer | stale result warning, code/diff local scroll |
| `/chatgpt-proposals` | Review chat-agent write proposals | Filters, proposal queue, review surface, decision zone, audit trail | pending/applied distinction, no secret exposure |
| `/mcp-tools` | Explain ContextHub MCP and REST surfaces | Endpoint comparison, tool catalog, suggested task sequence | direct versus proposal-gated writes, no secret-bearing configuration examples |

## Operations and Personal

| Route | Page Purpose | Key Functions | Required States / Guardrails |
| --- | --- | --- | --- |
| `/preferences` | Manage user display and behavior preferences | Preference forms, dirty save bar, local-time preview | saving, validation, disabled explanation |
| `/storage` | Inspect storage and retention usage | Usage cards, connection test, namespace table | stale, failed test, local overflow |
| `/security` | Manage roles, sessions, audit, and dangerous actions | Roles matrix, audit log, session controls, remove user | destructive confirmation, audit trail |
| `/settings` | Manage instance-level behavior settings | Grouped forms, save/revert, effective config diagnostics | high-impact confirmation, secret redaction |
| `/account/tokens` | Manage personal API tokens | Token list, create modal, one-time reveal, revoke, expiration warning | one-time secret display, revoke confirmation |

## Boundary Pages

| Route | Purpose | Guardrails |
| --- | --- | --- |
| `/login` | Focused authentication entry | validation, disabled login reason |
| `/forbidden` | Permission explanation | no secret leakage, recovery action |
| `/not-found` | Route recovery | search or home action |
| `/Error` | Error recovery | selectable diagnostic ID, redacted diagnostics |

## Coverage Notes

- All current dashboard routes are represented.
- Future routes should update this inventory and the Quiet Signal route matrix.
- Browser tests should cover scroll ownership, responsive navigation, loading, refresh, stale, and error behavior.
