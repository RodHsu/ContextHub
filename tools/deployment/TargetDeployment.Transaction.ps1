#Requires -Version 7.4

function Get-TargetDeploymentResult {
    param([string]$Status, [AllowNull()][object]$TargetRevision, [string]$Stage, [AllowNull()][object]$FailureKind)

    [pscustomobject]@{ Status = $Status; TargetRevision = $TargetRevision; Stage = $Stage; FailureKind = $FailureKind }
}

function Get-TargetDeploymentStateTemplate {
    param(
        [AllowNull()][object]$TargetRevision,
        [AllowNull()][object]$Outcome,
        [AllowNull()][Collections.IDictionary]$LastGood,
        [AllowNull()][Collections.IDictionary]$PreviousGood
    )

    [ordered]@{
        schemaVersion = 1
        lastProcessedTargetRevision = $TargetRevision
        lastOutcome = $Outcome
        lastGood = $LastGood
        previousGood = $PreviousGood
        pending = $null
    }
}

function Get-TargetDeploymentState {
    param([string]$Path)

    if (-not [IO.File]::Exists($Path)) { return Get-TargetDeploymentStateTemplate $null $null }

    $state = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($Path)) -AsHashtable -ErrorAction Stop
    if ($state -isnot [Collections.IDictionary] -or $state.schemaVersion -ne 1 -or
        -not $state.Contains('lastProcessedTargetRevision') -or -not $state.Contains('lastOutcome') -or
        -not $state.Contains('pending') -or
        ($null -ne $state.lastProcessedTargetRevision -and $state.lastProcessedTargetRevision -isnot [string])) {
        throw 'TARGET_DEPLOYMENT_STATE_INVALID'
    }

    foreach ($name in @('lastGood', 'previousGood')) {
        $release = $state[$name]
        if ($null -ne $release -and ($release -isnot [Collections.IDictionary] -or $release.Count -eq 0)) {
            throw 'TARGET_DEPLOYMENT_SETTLED_RELEASE_INVALID'
        }
        if (-not $state.Contains($name)) { $state[$name] = $null }
    }

    $pending = $state.pending
    if ($null -ne $pending -and (
        $pending -isnot [Collections.IDictionary] -or $pending.phase -cnotin @('Applying', 'RollbackFailed') -or
        $pending.targetRevision -isnot [string] -or [string]::IsNullOrWhiteSpace($pending.targetRevision) -or
        $pending.previous -isnot [Collections.IDictionary] -or $pending.previous.Count -eq 0 -or
        $pending.candidate -isnot [Collections.IDictionary] -or $pending.candidate.Count -eq 0 -or
        ($null -ne $pending['failureKind'] -and $pending['failureKind'] -cnotin @('Timeout', 'Error')))) {
        throw 'TARGET_DEPLOYMENT_PENDING_INVALID'
    }
    return $state
}

function Write-TargetDeploymentState {
    param([string]$Path, [Collections.IDictionary]$State)

    $json = ConvertTo-Json -InputObject $State -Depth 64 -WarningAction Stop -ErrorAction Stop
    $temporary = "$Path.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        $file = [IO.FileStream]::new($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $bytes = [Text.Encoding]::UTF8.GetBytes($json)
            $file.Write($bytes, 0, $bytes.Length)
            $file.Flush($true)
        } finally {
            $file.Dispose()
        }
        [IO.File]::Move($temporary, $Path, $true)
    } finally {
        if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
    }
}

function Invoke-TargetDeploymentRecordCallback {
    param([scriptblock]$Callback, [AllowNull()][object]$Argument, [switch]$AllowEmpty)

    $records = @(& $Callback $Argument 2>$null 3>$null 4>$null 5>$null 6>$null)
    if ($AllowEmpty -and ($records.Count -eq 0 -or ($records.Count -eq 1 -and $null -eq $records[0]))) {
        return $null
    }
    if ($records.Count -ne 1 -or
        ($records[0] -isnot [Collections.IDictionary] -and $records[0] -isnot [pscustomobject])) {
        throw 'TARGET_DEPLOYMENT_RECORD_INVALID'
    }

    $json = ConvertTo-Json -InputObject $records[0] -Depth 60 -WarningAction Stop -ErrorAction Stop
    $record = ConvertFrom-Json -InputObject $json -AsHashtable -ErrorAction Stop
    if ($record -isnot [Collections.IDictionary] -or $record.Count -eq 0) {
        throw 'TARGET_DEPLOYMENT_RECORD_EMPTY'
    }
    return $record
}

