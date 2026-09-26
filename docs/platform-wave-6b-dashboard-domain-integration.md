# Platform Wave 6B Dashboard Domain Integration

> Capability coverage, management-to-monitoring drill-down, destructive-cleanup decisions, and black-box acceptance for Wave 6B.

## Authority and boundary

- Baseline: `origin/main = origin/dev = 673a22c4362806e1661ff5b8b8307c9ce6a54793`.
- Wave 6B completes the Dashboard integration of existing Files, Secrets, Agents, Projects, Governance, Security, and Operations capabilities.
- Wave 7 retains the legacy Artifact/MCP breaking cutover. No legacy authority contract is removed or reinterpreted here.
- Management pages read business authority. Operations and monitoring pages show rebuildable projections and always drill back to authority.
- Browser, API, and telemetry contracts expose ContextHub logical identities and normalized states only. Storage-provider identity and credential material remain server-internal.

## Canonical domain coverage

| Domain | Business authority | Canonical management surface | Monitoring drill-down | Effective rights / assurance | Decision |
| --- | --- | --- | --- | --- | --- |
| Projects & Work | Project information, hierarchy, work items, discussions | `/project-information`, `/project-tree`, `/project-work-items`, `/discussions` | `/operations` projection row links back to the project authority | Existing project ACL plus current-actor authorization | Keep and connect |
| Files | `FileAsset`, immutable `FileVersion`, classification and findings | `/files` | `/operations#logical-storage` | Per-file effective-right read-back; quarantine release remains disabled until server assurance and approval are satisfied | Add canonical management surface |
| Knowledge | Memory authority, sources, retention | `/memories`, `/graph`, `/sources`, `/retention` | `/operations` and existing projection drill-downs | Existing project/user ACL | Keep and connect |
| Secrets and credentials | Secret metadata, version, grant, lease, MFA authority | `/secrets`, `/security` | `/operations#logical-storage`, `/agent-executions` | Per-secret rights; Reveal and Revoke use server requirement previews and cannot downgrade AAL | Add canonical metadata surface |
| Agents and skills | AgentExecution, skill/version/binding authority | `/agent-executions`, `/skills`, `/connectivity` | `/operations#agent-executions` | Existing execution/skill scopes and approvals | Keep and connect |
| Governance and security | Governance receipts, proposals, identity, roles, tokens, audit | `/governance`, `/inbox`, `/chatgpt-proposals`, `/evaluation`, `/security` | `/operations`, `/logs` | Existing admin/security scopes; high-risk actions remain server-authorized | Keep and connect |
| Operations | Authority outbox plus rebuildable projections/runs | `/operations` | `/monitoring`, `/runtime`, `/jobs`, `/logs`, `/performance`, `/storage` | Admin-only observation; no business mutation authority | Preserve non-authoritative role |

`DomainSurfaceNav` supplies the same authority label and management/monitoring drill-down pattern to the integrated domain hubs. It does not create a second route registry; the sidebar and command palette remain sourced from `DashboardNavigation`.

## Files and Secrets black-box contracts

### Managed Files

`GET /api/files` returns logical file/version identity, lifecycle, classification revision, rescan state, normalized finding counts, relation count, and update time. The service first applies tenant/user scope, project authorization, and the resource-level `metadata` right. Denied resources are omitted, including their existence.

The response does not carry managed-object identity, provider, vendor, endpoint, bucket, object key, signed URL, direct URL, encryption key identifiers, or wrapped key material.

### Secrets and MFA

The existing secret inventory returns metadata only. `/secrets` shows logical name, kind, state, version, revision, effective rights, and server assurance requirements. It never requests or renders secret material, encryption internals, lease payloads, or provider coordinates.

`GET /api/step-up/requirements` delegates to the server step-up service with no proof. It can report the current assurance floor but cannot authorize the operation. Reveal, revoke, and restricted release controls remain disabled until a separate server-issued ceremony returns `Allowed`.

### Effective rights

`GET /api/projects/hierarchy/{projectId}/effective-rights` evaluates only the current authenticated principal. The caller cannot submit another principal identity. Rights are bounded, deduplicated, and evaluated through the existing Foundation authority.

## Destructive cleanup and capability coverage

| Candidate duplicate or legacy surface | Coverage proof | Wave 6B action | Rationale |
| --- | --- | --- | --- |
| Separate sidebar and command-palette route lists | Both already consume `DashboardNavigation` | Keep merged registry; add `/files` and `/secrets` once | Duplicate registry was removed in 6A; reintroducing one would regress IA |
| Per-page domain link strips | `DomainSurfaceNav` provides authority and drill-down semantics | Merge domain-hub links into the shared primitive | Removes repeated link markup without removing a route or capability |
| Per-page project/search filter markup | `DashboardScopeBar` owns scope and search behavior | Reuse; add fail-closed `AllowAllProjects=false` for domain authority pages | Prevents accidental cross-project inventory |
| Per-page high-risk enablement rules | `HighRiskOperationGuard` plus server requirement endpoint | Merge display/disable behavior; add explicit unavailable reason | Frontend cannot infer or lower assurance |
| `/monitoring` | Unique advanced telemetry detail | Preserve as drill-down | Removing it would lose diagnostic capability |
| `/runtime` | Unique effective runtime/config detail | Preserve as drill-down | Removing it would lose configuration read-back |
| `/jobs` | Unique queue and maintenance management | Preserve as drill-down | Removing it would remove a management surface |
| `/storage` | Unique database diagnostics, distinct from logical managed files | Preserve as admin drill-down | It is not a replacement for `/files` and cannot be safely redirected |
| `/security` | Unique identity, role, token, MFA, and audit management | Preserve and connect to `/secrets` | Removing it would delete the only security administration surface |
| Provider-specific storage UI | No product capability requires provider coordinates | Do not create; provider-neutral logical health remains canonical | Provider opacity is a security boundary |

No route is destructively removed in Wave 6B because every remaining legacy-looking route above retains a unique management, audit, security, or diagnostic capability. The cleanup is limited to shared shell/navigation/filter/high-risk primitives with demonstrated coverage. This is the fail-closed outcome required by the capability inventory.

## Accessibility and responsive acceptance

- Domain navigation exposes `aria-current` and visible keyboard focus.
- Inventory rows are keyboard-focusable; table overflow remains inside the named table shell.
- Desktop, tablet, mobile, and 125% large-text layouts reject body-level horizontal overflow and overlapping primary panels.
- Loading, empty, degraded, error, denied/omitted, selected, and disabled high-risk states remain explicit.
- Browser black-box checks reject provider hostnames, storage schemes, signed-request markers, and execution-provider details.

## Release gate

Wave 6B passes only when focused API/UI/browser checks, full solution regression, formatting, Compose validation, dependency audit, and an in-memory security diff review all pass with zero failures and zero skips. After exact `origin/dev` and `origin/main` read-back and ContextHub evidence sync, Wave 6 is complete and work stops before Wave 7.
