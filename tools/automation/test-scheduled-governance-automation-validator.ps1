[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "ScheduledGovernanceAutomation.Common.ps1")

function Assert-ScheduledGovernanceValidation {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][scriptblock]$Mutation
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
    }
    finally {
        Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
    }
}

$positive = Test-ScheduledGovernanceAutomationSpec -Path (Get-ScheduledGovernanceAutomationDefaultSpecPath)
if (-not $positive.Valid) {
    throw "The canonical scheduled governance Automation artifact must remain valid."
}

Assert-ScheduledGovernanceValidation -Name "general-tool-fallback" -Mutation {
    param($spec)
    $spec.catalog.toolNames[0] = "governance_batch_execute"
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
    negativeCases = 6
    result = "fail-closed-validator-self-test-passed"
} | ConvertTo-Json -Compress
