#requires -Version 7.4
param([Parameter(Mandatory)][string]$ConfigPath)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'TargetDeployment.Transaction.ps1')

$config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json -AsHashtable
if ($config.targetBranch -cnotmatch '^targets/[a-z0-9][a-z0-9-]{0,62}$' -or
    $config.schemaBaselineSha -cnotmatch '^[0-9a-f]{40}$' -or
    [string]::IsNullOrWhiteSpace($config.rollbackContract) -or
    ![IO.Path]::IsPathFullyQualified($config.stateDirectory) -or @($config.groups).Count -eq 0) {
    throw 'Invalid target deployment configuration.'
}
$root = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($config.stateDirectory))
[void][IO.Directory]::CreateDirectory($root)
$bare = Join-Path $root 'source.git'
$statePath = Join-Path $root 'transaction.json'
$activePath = Join-Path $root 'apply-process.json'
$git = (Get-Command git -CommandType Application | Select-Object -First 1).Source
$docker = (Get-Command docker -CommandType Application | Select-Object -First 1).Source

foreach ($group in $config.groups) {
    if ($group.project -cnotmatch '^[a-z0-9][a-z0-9_-]+$' -or ![IO.Path]::IsPathFullyQualified($group.directory) -or
        @($group.files).Count -eq 0 -or @($group.services).Count -eq 0) { throw 'Invalid Compose group.' }
    foreach ($service in $group.services) {
        if ($service.name -cnotmatch '^[a-z0-9][a-z0-9_.-]+$' -or
            $service.dockerfile -cnotmatch '^[a-zA-Z0-9_./-]+$' -or
            [IO.Path]::IsPathRooted($service.dockerfile) -or ($service.dockerfile -split '/') -contains '..') {
            throw 'Invalid release build path or service name.'
        }
    }
}

function Invoke-StaleApplyRecovery {
    if (!(Test-Path -LiteralPath $activePath)) { return }
    $receipt = Get-Content -LiteralPath $activePath -Raw | ConvertFrom-Json
    $previous = Get-Process -Id $receipt.pid -ErrorAction SilentlyContinue
    if ($null -ne $previous -and !$previous.HasExited -and
        $previous.StartTime.ToUniversalTime().Ticks -eq [long]$receipt.startTicks) {
        $previous.Kill($true)
        if (!$previous.WaitForExit(5000)) { throw 'Previous Compose client could not be stopped.' }
    }
    Remove-Item -LiteralPath $activePath
}

function Invoke-Tool {
    param([string]$Executable, [string[]]$Arguments, [int]$Seconds, [switch]$TracksApply, [switch]$ReturnsStatus)
    if ($Seconds -lt 1 -or $Seconds -gt 7200) { throw 'Invalid command deadline.' }
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['GIT_TERMINAL_PROMPT'] = '0'
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    if ($TracksApply) { Invoke-StaleApplyRecovery }
    $process = [Diagnostics.Process]::Start($start)
    try {
        if ($TracksApply) {
            $temp = "$activePath.$PID.tmp"
            @{ pid = $process.Id; startTicks = [string]$process.StartTime.ToUniversalTime().Ticks } |
                ConvertTo-Json -Compress | Set-Content -LiteralPath $temp
            [IO.File]::Move($temp, $activePath, $true)
        }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit($Seconds * 1000)) {
            $process.Kill($true)
            if (!$process.WaitForExit(5000)) { throw 'Timed-out client could not be stopped.' }
            throw [TimeoutException]::new("$([IO.Path]::GetFileName($Executable)) exceeded the command deadline.")
        }
        if (!$stdout.Wait(5000) -or !$stderr.Wait(5000)) { throw 'A client output stream did not close.' }
        if ($ReturnsStatus) { return $process.ExitCode }
        if ($process.ExitCode -ne 0) { throw "$([IO.Path]::GetFileName($Executable)) returned exit $($process.ExitCode)." }
        return $stdout.Result.Trim()
    }
    finally {
        if (!$process.HasExited) {
            try { $process.Kill($true); [void]$process.WaitForExit(5000) } catch { Write-Verbose 'Apply client is still tracked for recovery.' }
        }
        if ($TracksApply -and $process.HasExited -and (Test-Path -LiteralPath $activePath)) {
            Remove-Item -LiteralPath $activePath
        }
        $process.Dispose()
    }
}

