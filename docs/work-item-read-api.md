# Work item reads

`GET /api/work-items` accepts `projectId`, `status`, `definitionState`, `limit`, `includeArchived`, and `offset`. Offset defaults to zero; negative offsets are treated as zero, as in the Application list contract. The existing status, priority, due date, and update ordering is retained, with ID as the final stable tie-break. Offset pagination is not a snapshot: concurrent changes may move rows between pages.

`GET /api/work-items/{id}?includeArchived=false` returns one work item or 404 when it is absent from the actor's tenant/owner scope. Archived items are excluded unless explicitly requested. Both reads require authentication, `memory:read`, and project access. Service actors retain the existing tenant-wide ownership semantics. Detail reads do not update the work item or mark it read.

These REST additions do not add an MCP tool, change write/CAS contracts, or change WorkStatus and DefinitionState.

The existing project-information workspace also displays read-only canonical tag quality for 7, 30, or 90 days. It uses the existing quality endpoint and its sample threshold. Empty, unsupported, failed, and sample-free results remain distinct; observed quality is neither a registry inventory nor authorization evidence. The panel does not expose tag mutations.
