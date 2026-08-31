# Scheduled Governance Automation Readiness

`scheduled-governance-automation.json` is the offline canonical contract for the ContextHub scheduled Automation migration. It is a reviewable artifact, not a live ChatGPT Automation export and it must not be copied into or edited through the production UI by a script.

Validate the artifact without network access:

```powershell
pwsh -NoProfile -File tools/automation/validate-scheduled-governance-automation.ps1
pwsh -NoProfile -File tools/automation/test-scheduled-governance-automation-validator.ps1
```

The validator enforces the exact four-tool catalog, `governance:scheduled` scope, `/mcp-automation` resource, four-hour cadence, six-run reliability window, fresh run identifiers, server-only scope resolution, all four server decisions, fixed reversible bounds, isolated-only synthetic fixtures, compact evidence, and forbidden fallback or authority controls. The self-test also proves that general-tool fallback, `/mcp-chat`, reused run identifiers, client-selected projects, Production synthetic fixtures, and irreversible policy changes fail closed.

After the Automation OAuth gate is available, run the black-box verifier. Without a token it performs only protected-resource discovery and offline contract readiness; it never attempts a different surface or an alternate transport.

```powershell
pwsh -NoProfile -File tools/test-contexthub-scheduled-governance-acceptance.ps1 `
  -Mode A0 `
  -Endpoint https://context-hub.example.com/mcp-automation `
  -OAuthResource https://context-hub.example.com/mcp-automation `
  -RequireAuthorizationToken
```

`-Mode A1 -A1Environment Production` consumes only a server-reported naturally available reversible candidate. The runner never creates fixtures. Synthetic fixtures require an externally provisioned isolated endpoint and explicit `-AllowIsolatedSyntheticFixtures`; that switch is rejected for Production.

The runner uses only the four published tools. It checks initialize identity, catalog and schema, invokes forbidden tool names only to confirm rejection, and records compact JSON evidence. A0 never calls the executor. A1 binds the server snapshot, reads the receipt before retrying an unknown outcome, replays the identical request, and performs same-run re-review. Protected `DisplayName` and business Work Item before/after evidence is accepted only from an explicitly supplied external read-only manifest:

```json
{
  "displayName": { "before": "ContextHub", "after": "ContextHub" },
  "businessWorkItems": [
    {
      "id": "readback-id",
      "before": { "status": "InProgress", "isArchived": false },
      "after": { "status": "InProgress", "isArchived": false }
    }
  ]
}
```

The runner does not call REST, database, admin, general MCP, or fallback surfaces, and it does not modify the live Automation configuration. Omit `-EvidencePath` to keep evidence on stdout; when used, it writes one compact JSON record to the caller-selected path.
