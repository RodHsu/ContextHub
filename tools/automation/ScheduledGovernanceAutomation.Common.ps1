Set-StrictMode -Version Latest

$script:ScheduledGovernanceAutomationRoot = $PSScriptRoot

function Get-ScheduledGovernanceAutomationDefaultSpecPath {
    return Join-Path $script:ScheduledGovernanceAutomationRoot "scheduled-governance-automation.json"
}

function Get-ScheduledGovernanceExpectedToolName {
    return @(
        "scheduled_governance_contract_get",
        "scheduled_governance_review",
        "scheduled_governance_execute",
        "scheduled_governance_run_get"
    )
}

function Get-ScheduledGovernanceExpectedDecision {
    return @(
        "NoOpConverged",
        "ReversibleExecutionRequired",
        "HumanDecisionOnly",
        "CoverageIncomplete"
    )
}

function Get-ScheduledGovernanceForbiddenToolName {
    return @(
        "governance_batch_execute",
        "memory_delete",
        "project_cleanup_apply",
        "governance_tombstone_get"
    )
}

function Get-ScheduledGovernanceForbiddenToken {
    return @(
        "/mcp-chat",
        "governance_batch_execute",
        "memory_delete",
        "project_cleanup_apply",
        "governance_tombstone_get",
        "projectIds",
        "explicitProjectId",
        "alternateRunId",
        "REST",
        "DB",
        "admin",
        "direct delete",
        "allowHardDelete",
        "allowMaturedDelete",
        "MaturedDelete"
    )
}

function Get-ScheduledGovernanceProperty {
    param(
        [AllowNull()]
        [object]$Object,
        [Parameter(Mandatory)]
        [string]$Name
    )

    if ($null -eq $Object) {
        return $null
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function ConvertTo-ScheduledGovernanceArray {
    param([AllowNull()][object]$Value)

    if ($null -eq $Value) {
        return @()
    }

    return @($Value)
}

function Test-ScheduledGovernanceSetEqual {
    param(
        [AllowNull()][object[]]$Actual,
        [AllowNull()][object[]]$Expected
    )

    $actualValues = @($Actual | ForEach-Object { [string]$_ } | Sort-Object)
    $expectedValues = @($Expected | ForEach-Object { [string]$_ } | Sort-Object)
    return $actualValues.Count -eq $expectedValues.Count -and
        (($actualValues -join "`n") -ceq ($expectedValues -join "`n"))
}

function Get-ScheduledGovernanceSha256Hex {
    param([Parameter(Mandatory)][string]$Text)

    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha256.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Text))
    }
    finally {
        $sha256.Dispose()
    }

    return ([BitConverter]::ToString($hash) -replace '-', '').ToLowerInvariant()
}

function Get-ScheduledGovernanceCatalogHash {
    $names = @(Get-ScheduledGovernanceExpectedToolName | Sort-Object)
    return Get-ScheduledGovernanceSha256Hex -Text ($names -join "`n")
}

function Add-ScheduledGovernanceSpecError {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[string]]$Errors,
        [Parameter(Mandatory)]
        [string]$Message
    )

    [void]$Errors.Add($Message)
}

