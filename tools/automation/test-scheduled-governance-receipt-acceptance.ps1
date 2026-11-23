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
        "Get-ScheduledGovernanceReceiptEvidence",
        "Get-ScheduledGovernanceReviewReceiptEvidence")) {
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

[ordered]@{
    valid = $true
    positiveCases = 6
    negativeCases = $cases.Count + 3
    result = "scheduled-governance-receipt-bound-to-review"
} | ConvertTo-Json -Compress