function Invoke-Git {
    param([string[]]$Arguments, [switch]$ReturnsStatus)
    Invoke-Tool $git (@("--git-dir=$bare") + $Arguments) 90 -ReturnsStatus:$ReturnsStatus
}

function Get-ComposeArgument {
    param($Group)
    $result = @('compose', '--project-directory', [string]$Group.directory, '--project-name', [string]$Group.project)
    if ($Group.Contains('envFile')) { $result += @('--env-file', [string]$Group.envFile) }
    foreach ($file in $Group.files) { $result += @('-f', [string]$file) }
    return $result
}

function Get-ComposeFingerprint {
    param($Group)
    $rendered = Invoke-Tool $docker ((Get-ComposeArgument $Group) + @('config', '--format', 'json')) 30
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($rendered)))
}

function Get-LiveServiceImage {
    param($Group, [string]$Service)
    $container = Invoke-Tool $docker ((Get-ComposeArgument $Group) + @('ps', '--all', '--quiet', $Service)) 30
    if ($container -cnotmatch '^[0-9a-f]{64}$') { throw "Expected one owned container for $Service." }
    $template = '{{.Image}}|{{.State.Status}}|{{if .State.Health}}{{.State.Health.Status}}{{end}}|{{index .Config.Labels "com.docker.compose.project"}}|{{index .Config.Labels "com.docker.compose.service"}}'
    $info = (Invoke-Tool $docker @('inspect', '--type', 'container', '--format', $template, $container) 30).Split('|')
    if ($info.Count -ne 5 -or $info[0] -cnotmatch '^sha256:[0-9a-f]{64}$' -or $info[1] -cne 'running' -or
        $info[2] -cne 'healthy' -or $info[3] -cne $Group.project -or $info[4] -cne $Service) {
        throw "Ownership or health check failed for $Service."
    }
    return $info[0]
}

function Get-ImageId {
    param([string]$Tag)
    $result = Invoke-Tool $docker @('image', 'inspect', '--format', '{{.Id}}', $Tag) 30
    if ($result -cnotmatch '^sha256:[0-9a-f]{64}$') { throw 'Image ID was not verified locally.' }
    return $result
}

function Invoke-ReleasePreparation {
    param([string]$Revision, [switch]$Baseline, [string]$SourcePath, [string]$Version, [string]$SourceSha)
    $nonce = [guid]::NewGuid().ToString('N')
    $release = @{ revision = $Revision; sourceSha = $SourceSha; version = $Version; baseline = [bool]$Baseline; groups = @() }
    for ($i = 0; $i -lt $config.groups.Count; $i++) {
        $group = $config.groups[$i]
        $entry = @{ fingerprint = (Get-ComposeFingerprint $group); images = @() }
        for ($j = 0; $j -lt $group.services.Count; $j++) {
            $service = $group.services[$j]
            $tag = "localhost/contexthub-target-$($group.project):$nonce-$i-$j"
            if ($Baseline) {
                $live = Get-LiveServiceImage $group $service.name
                [void](Invoke-Tool $docker @('image', 'tag', $live, $tag) 30)
            }
            else {
                $dockerfile = Join-Path $SourcePath $service.dockerfile
                if (!(Test-Path -LiteralPath $dockerfile -PathType Leaf)) { throw 'Candidate Dockerfile does not exist.' }
                [void](Invoke-Tool $docker @('build', '--quiet', '--label', "org.opencontainers.image.revision=$SourceSha",
                    '--build-arg', "APP_VERSION=$Version", '--build-arg', "BUILD_TIMESTAMP_UTC=$([DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ'))", '--file', $dockerfile, '--tag', $tag, $SourcePath) ([int]$config.buildSeconds))
            }
            $entry.images += @{ service = $service.name; tag = $tag; id = (Get-ImageId $tag) }
        }
        $release.groups += $entry
    }
    return $release
}