function Test-ScheduledGovernanceAutomationSpec {
    param([Parameter(Mandatory)][string]$Path)

    $errors = [System.Collections.Generic.List[string]]::new()
    $spec = $null
    try {
        $raw = Get-Content -LiteralPath $Path -Raw -Encoding UTF8
        $spec = $raw | ConvertFrom-Json
    }
    catch {
        Add-ScheduledGovernanceSpecError $errors "Cannot parse specification JSON: $($_.Exception.Message)"
        return [pscustomobject]@{
            Valid = $false
            Errors = @($errors)
            Spec = $null
            Path = $Path
        }
    }

    if ((Get-ScheduledGovernanceProperty $spec "artifactKind") -cne "ContextHub.ScheduledGovernanceAutomation") {
        Add-ScheduledGovernanceSpecError $errors "artifactKind must identify ContextHub.ScheduledGovernanceAutomation."
    }
    if ((Get-ScheduledGovernanceProperty $spec "artifactVersion") -cne "1.0") {
        Add-ScheduledGovernanceSpecError $errors "artifactVersion must be 1.0."
    }
    if ((Get-ScheduledGovernanceProperty $spec "projectId") -cne "ContextHub") {
        Add-ScheduledGovernanceSpecError $errors "projectId must remain ContextHub."
    }
    if ((Get-ScheduledGovernanceProperty $spec "surface") -cne "/mcp-automation") {
        Add-ScheduledGovernanceSpecError $errors "surface must be /mcp-automation."
    }

    $oauth = Get-ScheduledGovernanceProperty $spec "oauth"
    if ((Get-ScheduledGovernanceProperty $oauth "requiredScope") -cne "governance:scheduled") {
        Add-ScheduledGovernanceSpecError $errors "OAuth requiredScope must be governance:scheduled."
    }
    if ((Get-ScheduledGovernanceProperty $oauth "resourceSuffix") -cne "mcp-automation") {
        Add-ScheduledGovernanceSpecError $errors "OAuth resourceSuffix must be mcp-automation."
    }
    if ((Get-ScheduledGovernanceProperty $oauth "missingTokenMode") -cne "discovery-contract-readiness-only") {
        Add-ScheduledGovernanceSpecError $errors "Missing-token mode must be discovery-contract-readiness-only."
    }
    if ((Get-ScheduledGovernanceProperty $oauth "tokenPersistence") -cne "host-managed-only") {
        Add-ScheduledGovernanceSpecError $errors "OAuth tokens must remain host-managed-only."
    }

    $cadence = Get-ScheduledGovernanceProperty $spec "cadence"
    if ((Get-ScheduledGovernanceProperty $cadence "intervalHours") -ne 4) {
        Add-ScheduledGovernanceSpecError $errors "Automation cadence intervalHours must be 4."
    }
    if ((Get-ScheduledGovernanceProperty $cadence "reliabilityRuns") -ne 6) {
        Add-ScheduledGovernanceSpecError $errors "Reliability evidence requires six natural runs."
    }
    if ((Get-ScheduledGovernanceProperty $cadence "reliabilityWindowHours") -ne 24) {
        Add-ScheduledGovernanceSpecError $errors "Reliability window must be 24 hours."
    }

    $catalog = Get-ScheduledGovernanceProperty $spec "catalog"
    $catalogNames = @(ConvertTo-ScheduledGovernanceArray (Get-ScheduledGovernanceProperty $catalog "toolNames"))
    if ($catalogNames.Count -ne 4 -or
        -not (Test-ScheduledGovernanceSetEqual $catalogNames (Get-ScheduledGovernanceExpectedToolName))) {
        Add-ScheduledGovernanceSpecError $errors "Catalog must contain exactly the four scheduled governance tools."
    }
    if ((Get-ScheduledGovernanceProperty $catalog "toolCount") -ne 4) {
        Add-ScheduledGovernanceSpecError $errors "Catalog toolCount must be 4."
    }
    $expectedCatalogHash = Get-ScheduledGovernanceCatalogHash
    if ((Get-ScheduledGovernanceProperty $catalog "publishedCatalogHash") -cne $expectedCatalogHash) {
        Add-ScheduledGovernanceSpecError $errors "publishedCatalogHash does not match the canonical four-tool set."
    }
    if ((Get-ScheduledGovernanceProperty $catalog "publishedCatalogVersion") -cne "2026-08-31-automation-v3") {
        Add-ScheduledGovernanceSpecError $errors "publishedCatalogVersion does not match the deployed automation contract."
    }
    $runtime = Get-ScheduledGovernanceProperty $catalog "runtimeIdentity"
    if ((Get-ScheduledGovernanceProperty $runtime "serverName") -cne "Memory.ScheduledGovernanceGateway") {
        Add-ScheduledGovernanceSpecError $errors "runtimeIdentity.serverName is not the scheduled gateway."
    }
    if ((Get-ScheduledGovernanceProperty $runtime "serverVersion") -cne "2026-08-31-automation-v3+$($expectedCatalogHash.Substring(0, 12))") {
        Add-ScheduledGovernanceSpecError $errors "runtimeIdentity.serverVersion does not match catalog identity."
    }

    $contract = Get-ScheduledGovernanceProperty $spec "contract"
    if ((Get-ScheduledGovernanceProperty $contract "toolContractVersion") -cne "1.2") {
        Add-ScheduledGovernanceSpecError $errors "Scheduled tool contract version must be 1.2."
    }
    if ((Get-ScheduledGovernanceProperty $contract "schemaHash") -cne "de1a67e9a2d6f5160d975fc3f4414c220ebbd7f68c6b66bc86e4e506b6244ee8") {
        Add-ScheduledGovernanceSpecError $errors "Scheduled execute schema hash does not match the canonical contract."
    }
    $decisions = @(ConvertTo-ScheduledGovernanceArray (Get-ScheduledGovernanceProperty $contract "decisions"))
    if ($decisions.Count -ne 4 -or
        -not (Test-ScheduledGovernanceSetEqual $decisions (Get-ScheduledGovernanceExpectedDecision))) {
        Add-ScheduledGovernanceSpecError $errors "Contract decisions must contain exactly the four server decisions."
    }
    $fixedPolicy = Get-ScheduledGovernanceProperty $contract "fixedPolicy"
    if ((Get-ScheduledGovernanceProperty $fixedPolicy "scopeResolution") -cne "server-resolved-complete-authorized-scope") {
        Add-ScheduledGovernanceSpecError $errors "Fixed policy must resolve the complete authorized scope on the server."
    }
    if ((Get-ScheduledGovernanceProperty $fixedPolicy "risk") -cne "Low" -or
        (Get-ScheduledGovernanceProperty $fixedPolicy "maxMutations") -ne 100 -or
        (Get-ScheduledGovernanceProperty $fixedPolicy "maxDurationSeconds") -ne 120 -or
        (Get-ScheduledGovernanceProperty $fixedPolicy "reversibleOnly") -ne $true) {
        Add-ScheduledGovernanceSpecError $errors "Fixed execution policy bounds or reversibility changed."
    }
    if ((Get-ScheduledGovernanceProperty $fixedPolicy "irreversibleRetentionOwner") -cne "ContextHubInternalRetentionWorker") {
        Add-ScheduledGovernanceSpecError $errors "Irreversible retention ownership must remain internal."
    }

    $catalogTools = @(ConvertTo-ScheduledGovernanceArray (Get-ScheduledGovernanceProperty $spec "catalogTools"))
    if ($catalogTools.Count -ne 4) {
        Add-ScheduledGovernanceSpecError $errors "catalogTools must describe exactly four tools."
    }
    foreach ($tool in $catalogTools) {
        $name = [string](Get-ScheduledGovernanceProperty $tool "name")
        if ($name -notin (Get-ScheduledGovernanceExpectedToolName)) {
            Add-ScheduledGovernanceSpecError $errors "catalogTools contains an unapproved tool name."
        }
        $fields = @(ConvertTo-ScheduledGovernanceArray (Get-ScheduledGovernanceProperty $tool "inputFields"))
        foreach ($field in $fields) {
            if ([string]$field -in @("projectIds", "allowHardDelete", "allowMaturedDelete", "allowedActionTypes", "maxRiskLevel", "dryRun", "executionMode")) {
                Add-ScheduledGovernanceSpecError $errors "catalogTools exposes a forbidden authority field."
            }
        }
    }

    $orchestration = Get-ScheduledGovernanceProperty $spec "orchestration"
    $prompt = [string](Get-ScheduledGovernanceProperty $orchestration "prompt")
    if ([string]::IsNullOrWhiteSpace($prompt) -or $prompt.Length -gt 1200) {
        Add-ScheduledGovernanceSpecError $errors "The canonical orchestration prompt must be non-empty and short."
    }
    foreach ($cue in @("new run", "decision", "receipt", "review")) {
        if ($prompt.IndexOf($cue, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            Add-ScheduledGovernanceSpecError $errors "The orchestration prompt is missing the '$cue' control cue."
        }
    }
    if ((Get-ScheduledGovernanceProperty $orchestration "runIdPolicy") -cne "fresh-per-schedule" -or
        (Get-ScheduledGovernanceProperty $orchestration "scopePolicy") -cne "server-resolved-complete-authorized-scope" -or
        (Get-ScheduledGovernanceProperty $orchestration "projectSelection") -cne "server-only") {
        Add-ScheduledGovernanceSpecError $errors "Orchestration must use a fresh run and server-only scope resolution."
    }

    $branches = @(ConvertTo-ScheduledGovernanceArray (Get-ScheduledGovernanceProperty $orchestration "branches"))
    if ($branches.Count -ne 4) {
        Add-ScheduledGovernanceSpecError $errors "Orchestration must define four decision branches."
    }
    $expectedCalls = @{
        NoOpConverged = @("scheduled_governance_run_get")
        ReversibleExecutionRequired = @("scheduled_governance_execute", "scheduled_governance_run_get", "scheduled_governance_review", "scheduled_governance_run_get")
        HumanDecisionOnly = @()
        CoverageIncomplete = @()
    }
    $expectedTerminals = @{
        NoOpConverged = "read-receipt-and-finish"
        ReversibleExecutionRequired = "bounded-execute-recover-re-review"
        HumanDecisionOnly = "stop-and-report"
        CoverageIncomplete = "fail-closed-and-report"
    }
    foreach ($decision in Get-ScheduledGovernanceExpectedDecision) {
        $branch = @($branches | Where-Object { [string](Get-ScheduledGovernanceProperty $_ "decision") -ceq $decision })
        if ($branch.Count -ne 1) {
            Add-ScheduledGovernanceSpecError $errors "Decision branch '$decision' must occur exactly once."
            continue
        }
        $calls = @(ConvertTo-ScheduledGovernanceArray (Get-ScheduledGovernanceProperty $branch[0] "calls"))
        if (-not (Test-ScheduledGovernanceSetEqual $calls $expectedCalls[$decision]) -or
            (($calls | ForEach-Object { [string]$_ }) -join "`n") -cne (($expectedCalls[$decision]) -join "`n")) {
            Add-ScheduledGovernanceSpecError $errors "Decision branch '$decision' has an unsafe call sequence."
        }
        if ((Get-ScheduledGovernanceProperty $branch[0] "terminalAction") -cne $expectedTerminals[$decision]) {
            Add-ScheduledGovernanceSpecError $errors "Decision branch '$decision' has an unsafe terminal action."
        }
    }

    $executableText = @(
        $prompt
        [string](Get-ScheduledGovernanceProperty $orchestration "runIdPolicy")
        [string](Get-ScheduledGovernanceProperty $orchestration "scopePolicy")
        [string](Get-ScheduledGovernanceProperty $orchestration "projectSelection")
        ($branches | ConvertTo-Json -Depth 20 -Compress)
    ) -join "`n"
    foreach ($token in Get-ScheduledGovernanceForbiddenToken) {
        if ($executableText.IndexOf($token, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            Add-ScheduledGovernanceSpecError $errors "Executable orchestration contains forbidden token '$token'."
        }
    }

    $a0 = Get-ScheduledGovernanceProperty (Get-ScheduledGovernanceProperty $spec "acceptance") "a0"
    if ((Get-ScheduledGovernanceProperty $a0 "freshRunRequired") -ne $true -or
        (Get-ScheduledGovernanceProperty $a0 "executorCalls") -ne 0 -or
        (Get-ScheduledGovernanceProperty $a0 "requiredDecision") -cne "NoOpConverged" -or
        (Get-ScheduledGovernanceProperty $a0 "requiresFinalReviewAndReceipt") -ne $true -or
        (Get-ScheduledGovernanceProperty $a0 "requiresProtectedInvariantReadback") -ne $true) {
        Add-ScheduledGovernanceSpecError $errors "A0 acceptance safety invariants are incomplete."
    }
    $a1 = Get-ScheduledGovernanceProperty (Get-ScheduledGovernanceProperty $spec "acceptance") "a1"
    foreach ($propertyName in @(
        "productionSyntheticFixtureCreation",
        "isolatedSyntheticFixturesOnly",
        "requiresSnapshotBinding",
        "requiresAuthorizationRevalidation",
        "requiresExactReplayEvidence",
        "requiresOutcomeRecoveryBeforeRetry",
        "requiresFinalReReview",
        "requiresProtectedInvariantReadback")) {
        if ($propertyName -eq "productionSyntheticFixtureCreation") {
            if ((Get-ScheduledGovernanceProperty $a1 $propertyName) -ne $false) {
                Add-ScheduledGovernanceSpecError $errors "Production synthetic fixture creation must remain disabled."
            }
        }
        elseif ((Get-ScheduledGovernanceProperty $a1 $propertyName) -ne $true) {
            Add-ScheduledGovernanceSpecError $errors "A1 invariant '$propertyName' must remain enabled."
        }
    }

    $evidence = Get-ScheduledGovernanceProperty $spec "evidence"
    if ((Get-ScheduledGovernanceProperty $evidence "format") -cne "compact-json" -or
        (Get-ScheduledGovernanceProperty $evidence "maxBytes") -ne 12000 -or
        (Get-ScheduledGovernanceProperty $evidence "rawPayloads") -ne $false -or
        (Get-ScheduledGovernanceProperty $evidence "rawLogs") -ne $false) {
        Add-ScheduledGovernanceSpecError $errors "Evidence must be compact and exclude raw payloads/logs."
    }
    $requiredEvidenceFields = @(
        "observedAtUtc", "mode", "surface", "oauthGate", "catalog", "contract",
        "runtimeIdentity", "governanceRunId", "decision", "coverage", "receipt",
        "replay", "protectedInvariants", "outcome")
    $actualEvidenceFields = @(ConvertTo-ScheduledGovernanceArray (Get-ScheduledGovernanceProperty $evidence "requiredFields"))
    if (-not (Test-ScheduledGovernanceSetEqual $actualEvidenceFields $requiredEvidenceFields)) {
        Add-ScheduledGovernanceSpecError $errors "Evidence requiredFields do not cover the controlled acceptance record."
    }
    if ((Get-ScheduledGovernanceProperty $evidence "protectedInvariantSource") -cne "external-read-only-manifest") {
        Add-ScheduledGovernanceSpecError $errors "Protected invariant evidence must come from an external read-only manifest."
    }

    $forbidden = Get-ScheduledGovernanceProperty $spec "forbidden"
    $forbiddenTools = @(ConvertTo-ScheduledGovernanceArray (Get-ScheduledGovernanceProperty $forbidden "toolNames"))
    if (-not (Test-ScheduledGovernanceSetEqual $forbiddenTools (Get-ScheduledGovernanceForbiddenToolName))) {
        Add-ScheduledGovernanceSpecError $errors "Forbidden tool probe set is incomplete or changed."
    }
    $forbiddenTokens = @(ConvertTo-ScheduledGovernanceArray (Get-ScheduledGovernanceProperty $forbidden "tokens"))
    if (-not (Test-ScheduledGovernanceSetEqual $forbiddenTokens (Get-ScheduledGovernanceForbiddenToken))) {
        Add-ScheduledGovernanceSpecError $errors "Forbidden token set is incomplete or changed."
    }
    if ((Get-ScheduledGovernanceProperty $forbidden "liveAutomationMutation") -cne "forbidden") {
        Add-ScheduledGovernanceSpecError $errors "The artifact must explicitly forbid live Automation mutation."
    }

    [pscustomobject]@{
        Valid = $errors.Count -eq 0
        Errors = @($errors)
        Spec = $spec
        Path = $Path
    }
}
