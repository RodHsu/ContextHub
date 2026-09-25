# Agent Execution / Work Dispatch

AgentExecution provides a lease-safe queue for Codex-style repository workers. The authoritative requirement remains the referenced Project Work Item and authority refs in the immutable Execution Package.

## Lifecycle

1. An owner/admin prepares one package for an active Work Item.
2. A compatible worker calls `agent_execution_claim_next` with ProjectId, repository identity, agent type, current capabilities/tools, and an idempotency key. ContextHub revalidates any pinned Skill snapshot and resolves logical resource requirements before granting the lease.
3. The worker uses the returned lease token and fencing version for heartbeat, checkpoint, block, fail, complete, or abandon.
4. ContextHub records append-only evidence. Completion does not mutate the Work Item lifecycle.
5. If the package contains a Skill snapshot, heartbeat and checkpoint revalidate exact pins and policy. Revocation blocks execution immediately.

## Logical resource requirements

Contract `2.0` extends the existing Execution Package with logical `File`, `Credential`, `ConnectionProfile`, and `Skill` requirements. The package never stores raw secrets, provider locators, or key-encryption material. Every resolution pass creates an immutable, attempt-scoped `ResolutionSnapshot` containing only resolved version identities, integrity hashes, authority/policy revisions, bounded evidence, and opaque lease identifiers.

A `ResolutionSnapshot` is immutable identity and audit evidence, not materialization authority. Any component that opens a managed file, redeems a secret lease, connects through a profile, or invokes a Skill must revalidate current tenant/project authority, revocation, and security policy at the consumption boundary; a stale snapshot can never override current denial.

Credential requirements contain a bounded logical purpose only. ContextHub derives the SecretLease target from the execution and requirement IDs; callers cannot persist a provider locator, host, path, or secret-bearing target in the package.

- `ReResolve` resolves logical-current requirements again on retry.
- `ReuseSnapshot` reuses the prior exact identities only when current authority, revocation, classification, and integrity checks still pass.
- Credentials require an interactive, step-up-authorized REST approval for the current attempt. Claim returns a short-lived use capability once through the protected idempotent receipt; the package and snapshot never persist the capability value.
- Heartbeat, checkpoint, and successful completion revalidate current resource authority. Revocation or authority drift clears the lease and blocks execution.

Resource approval is management-plane only at `/api/agent-executions/{executionId}/resource-approvals`; it is intentionally not a tenth worker-facing MCP tool.

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

Migration `043_agent_execution.sql` is applied after the Skills migration chain including `040_agent_skills.sql`; migration `050_platform_foundation_c_agent_resources.sql` adds the resource-resolution authority records. The nine worker-facing `agent_execution_*` tools remain published on both the trusted backend `/mcp` catalog and the OAuth-protected general `/mcp-chat` catalog; they are never published on the exactly-four-tool `/mcp-automation` surface. Server-side acceptance covers claim, checkpoint, completion, read-back, replay, crash handoff, Skill/resource revocation, ACL/lease enforcement, and Work Item lifecycle separation. ChatGPT connector discovery remains a host-controlled acceptance step after each catalog release and must not be inferred from server-side E2E alone.
