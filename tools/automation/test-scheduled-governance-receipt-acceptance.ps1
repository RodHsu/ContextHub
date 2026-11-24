[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ScheduledGovernanceAutomation.Common.ps1")

# Load only the receipt evidence functions. The runner's HTTP entry point is never invoked.
$runnerPath = Join-Path (Split-Path $PSScriptRoot -Parent) "test-contexthub-scheduled-governance-acceptance.ps1"
$tokens = $null
$parseErrors = $null
$runner = [System.Management.Automation.Language.Parser]::ParseFile($runnerPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) {
    throw "Cannot parse the acceptance runner: $($parseErrors.Message -join ' ')"
}
foreach ($name in @(
        "Get-ScheduledGovernancePropertyValue",
        "Get-ScheduledGovernanceArrayProperty",
        "Assert-ScheduledGovernanceRpcSuccess",
        "Get-ScheduledGovernanceToolData",
        "Test-ScheduledGovernanceIdsEqual",
        "Test-ScheduledGovernanceExecutionResult",
        "Get-ScheduledGovernanceReceiptEvidence",
        "Get-ScheduledGovernanceReviewReceiptEvidence",
        "Get-ScheduledGovernanceBatchCounts",
        "Get-ScheduledGovernanceBatchAuditIds",
        "Assert-ScheduledGovernanceFirstBatchBaseline",
        "Get-ScheduledGovernanceBatchReceiptEvidence",
        "Invoke-ScheduledGovernanceVerifiedReplay",
        "Read-ScheduledGovernanceProtectedInvariantEvidence")) {
    $definition = $runner.Find({
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name
        }, $false)
    if ($null -eq $definition) {
        throw "Cannot find $name in the acceptance runner."
    }
    . ([scriptblock]::Create($definition.Extent.Text))
}

$verifiedContract = [ordered]@{
    toolContractVersion = "1.5"
    schemaHash = "1" * 64
    publishedCatalogVersion = "catalog-fixture"
}
$runId = "scheduled-fixture"
function Get-ReceiptFixture {
    param(
        [string]$Decision = "NoOpConverged",
        [switch]$ReReview
    )

    $coverageComplete = $Decision -cne "CoverageIncomplete"
    $snapshot = if (-not $coverageComplete) {
        ""
    }
    elseif ($ReReview) {
        "kg:fixture:r1"
    }
    else {
        "kg:fixture:i0"
    }
    return @{
        Review = [pscustomobject]@{
            decision = $Decision
            coverageComplete = $coverageComplete
            snapshotToken = $snapshot
            isReReview = [bool]$ReReview
        }
        Receipt = [pscustomobject]@{
            receiptId = "6d2aecbd-09eb-4a8a-ae32-55f23c349299"
            governanceRunId = $runId
            runExists = $true
            received = $true
            terminal = $true
            status = "Completed"
            finalConvergenceStatus = $Decision
            decision = $Decision
            outcome = $Decision
            coverageComplete = $coverageComplete
            finalSnapshotToken = $snapshot
            toolContractVersion = $verifiedContract.toolContractVersion
            schemaHash = $verifiedContract.schemaHash
            publishedCatalogVersion = $verifiedContract.publishedCatalogVersion
            latestBatchReceived = $false
            auditIds = @()
        }
    }
}

function Assert-RejectedReceiptFixture {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][hashtable]$Fixture,
        [Parameter(Mandatory)][string]$ExpectedError,
        [switch]$AllowCompletedBatchOverlay
    )

    try {
        $null = Get-ScheduledGovernanceReviewReceiptEvidence -Receipt $Fixture.Receipt -ExpectedRunId $runId `
            -Review $Fixture.Review -Contract $verifiedContract -AllowCompletedBatchOverlay:$AllowCompletedBatchOverlay
    }
    catch {
        if ($_.Exception.Message -ceq $ExpectedError) {
            return
        }
        throw "Fixture '$Name' failed for the wrong reason. $($_.Exception.Message)"
    }

    throw "Fixture '$Name' was unexpectedly accepted."
}

foreach ($decision in @("NoOpConverged", "ReversibleExecutionRequired", "HumanDecisionOnly", "CoverageIncomplete")) {
    $fixture = Get-ReceiptFixture -Decision $decision
    $evidence = Get-ScheduledGovernanceReviewReceiptEvidence -Receipt $fixture.Receipt -ExpectedRunId $runId `
        -Review $fixture.Review -Contract $verifiedContract
    if ($evidence.decision -cne $decision -or $evidence.coverageComplete -ne $fixture.Review.coverageComplete -or
        $evidence.auditCount -ne 0) {
        throw "Receipt fixture '$decision' did not preserve the review decision, coverage or empty audit count."
    }
}

