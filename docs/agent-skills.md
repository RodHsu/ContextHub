# Agent Skills

ContextHub Agent Skills is a tenant-owned portable registry and execution discovery system. It returns legal, relevant candidates; the agent remains responsible for deciding whether zero, one, or multiple Skills apply.

## Portable bundle and provenance

A bundle contains a root `SKILL.md` plus optional `scripts/`, `references/`, `assets/`, and `.contexthub/self-test.json`. Import preview validates normalized relative paths, file-count and decoded-size limits, duplicate paths, symlinks, executable placement, secret/private-key patterns, UTF-8 frontmatter, semantic version, license, provenance, signature attestation, and declarative self-tests. Canonical file ordering and bytes produce the SHA-256 `contentHash`.

`Skill` stores the stable identity and mutable sidecar discovery metadata. `SkillVersion` stores the immutable bundle and provenance. Publishing a risky, executable, network-dependent, or secret-dependent version requires explicit approval. Database enforcement prevents changes to a Published version's bundle, hash, version, provenance, trust, or compatibility fields.

Lifecycle is `Draft -> Published -> Deprecated/Revoked -> Archived`. Default-version updates are optimistic-concurrency checked and support rollback to another Published version. Revocation removes the version at the service hard-filter even if an older index generation still contains a document, marks active materializations revoked, and prevents subsequent exact-version reads or materialization.

## Discovery and execution

At execution startup call `skills_search_for_execution`. Supply project, repository, agent type, objective, capabilities, tools, allowed actions, policy, and an idempotency key. Explicit Skill requests still pass ACL, lifecycle, capability, compatibility, and policy filters.

Search uses the active generation and combines keyword, embedding, tags, `WhenToUse`, binding, lifecycle, Top-N, and threshold signals. A failed embedding call may use bounded keyword-only fallback only when policy permits. No match returns `NoApplicableSkill`; ContextHub never forces an irrelevant Skill.

The agent can:

1. Reject a candidate with `skills_resolution_feedback`, a stable stage/reason class, bounded redacted explanation, and evidence.
2. Run another search round with rejected versions excluded and alternative tags/keywords. `MaxSearchRounds` bounds this loop.
3. Select one or more candidates with `skills_select_for_execution`. ContextHub resolves required dependency constraints, detects cycles/conflicts, caps the closure, and pins exact version plus `contentHash`.
4. Read or materialize only an exact pinned version. Materialization uses a content-addressed read-only cache and separate execution directories.
5. Record invocation start/success/failure evidence and clean the execution directory at termination.

## Telemetry and metadata governance

Append-only idempotent events cover search impressions, selection, rejection/release, materialization, invocation, success/failure, and cancellation. Rejections use these stable stages:

- `SearchCandidateRejected`
- `SelectionCancelledBeforePin`
- `PinnedReleasedBeforeMaterialize`
- `MaterializedRejectedBeforeInvoke`
- `InvocationAborted`
- `PostInvocationRejected`
- `RevokedOrPolicyCancelled`

Telemetry stores query hashes and bounded/redacted evidence rather than raw prompts or secrets. Analytics expose funnel rates and a stage-by-reason matrix. Retention and access follow tenant/owner ACL.

Scheduled metadata governance remains inside the existing exactly-four-tool surface. Review produces typed Skill coverage and quality signals. Bounded Execute may only create an idempotent proposal pinned to the current `MetadataVersion` and metadata hash. It never edits a Published bundle in place. Content defects require a new SkillVersion proposal. A matching pending proposal becomes a governed exception on re-review so the run converges without duplicate proposals.

## REST management surface

The `/api/skills` group supports listing, detail, import preview/import, publish, lifecycle changes, default rollback, bindings, export, reindex, analytics, governance review, execution search/feedback/selection, exact version reads, materialization/cleanup, and invocation evidence. The Dashboard `/skills` page presents registry versions, default pins, analytics, governance count, and an authorized reindex action.

Changing embedding model, version, or search profile creates a shadow generation, builds all eligible Published/Deprecated documents, validates counts and benchmark evidence, retires the prior generation, and activates the new generation. Failed builds remain non-active and preserve the previous active index.
