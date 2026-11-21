#requires -Version 7.4
param(
    [Parameter(Mandatory)][string]$Root,
    [string]$Branch = 'targets/example',
    [string]$FixtureImage = 'redis:7.4-alpine',
    [string]$EvidencePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$runner = Join-Path (Split-Path -Parent $PSScriptRoot) 'Invoke-TargetDeployment.ps1'
$rootFull = [IO.Path]::GetFullPath($Root)
$parent = (Resolve-Path -LiteralPath (Split-Path -Parent $rootFull)).Path
if (Test-Path -LiteralPath $rootFull) { throw 'Fixture work directory already exists.' }
if ($Branch -cnotmatch '^targets/[a-z0-9][a-z0-9-]+$' -or
    $FixtureImage -cnotmatch '^[a-zA-Z0-9._/:@-]+$') { throw 'Invalid fixture branch or image.' }

function Invoke-FixtureNative {
    param([string]$Executable, [string[]]$Arguments)
    $output = & $Executable @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Fixture $Executable failed with exit $LASTEXITCODE." }
    return ($output -join [Environment]::NewLine).Trim()
}

function Invoke-FixtureGit {
    param([string]$Directory, [string[]]$Arguments)
    Invoke-FixtureNative git (@('-C', $Directory, '-c', 'user.name=Target Deployment Fixture',
        '-c', 'user.email=fixture@example.invalid') + $Arguments)
}

function Assert-FixtureEqual {
    param($Actual, $Expected, [string]$Check)
    if ($Actual -cne $Expected) { throw "$Check expected $Expected but received $Actual." }
}

$source = Join-Path $rootFull 'source'
$target = Join-Path $rootFull 'target'
$remote = Join-Path $rootFull 'origin.git'
$state = Join-Path $rootFull 'state'
$project = 'ctx-target-test-' + [guid]::NewGuid().ToString('N').Substring(0, 10)
$compose = Join-Path $rootFull 'compose.yml'
$config = Join-Path $rootFull 'runner.json'
[void][IO.Directory]::CreateDirectory($rootFull)

function Get-FixtureLiveImage {
    $container = Invoke-FixtureNative docker @('compose', '-p', $project, '-f', $compose, 'ps', '--quiet', 'canary')
    Invoke-FixtureNative docker @('inspect', '--format', '{{.Image}}|{{.State.Health.Status}}', $container)
}

function Push-FixtureSource {
    param([string]$Mode)
    [IO.File]::WriteAllText((Join-Path $source 'mode'), $Mode + [char]10)
    $command = if ($Mode -eq 'failure') { 'ENTRYPOINT ["/missing-fixture-entrypoint"]' } else { 'CMD ["sh", "-c", "while :; do sleep 1; done"]' }
    $dockerfile = @("FROM $FixtureImage", 'ARG APP_VERSION', 'COPY mode /fixture-mode')
    if ($Mode -eq 'timeout') { $dockerfile += 'RUN touch /fixture-never-ready' }
    $dockerfile += $command
    $dockerfile | Set-Content -LiteralPath (Join-Path $source 'Dockerfile')
    [void](Invoke-FixtureGit $source @('add', '--', 'Dockerfile', 'mode'))
    [void](Invoke-FixtureGit $source @('commit', '-m', $Mode))
    [void](Invoke-FixtureGit $source @('push', $remote, 'HEAD:refs/heads/main'))
    return Invoke-FixtureGit $source @('rev-parse', 'HEAD')
}

function Push-FixtureTarget {
    param([string]$SourceSha, [string]$Version)
    @{ target = $Branch; sourceSha = $SourceSha; version = $Version; rollbackContract = 'fixture-v1' } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $target 'promotion.json')
    [void](Invoke-FixtureGit $target @('add', '--', 'promotion.json'))
    [void](Invoke-FixtureGit $target @('commit', '-m', "Promote $Version"))
    [void](Invoke-FixtureGit $target @('push', $remote, "HEAD:refs/heads/$Branch"))
    return Invoke-FixtureGit $target @('rev-parse', 'HEAD')
}

