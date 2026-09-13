# Agent Skills

ContextHub Agent Skills is a tenant-owned portable registry and execution discovery system. It returns legal, relevant candidates; the agent remains responsible for deciding whether zero, one, or multiple Skills apply.

## Portable bundle and provenance

A bundle contains a root `SKILL.md` plus optional `scripts/`, `references/`, `assets/`, and `.contexthub/self-test.json`. Import preview validates normalized relative paths, file-count and decoded-size limits, duplicate paths, symlinks, executable placement, secret/private-key patterns, UTF-8 frontmatter, semantic version, license, provenance, signature attestation, and declarative self-tests. Canonical file ordering and bytes produce the SHA-256 `contentHash`.

`Skill` stores the stable identity and mutable sidecar discovery metadata. `SkillVersion` stores the immutable bundle and provenance. Publishing a risky, executable, network-dependent, or secret-dependent version requires both Publisher authority and Owner/`skills:security` risk approval with a bounded approval reference and reason. `.contexthub/self-test.json` may declare a bounded Linux shell test through `sandbox.entrypoint`, `arguments`, and `timeoutSeconds`; the entrypoint must be an executable `scripts/*.sh` file. Publication sends the exact bundle and content hash to the isolated sandbox and accepts only a matching HMAC receipt. Executable content without a trusted sandbox result additionally requires an explicit self-test waiver; the evidence records the waiver and optional canary reference without claiming a sandbox PASS. Database enforcement prevents changes to a Published version's bundle, hash, version, provenance, trust, or compatibility fields.

Lifecycle is `Draft -> Published -> Deprecated/Revoked -> Archived`. Default-version updates are optimistic-concurrency checked and support rollback to another Published version. Revocation removes the version at the service hard-filter even if an older index generation still contains a document, marks active materializations revoked, and prevents subsequent exact-version reads or materialization.

## Discovery and execution

At execution startup call `skills_search_for_execution`. Supply project, repository, agent type, objective, capabilities, tools, allowed actions, policy, and an idempotency key. Explicit Skill requests still pass ACL, lifecycle, capability, compatibility, and policy filters.

Search uses the active generation and combines keyword, embedding, tags, `WhenToUse`, binding, lifecycle, Top-N, and threshold signals. A failed embedding call may use bounded keyword-only fallback only when policy permits. No match returns `NoApplicableSkill`; ContextHub never forces an irrelevant Skill.

The agent can:

1. Reject a candidate with `skills_resolution_feedback`, a stable stage/reason class, bounded redacted explanation, and evidence.
2. Run another search round with rejected versions excluded and alternative tags/keywords. `MaxSearchRounds` bounds this loop.
3. Select one or more candidates with `skills_select_for_execution`. ContextHub resolves required dependency constraints, detects cycles/conflicts, caps the closure, revalidates every transitive version against the search-time capability/tool/action/risk and scope-binding policy snapshot, and pins exact version plus `contentHash`.
4. Before an AgentExecution package is persisted, call `CreateExecutionSnapshotAsync` through the internal application boundary. The returned contract binds execution/work-item/repository identity, context version, search generation/model/profile, dependency closure, scope-policy hashes, and exact version/content hashes under a deterministic snapshot hash.
5. On retry, heartbeat/checkpoint policy changes, or before materialization/invocation, call `RevalidateExecutionSnapshotAsync`. It returns `Continue`, `ReResolve`, `StopRevoked`, or `RequiresHumanDecision`; context/pin/policy drift cannot silently reuse a stale package, and revocation is terminal for further Skill use.
6. Read or materialize only an exact pinned version. Materialization uses a content-addressed read-only cache and separate execution directories.
7. Record invocation start/success/failure evidence and clean the execution directory at termination.

## Telemetry and metadata governance

Append-only idempotent events cover search impressions, selection, rejection/release, materialization, invocation, success/failure, and cancellation. Rejections use these stable stages:

- `SearchCandidateRejected`
- `SelectionCancelledBeforePin`
- `PinnedReleasedBeforeMaterialize`
- `MaterializedRejectedBeforeInvoke`
- `InvocationAborted`
- `PostInvocationRejected`
- `RevokedOrPolicyCancelled`

