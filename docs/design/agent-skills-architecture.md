# Agent Skills architecture decision

## Decision

ContextHub owns a portable, tenant-scoped Skill registry and the governed candidate-selection boundary. It provides legal and relevant candidates, but the execution agent remains the authority that chooses zero to many Skills. AgentExecution integration consumes the exact-version contracts; this feature does not implement or complete the broader AgentExecution work item.

```text
portable source -> validate/import Draft -> approve/publish immutable SkillVersion
                         |                     ^
                         +-> no-network sandbox self-test -> signed hash-bound receipt
                                               |
execution startup -> active generation -> hard filters -> hybrid rank -> Top-N
       ^                                                       |
       +-- bounded re-search <- reject + structured evidence <-+
                                                               |
agent selects 0..N -> dependency closure/conflict check -> version+hash pins
       -> execution snapshot hash -> retry/heartbeat revalidation
       -> isolated materialization -> invocation telemetry -> cleanup

raw telemetry -> idempotent ledger -> daily aggregates -> dashboard/signals
source observation -> drift/trust classification -> new Draft or revoke proposal
```

## Reuse boundaries

- REST and MCP resolve the same `ISkillService` application use cases and PostgreSQL entities. There is no separate Skills service or durable Redis authority.
- Search generations are immutable snapshots. A shadow build must contain exactly the currently eligible version set before activation. Rollback is a transactionally atomic generation switch.
- Published bundle/provenance fields are database-protected immutable data. Mutable discovery metadata is a versioned sidecar guarded by metadata version/hash optimistic concurrency.
- Exact pins are the execution authority. The internal AgentExecution boundary emits a deterministic snapshot of package context, resolution/search generation, dependencies, scope policy, and exact content hashes. Retry or heartbeat processing must revalidate this snapshot; context/policy drift requires bounded re-resolution, tampering requires human decision, and revocation stops further Skill use. This boundary does not claim or complete the separate AgentExecution queue/lifecycle work item.
- Materialization uses a content-addressed read-only cache and an execution-specific directory. Cleanup is idempotent and cache ownership is not shared with writable execution state.
- External retrieval, credential use, and signature-key trust are adapter responsibilities. ContextHub accepts bounded observations and records their provenance; it does not turn untrusted source references into server-side fetches.

## Security and privacy invariants

- Management, publish, bind, reindex, security/revoke, read, and execute scopes remain distinct; tenant/owner/project ACL is applied server-side.
- Import rejects traversal, symlinks, duplicate paths, oversized bundles, unsupported executable placement, secret/private-key patterns, and invalid attestations. Declarative fixture validation and executable sandbox execution are separate evidence fields. The executable runner is a dedicated no-network container with a read-only root, bounded resources, private HMAC queue, unprivileged child process, cleared environment, timeout/tree kill, mutation detection, and cleanup; MCP Server never executes bundle code or receives broader Docker socket authority. Risky publication requires Publisher plus Owner/security authority and structured approval evidence; when no trusted sandbox runner exists, an explicit waiver is recorded and sandbox PASS remains false.
- Telemetry stores query hashes and redacted bounded evidence. Reconciliation uses a transaction lock and per-event ledger, retains protected policy/security evidence, and provides longer-lived aggregates without double counting.
- A stale search document cannot leak a Revoked version because lifecycle and policy hard filters are evaluated against current version state.

## Scheduled Governance integration

Skills metadata governance is a typed coverage inside the existing server-side Review → bounded Execute → Re-review → Receipt flow. It does not add a fifth tool. The catalog/schema hash is versioned when typed actions or counters change. Execute may create a hash-pinned proposal or normal project Work Item, but cannot mutate a Published SkillVersion bundle in place. Existing OAuth, ACL, Owner/Admin, DisplayName, and business Work Item invariants remain authoritative.

## Operational consequences

- Embedding model/profile changes require a shadow reindex. Keep the prior generation until the new generation passes count and benchmark evidence.
- Search quality is measured with persisted 2500-Skill fixtures plus precision/recall/Top-N assertions; production corpus drift still requires monitored evaluation.
- Source drift checks are manual/provider-triggered in this implementation. A future scheduler may call the same observation contract, but must not expand the exactly-four-tool governance catalog.
- Production deployment and controlled E2E must wait for any active Scheduled Governance reliability window, then re-run migrations, contract hashes, replay, restart, ACL, and search/revocation checks against the deployed runtime.
