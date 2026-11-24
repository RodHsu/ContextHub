# Scheduled Governance Automation Readiness

`scheduled-governance-automation.json` is the offline canonical contract for the ContextHub scheduled Automation migration. It is a reviewable artifact, not a live ChatGPT Automation export and it must not be copied into or edited through the production UI by a script.

Validate the artifact without network access:

```powershell
pwsh -NoProfile -File tools/automation/validate-scheduled-governance-automation.ps1
pwsh -NoProfile -File tools/automation/test-scheduled-governance-automation-validator.ps1
```

The validator enforces the exact four-tool catalog, the published aggregate input/output schema identity, `governance:scheduled` scope, `/mcp-automation` resource, four-hour cadence, six-run reliability window, fresh run identifiers, server-only scope resolution, all four server decisions, fixed reversible bounds, isolated-only synthetic fixtures, compact evidence, and forbidden fallback or authority controls. Custom spec paths are checked against the catalog version and digest in the checked-in artifact. Gateway contract tests separately compare that artifact with the server schema. The initialize server version represents the catalog identity.

For intermediate receipt recovery, send the same run ID with `includeReliabilityHistory: false`. If the receipt is unavailable or cannot establish the original batch outcome, stop and report an unknown outcome without resubmitting. Full history is requested for final reliability qualification. The offline self-test rejects catalog/schema drift, a lost or disabled receipt recovery option, general-tool fallback, `/mcp-chat`, reused run identifiers, client-selected projects, Production synthetic fixtures, and irreversible policy changes.

After the Automation OAuth gate is available, run the black-box verifier. Without a token it performs only protected-resource discovery and offline contract readiness; it never attempts a different surface or an alternate transport.

```powershell
pwsh -NoProfile -File tools/test-contexthub-scheduled-governance-acceptance.ps1 `
  -Mode A0 `
  -Endpoint https://context-hub.example.com/mcp-automation `
  -OAuthResource https://context-hub.example.com/mcp-automation `
  -RequireAuthorizationToken
```

`-Mode A1 -A1Environment Production` consumes only a server-reported naturally available reversible candidate. The runner never creates fixtures. Synthetic fixtures require an externally provisioned isolated endpoint and explicit `-AllowIsolatedSyntheticFixtures`; that switch is rejected for Production.

For A1, provide `-A1CheckpointPath` in an existing directory, or provide `-EvidencePath` to use its `.a1-checkpoint.json` sibling. Use one checkpoint path per acceptance attempt and retain it with the evidence. The runner claims that path before the first review and saves the run ID and a digest of the endpoint, OAuth resource, environment and contract. A later invocation with the same checkpoint reads only that run's receipt and writes the result to stdout with a non-acceptance exit code. Use a new checkpoint only when starting a separately authorized attempt. This file coordinates runners sharing the same path on one filesystem.

The runner uses only the four published tools. It checks initialize identity, catalog and schema, invokes forbidden tool names only to confirm rejection, and records compact JSON evidence. A0 never calls the executor. A1 starts from a fresh review receipt with no earlier batch. It reads the completed batch receipt before replay and checks the approved snapshot, contract, execution totals, and audit IDs. An unavailable or mismatched receipt stops the verifier before replay. After sending the original request again, the verifier checks the replay response and reads back the durable counts, request identity, and audit IDs before same-run re-review. After a lost response, it reads the same-run receipt once and records uncertainty without calling the executor again. Protected `DisplayName` and business Work Item evidence requires an independent read-only observation before and after the run. To coordinate the after observation, supply `-ProtectedInvariantReadbackRequestPath` and `-ProtectedInvariantEvidencePath` with separate, fresh paths. The verifier writes the request atomically after validating the final review receipt, then waits up to `-ProtectedInvariantReadbackTimeoutSec` (30 seconds by default). The observer reads the request, collects after values for the same scope as before, and atomically publishes the manifest at the evidence path. The request contains only the binding shown below. Copy it unchanged. Keep the path for the verifier's own `-EvidencePath` distinct from both files.

The manifest must match the exact run, validated OAuth resource, and initial and final review receipt IDs. A missing or unmatched manifest leaves acceptance incomplete. The binding correlates observations with a run. The observer remains responsible for collecting the values from the authorized scope.

A complete review must prove that the authorized, covered, scanned and total memory counts agree, with Shared counted once and user scope handled separately. The initial receipt and the A0 final receipt must each confirm no batch, zero execution totals and an empty audit ID array. For A1 the final receipt must confirm the re-review decision and preserve the request identity, audit IDs and cumulative counts verified after replay. The verifier requests the protected manifest after those checks pass.

An A1 final review can stop at `HumanDecisionOnly`. An available exception delta must report a new or escalated exception. With no delta, the review must report a human-decision candidate or governed exception. The verifier records these counts and makes no additional executor call after that decision.

An initial execution or exact replay timeout, JSON-RPC error, tool error or response that cannot prove the expected run and snapshot triggers one receipt lookup for the same run with history disabled. The lookup must match the verified contract and identify whether the run exists. The verifier records the observation and stops without sending another execution or claiming replay acceptance.

```json
{
  "binding": {
    "governanceRunId": "scheduled-acceptance-example",
    "resource": "https://context-hub.example.com/mcp-automation",
    "initialReceiptId": "84758af4-6c9c-4e82-80bd-8a7b68858727",
    "finalReceiptId": "2a4335df-a9ca-456c-9091-17a928ad87ba"
  },
  "displayName": { "before": "ContextHub", "after": "ContextHub" },
  "businessWorkItems": [
    {
      "id": "6d2aecbd-09eb-4a8a-ae32-55f23c349299",
      "before": { "status": "InProgress", "isArchived": false },
      "after": { "status": "InProgress", "isArchived": false }
    }
  ]
}
```

Use distinct Work Item IDs and record a valid status and JSON boolean before and after each item. Supply an empty array only when the independent readback found no business Work Items in scope.

The runner does not call REST, database, admin, general MCP, or fallback surfaces, and it does not modify the live Automation configuration. Omit `-EvidencePath` to keep evidence on stdout; when used, it writes one compact JSON record to the caller-selected path.
