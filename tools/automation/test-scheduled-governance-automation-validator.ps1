[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "ScheduledGovernanceAutomation.Common.ps1")

function Assert-ScheduledGovernanceValidation {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][scriptblock]$Mutation,
        [string]$ExpectedReason = ""
    )

    $canonical = Get-Content -LiteralPath (Get-ScheduledGovernanceAutomationDefaultSpecPath) -Raw |
        ConvertFrom-Json
    & $Mutation $canonical

    $temporaryPath = Join-Path ([System.IO.Path]::GetTempPath()) (
        "contexthub-automation-validator-{0}.json" -f [Guid]::NewGuid().ToString("N"))
    try {
        $canonical | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $temporaryPath -Encoding UTF8
        $validation = Test-ScheduledGovernanceAutomationSpec -Path $temporaryPath
        if ($validation.Valid) {
            throw "Negative validator case '$Name' was accepted."
        }
        if (@($validation.Errors).Count -eq 0) {
            throw "Negative validator case '$Name' did not provide a fail-closed reason."
        }
        if ($ExpectedReason -and $ExpectedReason -cnotin $validation.Errors) {
            throw "Negative validator case '$Name' did not report the expected reason: $ExpectedReason"
        }
    }
    finally {
        Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
    }
}

$positive = Test-ScheduledGovernanceAutomationSpec -Path (Get-ScheduledGovernanceAutomationDefaultSpecPath)
if (-not $positive.Valid) {
    throw "The canonical scheduled governance Automation artifact must remain valid. $($positive.Errors -join ' ')"
}

Assert-ScheduledGovernanceValidation -Name "general-tool-fallback" -Mutation {
    param($spec)
    $spec.catalog.toolNames[0] = "governance_batch_execute"
}
Assert-ScheduledGovernanceValidation -Name "invalid-published-tool-schemas-hash" -Mutation {
    param($spec)
    $spec.catalog.publishedToolSchemasHash = "invalid"
}
Assert-ScheduledGovernanceValidation -Name "well-formed-but-wrong-tool-schemas-hash" `
    -ExpectedReason "publishedToolSchemasHash does not match the checked-in four-tool schemas." -Mutation {
    param($spec)
    $spec.catalog.publishedToolSchemasHash = "0" * 64
}
Assert-ScheduledGovernanceValidation -Name "stale-catalog-with-matching-server-version" `
    -ExpectedReason "publishedCatalogVersion does not match the checked-in catalog." -Mutation {
    param($spec)
    $spec.catalog.publishedCatalogVersion = "2026-09-28-automation-v8"
    $spec.catalog.runtimeIdentity.serverVersion =
        "$($spec.catalog.publishedCatalogVersion)+$($spec.catalog.publishedCatalogHash.Substring(0, 12))"
}
Assert-ScheduledGovernanceValidation -Name "observed-runtime-version-is-not-catalog-identity" `
    -ExpectedReason "runtimeIdentity.serverVersion does not match catalog identity." -Mutation {
    param($spec)
    $spec.catalog.runtimeIdentity.serverVersion = "v1.1.116"
}
Assert-ScheduledGovernanceValidation -Name "receipt-recovery-tool-duplicated" `
    -ExpectedReason "catalogTools must describe each of the four scheduled governance tools exactly once." -Mutation {
    param($spec)
    $spec.catalogTools[3] = $spec.catalogTools[0]
}
Assert-ScheduledGovernanceValidation -Name "receipt-recovery-option-missing" `
    -ExpectedReason "Receipt recovery must expose exactly governanceRunId and includeReliabilityHistory." -Mutation {
    param($spec)
    $receipt = $spec.catalogTools | Where-Object name -CEQ "scheduled_governance_run_get"
    $receipt.inputFields = @("governanceRunId")
}
Assert-ScheduledGovernanceValidation -Name "receipt-recovery-prompts-for-full-history" `
    -ExpectedReason "The orchestration prompt must disable reliability history for intermediate receipt recovery." -Mutation {
    param($spec)
    $spec.orchestration.prompt = $spec.orchestration.prompt.Replace(
        "includeReliabilityHistory to false", "includeReliabilityHistory to true")
}
Assert-ScheduledGovernanceValidation -Name "chat-gateway-fallback" -Mutation {
    param($spec)
    $spec.orchestration.prompt = "$($spec.orchestration.prompt) Fall back to /mcp-chat."
}
Assert-ScheduledGovernanceValidation -Name "non-fresh-run" -Mutation {
    param($spec)
    $spec.orchestration.runIdPolicy = "reuse-previous"
}
Assert-ScheduledGovernanceValidation -Name "client-project-selection" -Mutation {
    param($spec)
    $spec.orchestration.projectSelection = "client-explicit"
}
Assert-ScheduledGovernanceValidation -Name "production-synthetic-fixture" -Mutation {
    param($spec)
    $spec.acceptance.a1.productionSyntheticFixtureCreation = $true
}
Assert-ScheduledGovernanceValidation -Name "irreversible-policy" -Mutation {
    param($spec)
    $spec.contract.fixedPolicy.reversibleOnly = $false
}

[ordered]@{
    valid = $true
    positiveCases = 1
    negativeCases = 13
    result = "fail-closed-validator-self-test-passed"
} | ConvertTo-Json -Compress