$getCandidate = {
    param($LastProcessed)
    if (!(Test-Path -LiteralPath (Join-Path $bare 'HEAD'))) {
        [void](Invoke-Tool $git @('init', '--bare', $bare) 30)
    }
    $ref = "+refs/heads/$($config.targetBranch):refs/heads/target"
    [void](Invoke-Git @('fetch', '--no-tags', '--no-recurse-submodules', [string]$config.repository,
        '+refs/heads/main:refs/heads/authority', $ref))
    $head = Invoke-Git @('rev-parse', 'refs/heads/target')
    if ($head -ceq $LastProcessed) { return $null }
    if ($LastProcessed) {
        $chain = (Invoke-Git @('rev-list', '--first-parent', $head)).Split([char]10).Trim()
        $index = [Array]::IndexOf($chain, [string]$LastProcessed)
        if ($index -le 0) { throw 'Target branch history was rewritten.' }
        $head = $chain[$index - 1]
    }
    $candidate = @{ targetRevision = $head; sourceSha = $null; version = $null }
    try {
        $promotion = (Invoke-Git @('show', "$($head):promotion.json")) | ConvertFrom-Json -AsHashtable
        $candidate.sourceSha = $promotion.sourceSha
        $candidate.version = $promotion.version
        $candidate.promotionTarget = $promotion.target
        $candidate.rollbackContract = $promotion.rollbackContract
    }
    catch {
        # Keep the immutable target revision so the transaction records the rejection.
        $candidate.sourceSha = $null
        $candidate.version = $null
    }
    return $candidate
}

