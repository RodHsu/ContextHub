[CmdletBinding()]
param(
    [string]$Path = ""
)

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "ScheduledGovernanceAutomation.Common.ps1")

if ([string]::IsNullOrWhiteSpace($Path)) {
    $Path = Get-ScheduledGovernanceAutomationDefaultSpecPath
}

$resolvedPath = try {
    (Resolve-Path -LiteralPath $Path -ErrorAction Stop).Path
}
catch {
    $Path
}

$validation = Test-ScheduledGovernanceAutomationSpec -Path $Path
$result = [ordered]@{
    valid = [bool]$validation.Valid
    artifact = "ContextHub.ScheduledGovernanceAutomation"
    path = $resolvedPath
    checks = @(
        "exact-four-tools",
        "oauth-scope-and-resource",
        "four-hour-cadence-and-six-run-window",
        "fresh-run-and-server-only-scope",
        "decision-branch-safety",
        "a0-no-executor",
        "a1-production-natural-or-isolated-synthetic",
        "compact-evidence",
        "forbidden-fallback-and-authority"
    )
    errors = @($validation.Errors)
}

$json = $result | ConvertTo-Json -Depth 20 -Compress
Write-Output $json

if (-not $validation.Valid) {
    exit 1
}