Telemetry stores query hashes and bounded/redacted evidence rather than raw prompts or secrets. Analytics expose funnel rates, zero-filled daily trends, and a stage-by-reason matrix by Skill, SkillVersion, Project, repository, or agent type. A transactionally locked reconciliation run aggregates each raw event exactly once through a ledger, preserves protected revocation/policy/permission evidence, deletes ordinary raw events only after at least 90 days, and keeps daily aggregates longer (365–3650 days). Metadata-governance evidence windows are therefore bounded to 90 days. Replaying the same reconciliation idempotency key returns the persisted receipt without double counting.

Scheduled metadata governance remains inside the existing exactly-four-tool surface. Review produces typed Skill coverage and quality signals. Bounded Execute may only create an idempotent proposal pinned to the current `MetadataVersion` and metadata hash. It never edits a Published bundle in place. Content defects require a new SkillVersion proposal. A matching pending proposal becomes a governed exception on re-review so the run converges without duplicate proposals.

## REST management surface

The `/api/skills` group supports listing, detail, import preview/import, publish, lifecycle changes, version diff, default rollback, bindings, export, source observations, reindex and generation activation, analytics trends/reconciliation, governance review, execution search/feedback/selection, resolution audit, exact version reads, materialization/cleanup, and invocation evidence. The Dashboard `/skills` page presents ownership/maintainers, lifecycle management, provenance/source drift, publish checks, dependency constraints, version diff, default pins, bindings, generation rollback, 7/30/90-day analytics and trends, stage-by-reason evidence, recent execution resolutions, metadata-governance signals, and authorized maintenance actions.

Changing embedding model, version, or search profile requires a full rebuild. A full rebuild creates a shadow generation and embeds all eligible Published/Deprecated documents. A compatible incremental build copies unchanged documents from the active generation and regenerates new or metadata-changed documents, while still producing a complete immutable generation snapshot. Activation validates the exact eligible version set and current discovery text, records benchmark/reuse evidence, retires the prior generation, and atomically activates the new generation. Failed builds remain non-active and preserve the previous active index.

External source refresh is an explicit provider boundary. The current service records normalized append-only observations supplied by an authorized source adapter or operator. It classifies content/revision deletion, trust change, signature compromise, or in-sync state against immutable provenance. It never fetches arbitrary URLs and never mutates a Published bundle: content drift requires a new Draft, while trust compromise recommends emergency revocation.

## Operations

- Rebuild with `POST /api/skills/reindex`; activate a validated retired generation with `POST /api/skills/search-generations/{generationId}/activate`. Activation rejects incomplete document sets.
- Use `FullRebuild=false` only when the active generation has the same search profile and embedding model/version. Application restart preserves generation and idempotency receipts in PostgreSQL; repeated activation, indexing, search, feedback, selection, materialization, invocation, and reconciliation operations use transaction locks and exact replay receipts.
- Compare immutable releases through `GET /api/skills/{skillId}/versions/diff`; inspect zero-filled quality trends through `GET /api/skills/analytics/trend`.
- Aggregate and retain telemetry with `POST /api/skills/analytics/reconcile`. Use a durable unique idempotency key per scheduled reconciliation run.
- Inspect tenant/project-authorized decisions at `GET /api/skills/resolutions`; evidence remains bounded and redacted.
- Record upstream checks through `POST /api/skills/{skillId}/source-observations`; the caller is responsible for authenticated source retrieval.
- Revocation is the immediate fail-closed response. Reindex afterward to remove stale documents physically, although service hard filters prevent a stale index from returning the revoked version before that rebuild.
- `skill-sandbox` is a separate compose service with no network namespace, read-only root filesystem, bounded CPU/memory/PIDs, dropped capabilities, a private queue, and a tmpfs job workspace. The supervisor validates request freshness/signature/content hash, drops each script to uid/gid 65534, clears its environment, bounds output and time, detects input mutation, signs the receipt, and cleans the workspace. Configure a dedicated `CONTEXTHUB_SKILL_SANDBOX_RECEIPT_KEY` of at least 32 characters; missing/invalid configuration fails closed and never silently executes in the MCP process.