function Get-TargetDeploymentFailureKind {
    param([Exception]$Exception)

    while ($null -ne $Exception) {
        if ($Exception -is [TimeoutException]) { return 'Timeout' }
        $Exception = $Exception.InnerException
    }
    return 'Error'
}

function Restore-TargetDeploymentRelease {
    param(
        [string]$StatePath,
        [Collections.IDictionary]$State,
        [scriptblock]$Apply,
        [scriptblock]$Verify,
        [scriptblock]$PrepareRollback,
        [string]$Stage,
        [AllowNull()][object]$FailureKind,
        [switch]$Recovering
    )

    $pending = $State.pending
    $rollbackStage = $null
    if ($null -ne $PrepareRollback) {
        try { & $PrepareRollback $pending *> $null }
        catch { $rollbackStage = 'RollbackPrepare' }
    }

    if ($null -eq $rollbackStage) {
        try { & $Apply $pending.previous *> $null }
        catch { $rollbackStage = 'RollbackApply' }

        try { & $Verify $pending.previous *> $null }
        catch { if ($null -eq $rollbackStage) { $rollbackStage = 'RollbackVerify' } }
    }

    if ($null -eq $rollbackStage) {
        $status = if ($Recovering) { 'Recovered' } else { 'RolledBack' }
        try {
            $settled = Get-TargetDeploymentStateTemplate -TargetRevision $pending.targetRevision -Outcome $status `
                -LastGood $pending.previous -PreviousGood $State.previousGood
            Write-TargetDeploymentState $StatePath $settled
            return Get-TargetDeploymentResult -Status $status -TargetRevision $pending.targetRevision -Stage $Stage -FailureKind $FailureKind
        } catch {
            $rollbackStage = 'RollbackFinalize'
        }
    }

    $pending.phase = 'RollbackFailed'
    $pending.failureStage = $Stage
    $pending.failureKind = $FailureKind
    $pending.rollbackStage = $rollbackStage
    try { Write-TargetDeploymentState $StatePath $State }
    catch {
        # The prior pending journal was committed before apply. If storage no longer accepts a write,
        # the durable pending entry still gates the next run on recovery.
        return Get-TargetDeploymentResult -Status 'RollbackFailed' -TargetRevision $pending.targetRevision -Stage 'RollbackJournalWrite' -FailureKind $FailureKind
    }
    return Get-TargetDeploymentResult -Status 'RollbackFailed' -TargetRevision $pending.targetRevision -Stage $rollbackStage -FailureKind $FailureKind
}

function Invoke-TargetDeploymentTransaction {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$StatePath,
        [Parameter(Mandatory)][scriptblock]$GetCandidate,
        [Parameter(Mandatory)][scriptblock]$Build,
        [Parameter(Mandatory)][scriptblock]$Capture,
        [Parameter(Mandatory)][scriptblock]$Apply,
        [Parameter(Mandatory)][scriptblock]$Verify,
        [scriptblock]$PrepareRollback
    )

    $ErrorActionPreference = 'Stop'
    if (-not [IO.Path]::IsPathFullyQualified($StatePath)) { throw 'TARGET_DEPLOYMENT_STATE_PATH_NOT_ABSOLUTE' }
    $path = [IO.Path]::GetFullPath($StatePath)
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))

    try {
        $lock = [IO.File]::Open("$path.lock", [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    } catch [IO.IOException] {
        $code = $_.Exception.HResult -band 0xffff
        if ($code -in @(32, 33) -or (-not $IsWindows -and $code -eq 11)) {
            return Get-TargetDeploymentResult -Status 'Busy' -TargetRevision $null -Stage 'Lock'
        }
        throw
    }

    try {
        try { $state = Get-TargetDeploymentState $path }
        catch { return Get-TargetDeploymentResult -Status 'RollbackFailed' -TargetRevision $null -Stage 'StateRead' }

        if ($null -ne $state.pending) {
            if ($state.pending.phase -ceq 'RollbackFailed') {
                return Get-TargetDeploymentResult -Status 'RollbackFailed' -TargetRevision $state.pending.targetRevision `
                    -Stage 'RollbackLatched' -FailureKind $state.pending['failureKind']
            }
            return Restore-TargetDeploymentRelease -StatePath $path -State $state -Apply $Apply -Verify $Verify `
                -PrepareRollback $PrepareRollback -Stage 'Recovery' -Recovering
        }

        try { $candidate = Invoke-TargetDeploymentRecordCallback $GetCandidate $state.lastProcessedTargetRevision -AllowEmpty }
        catch { return Get-TargetDeploymentResult -Status 'Rejected' -TargetRevision $null -Stage 'CandidateRead' }
        if ($null -eq $candidate) {
            return Get-TargetDeploymentResult -Status 'NoChange' -TargetRevision $state.lastProcessedTargetRevision -Stage 'CandidateRead'
        }
        $revision = $candidate['targetRevision']
        if ($revision -isnot [string] -or [string]::IsNullOrWhiteSpace($revision)) {
            return Get-TargetDeploymentResult -Status 'Rejected' -TargetRevision $null -Stage 'CandidateValidation'
        }
        if ($revision -ceq $state.lastProcessedTargetRevision) {
            return Get-TargetDeploymentResult -Status 'NoChange' -TargetRevision $revision -Stage 'CandidateRead'
        }

        $stage = 'CandidateValidation'
        try {
            foreach ($name in @('sourceSha', 'version')) {
                if ($candidate[$name] -isnot [string] -or [string]::IsNullOrWhiteSpace($candidate[$name])) {
                    throw 'TARGET_DEPLOYMENT_CANDIDATE_INVALID'
                }
            }
            $stage = 'Build'
            $candidateRelease = Invoke-TargetDeploymentRecordCallback $Build $candidate
            $stage = 'Capture'
            $previousRelease = Invoke-TargetDeploymentRecordCallback $Capture $candidateRelease
            $stage = 'BaselineVerify'
            & $Verify $previousRelease *> $null
        } catch {
            $settled = Get-TargetDeploymentStateTemplate -TargetRevision $revision -Outcome 'Rejected' `
                -LastGood $state.lastGood -PreviousGood $state.previousGood
            Write-TargetDeploymentState $path $settled
            return Get-TargetDeploymentResult -Status 'Rejected' -TargetRevision $revision -Stage $stage
        }

        $state.pending = [ordered]@{
            phase = 'Applying'
            targetRevision = $revision
            previous = $previousRelease
            candidate = $candidateRelease
        }
        Write-TargetDeploymentState $path $state

        $stage = 'Apply'
        try {
            & $Apply $candidateRelease *> $null
            $stage = 'Verify'
            & $Verify $candidateRelease *> $null
            $stage = 'Finalize'
            $settled = Get-TargetDeploymentStateTemplate -TargetRevision $revision -Outcome 'Applied' `
                -LastGood $candidateRelease -PreviousGood $previousRelease
            Write-TargetDeploymentState $path $settled
            return Get-TargetDeploymentResult -Status 'Applied' -TargetRevision $revision -Stage $stage
        } catch {
            $failureKind = if ($stage -cin @('Apply', 'Verify')) { Get-TargetDeploymentFailureKind -Exception $_.Exception }
            return Restore-TargetDeploymentRelease -StatePath $path -State $state -Apply $Apply -Verify $Verify `
                -PrepareRollback $PrepareRollback -Stage $stage -FailureKind $failureKind
        }
    } finally {
        $lock.Dispose()
    }
}