$build = {
    param($Candidate)
    $revision = $Candidate.targetRevision
    $promotion = $Candidate
    if ($promotion.promotionTarget -cne $config.targetBranch -or $promotion.sourceSha -cnotmatch '^[0-9a-f]{40}$' -or
        $promotion.version -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[a-zA-Z0-9.-]{1,36})?$' -or
        $promotion.rollbackContract -cne $config.rollbackContract) { throw 'Invalid or incompatible promotion record.' }
    $source = [string]$promotion.sourceSha
    if ((Invoke-Git @('merge-base', '--is-ancestor', $source, 'refs/heads/authority') -ReturnsStatus) -ne 0 -or
        (Invoke-Git @('merge-base', '--is-ancestor', [string]$config.schemaBaselineSha, $source) -ReturnsStatus) -ne 0) {
        throw 'Candidate source is outside the approved Git lineage.'
    }
    $known = (Get-TargetDeploymentState -Path $statePath).lastGood
    if ($null -ne $known -and $known['sourceSha'] -and
        (Invoke-Git @('merge-base', '--is-ancestor', [string]$known.sourceSha, $source) -ReturnsStatus) -ne 0) {
        throw 'Promotion would move behind the current source lineage.'
    }
    $migrations = 'src/Memory.Infrastructure/Sql/Migrations'
    if ((Invoke-Git @('ls-tree', '-r', [string]$config.schemaBaselineSha, '--', $migrations)) -cne
        (Invoke-Git @('ls-tree', '-r', $source, '--', $migrations))) {
        throw 'Candidate changes the approved migration set.'
    }
    $stage = Join-Path $root ("build-" + [guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($stage)
    try {
        $archive = Join-Path $stage 'source.zip'
        $extract = Join-Path $stage 'source'
        [void](Invoke-Git @('archive', '--format=zip', "--output=$archive", $source))
        [IO.Compression.ZipFile]::ExtractToDirectory($archive, $extract)
        Invoke-ReleasePreparation -Revision $revision -SourcePath $extract -Version $promotion.version -SourceSha $source
    }
    finally {
        $resolved = (Resolve-Path -LiteralPath $stage).Path
        if ([IO.Path]::GetDirectoryName($resolved) -ne $root -or
            (Get-Item -LiteralPath $resolved).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
            throw 'Build directory could not be safely removed.'
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

$capture = {
    param($CandidateRelease)
    $known = (Get-TargetDeploymentState -Path $statePath).lastGood
    if ($null -ne $known) { & $verify $known }
    $baseline = Invoke-ReleasePreparation -Revision $CandidateRelease.revision -Baseline -SourceSha $(if ($known) { $known['sourceSha'] }) -Version $(if ($known) { $known['version'] })
    for ($i = 0; $i -lt $config.groups.Count; $i++) {
        if ($baseline.groups[$i].fingerprint -cne $CandidateRelease.groups[$i].fingerprint) {
            throw 'Compose settings changed while candidate images were built.'
        }
    }
    return $baseline
}

$verify = {
    param($Release)
    for ($i = 0; $i -lt $config.groups.Count; $i++) {
        $group = $config.groups[$i]
        if ((Get-ComposeFingerprint $group) -cne $Release.groups[$i].fingerprint) {
            throw 'Compose configuration changed during the deployment.'
        }
        foreach ($image in $Release.groups[$i].images) {
            if ((Get-ImageId $image.tag) -cne $image.id -or
                (Get-LiveServiceImage $group $image.service) -cne $image.id) {
                throw "Live image identity check failed for $($image.service)."
            }
        }
    }
}

$apply = {
    param($Release)
    Invoke-StaleApplyRecovery
    for ($i = 0; $i -lt $config.groups.Count; $i++) {
        $group = $config.groups[$i]
        if ((Get-ComposeFingerprint $group) -cne $Release.groups[$i].fingerprint) {
            throw 'The live Compose configuration no longer matches the release.'
        }
        foreach ($image in $Release.groups[$i].images) {
            if ((Get-ImageId $image.tag) -cne $image.id) { throw 'Retained image tag no longer identifies the expected image.' }
        }
    }
    $seconds = if ($Release.baseline) { [int]$config.rollbackSeconds } else { [int]$config.applySeconds }
    $elapsed = [Diagnostics.Stopwatch]::StartNew()
    for ($i = 0; $i -lt $config.groups.Count; $i++) {
        $group = $config.groups[$i]
        $overlay = Join-Path $root ("images-" + [guid]::NewGuid().ToString('N') + '.json')
        $services = [ordered]@{}
        foreach ($image in $Release.groups[$i].images) { $services[$image.service] = @{ image = $image.tag } }
        @{ services = $services } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $overlay
        # A canceled Compose client can leave an in-flight daemon rename. Retry only the
        # same rollback images, within one deadline, while the daemon settles.
        $attempts = if ($Release.baseline) { 3 } else { 1 }
        for ($attempt = 1; $attempt -le $attempts; $attempt++) {
            $remaining = $seconds - [int][Math]::Floor($elapsed.Elapsed.TotalSeconds)
            if ($remaining -lt 1) { throw [TimeoutException]::new('Compose deployment deadline elapsed.') }
            $composeApply = (Get-ComposeArgument $group) + @('-f', $overlay, 'up', '--detach', '--no-deps', '--no-build',
                '--pull', 'never', '--wait', '--wait-timeout', [string]($remaining + 5)) + @($services.Keys)
            try {
                [void](Invoke-Tool $docker $composeApply $remaining -TracksApply)
                break
            }
            catch {
                if ($attempt -eq $attempts -or $elapsed.Elapsed.TotalSeconds -ge $seconds) { throw }
                Start-Sleep -Seconds 1
            }
        }
    }
}


function Invoke-ReleaseRetention {
    try {
        $lock = [IO.File]::Open("$statePath.lock", 'OpenOrCreate', 'ReadWrite', 'None')
    }
    catch [IO.IOException] { return }
    try {
        $saved = Get-TargetDeploymentState -Path $statePath
        if ($null -ne $saved.pending -or $null -eq $saved.lastGood) { return }
        $retained = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($release in @($saved.lastGood, $saved.previousGood)) {
            if (!$release) { continue }
            foreach ($groupEntry in $release.groups) {
                foreach ($image in $groupEntry.images) { [void]$retained.Add($image.tag) }
            }
        }
        foreach ($group in $config.groups) {
            $prefix = "localhost/contexthub-target-$($group.project)"
            $list = Invoke-Tool $docker @('image', 'ls', '--format', '{{.Repository}}:{{.Tag}}', $prefix) 30
            $pattern = '^' + [regex]::Escape($prefix) + ':[0-9a-f]{32}-[0-9]+-[0-9]+$'
            foreach ($tag in ($list -split '\r?\n')) {
                if ($tag -cmatch $pattern -and !$retained.Contains($tag)) {
                    try { [void](Invoke-Tool $docker @('image', 'rm', $tag) 30) }
                    catch { Write-Verbose 'A retired deployment tag remains in use.' }
                }
            }
        }
    }
    finally { $lock.Dispose() }
}

$result = Invoke-TargetDeploymentTransaction -StatePath $statePath -GetCandidate $getCandidate -Build $build -Capture $capture -Apply $apply -Verify $verify
if ($result.Status -cin @('Applied', 'Recovered', 'RolledBack', 'Rejected')) {
    try { Invoke-ReleaseRetention }
    catch { Write-Verbose 'Deployment is settled, but retired tags could not be enumerated.' }
}
$result
