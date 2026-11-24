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

The runner uses only the four published tools. It checks initialize identity, catalog and schema, invokes forbidden tool names only to confirm rejection, and records compact JSON evidence. A0 never calls the executor. A1 starts from a fresh review receipt with no earlier batch. It reads the completed batch receipt before replay and checks the approved snapshot, contract, execution totals, and audit IDs. An unavailable or mismatched receipt stops the verifier before replay. After sending the original request again, the verifier checks the replay response and reads back the durable counts, request identity, and audit IDs before same-run re-review. After a lost response, it reads the same-run receipt once and records uncertainty without calling the executor again. Protected `DisplayName` and business Work Item evidence requires an independent read-only observation before and after the run. To coordinate the after observation, supply `-ProtectedInvariantReadbackRequestPath` and `-ProtectedInvariantEvidencePath` with separate, fresh paths. The verifier writes the request atomically after validating the final review receipt, then waits up to `-ProtectedInvariantReadbackTimeoutSec` (30 seconds by default). The observer reads the request, collects after values for the same scope as before, and atomically publishes the manifest at the evidence path. The request contains only the binding shown below. Copy it unchanged. Keep the path for the verifier's own `-EvidencePath` distinct from both files.

The manifest must match the exact run, validated OAuth resource, and initial and final review receipt IDs. A missing or unmatched manifest leaves acceptance incomplete. The binding correlates observations with a run. The observer remains responsible for collecting the values from the authorized scope.

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
