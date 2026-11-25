#Requires -Version 7.4
[CmdletBinding()]
param([string]$CrashStatePath)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../TargetDeployment.Transaction.ps1')

if ($CrashStatePath) {
    Invoke-TargetDeploymentTransaction -StatePath $CrashStatePath `
        -GetCandidate { @{ targetRevision = 'crash-target'; sourceSha = 'crash-source'; version = 'new' } } `
        -Build { param($candidate) @{ Version = $candidate.version; Values = @('one', 'two') } } `
        -Capture { @{ Version = 'old'; Values = @('three', 'four') } } `
        -Apply { param($release) [IO.File]::WriteAllText("$CrashStatePath.live", $release.Version); [Environment]::Exit(27) } `
        -Verify { param($release) if ($release.Version -cne 'old') { throw 'unexpected baseline' } } | Out-Null
    throw 'CRASH_TEST_DID_NOT_EXIT'
}

function Assert-Equal {
    param([AllowNull()][object]$Actual, [AllowNull()][object]$Expected, [string]$Case)

    if ($Actual -cne $Expected) { throw "$Case expected [$Expected], received [$Actual]" }
}

function Get-TestTarget {
    param([string]$Path, [string]$Failure = '')

    $fixture = @{
        Live = 'old'
        Failure = $Failure
        Revision = 'target-one'
        Events = [Collections.Generic.List[string]]::new()
    }
    $callbacks = @{
        StatePath = $Path
        GetCandidate = {
            param($previous)
            $fixture.Events.Add("Get:$previous")
            Write-Warning 'not a result'
            if ($fixture.Revision) {
                [pscustomobject]@{ targetRevision = $fixture.Revision; sourceSha = 'source-one'; version = 'new' }
            }
        }.GetNewClosure()
        Build = {
            param($candidate)
            $fixture.Events.Add("Build:$($candidate.targetRevision)")
            if ($fixture.Failure -ceq 'Build') { Write-Error 'build failed'; return }
            @{ Version = $candidate.version; Images = @{ main = 'new-id'; aux = 'new-aux-id' } }
        }.GetNewClosure()
        Capture = {
            param($candidateRelease)
            $fixture.Events.Add("Capture:$($candidateRelease.Version)")
            @{ Version = $fixture.Live; Images = @{ main = 'old-id'; aux = 'old-aux-id' } }
        }.GetNewClosure()
        Apply = {
            param($release)
            $fixture.Events.Add("Apply:$($release.Version)")
            'discard callback output'
            if ($fixture.Failure -ceq 'Rollback' -and $release.Version -ceq 'old') { throw 'rollback failed' }
            $fixture.Live = $release.Version
            if ($fixture.Failure -cin @('Apply', 'Rollback') -and $release.Version -ceq 'new') { throw 'partial apply failed' }
            if ($fixture.Failure -ceq 'ApplyTimeout' -and $release.Version -ceq 'new') {
                throw [InvalidOperationException]::new('wrapped failure', [TimeoutException]::new('apply timed out'))
            }
        }.GetNewClosure()
        Verify = {
            param($release)
            $fixture.Events.Add("Verify:$($release.Version)")
            'discard callback output'
            if ($fixture.Live -cne $release.Version) { throw 'wrong live version' }
            if ($fixture.Failure -ceq 'Baseline' -and $release.Version -ceq 'old') { throw 'rollback baseline unavailable' }
            if ($fixture.Failure -ceq 'Timeout' -and $release.Version -ceq 'new') { throw [TimeoutException]::new('readiness timed out') }
        }.GetNewClosure()
    }
    [pscustomobject]@{ Fixture = $fixture; Callbacks = $callbacks }
}

function Assert-TestCallbackOrder {
    param([Collections.IDictionary]$Fixture, [string]$Expected)
    Assert-Equal -Actual ($Fixture.Events -join ',') -Expected $Expected -Case 'callback order'
}

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../../..')).Path
$fixtureParent = Join-Path $repoRoot '.agent/local/target-transaction-tests'
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path $fixtureParent ([Guid]::NewGuid().ToString('N'))))
$null = New-Item -ItemType Directory -Path $fixtureRoot
try {
    $success = Get-TestTarget (Join-Path $fixtureRoot 'success.json')
    $callbacks = $success.Callbacks
    $result = @(Invoke-TargetDeploymentTransaction @callbacks)
    Assert-Equal -Actual $result.Count -Expected 1 -Case 'one sanitized result'
    Assert-Equal -Actual $result[0].Status -Expected 'Applied' -Case 'successful apply'
    Assert-Equal -Actual $result[0].TargetRevision -Expected 'target-one' -Case 'consumed revision'
    Assert-Equal -Actual $result[0].FailureKind -Expected $null -Case 'success does not report a failure kind'
    Assert-TestCallbackOrder $success.Fixture 'Get:,Build:target-one,Capture:new,Verify:old,Apply:new,Verify:new'
    $state = Get-TargetDeploymentState $callbacks.StatePath
    Assert-Equal -Actual $state.lastOutcome -Expected 'Applied' -Case 'persisted success'
    Assert-Equal -Actual $state.pending -Expected $null -Case 'success clears pending'
    Assert-Equal -Actual $state.lastGood.Images.aux -Expected 'new-aux-id' -Case 'successful candidate retained as last good'
    Assert-Equal -Actual $state.previousGood.Images.aux -Expected 'old-aux-id' -Case 'baseline retained as previous good'

    $success.Fixture.Events.Clear()
    Assert-Equal -Actual (Invoke-TargetDeploymentTransaction @callbacks).Status -Expected 'NoChange' -Case 'duplicate target no-op'
    Assert-TestCallbackOrder $success.Fixture 'Get:target-one'

    $success.Fixture.Events.Clear()
    $success.Fixture.Revision = 'target-two'
    $success.Fixture.Failure = 'Build'
    $result = Invoke-TargetDeploymentTransaction @callbacks
    Assert-Equal -Actual $result.Status -Expected 'Rejected' -Case 'later pre-apply rejection'
    Assert-Equal -Actual $result.FailureKind -Expected $null -Case 'build does not report an apply failure kind'
    $state = Get-TargetDeploymentState $callbacks.StatePath
    Assert-Equal -Actual $state.lastGood.Images.aux -Expected 'new-aux-id' -Case 'pre-apply rejection keeps last good'
    Assert-Equal -Actual $state.previousGood.Images.aux -Expected 'old-aux-id' -Case 'pre-apply rejection keeps previous good'
    $success.Fixture.Events.Clear()
    $success.Fixture.Revision = $null
    Assert-Equal -Actual (Invoke-TargetDeploymentTransaction @callbacks).Status -Expected 'NoChange' -Case 'empty candidate no-op'
    Assert-TestCallbackOrder $success.Fixture 'Get:target-two'

    foreach ($example in @(
        @{ Failure = 'Build'; Stage = 'Build'; Events = 'Get:,Build:target-one' },
        @{ Failure = 'Baseline'; Stage = 'BaselineVerify'; Events = 'Get:,Build:target-one,Capture:new,Verify:old' }
    )) {
        $test = Get-TestTarget (Join-Path $fixtureRoot "$($example.Failure).json") $example.Failure
        $callbacks = $test.Callbacks
        $result = Invoke-TargetDeploymentTransaction @callbacks
        Assert-Equal -Actual $result.Status -Expected 'Rejected' -Case 'pre-apply failure'
        Assert-Equal -Actual $result.Stage -Expected $example.Stage -Case 'pre-apply classification'
        Assert-TestCallbackOrder $test.Fixture $example.Events
        $test.Fixture.Events.Clear()
        Assert-Equal -Actual (Invoke-TargetDeploymentTransaction @callbacks).Status -Expected 'NoChange' -Case 'pre-apply failure consumes target'
        Assert-TestCallbackOrder $test.Fixture 'Get:target-one'
    }

    foreach ($example in @(
        @{ Failure = 'Apply'; Kind = 'Error'; Stage = 'Apply'; Events = 'Get:prior-target,Build:target-one,Capture:new,Verify:old,Apply:new,Apply:old,Verify:old' },
        @{ Failure = 'ApplyTimeout'; Kind = 'Timeout'; Stage = 'Apply'; Events = 'Get:prior-target,Build:target-one,Capture:new,Verify:old,Apply:new,Apply:old,Verify:old' },
        @{ Failure = 'Timeout'; Kind = 'Timeout'; Stage = 'Verify'; Events = 'Get:prior-target,Build:target-one,Capture:new,Verify:old,Apply:new,Verify:new,Apply:old,Verify:old' }
    )) {
        $test = Get-TestTarget (Join-Path $fixtureRoot "$($example.Failure).json") $example.Failure
        $callbacks = $test.Callbacks
        $prior = Get-TargetDeploymentStateTemplate -TargetRevision 'prior-target' -Outcome 'Applied' `
            -LastGood @{ Version = 'old' } -PreviousGood @{ Version = 'older'; Images = @{ main = 'older-id' } }
        Write-TargetDeploymentState $callbacks.StatePath $prior
        $result = Invoke-TargetDeploymentTransaction @callbacks
        Assert-Equal -Actual $result.Status -Expected 'RolledBack' -Case 'automatic rollback'
        Assert-Equal -Actual $result.Stage -Expected $example.Stage -Case 'initial failure stage'
        Assert-Equal -Actual $result.FailureKind -Expected $example.Kind -Case 'sanitized apply or verify failure kind'
        Assert-Equal -Actual $test.Fixture.Live -Expected 'old' -Case 'previous live version restored'
        $state = Get-TargetDeploymentState $callbacks.StatePath
        Assert-Equal -Actual $state.lastGood.Images.main -Expected 'old-id' -Case 'verified rollback descriptor is retained'
        Assert-Equal -Actual $state.previousGood.Images.main -Expected 'older-id' -Case 'rollback keeps former previous good'
        Assert-TestCallbackOrder $test.Fixture $example.Events
        $test.Fixture.Events.Clear()
        Assert-Equal -Actual (Invoke-TargetDeploymentTransaction @callbacks).Status -Expected 'NoChange' -Case 'rollback consumes target'
        Assert-TestCallbackOrder $test.Fixture 'Get:target-one'
    }

    $latched = Get-TestTarget (Join-Path $fixtureRoot 'latch.json') 'Rollback'
    $callbacks = $latched.Callbacks
    $result = Invoke-TargetDeploymentTransaction @callbacks
    Assert-Equal -Actual $result.Status -Expected 'RollbackFailed' -Case 'rollback failure reported'
    Assert-Equal -Actual $result.Stage -Expected 'RollbackApply' -Case 'rollback failure stage recorded'
    Assert-Equal -Actual $result.FailureKind -Expected 'Error' -Case 'rollback failure keeps original apply classification'
    Assert-TestCallbackOrder $latched.Fixture 'Get:,Build:target-one,Capture:new,Verify:old,Apply:new,Apply:old,Verify:old'
    $state = Get-TargetDeploymentState $callbacks.StatePath
    Assert-Equal -Actual $state.pending.phase -Expected 'RollbackFailed' -Case 'durable failure latch'
    Assert-Equal -Actual $state.pending.failureKind -Expected 'Error' -Case 'latch retains sanitized classification'
    Assert-Equal -Actual $state.pending.previous.Images.aux -Expected 'old-aux-id' -Case 'retained rollback descriptor'
    Assert-Equal -Actual $state.pending.candidate.Images.aux -Expected 'new-aux-id' -Case 'retained candidate descriptor'
    $latched.Fixture.Events.Clear()
    $latched.Fixture.Revision = 'later-target'
    Assert-Equal -Actual (Invoke-TargetDeploymentTransaction @callbacks).Stage -Expected 'RollbackLatched' -Case 'latch blocks new targets'
    Assert-TestCallbackOrder $latched.Fixture ''

    foreach ($fails in @($false, $true)) {
        $guarded = Get-TestTarget (Join-Path $fixtureRoot "guarded-$fails.json") 'Apply'
        $callbacks = $guarded.Callbacks
        $callbacks.PrepareRollback = {
            param($pending)
            $guarded.Fixture.Events.Add("Prepare:$($pending.previous.Version)")
            if ($fails) { throw 'rollback preparation failed' }
        }.GetNewClosure()
        $result = Invoke-TargetDeploymentTransaction @callbacks
        if ($fails) {
            Assert-Equal $result.Status 'RollbackFailed' 'preparation failure keeps the rollback latched'
            Assert-Equal $result.Stage 'RollbackPrepare' 'preparation failure is classified'
            Assert-TestCallbackOrder $guarded.Fixture 'Get:,Build:target-one,Capture:new,Verify:old,Apply:new,Prepare:old'
            Assert-Equal (Get-TargetDeploymentState $callbacks.StatePath).pending.rollbackStage 'RollbackPrepare' 'preparation failure is durable'
            Assert-Equal (Invoke-TargetDeploymentTransaction @callbacks).Stage 'RollbackLatched' 'failed preparation prevents later apply'
        } else {
            Assert-Equal $result.Status 'RolledBack' 'prepared rollback settles'
            Assert-TestCallbackOrder $guarded.Fixture 'Get:,Build:target-one,Capture:new,Verify:old,Apply:new,Prepare:old,Apply:old,Verify:old'
        }
    }

    $crashPath = Join-Path $fixtureRoot 'crash.json'
    $prior = Get-TargetDeploymentStateTemplate -TargetRevision 'prior-target' -Outcome 'Applied' `
        -LastGood @{ Version = 'old' } -PreviousGood @{ Version = 'older'; Images = @{ main = 'older-id' } }
    Write-TargetDeploymentState $crashPath $prior
    $pwsh = Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })
    $nativePreference = $PSNativeCommandUseErrorActionPreference
    try {
        $PSNativeCommandUseErrorActionPreference = $false
        & $pwsh -NoProfile -NonInteractive -File $PSCommandPath -CrashStatePath $crashPath
        Assert-Equal -Actual $LASTEXITCODE -Expected 27 -Case 'owned child interrupted during apply'
    } finally {
        $PSNativeCommandUseErrorActionPreference = $nativePreference
    }
    Assert-Equal -Actual ([IO.File]::ReadAllText("$crashPath.live")) -Expected 'new' -Case 'child changed simulated runtime'
    $state = Get-TargetDeploymentState $crashPath
    Assert-Equal -Actual $state.pending.phase -Expected 'Applying' -Case 'durable journal predates mutation'
    Assert-Equal -Actual ($state.pending.previous.Values -join ',') -Expected 'three,four' -Case 'serialized rollback descriptor'

    $restarted = Get-TestTarget $crashPath
    $restarted.Fixture.Live = [IO.File]::ReadAllText("$crashPath.live")
    $callbacks = $restarted.Callbacks
    $callbacks.PrepareRollback = {
        param($pending)
        $restarted.Fixture.Events.Add("Prepare:$($pending.previous.Version)")
    }.GetNewClosure()
    $result = Invoke-TargetDeploymentTransaction @callbacks
    Assert-Equal -Actual $result.Status -Expected 'Recovered' -Case 'restart recovered previous'
    Assert-Equal -Actual $result.TargetRevision -Expected 'crash-target' -Case 'restart consumed interrupted revision'
    Assert-TestCallbackOrder $restarted.Fixture 'Prepare:old,Apply:old,Verify:old'
    $state = Get-TargetDeploymentState $crashPath
    Assert-Equal -Actual $state.lastProcessedTargetRevision -Expected 'crash-target' -Case 'restart recorded on disk'
    Assert-Equal -Actual ($state.lastGood.Values -join ',') -Expected 'three,four' -Case 'verified restart rollback descriptor is retained'
    Assert-Equal -Actual $state.previousGood.Images.main -Expected 'older-id' -Case 'restart keeps former previous good'

    $legacy = Get-TestTarget (Join-Path $fixtureRoot 'legacy.json')
    $callbacks = $legacy.Callbacks
    [IO.File]::WriteAllText($callbacks.StatePath, '{"schemaVersion":1,"lastProcessedTargetRevision":"target-one","lastOutcome":"Applied","pending":null}')
    Assert-Equal -Actual (Invoke-TargetDeploymentTransaction @callbacks).Status -Expected 'NoChange' -Case 'legacy settled journal can be read'
    $state = Get-TargetDeploymentState $callbacks.StatePath
    Assert-Equal -Actual $state.lastGood -Expected $null -Case 'legacy settled journal has optional last good'
    Assert-Equal -Actual $state.previousGood -Expected $null -Case 'legacy settled journal has optional previous good'

    $busy = Get-TestTarget (Join-Path $fixtureRoot 'busy.json')
    $callbacks = $busy.Callbacks
    $held = [IO.File]::Open("$($callbacks.StatePath).lock", [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        Assert-Equal -Actual (Invoke-TargetDeploymentTransaction @callbacks).Status -Expected 'Busy' -Case 'exclusive host lock'
        Assert-TestCallbackOrder $busy.Fixture ''
        Assert-Equal -Actual ([IO.File]::Exists($callbacks.StatePath)) -Expected $false -Case 'busy does not write journal'
    } finally {
        $held.Dispose()
    }

    $broken = Get-TestTarget (Join-Path $fixtureRoot 'malformed.json')
    $callbacks = $broken.Callbacks
    [IO.File]::WriteAllText($callbacks.StatePath, '{invalid journal')
    Assert-Equal -Actual (Invoke-TargetDeploymentTransaction @callbacks).Status -Expected 'RollbackFailed' -Case 'corrupt state fails closed'
    Assert-TestCallbackOrder $broken.Fixture ''
    Assert-Equal -Actual ([IO.File]::ReadAllText($callbacks.StatePath)) -Expected '{invalid journal' -Case 'corrupt evidence retained'

    'TARGET_DEPLOYMENT_TRANSACTION_PASS'
} finally {
    $resolved = (Resolve-Path -LiteralPath $fixtureRoot).Path
    $parent = (Resolve-Path -LiteralPath $fixtureParent).Path
    if ($resolved -ne $fixtureRoot -or [IO.Path]::GetDirectoryName($resolved) -ne $parent -or
        ((Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'TARGET_TRANSACTION_TEST_CLEANUP_PATH_REJECTED'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
