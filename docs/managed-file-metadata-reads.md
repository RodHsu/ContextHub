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

## Protected size/hash and masked audit reads

Ordinary inventory and history responses still omit size and content hash. A separate mutation-free `POST /api/files/{fileId}/versions/{versionId}/content-metadata` takes `{ "projectId": "...", "purpose": "..." }`. Purpose must be explicit, nonblank and at most 200 characters; keep secret material out of this field. It is sent in the body rather than the URL. The exact logical pin is resolved in the existing tenant/owner/admin/service and project scope, then the same download checks used by transfer initialization validate the object/project, Ready/NeedsRescan, classification, purpose, effective `download` right and unassigned-uploader rule. This read never creates a transfer session or invokes the access-recording authorization mutation.

Only after that check does the response contain `fileId`, `fileVersionId`, `sizeBytes` and the stored `contentSha256`. Missing/invalid persisted hash or length is unknown, not fabricated. These values are stored metadata, not a newly computed byte-integrity receipt. Unavailable, denied or mismatched resources share an empty 404; ordinary metadata permission alone does not return these fields.

`GET /api/files/{fileId}/versions/{versionId}/audit?projectId=...&limit=25&offset=0` reuses the existing security audit reader predicate, `actor.IsAdmin`, after authentication/read scope, and additionally requires the current tenant/project and logical version's metadata permission. It grants no new role or right. Unauthorized readers receive 403 without a resource lookup; unavailable authorized references return 404. No broader discovery follows from this endpoint.

Audit pages identify the requested file/version and contain only event ID, operation and time. Actor and purpose are masked by omission from the server projection. Denied events and raw reason strings are excluded before paging; no denied count, timing, actor, target or hidden-resource detail is returned. `actorMasked`, `purposeMasked` and `deniedDetailsMasked` are always true, independently of whether denied events exist. `hasMore` refers only to another visible successful event. An empty page therefore does not prove absence of all audit activity. Ordering is descending event time, then ID; bounded queries and visible offsets follow the same page limits as metadata reads.

Both disclosures use no-store responses and cooperative bounded cancellation. The Files workspace requests them only on an explicit action, clears results on scope/pin changes and rejects obsolete replies/errors. Raw audit actor/purpose/reason values are never included in a hidden DOM or response field. Reveal, quarantine release and transfer operations remain unchanged. These checks are per-request and do not introduce an atomic policy-change lock, immediate revocation of already displayed data, new readers, or Production acceptance.
