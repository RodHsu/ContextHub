#Requires -Version 7.4
[CmdletBinding()]
param([string]$CrashStatePath)

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
    Assert-TestCallbackOrder $success.Fixture 'Get:,Build:target-one,Capture:new,Verify:old,Apply:new,Verify:new'
    $state = Get-TargetDeploymentState $callbacks.StatePath
    Assert-Equal -Actual $state.lastOutcome -Expected 'Applied' -Case 'persisted success'
    Assert-Equal -Actual $state.pending -Expected $null -Case 'success clears pending'

    $success.Fixture.Events.Clear()
    Assert-Equal -Actual (Invoke-TargetDeploymentTransaction @callbacks).Status -Expected 'NoChange' -Case 'duplicate target no-op'
    Assert-TestCallbackOrder $success.Fixture 'Get:target-one'
    $success.Fixture.Events.Clear()
    $success.Fixture.Revision = $null
    Assert-Equal -Actual (Invoke-TargetDeploymentTransaction @callbacks).Status -Expected 'NoChange' -Case 'empty candidate no-op'
    Assert-TestCallbackOrder $success.Fixture 'Get:target-one'

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
        @{ Failure = 'Apply'; Stage = 'Apply'; Events = 'Get:,Build:target-one,Capture:new,Verify:old,Apply:new,Apply:old,Verify:old' },
        @{ Failure = 'Timeout'; Stage = 'Verify'; Events = 'Get:,Build:target-one,Capture:new,Verify:old,Apply:new,Verify:new,Apply:old,Verify:old' }
    )) {
        $test = Get-TestTarget (Join-Path $fixtureRoot "$($example.Failure).json") $example.Failure
        $callbacks = $test.Callbacks
        $result = Invoke-TargetDeploymentTransaction @callbacks
        Assert-Equal -Actual $result.Status -Expected 'RolledBack' -Case 'automatic rollback'
        Assert-Equal -Actual $result.Stage -Expected $example.Stage -Case 'initial failure stage'
        Assert-Equal -Actual $test.Fixture.Live -Expected 'old' -Case 'previous live version restored'
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
    Assert-TestCallbackOrder $latched.Fixture 'Get:,Build:target-one,Capture:new,Verify:old,Apply:new,Apply:old,Verify:old'
    $state = Get-TargetDeploymentState $callbacks.StatePath
    Assert-Equal -Actual $state.pending.phase -Expected 'RollbackFailed' -Case 'durable failure latch'
    Assert-Equal -Actual $state.pending.previous.Images.aux -Expected 'old-aux-id' -Case 'retained rollback descriptor'
    Assert-Equal -Actual $state.pending.candidate.Images.aux -Expected 'new-aux-id' -Case 'retained candidate descriptor'
    $latched.Fixture.Events.Clear()
    $latched.Fixture.Revision = 'later-target'
    Assert-Equal -Actual (Invoke-TargetDeploymentTransaction @callbacks).Stage -Expected 'RollbackLatched' -Case 'latch blocks new targets'
    Assert-TestCallbackOrder $latched.Fixture ''

    $crashPath = Join-Path $fixtureRoot 'crash.json'
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
    $result = Invoke-TargetDeploymentTransaction @callbacks
    Assert-Equal -Actual $result.Status -Expected 'Recovered' -Case 'restart recovered previous'
    Assert-Equal -Actual $result.TargetRevision -Expected 'crash-target' -Case 'restart consumed interrupted revision'
    Assert-TestCallbackOrder $restarted.Fixture 'Apply:old,Verify:old'
    $state = Get-TargetDeploymentState $crashPath
    Assert-Equal -Actual $state.lastProcessedTargetRevision -Expected 'crash-target' -Case 'restart recorded on disk'

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