$fixture = Get-ReceiptFixture -ReReview
$evidence = Get-ScheduledGovernanceReviewReceiptEvidence -Receipt $fixture.Receipt -ExpectedRunId $runId `
    -Review $fixture.Review -Contract $verifiedContract
if ($evidence.decision -cne "NoOpConverged" -or -not $evidence.coverageComplete) {
    throw "The completed zero-execute re-review was not accepted."
}

$fixture = Get-ReceiptFixture -Decision HumanDecisionOnly -ReReview
$fixture.Receipt.decision = $null
$fixture.Receipt.outcome = "ReReviewRequired"
$fixture.Receipt.latestBatchReceived = $true
$fixture.Receipt.auditIds = @("e8b0fd25-2506-4dcc-b84c-b74ff1c6b379")
$evidence = Get-ScheduledGovernanceReviewReceiptEvidence -Receipt $fixture.Receipt -ExpectedRunId $runId `
    -Review $fixture.Review -Contract $verifiedContract -AllowCompletedBatchOverlay
if ($null -ne $evidence.decision -or $evidence.outcome -cne "ReReviewRequired" -or $evidence.auditCount -ne 1) {
    throw "The completed batch overlay did not preserve the raw outcome or the single audit count."
}

$decisionMismatch = "Scheduled governance review receipt did not match the persisted review decision."
Assert-RejectedReceiptFixture -Name "completed batch overlay without permission" -Fixture $fixture -ExpectedError $decisionMismatch
$fixture.Receipt.latestBatchReceived = "true"
Assert-RejectedReceiptFixture -Name "completed batch overlay without boolean batch evidence" -Fixture $fixture `
    -ExpectedError $decisionMismatch -AllowCompletedBatchOverlay

$fixture.Receipt.latestBatchReceived = $true
$fixture.Review.isReReview = $false
Assert-RejectedReceiptFixture -Name "completed batch overlay on initial review" -Fixture $fixture `
    -ExpectedError $decisionMismatch -AllowCompletedBatchOverlay

$completedReceiptMissing = "Scheduled governance review did not have a completed receipt."
$cases = @(
    @{ field = "receiptId"; value = [Guid]::Empty.ToString(); error = $completedReceiptMissing }
    @{ field = "governanceRunId"; value = "another-run"; error = "Scheduled governance receipt did not match the requested run." }
    @{ field = "status"; value = "ContractMismatch"; error = $completedReceiptMissing }
    @{ field = "status"; value = "ModeMismatch"; error = $completedReceiptMissing }
    @{ field = "status"; value = "Running"; error = $completedReceiptMissing }
    @{ field = "received"; value = "true"; error = "Scheduled governance review receipt did not confirm received." }
    @{ field = "terminal"; value = $false; error = "Scheduled governance review receipt did not confirm terminal." }
    @{ field = "runExists"; value = $false; error = "Scheduled governance review receipt did not confirm runExists." }
    @{ field = "toolContractVersion"; value = "1.4"; error = "Scheduled governance review receipt did not match the verified toolContractVersion." }
    @{ field = "schemaHash"; value = ("2" * 64); error = "Scheduled governance review receipt did not match the verified schemaHash." }
    @{ field = "publishedCatalogVersion"; value = "old-catalog"; error = "Scheduled governance review receipt did not match the verified publishedCatalogVersion." }
    @{ field = "coverageComplete"; value = $false; error = "Scheduled governance review receipt did not match the review coverage." }
    @{ field = "finalSnapshotToken"; value = "kg:fixture:old"; error = "Scheduled governance review receipt did not match the review snapshot." }
    @{ field = "finalConvergenceStatus"; value = "CoverageIncomplete"; error = $decisionMismatch }
    @{ field = "decision"; value = "CoverageIncomplete"; error = $decisionMismatch }
    @{ field = "outcome"; value = "ReReviewRequired"; error = $decisionMismatch }
)
foreach ($case in $cases) {
    $fixture = Get-ReceiptFixture
    $fixture.Receipt.($case.field) = $case.value
    Assert-RejectedReceiptFixture -Name "$($case.field):$($case.value)" -Fixture $fixture -ExpectedError $case.error
}