try {
    [void](Invoke-FixtureNative docker @('image', 'inspect', '--format', '{{.Id}}', $FixtureImage))
    [void](Invoke-FixtureNative git @('init', '--bare', $remote))
    [void](Invoke-FixtureNative git @('init', '--initial-branch=main', $source))
    [void](Invoke-FixtureNative git @('init', "--initial-branch=$Branch", $target))
    $approved = Push-FixtureSource 'healthy'
    @"
services:
  canary:
    image: $FixtureImage
    stop_grace_period: 1s
    healthcheck:
      test: ["CMD-SHELL", "test ! -e /fixture-never-ready"]
      interval: 1s
      timeout: 1s
      retries: 60
"@ | Set-Content -LiteralPath $compose
    @{
        repository = $remote; targetBranch = $Branch; stateDirectory = $state; schemaBaselineSha = $approved;
        rollbackContract = 'fixture-v1'; buildSeconds = 120; applySeconds = 20; rollbackSeconds = 50;
        groups = @(@{ project = $project; directory = $rootFull; files = @($compose);
            services = @(@{ name = 'canary'; dockerfile = 'Dockerfile' }) })
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $config
    [void](Invoke-FixtureNative docker @('compose', '-p', $project, '-f', $compose, 'up', '--detach',
        '--pull', 'never', '--wait', '--wait-timeout', '25'))
    $bootstrap = Get-FixtureLiveImage

    $goodRevision = Push-FixtureTarget $approved '1.0.0'
    $good = & $runner -ConfigPath $config
    Assert-FixtureEqual $good.Status 'Applied' "Healthy push stage $($good.Stage)"
    Assert-FixtureEqual $good.TargetRevision $goodRevision 'Healthy target revision'
    $goodImage = Get-FixtureLiveImage
    if ($goodImage -eq $bootstrap) { throw 'The healthy push did not replace the image.' }

    $failedSource = Push-FixtureSource 'failure'
    $failureRevision = Push-FixtureTarget $failedSource '1.0.1'
    $failure = & $runner -ConfigPath $config
    Assert-FixtureEqual $failure.Status 'RolledBack' "Apply error stage $($failure.Stage)"
    Assert-FixtureEqual $failure.FailureKind 'Error' 'Apply error classification'
    Assert-FixtureEqual $failure.TargetRevision $failureRevision 'Failed target revision'
    Assert-FixtureEqual (Get-FixtureLiveImage) $goodImage 'Apply error recovery image'

    $timeoutSource = Push-FixtureSource 'timeout'
    $timeoutRevision = Push-FixtureTarget $timeoutSource '1.0.2'
    $timeout = & $runner -ConfigPath $config
    Assert-FixtureEqual $timeout.Status 'RolledBack' "Apply timeout stage $($timeout.Stage)"
    Assert-FixtureEqual $timeout.FailureKind 'Timeout' 'Apply timeout classification'
    Assert-FixtureEqual $timeout.TargetRevision $timeoutRevision 'Timed-out target revision'
    Assert-FixtureEqual (Get-FixtureLiveImage) $goodImage 'Timeout recovery image'

    $same = & $runner -ConfigPath $config
    Assert-FixtureEqual $same.Status 'NoChange' 'Processed revision replay'
    $evidence = [ordered]@{
        branch = $Branch; isolatedCompose = $project;
        applied = $good; failedAndRolledBack = $failure; timedOutAndRolledBack = $timeout;
        replay = $same.Status; recoveredImageAndHealth = $goodImage
    }
    if ($EvidencePath) { $evidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $EvidencePath }
    $evidence | ConvertTo-Json -Depth 8
}
finally {
    if (Test-Path -LiteralPath $compose) {
        [void](Invoke-FixtureNative docker @('compose', '-p', $project, '-f', $compose, 'down'))
    }
    $tags = Invoke-FixtureNative docker @('image', 'ls', '--format', '{{.Repository}}:{{.Tag}}',
        "localhost/contexthub-target-$project")
    foreach ($tag in ($tags -split '\r?\n')) {
        if ($tag -match ('^localhost/contexthub-target-' + [regex]::Escape($project) + ':[0-9a-f-]+$')) {
            [void](Invoke-FixtureNative docker @('image', 'rm', $tag))
        }
    }

    $resolved = (Resolve-Path -LiteralPath $rootFull).Path
    if ([IO.Path]::GetDirectoryName($resolved) -ne $parent -or
        (Get-Item -LiteralPath $resolved).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
        throw 'Fixture work directory could not be safely removed.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
