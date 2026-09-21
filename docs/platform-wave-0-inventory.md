# Platform Wave 0 inventory

> Authority baseline: Work Item `c4d248ab-d392-4b5c-a7eb-7f36ffaf6169`, Platform Master `aecc1f80-699b-4cdd-8836-9aa3cfa07bdb`, Architecture Freeze `59809328-a444-4cd7-9992-e31e80a1d218`.

Inventory date: 2026-09-21. Repository baseline: `77551083ac062c2fb27cd1c4ba504b8ce0a76554`.

## DefinitionState migration

- Next migration is `044_project_work_item_definition_state.sql` after `043_agent_execution.sql`.
- `WorkStatus` remains execution authority: `Pending`, `InProgress`, `Blocked`, `Completed`, `Cancelled`.
- `DefinitionState` is independent definition maturity: `Discussing`, `Draft`, `ReadyForDevelopment`, `Frozen`, `Superseded`.
- Existing rows are never classified from `WorkStatus`. Explicit compatibility labels are recognized; unmarked rows receive the conservative `Draft` default.
- More than one explicit compatibility label on a row aborts the migration and rolls the transaction back.
- Current read-only production inventory covered 27 readable projects and 193 work items: 0 conflicting rows, 11 rows with one explicit compatibility label, 182 unmarked rows, and 43 unmarked `InProgress` rows. The latter remain `InProgress + Draft`, proving that execution status is not reinterpreted as discussion maturity.

## Project hierarchy baseline

- Persistence: `project_hierarchies` from migration `016_project_discussions.sql`; identity is tenant, owner, parent, child.
- Model and mapping: `ProjectHierarchy` and `MemoryDbContext`.
- Producer: `ProjectDiscussionService.SetChildrenAsync`; REST `/api/discussions/hierarchy/{parentProjectId}` and MCP/ChatGPT `project_hierarchy_set_children`.
- Consumers: `ProjectDiscussionService.GetChildrenAsync`, `FullGovernancePlanService`, `GovernanceBatchExecutor`, Dashboard Project Information, governance review coverage, and autonomous-retention evidence locking.
- Existing storage already permits several parent rows for the same child, but has no cycle guard, authorization-inheritable edge flag, typed dimension, topology revision, or authorization semantics. Legacy set-children remains metadata-only until Wave 1 establishes the new contract.

## Legacy Artifact/ObjectRef inventory

### Producers

- `ProjectArtifactExchangeService.PublishAsync` accepts caller-supplied `ProjectArtifactObjectRef` for `FileReference` and `ExternalObject`.
- `ProjectArtifactExchangeService.UploadManagedObjectAsync` calls `IProjectArtifactObjectStore.UploadAsync` and publishes an `ExternalObject`.
- Direct MCP and ChatGPT Gateway expose `project_artifact_publish` and `project_artifact_upload_object`; proposal application can publish artifacts through `ChatGptProposalService`.

### Consumers

- `ProjectArtifactExchangeService` list, search, get, expiration pruning, metadata serialization, and content previews.
- `FullGovernancePlanService` artifact governance projection.
- `S3CompatibleProjectArtifactObjectStore` upload/delete implementation.
- Direct MCP, ChatGPT Gateway, MCP tool catalog, Dashboard MCP Tools documentation, public MCP usage documentation, API/ChatGPT/integration/unit regressions.

Repository-managed consumer files outside tests/docs: 9 (`Contracts.cs`, `ProjectArtifactExchangeService.cs`, `ChatGptProposalService.cs`, `Services.cs`, `MemoryMcpTools.cs`, `McpPublishedToolCatalog.cs`, `ChatGptGatewayTools.cs`, `McpTools.razor`, `ProjectArtifactObjectStorage.cs`). Unmanaged consumer count is 0 in the repository and current persisted runtime inventory. Externally cached clients are not observable, so the legacy contract is intentionally preserved through Waves 0 and 1.

Current read-only production artifact inventory covered the same 27 projects and 45 records: 42 `Summary`, 3 `FileReference`, 0 `ExternalObject`, and 0 non-null `ObjectRef`.

## Provider leakage inventory

The following provider-specific fields remain on the legacy public contract and must be removed only in the authorized breaking cutover wave:

- `ProjectArtifactObjectRef.Provider`, `Bucket`, `Key`, `Uri`.
- `ProjectArtifactResult.ObjectRef` returned by list, search, and get.
- `ProjectArtifactExpiredObjectPruneItem.Bucket` and `Key`.
- Metadata JSON `objectRef` and generated preview text containing provider/bucket/key.
- Object-store configuration and implementation fields for provider, endpoint, bucket, public base URL, access key, and secret key. Configuration is server-side, but error/response projection must remain under provider-leak review.

Current persisted runtime leakage count is 0 because no artifact has a non-null `ObjectRef`. This does not make the contract safe; it only confirms there is no data migration ambiguity for the current rows.

## Dashboard and reusable primitives

- Dashboard currently has 31 routed pages. Wave 0 changes only `/project-work-items` to create, display, filter, and update typed definition maturity.
- `/project-information` owns current hierarchy editing. `/agent-executions` and `/skills` are existing Production capabilities and are reuse points, not rebuild targets.
- Reusable AgentExecution primitives: immutable package hash/context version, lease token fencing/version, append-only events, idempotent operation keys, capability/tool compatibility, and skill snapshot revalidation.
- Reusable Skills primitives: immutable published versions/content hashes, dependency/conflict closure, search generation/resolution/pins, materialization lifecycle, and append-only invocation evidence.

## Migration dependencies and cutover manifest

| Wave | Allowed change | Preserved boundary |
| --- | --- | --- |
| 0 | Add typed Work Item `DefinitionState`; update DB/domain/application/REST/MCP/ChatGPT/Dashboard/governance projections | No Artifact/ObjectRef cutover; no Managed Files, Secrets, or Storage Gateway |
| 1 | Add topology edge semantics, effective authorization foundation, revisions/cache validation, and canonical tag base | Legacy hierarchy tools remain compatible; Artifact tools and provider-specific payload remain unchanged |
| 2+ | Consume the Wave 1 evaluator from Managed Storage/Files/Secrets | No inference of hierarchy from ProjectId names |
| 7 | Replace provider-specific Artifact/ObjectRef APIs and perform the breaking migration plus catalog/schema/hash bump and host reacceptance | Only this wave may remove the legacy Agent-facing contract |

## Wave 1 safe modification boundaries

- Add new tables/columns and services without deleting or reinterpreting existing hierarchy rows.
- Every authorization-inheritable edge must be explicit; existing rows default to non-inheritable until a dry-run supplies an unambiguous decision.
- Add cycle prevention and optimistic revisions at the database and application layers.
- Keep token/project hard authorization as the outer non-observability boundary; the effective-right engine can only further restrict or explain access.
- Tags may rank or classify only after the ACL hard filter. Suggestions cannot directly create canonical definitions.
- Full reconciliation may detect/repair drift or prewarm caches, but request-time revision validation remains authority.
- Do not modify Scheduled Governance exactly-four publication, AgentExecution/Skills lease semantics, Artifact APIs, Managed Files, Secrets, Storage Gateway, or Dashboard information architecture.
