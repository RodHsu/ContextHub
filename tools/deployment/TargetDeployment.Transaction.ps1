#Requires -Version 7.4

function Get-TargetDeploymentResult {
    param([string]$Status, [AllowNull()][object]$TargetRevision, [string]$Stage)

    [pscustomobject]@{ Status = $Status; TargetRevision = $TargetRevision; Stage = $Stage }
}

function Get-TargetDeploymentStateTemplate {
    param([AllowNull()][object]$TargetRevision, [AllowNull()][object]$Outcome)

    [ordered]@{
        schemaVersion = 1
        lastProcessedTargetRevision = $TargetRevision
        lastOutcome = $Outcome
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

    $pending = $state.pending
    if ($null -ne $pending -and (
        $pending -isnot [Collections.IDictionary] -or $pending.phase -cnotin @('Applying', 'RollbackFailed') -or
        $pending.targetRevision -isnot [string] -or [string]::IsNullOrWhiteSpace($pending.targetRevision) -or
        $pending.previous -isnot [Collections.IDictionary] -or $pending.previous.Count -eq 0 -or
        $pending.candidate -isnot [Collections.IDictionary] -or $pending.candidate.Count -eq 0)) {
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

function Restore-TargetDeploymentRelease {
    param(
        [string]$StatePath,
        [Collections.IDictionary]$State,
        [scriptblock]$Apply,
        [scriptblock]$Verify,
        [string]$Stage,
        [switch]$Recovering
    )

    $pending = $State.pending
    $rollbackStage = $null
    try { & $Apply $pending.previous *> $null }
    catch { $rollbackStage = 'RollbackApply' }

    try { & $Verify $pending.previous *> $null }
    catch { if ($null -eq $rollbackStage) { $rollbackStage = 'RollbackVerify' } }

    if ($null -eq $rollbackStage) {
        $status = if ($Recovering) { 'Recovered' } else { 'RolledBack' }
        try {
            Write-TargetDeploymentState $StatePath (Get-TargetDeploymentStateTemplate $pending.targetRevision $status)
            return Get-TargetDeploymentResult -Status $status -TargetRevision $pending.targetRevision -Stage $Stage
        } catch {
            $rollbackStage = 'RollbackFinalize'
        }
    }

    $pending.phase = 'RollbackFailed'
    $pending.failureStage = $Stage
    $pending.rollbackStage = $rollbackStage
    try { Write-TargetDeploymentState $StatePath $State }
    catch {
        # The prior pending journal was committed before apply. If storage no longer accepts a write,
        # the durable pending entry still gates the next run on recovery.
        return Get-TargetDeploymentResult -Status 'RollbackFailed' -TargetRevision $pending.targetRevision -Stage 'RollbackJournalWrite'
    }
    return Get-TargetDeploymentResult -Status 'RollbackFailed' -TargetRevision $pending.targetRevision -Stage $rollbackStage
}

function Invoke-TargetDeploymentTransaction {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$StatePath,
        [Parameter(Mandatory)][scriptblock]$GetCandidate,
        [Parameter(Mandatory)][scriptblock]$Build,
        [Parameter(Mandatory)][scriptblock]$Capture,
        [Parameter(Mandatory)][scriptblock]$Apply,
        [Parameter(Mandatory)][scriptblock]$Verify
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
                return Get-TargetDeploymentResult -Status 'RollbackFailed' -TargetRevision $state.pending.targetRevision -Stage 'RollbackLatched'
            }
            return Restore-TargetDeploymentRelease -StatePath $path -State $state -Apply $Apply -Verify $Verify -Stage 'Recovery' -Recovering
        }

        try { $candidate = Invoke-TargetDeploymentRecordCallback $GetCandidate $state.lastProcessedTargetRevision -AllowEmpty }
        catch { return Get-TargetDeploymentResult -Status 'Rejected' -TargetRevision $null -Stage 'CandidateRead' }
        if ($null -eq $candidate) {
            return Get-TargetDeploymentResult -Status 'NoChange' -TargetRevision $state.lastProcessedTargetRevision -Stage 'CandidateRead'
        }
        $revision = $candidate.targetRevision
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
            Write-TargetDeploymentState $path (Get-TargetDeploymentStateTemplate $revision 'Rejected')
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
            Write-TargetDeploymentState $path (Get-TargetDeploymentStateTemplate $revision 'Applied')
            return Get-TargetDeploymentResult -Status 'Applied' -TargetRevision $revision -Stage $stage
        } catch {
            return Restore-TargetDeploymentRelease -StatePath $path -State $state -Apply $Apply -Verify $Verify -Stage $stage
        }
    } finally {
        $lock.Dispose()
    }
}
