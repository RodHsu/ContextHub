# AgentExecution Architecture Decision Record

Status: Implemented for single ContextHub project/repository dispatch; Production acceptance pending.

## Decision

ContextHub owns an `AgentExecution` bounded context backed by PostgreSQL. A Project Work Item remains the user-managed business task; an AgentExecution is one execution attempt and never implicitly completes, cancels, or archives that Work Item.

```text
Project Work Item
      |
      | management prepare (immutable package + hash)
      v
AgentExecution: Ready -> Claimed -> Running -> Completed
                    |         |          +-> Blocked
                    |         +------------> FailedRetryable -> Ready
                    +----------------------> Expired -> Ready
                                               +-> FailedTerminal / Abandoned

SkillResolution exact pins -> immutable Skill snapshot in Execution Package
                                  |
                                  +-> heartbeat/checkpoint revalidation
                                      Continue | ReResolve | StopRevoked | HumanDecision
```

## Invariants

- A PostgreSQL partial unique index permits at most one `Claimed` or `Running` execution per tenant and Work Item.
- A second partial unique index permits at most one open (`Ready`, `Claimed`, `Running`, `Blocked`, or `FailedRetryable`) execution per tenant and Work Item; a manager must terminally resolve or cancel it before preparing another package.
- `claim_next` applies project ACL, repository, agent type, current capability/tool availability, pinned Skill policy, eligibility, priority, and age on the server. Workers do not scan or choose arbitrary Work Items.
- Lease ownership requires the exact random token and monotonically increasing fencing version. The execution row retains only the token hash; the recoverable token exists only in the encrypted idempotent claim receipt.
- Execution Package identity, policy, content, hash, and Skill snapshot are immutable after preparation. Events and idempotency receipts are append-only.
- Every mutation has a bounded idempotency key. Reuse with a different request hash is rejected.
- Stale and foreign owners fail closed. Expired attempts are audit-recorded and may be safely requeued only while the attempt budget remains.
- Skills are referenced by existing resolution pins and content hashes. AgentExecution does not duplicate search, dependency, conflict, materialization, or revocation logic.
- Scheduled Governance remains a separate exactly-four-tool surface. AgentExecution adds no Scheduled Governance tool and has no authority over Automation or governance acceptance.

## Security

The API uses four least-privilege scopes: `agent-executions:read`, `agent-executions:claim`, `agent-executions:write`, and `agent-executions:manage`. Every operation also applies tenant ownership and ProjectId authorization. Package creation is management-plane only; worker mutation requires an owned live lease.

## Trade-offs

- PostgreSQL advisory locking plus a partial unique index is simpler and safer for the current single-service deployment than introducing Kafka or a separate orchestrator. A separate service is deferred until measured queue or isolation pressure justifies it.
- Retry reuses the same execution identity and increments `Attempt`; append-only events preserve each expiry/failure boundary. This keeps Skill exact pins stable across crash handoff.
- Queue ordering adds one effective priority point per eligible hour (capped at 100), then uses eligible and creation time. This bounded aging prevents a continuously replenished high-priority queue from starving older legal work; more complex tenant-fair scheduling remains deferred until measured demand justifies it.