$execution = [pscustomobject]@{
    succeeded = $true
    isReplay = $false
    stoppedReason = "Completed"
    governanceRunId = $runId
    snapshotToken = "kg:fixture:i0"
}
foreach ($expectedReplay in @($false, $true)) {
    $execution.isReplay = $expectedReplay
    if (-not (Test-ScheduledGovernanceExecutionResult $execution $runId "kg:fixture:i0" $expectedReplay)) {
        throw "A successful execution with the expected run, snapshot and replay flag was rejected."
    }
}
$invalidExecutions = @(
    @{ field = "succeeded"; value = $false }
    @{ field = "succeeded"; value = "true" }
    @{ field = "isReplay"; value = $false }
    @{ field = "isReplay"; value = "true" }
    @{ field = "stoppedReason"; value = "UnknownResult" }
    @{ field = "stoppedReason"; value = $null }
    @{ field = "governanceRunId"; value = "another-run" }
    @{ field = "snapshotToken"; value = "kg:fixture:old" }
)
foreach ($case in $invalidExecutions) {
    $invalid = $execution.PSObject.Copy()
    $invalid.($case.field) = $case.value
    if (Test-ScheduledGovernanceExecutionResult $invalid $runId "kg:fixture:i0" $true) {
        throw "An invalid execution result was accepted for '$($case.field)'."
    }
}

function New-ReplayFixture {
    param([switch]$ZeroItems)

    $before = (Get-ReceiptFixture -Decision ReversibleExecutionRequired).Receipt
    $auditId = "e8b0fd25-2506-4dcc-b84c-b74ff1c6b379"
    $firstBatch = $execution.PSObject.Copy()
    $firstBatch.isReplay = $false
    $firstBatch | Add-Member -NotePropertyMembers @{
        requiresReReview = -not $ZeroItems
        appliedCount = if ($ZeroItems) { 0 } else { 1 }
        failedCount = 0
        quarantinedCount = 0
        semanticAutoResolvedCount = 0
        auditIds = @($auditId)
        nextCursor = "server-cursor-must-not-be-resubmitted"
    }
    $before.receiptId = [Guid]::NewGuid().ToString("D")
    if (-not $ZeroItems) {
        $before.decision = $null
        $before.outcome = "ReReviewRequired"
    }
    $before.latestBatchReceived = $true
    $before.auditIds = @($auditId)
    $before | Add-Member -NotePropertyMembers @{
        initialSnapshotToken = $before.finalSnapshotToken
        requestIdentityHash = "a" * 64
        stoppedReason = "Completed"
        isReplay = $false
        applied = $firstBatch.appliedCount
        failed = 0
        quarantined = 0
        semanticAutoResolved = 0
    }
    $after = $before.PSObject.Copy()
    $after.receiptId = [Guid]::NewGuid().ToString("D")
    $after.isReplay = $true
    $replay = $firstBatch.PSObject.Copy()
    $replay.isReplay = $true
    return @{
        First = $firstBatch
        Before = $before
        After = $after
        Replay = $replay
        Arguments = @{ request = @{ governanceRunId = $runId; snapshotToken = "kg:fixture:i0"; maxMutations = 100 } }
        Evidence = @{
            receipt = @{ initial = @{ receiptId = "6d2aecbd-09eb-4a8a-ae32-55f23c349299" } }
            outcome = @{ executorCalls = 1 }
            replay = @{ exercised = $false; exact = $false }
        }
        Calls = [System.Collections.Generic.List[int]]::new()
    }
}

function Invoke-ScheduledGovernanceTool {
    param($Token, $ToolName, $Arguments, $RequestId)

    $script:replayFixture.Calls.Add($RequestId)
    switch ($RequestId) {
        { $_ -in @(42, 46) } {
            if ($ToolName -cne "scheduled_governance_run_get" -or $Arguments.governanceRunId -cne $runId -or
                $Arguments.includeReliabilityHistory -isnot [bool] -or $Arguments.includeReliabilityHistory) {
                throw "Fixture received an unexpected receipt-read request."
            }
            $response = if ($RequestId -eq 42) { $script:replayFixture.Before } else { $script:replayFixture.After }
            if ($null -eq $response) { throw "Fixture receipt temporarily unavailable." }
        }
        43 {
            if ($ToolName -cne "scheduled_governance_execute" -or
                -not [object]::ReferenceEquals($Arguments, $script:replayFixture.Arguments) -or
                $Arguments.request.ContainsKey("cursor")) {
                throw "Fixture received an altered execution replay."
            }
            $response = $script:replayFixture.Replay
        }
        default { throw "Fixture received an unexpected request ID." }
    }
    return [pscustomobject]@{ result = [pscustomobject]@{ structuredContent = $response } }
}

