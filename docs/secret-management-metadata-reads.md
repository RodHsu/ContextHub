# Secret management metadata reads

The Secrets workspace provides explicit version-expiry, lease-state and masked-history reads. Selecting a secret does not prefetch these views. Reveal and Revoke remain unavailable from this workspace.

## Reader boundary

The new REST reads require an authenticated existing administrator, `secrets:read` and `secrets:manage`, the requested project, the existing tenant/owner or service-actor scope, Foundation `secret.metadata` and `secret.manage`, and the existing Secret Manage grant/policy decision. A grant at the resource tier takes precedence over secret policy, then project policy; Deny wins within the chosen tier. These reads create no role, grant, lease, assertion or capability. Inaccessible, mismatched and unauthorized secret identities remain unavailable.

The endpoints are GET `/api/secrets/{secretId}/management/expiry`, `/leases` and `/history`, each with an explicit `projectId`. The latter two accept `limit` (default25, maximum100) and nonnegative `offset`. They return a single authorized page and a lookahead-derived `hasMore`, without a total. Ordering is timestamp then logical ID. Pages are live queries, not immutable snapshots.

## Safe projection

Expiry reports only the selected secret revision and safe current-version metadata. Missing version or expiry is unknown/unset, never proof of perpetual validity. Lease rows report kind, persisted state, expiry, an explicit expired flag and usage counts. They are diagnostic metadata, never authorization to use a lease; current policy and all existing use checks still apply.

History includes allowed operation/time only. Actor identity, purpose, target, request identity, reason, denied details, capability data, encrypted material and provider location never enter these DTOs. Denied events are excluded before pagination and do not affect a count or masking flag. Empty history is not proof that no other events exist. The UI labels this as a partial safe projection rather than a complete audit receipt.

Responses use `no-store`. Each Application read has a cooperative15-second cancellation budget; an incomplete read fails rather than publishing an empty successful page. Provider cancellation is not a hard database deadline. The workspace discards stale project/secret/revision/tab results, cancels obsolete work and clears projection on selection, scope change or disposal. It does not revoke bytes already displayed to a previously authorized reader.

These source contracts do not establish real-database concurrency, browser or Production acceptance, and do not alter existing secret mutations, assurance requirements, readers of the ordinary metadata inventory or MCP publication.
