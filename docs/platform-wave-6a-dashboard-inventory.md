# Platform Wave 6A Dashboard Inventory

> Capability coverage, canonical information architecture, and bounded cleanup evidence for Wave 6A.

## Authority and boundary

- Baseline: `origin/main = origin/dev = 7cad8d4b59b3338e4a995bb2ea51a02b084a6dc8`.
- Wave 6A owns capability inventory, canonical navigation, app shell, concise Overview, canonical Operations, shared state/scope primitives, and fail-closed high-risk UX primitives.
- Wave 6B retains full Files, Secrets, Agents, and Governance domain integration.
- Wave 7 retains legacy Artifact breaking cutover and Production acceptance.
- Monitoring and projections are rebuildable observations. They never become business authority.
- Managed storage/files product surfaces show ContextHub logical metrics and domain errors only; storage-provider identity, endpoint, bucket, object locator, and direct URL remain server-internal. Existing embedding diagnostics contracts are unchanged.

## Canonical IA

```text
Overview
Projects & Work
├── project workspace, topology, work items, discussions
Files & Knowledge
├── memories, graph, sources, retention
Agents
├── executions, skills, connectivity
Governance & Security
├── inbox, governance, proposals, evaluation, security
Operations
├── canonical operations, logs, performance, storage diagnostics, MCP/API, settings
Personal
└── preferences, access tokens
```

The sidebar and command palette consume one `DashboardNavigation` model. Route labels, grouping, authorization visibility, and descriptions no longer have independent copies.

## Capability-to-UI matrix

| Capability | Current UI before 6A | Authority / data source | Authorization | 6A decision | Canonical surface |
| --- | --- | --- | --- | --- | --- |
| Platform health summary | `/`, `/monitoring`, `/runtime` | Dashboard snapshots | Admin | Redesign | `/` concise alerts; `/operations` detail |
| Service/runtime/dependency health | Three overlapping pages | Dependency and runtime snapshots | Admin | Merge | `/operations`; legacy pages are drill-down |
| Authority commit sequence | No canonical UI | `authority_outbox_events` | Admin, tenant scope | Add | `/operations` authority card |
| Monitoring projection revision | No canonical UI | `monitoring_projection_states` | Admin, tenant/project scope | Add | `/operations` projection table |
| Incremental projection runs | No canonical UI | `platform_background_runs` | Admin, tenant/project scope | Add | `/operations` incremental path |
| Full reconciliation runs | Generic Jobs only | `platform_background_runs` | Admin, tenant/project scope | Redesign | `/operations` full path; Jobs drill-down |
| Monitoring staleness / lag | Generic snapshot warning | Authority sequence versus projection cursor | Admin | Add | `/operations`, explicitly non-authoritative |
| AgentExecution queue | `/agent-executions` | AgentExecution authority tables | Admin, tenant/project scope | Keep + summarize | `/operations` summary, `/agent-executions` management |
| Managed storage logical health | No canonical cross-domain view | ManagedObject authority | Admin, tenant/project scope | Add summary | `/operations`; domain details deferred to 6B |
| Transfer logical health | No canonical cross-domain view | ManagedTransferSession authority | Admin, tenant/project scope | Add summary | `/operations`; provider-neutral only |
| Logs and diagnostics | `/logs`, repeated error lists | Runtime log store | Admin | Keep / merge summary | `/` latest three, `/logs` reader |
| Performance probes | `/performance` | Performance API | Admin | Keep | Operations group / `/performance` |
| Release identity | Several pages | Build metadata | Admin | Merge | Overview footer and Operations |
| Runtime configuration | `/runtime` | Runtime snapshot | Admin | Keep as drill-down | Operations group / `/runtime` |
| Managed storage provider identity | Not exposed by the platform contract | Deployment-only storage configuration | No product role | Preserve opacity | Provider-neutral logical metrics only |
| Project information | `/project-information` | Project information authority | User/project ACL | Keep | Projects & Work |
| Project topology | `/project-tree` | Foundation A topology authority | User/project ACL | Keep | Projects & Work |
| Work Items | `/project-work-items`, Inbox label overlap | Project Work Item authority | Admin/project ACL | Keep / relabel | Projects & Work / 專案待辦 |
| Cross-project discussions | `/discussions` | Discussion authority | Participant project ACL | Keep | Projects & Work |
| Memories | `/memories` | Memory authority | Project/user ACL | Keep | Files & Knowledge |
| Memory graph | `/graph` | Rebuildable graph projection + memory authority | Project/user ACL | Keep | Files & Knowledge |
| Sources | `/sources` | Source authority and sync jobs | Admin | Keep | Files & Knowledge |
| Retention | `/retention` plus Jobs summary | Retention classification / governance | Admin | Keep canonical | Files & Knowledge / `/retention` |
| Managed Files detail workflows | Not yet integrated | Managed Files domain | Domain ACL + step-up | Defer | Wave 6B |
| Secrets/Credentials detail workflows | Not yet integrated | Secrets domain | Domain ACL + step-up | Defer | Wave 6B |
| Skills | `/skills` | Skills authority and telemetry | Admin | Keep | Agents |
| Connectivity | `/connectivity` | Connectivity telemetry projection | Admin | Keep | Agents |
| Governance | `/governance` | Governance authority and receipts | Admin | Keep | Governance & Security |
| RequiresUserDecision | `/inbox`, proposal pages | Domain/governance authority | Admin/human decision | Merge navigation | Governance & Security / 待決事項 |
| ChatGPT proposals | `/chatgpt-proposals` | Proposal authority | Admin | Keep | Governance & Security |
| Evaluation | `/evaluation` | Evaluation evidence | Admin | Keep | Governance & Security |
| Security administration | `/security` | Identity, role, audit authority | Admin | Keep | Governance & Security |
| Scope/search/filter/action layout | Per-page copies | UI state only | Same as page | Add shared primitive | `DashboardScopeBar` |
| Loading/empty/error/degraded/permission | Per-page ad hoc states | UI state only | Same as page | Add shared primitive | `DashboardAsyncState` |
| High-risk operation assurance | No shared Dashboard primitive | Server `StepUpAuthorizationResult` | Server policy | Add fail-closed primitive | `HighRiskOperationGuard` |

