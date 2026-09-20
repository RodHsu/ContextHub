# Agent Execution / Work Dispatch

AgentExecution provides a lease-safe queue for Codex-style repository workers. The authoritative requirement remains the referenced Project Work Item and authority refs in the immutable Execution Package.

## Lifecycle

1. An owner/admin prepares one package for an active Work Item.
2. A compatible worker calls `agent_execution_claim_next` with ProjectId, repository identity, agent type, current capabilities/tools, and an idempotency key. ContextHub revalidates any pinned Skill snapshot before granting the lease.
3. The worker uses the returned lease token and fencing version for heartbeat, checkpoint, block, fail, complete, or abandon.
4. ContextHub records append-only evidence. Completion does not mutate the Work Item lifecycle.
5. If the package contains a Skill snapshot, heartbeat and checkpoint revalidate exact pins and policy. Revocation blocks execution immediately.

REST equivalents are under `/api/agent-executions`. Management cancellation is available at `/api/agent-executions/{executionId}/cancel`; it invalidates any lease without changing the Work Item. The Dashboard page `/agent-executions` shows full queue counts and bounded package/event drill-down without exposing lease tokens.

## Scheduled worker contract

Use this short instruction for a scheduled worker:

> Call `agent_execution_claim_next` once for the configured ProjectId, repository, agent type, current capabilities, and available tools. If the result is `NoEligibleWork`, stop without scanning ContextHub or inventing work. If claimed, treat the returned Execution Package as authoritative, obey `allowedActions` and human gates, report current capabilities/tools on heartbeat/checkpoint/completion, checkpoint bounded evidence, renew only with the exact lease token/version, and end with complete, block, fail, or abandon. Never change the Project Work Item status yourself.

The worker must not broaden requirements, search the entire Work Item backlog, reuse a lease across executions, expose the token, or bypass a Skill revocation decision.

## Recovery and operations

- A crashed worker's lease expires. A later claim records `Expired`, increments the attempt, and requeues only within `MaxAttempts`.
- Retryable failures and retryable abandon operations use bounded backoff. Terminal failure, block, completion, non-retryable abandon, and cancellation are not auto-requeued.
- Exact replay returns the stored response, including the original claim token. A changed payload with the same key is rejected.
- Monitor Ready age, active/expired leases, retryable failures, blocked executions, and terminal failure classes in Dashboard.
- Preserve the workspace and evidence when a worker blocks; do not mark the Work Item completed from execution status alone.

## Production publication and acceptance

Migration `043_agent_execution.sql` is applied after the Skills migration chain including `040_agent_skills.sql`. The nine worker-facing `agent_execution_*` tools are published on both the trusted backend `/mcp` catalog and the OAuth-protected general `/mcp-chat` catalog; they are never published on the exactly-four-tool `/mcp-automation` surface. Server-side acceptance covers claim, checkpoint, completion, read-back, replay, crash handoff, Skill revocation, ACL/lease enforcement, and Work Item lifecycle separation. ChatGPT connector discovery remains a host-controlled acceptance step after each catalog release and must not be inferred from server-side E2E alone.
