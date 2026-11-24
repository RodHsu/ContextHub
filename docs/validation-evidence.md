# Validation evidence

Record verification against an exact Git commit so it can be compared with later code changes. Keep the environment and test scope with each result.

| Evidence | Retention |
| --- | --- |
| Shareable verification summary | GitHub. Include the tested commit, commands or test scope, outcome, and any limits. |
| Project decisions and operational evidence index | ContextHub under the repository's ProjectId. Use an existing work item for unresolved actions. |
| Raw TRX, screenshots, environment-specific logs, local backups | Private workspace or an approved private archive. Keep a source commit and checksum for evidence that must be retained. |
| Rebuildable binaries, intermediate files, and test caches | Temporary workspace. Retire them after checking process dependencies and saving the results. |

## Versioned Work Item editing and proposal recovery

This record applies to [commit 2c5c3cc](https://github.com/RodHsu/ContextHub/commit/2c5c3cc3c9e8e3d6e0da179311fe74d0363042ed) and the integrated Work Item changes in [PR #8](https://github.com/RodHsu/ContextHub/pull/8), [PR #9](https://github.com/RodHsu/ContextHub/pull/9), and [PR #10](https://github.com/RodHsu/ContextHub/pull/10).

| Check | Verified result |
| --- | --- |
| Solution validation | Offline restore, build, and format verification passed. The build retained one existing CS8604 warning. |
| Unit and Dashboard tests | 898 unit tests and 12 focused Dashboard tests passed. The focused cases cover proposal decisions and account export identity. |
| Proposal recovery | An isolated PostgreSQL 17.12 fixture applied 63 migration scripts. After a forced process exit, the new process recovered the committed receipt once. A proposal with only a dispatch marker remained unconfirmed and was never redispatched. |
| Scheduled OAuth | Four isolated Gateway cases passed against PostgreSQL and Redis. They verified audience separation and rejection of unauthorized or disabled test identities. |

These results cover the listed checks on an isolated test setup. Production rollout evidence is tracked separately. See [the Work Item editing contract](work-item-editing-core.md) and [proposal recovery](work-item-proposal-recovery.md) for behavior and acceptance boundaries.
