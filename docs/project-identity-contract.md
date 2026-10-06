# Project identity

Project IDs retain their spelling in stored records and API responses. Duplicate
checks, lookups, authorization references and cache scopes treat case variants as
one identity: `TT`, `Tt`, `tT` and `tt` identify the same project. Tenant, owner,
principal, resource and permission boundaries still apply independently.

`ProjectContext.Normalize` trims input without changing its spelling.
`ProjectContext.IdentityKey` provides a separate, versioned identity key. Contract
v1 freezes Unicode simple uppercase and whitespace rules so a runtime upgrade
cannot silently change database uniqueness or cache identity. It does not remove
accents, combine Unicode normalization forms or expand ligatures. Malformed UTF-16
is rejected. Other identifiers, query text and embedding model keys retain their
own contracts.

The SQL map resolves common ASCII characters before its frozen Unicode pairs.
ASCII lowercase retains its uppercase mapping; uppercase, digits, punctuation and
supported control characters have explicit identity pairs. This avoids scanning
the entire Unicode table for unchanged ASCII characters during broad queries.
The optimization retains contract v1, stored spelling and every existing key.

## Database and references

EF queries and parameterized SQL use the same `public.project_identity_key` and
`public.project_identity_equals` functions. Migration 056 adds functional indexes
without rewriting project IDs. Identity uniqueness retains the existing index's
other keys, predicate and NULL uniqueness behavior. Project metadata, project
Skill bindings, discussion participants and projection authority have explicit
identity constraints.

Project-filtered log lookups also index the identity key and descending creation
time. This preserves selective lookup and time-ordered limits for both aliases and
missing projects; the original spelling-based index remains available. Budget
index build time, additional storage and WAL within the coordinated migration
window before applying the contract to a larger log dataset.

The migration checks existing collisions before creating indexes. A collision
fails the migration; its transaction also rolls back its functions, indexes and
migration receipt. Operators must investigate the affected authority scope and
resolve it explicitly. The migration does not choose a canonical record, merge
owners or delete data. Application upserts retain an existing record's spelling;
Project Skill binding updates still require the current revision.

Generated global shared summaries also use their source ProjectId's identity.
Only the ProjectId suffix of `shared-summary:<ProjectId>` participates; ordinary
external keys retain their own exact contract. Alias refreshes update one existing
summary and preserve its external key and source-reference spelling. The migration
rejects multiple alias summaries or inconsistent source references without picking
a winner. A partial unique index prevents concurrent alias inserts; tenant-owned,
manual and nonsummary records are outside this generated-summary authority.

## Cache validity and rollout

Retrieval result keys use namespace v4. Dashboard memory and log keys use v3. Project
invalidation signals and graph projection publication use an identity v1
namespace. Embedding cache keys are unchanged by project identity.

Durable revisions preserve all legacy `project:<spelling>` rows and sum their
revisions by identity in one committed database snapshot. Thus an increment from
any old or new spelling invalidates every alias. Nonproject scopes, including
tenant/user scopes, keep their exact boundaries. Revisions must not be reset or
collapsed by selecting the largest alias revision.

Deploying this contract requires a coordinated release of readers and workers,
an authority collision preflight, migration rehearsal and authenticated runtime
acceptance. Local tests and an additive migration file do not establish Production
acceptance. Restoring older application code after accepting new case alias
writes requires a separate compatibility assessment; old readers use exact
selectors. Preserve the pre-release backup and revision ledger.
