# Managed file metadata reads

The existing `/api/files` array and MCP inventory contracts remain available. The additive REST reads require authentication, `memory:read`, the explicitly supplied project scope, and the existing resource-level `metadata` right:

- `GET /api/files/inventory?projectId=...&limit=25&offset=0`
- `GET /api/files/{fileId}?projectId=...`
- `GET /api/files/{fileId}/versions?projectId=...&limit=25&offset=0`
- `GET /api/files/{fileId}/versions/{versionId}?projectId=...`

Pages contain `items`, `limit`, `offset`, and `hasMore`. Limits are clamped to 1–100 and negative offsets to zero. Candidate queries use batches of at most 100; only authorized rows consume the visible offset, and `hasMore` requires another authorized row. No candidate total or denied-row count is returned. Reads propagate cancellation and use a cooperative 15-second budget matching the existing Dashboard client window; expiry fails the request rather than returning an incomplete page as complete. A database/provider must honor cancellation; this is not a hard process-kill guarantee.

Inventory ordering is descending asset update time, then ID. Version ordering is descending version number, then ID. Offset pagination is not a snapshot under concurrent changes. Every request rechecks authorization. A pinned version must belong to the requested visible file and need not be the latest; it retains current classification and access checks. Deleted files/versions, unavailable resources, and mismatched file/version pairs return 404 without distinguishing the reason.

These reads expose the same safe status fields as the existing inventory, including classification, rescan status and counts. They preserve existing inventory visibility for quarantine/hold status without granting content, transfer or security-operation permissions. They do not invoke the authorization endpoint that records access events. Content hashes, sizes, audit actor/purpose records, hold targets, object IDs, deduplication keys, encryption metadata and provider locators are not part of this projection.

The existing Files workspace consumes authorized pages, labels counts and filtering as page-local, and supports latest/pinned metadata and version history. An older server may fall back to the legacy inventory only with an explicit incomplete/unknown-pagination notice. Permission and service failures are not treated as an empty inventory.
