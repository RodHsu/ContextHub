# Platform Wave 2 managed storage foundation

Wave 2 adds the provider-neutral storage, encryption, and transfer foundations. It does not add the Managed Files, DLP, Secrets, Dashboard, or AgentExecution resource-integration domains and does not remove the legacy Artifact contract.

## Security boundary

- External callers receive only `ManagedObjectRef`, `CapabilityId`, a one-time bearer capability, and ContextHub `/api/transfers` paths. Provider identity, endpoint, container, storage key, direct URL, and provider errors are internal.
- Managed File content uses a random 256-bit DEK per managed object. Each chunk is encrypted independently with AES-256-GCM, a random 96-bit nonce, a 128-bit authentication tag, and AAD binding the tenant, project, object, encryption generation, chunk index, plaintext length, and schema.
- The object store receives ciphertext only. Nonces, tags, ciphertext checksums, wrapped DEKs, and versioned crypto metadata are persisted in PostgreSQL.
- DEKs are wrapped by a separately configured Managed File KEK. KEKs are supplied through the deployment secret environment and are not stored in PostgreSQL, the object store, or the managed-object volume. The `Secret` security-domain enum is reserved only to prevent accidental key/retention-domain reuse; Wave 2 does not implement Secrets.
- KEK rotation rewraps the 32-byte DEK. It does not rewrite large ciphertext objects. Algorithm/schema generation changes remain a separate future background re-encryption operation.
- Decryption is bounded to one configured chunk buffer, and plaintext/ciphertext working buffers are zeroed best-effort before return to the pool.

This is Managed Processing Encryption: TLS terminates at ContextHub, authorization is evaluated, and ContextHub decrypts bounded chunks for an authenticated response. It is not ContextHub-blind E2EE.

## Transfer contract

The REST-only binary path is rooted at `/api/transfers`:

- `POST /api/transfers/uploads` creates a staged object and upload capability.
- `PUT /api/transfers/{sessionId}/chunks/{chunkIndex}` accepts one exact bounded chunk. Required headers are `X-ContextHub-Capability`, `X-Transfer-Revision`, `X-Request-Id`, and `X-Content-SHA256`.
- `POST /api/transfers/{sessionId}/complete` atomically finalizes a complete chunk set.
- `POST /api/transfers/downloads` creates a range-limited download capability for a ready logical object.
- `GET /api/transfers/{sessionId}/content?offset=...&length=...` authenticates and streams only the requested logical plaintext range.
- `DELETE /api/transfers/{sessionId}` revokes a session.

Capabilities are random bearer values; only SHA-256 hashes are persisted. The stored binding includes tenant, owner/actor, project, optional agent/execution, logical object, operation, purpose, expiry, revision, encryption generation, byte limit, sustained byte-rate limit, and concurrency limit. A bounded first-chunk/range burst is allowed, after which the persisted session rate is enforced. Database advisory locking serializes each session. Foreign, expired, revoked, stale-revision, over-limit, and mutated-replay requests fail closed.

## Lifecycle and recovery

`Staged`, `Ready`, `Orphaned`, `Missing`, `Corrupt`, and `Tombstoned` are explicit metadata states. The worker reconciliation hook marks expired staged objects as orphaned and detects missing ready chunks. Store outages fail the run without treating unavailable data as absent. Restore requires all three authorities: ciphertext chunks, PostgreSQL encryption metadata including the wrapped DEK, and the matching external Key Authority version.

Migration `046_managed_storage_encryption_gateway.sql` creates only additive Wave 2 tables. It does not rewrite artifacts or expose provider mappings. Wave 7A later replaced the legacy Artifact/ObjectRef surface with logical Managed File contracts; see [Platform Wave 7A breaking cutover](platform-wave-7a-breaking-cutover.md).

## Deployment configuration

Managed storage is disabled by default. Enabling it requires the managed-object volume and a deployment-secret KEK:

```text
MANAGED_STORAGE_ENABLED=true
MANAGED_FILE_KEK_CURRENT_ID=v1
MANAGED_FILE_KEK_V1_BASE64=<32 random bytes encoded as base64 from the secret authority>
```

`v2` is available as a bounded rotation slot. Keep the old version present until every DEK has been rewrapped and recovery rehearsal has passed. Never place real KEKs in `.env.example`, appsettings, Git, PostgreSQL, or the managed-object volume.