function Invoke-ReplayFixture {
    Invoke-ScheduledGovernanceVerifiedReplay "offline-test" $script:replayFixture.Arguments $script:replayFixture.First `
        $verifiedContract $script:replayFixture.Evidence
}

foreach ($zeroItems in @($false, $true)) {
    $script:replayFixture = New-ReplayFixture -ZeroItems:$zeroItems
    Invoke-ReplayFixture
    if (($script:replayFixture.Calls -join ",") -cne "42,43,46" -or
        $script:replayFixture.Evidence.outcome.executorCalls -ne 2 -or
        -not $script:replayFixture.Evidence.replay.exact) {
        throw "Expected a completed batch receipt, exact replay and unchanged durable readback in that order."
    }
}

$batchRejections = @(
    @{ field = "governanceRunId"; value = "another-run" }
    @{ field = "receiptId"; value = "6d2aecbd-09eb-4a8a-ae32-55f23c349299" }
    @{ field = "receiptId"; value = [Guid]::Empty.ToString() }
    @{ field = "status"; value = "Running" }
    @{ field = "status"; value = "Stopped" }
    @{ field = "received"; value = "true" }
    @{ field = "terminal"; value = $false }
    @{ field = "latestBatchReceived"; value = $false }
    @{ field = "isReplay"; value = $true }
    @{ field = "requestIdentityHash"; value = "" }
    @{ field = "toolContractVersion"; value = "older" }
    @{ field = "schemaHash"; value = "2" * 64 }
    @{ field = "publishedCatalogVersion"; value = "older" }
    @{ field = "initialSnapshotToken"; value = "kg:fixture:old" }
    @{ field = "finalSnapshotToken"; value = "kg:fixture:old" }
    @{ field = "outcome"; value = "UnknownResult" }
    @{ field = "decision"; value = "ReversibleExecutionRequired" }
    @{ field = "stoppedReason"; value = "ItemFailed" }
    @{ field = "applied"; value = "1" }
    @{ field = "quarantined"; value = 1 }
    @{ field = "semanticAutoResolved"; value = 1 }
    @{ field = "auditIds"; value = @("e9b0fd25-2506-4dcc-b84c-b74ff1c6b379") }
    @{ field = "auditIds"; value = @("invalid") }
    @{ field = "auditIds"; value = $null }
)
$preReplayCases = @($batchRejections | ForEach-Object {
    $change = $_
    { param($fixture) $fixture.Before.($change.field) = $change.value }.GetNewClosure()
}) + @(
    { param($fixture) $fixture.Before = $null }
    { param($fixture) $fixture.Before.failed = 1; $fixture.First.failedCount = 1 }
    { param($fixture) $fixture.Before.auditIds = @(); $fixture.First.auditIds = @() }
)
foreach ($mutate in $preReplayCases) {
    $script:replayFixture = New-ReplayFixture
    & $mutate $script:replayFixture
    $rejected = $false
    try { Invoke-ReplayFixture } catch { $rejected = $true }
    if (-not $rejected -or ($script:replayFixture.Calls -join ",") -cne "42" -or
        $script:replayFixture.Evidence.outcome.executorCalls -ne 1 -or $script:replayFixture.Evidence.replay.exercised) {
        throw "The runner attempted replay after the initial batch receipt failed validation."
    }
}

$postReplayCases = @(
    { param($fixture) $fixture.After.applied = 2; $fixture.Replay.appliedCount = 2 }
    { param($fixture) $fixture.After.requestIdentityHash = "b" * 64 }
    { param($fixture) $fixture.After.receiptId = $fixture.Before.receiptId }
    {
        param($fixture)
        $fixture.After.auditIds = @("e9b0fd25-2506-4dcc-b84c-b74ff1c6b379")
        $fixture.Replay.auditIds = $fixture.After.auditIds
    }
)
foreach ($mutate in $postReplayCases) {
    $script:replayFixture = New-ReplayFixture
    & $mutate $script:replayFixture
    $rejected = $false
    try { Invoke-ReplayFixture } catch { $rejected = $true }
    if (-not $rejected -or ($script:replayFixture.Calls -join ",") -cne "42,43,46" -or
        $script:replayFixture.Evidence.replay.exact) {
        throw "The runner accepted replay despite conflicting durable evidence."
    }
}

$baseline = (Get-ReceiptFixture -Decision ReversibleExecutionRequired).Receipt
$baseline | Add-Member -NotePropertyMembers @{ applied = 0; failed = 0; quarantined = 0; semanticAutoResolved = 0 }
Assert-ScheduledGovernanceFirstBatchBaseline $baseline
$baselineRejections = @(
    @{ field = "latestBatchReceived"; value = $true }
    @{ field = "applied"; value = 1 }
    @{ field = "auditIds"; value = @("e8b0fd25-2506-4dcc-b84c-b74ff1c6b379") }
)
foreach ($case in $baselineRejections) {
    $invalid = $baseline.PSObject.Copy()
    $invalid.($case.field) = $case.value
    $rejected = $false
    try { Assert-ScheduledGovernanceFirstBatchBaseline $invalid } catch { $rejected = $true }
    if (-not $rejected) { throw "An initial receipt with earlier batch evidence was accepted." }
}

$manifestCases = @(
    @{ name = "complete observations"; expected = "verified"; change = {} }
    @{ name = "explicit empty inventory"; expected = "verified"; change = { param($m) $m.businessWorkItems = @() } }
    @{ name = "no Work Item inventory"; expected = "invalid"; change = { param($m) $m.Remove("businessWorkItems") } }
    @{ name = "Work Item object instead of array"; expected = "invalid"; change = { param($m) $m.businessWorkItems = $m.businessWorkItems[0] } }
    @{ name = "null Work Item inventory"; expected = "invalid"; change = { param($m) $m.businessWorkItems = $null } }
    @{ name = "empty before and after objects"; expected = "invalid"; change = { param($m) $m.businessWorkItems[0].before = @{}; $m.businessWorkItems[0].after = @{} } }
    @{ name = "missing post-run observation"; expected = "invalid"; change = { param($m) $m.businessWorkItems[0].after = $null } }
    @{ name = "string archive flag"; expected = "invalid"; change = { param($m) $m.businessWorkItems[0].before.isArchived = "false"; $m.businessWorkItems[0].after.isArchived = "false" } }
    @{ name = "unrecognized status"; expected = "invalid"; change = { param($m) $m.businessWorkItems[0].before.status = "Unknown"; $m.businessWorkItems[0].after.status = "Unknown" } }
    @{ name = "invalid Work Item ID"; expected = "invalid"; change = { param($m) $m.businessWorkItems[0].id = "not-an-id" } }
    @{ name = "duplicated Work Item"; expected = "invalid"; change = { param($m) $m.businessWorkItems += $m.businessWorkItems[0] } }
    @{ name = "non-string DisplayName"; expected = "invalid"; change = { param($m) $m.displayName.before = 3; $m.displayName.after = 3 } }
    @{ name = "DisplayName changed"; expected = "changed"; change = { param($m) $m.displayName.after = "Changed" } }
    @{ name = "Work Item status changed"; expected = "changed"; change = { param($m) $m.businessWorkItems[0].after.status = "Completed" } }
    @{ name = "Work Item was archived"; expected = "changed"; change = { param($m) $m.businessWorkItems[0].after.isArchived = $true } }
)
$localRoot = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) ".agent/local"
[void][IO.Directory]::CreateDirectory($localRoot)
$manifestPath = Join-Path $localRoot "a1-protected-manifest-$([Guid]::NewGuid().ToString('N')).json"
try {
    foreach ($case in $manifestCases) {
        $manifest = @{
            displayName = @{ before = "ContextHub"; after = "ContextHub" }
            businessWorkItems = @(@{
                    id = "6d2aecbd-09eb-4a8a-ae32-55f23c349299"
                    before = @{ status = "InProgress"; isArchived = $false }
                    after = @{ status = "InProgress"; isArchived = $false }
                })
        }
        & $case.change $manifest
        $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
        $protected = Read-ScheduledGovernanceProtectedInvariantEvidence $manifestPath
        if ($protected.status -cne $case.expected -or $protected.unchanged -ne ($case.expected -ceq "verified")) {
            throw "Protected manifest case '$($case.name)' returned '$($protected.status)' or the wrong acceptance flag."
        }
    }
}
finally {
    Remove-Item -LiteralPath $manifestPath
}

[ordered]@{
    valid = $true
    positiveCases = 11 + @($manifestCases | Where-Object expected -CEQ "verified").Count
    negativeCases = $cases.Count + 3 + $invalidExecutions.Count + $preReplayCases.Count +
        $postReplayCases.Count + $baselineRejections.Count + @($manifestCases | Where-Object expected -CNE "verified").Count
    result = "scheduled-governance-receipt-replay-and-protected-evidence"
} | ConvertTo-Json -Compress