## Bounded cleanup and route coverage

| Previous duplicate | 6A action | Coverage / redirect evidence |
| --- | --- | --- |
| Hard-coded sidebar navigation | Removed | `DashboardNavigation.Groups` renders sidebar |
| Separate command-palette route registry | Removed | Same `DashboardNavigation.Groups` renders command palette |
| Overview resource charts and Compose table | Removed from Overview | Canonical `/operations` plus existing `/monitoring` drill-down |
| Overview Docker/runtime detail | Removed from Overview | Existing `/runtime` and canonical `/operations` |
| Overview full error/job lists | Reduced to three actionable items | `/logs` and `/jobs` remain canonical detail surfaces |
| `/monitoring` route | Preserved | Advanced telemetry drill-down from `/operations` |
| `/runtime` route | Preserved | Effective runtime/config drill-down from `/operations` |
| `/jobs` route | Preserved | Background job and maintenance management drill-down |

No unique management, audit, security, or permission surface is removed in Wave 6A. Domain-heavy route deletion remains deferred to Wave 6B.

## High-risk UX contract

`HighRiskOperationGuard` defaults to disabled when no server decision exists. It renders required assurance, supported Password/TOTP/WebAuthn/FIDO/external-approval method, reason, impact preview, and outcome read-back.

The component enables execution only when the server returns `StepUpRequirementOutcome.Allowed`. A frontend caller cannot infer a lower assurance level. The Operations page intentionally leaves bounded full reconciliation disabled because Wave 6A does not publish a mutation endpoint with a server assurance decision.

## Acceptance focus

- Desktop, tablet, high-DPI-equivalent, mobile, and large-text browser coverage.
- One visible page scroll owner; local overflow for tables/readers.
- Keyboard focus and modal/overlay behavior.
- Loading, empty, error, degraded, and permission-denied states.
- Provider-neutral API/browser payload checks.
- Scheduled Governance remains exactly four tools.
