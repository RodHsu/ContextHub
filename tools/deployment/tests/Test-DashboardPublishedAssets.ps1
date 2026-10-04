[CmdletBinding()]
param([Parameter(Mandatory)][ValidatePattern('^sha256:[a-f0-9]{64}$')][string]$ImageId)
$ErrorActionPreference = 'Stop'
$fixture = 'contexthub-dashboard-assets-test-' + [Guid]::NewGuid().ToString('N')
$owner = [Guid]::NewGuid().ToString('N')
function Invoke-AssetDocker([string[]]$Arguments) {
    $previous = $ErrorActionPreference
    try { $ErrorActionPreference = 'Continue'; $output = @(& docker @Arguments 2>$null); $code = $LASTEXITCODE }
    finally { $ErrorActionPreference = $previous }
    if ($code) { throw 'DASHBOARD_PUBLISHED_ASSET_DOCKER_FAILED' }
    return $output
}
$created = $false
try {
    $null = Invoke-AssetDocker @('run','-d','--name',$fixture,'--label',('contexthub.asset-test-owner='+$owner),'--network','none','--memory','2g','--cpus','2','-e','ASPNETCORE_ENVIRONMENT=Testing','-e','ASPNETCORE_URLS=http://+:8088','-e','Dashboard__UseBrowserTestDoubles=true',$ImageId)
    $created = $true
    $container = @(Invoke-AssetDocker @('inspect',$fixture) | ConvertFrom-Json)[0]
    if ($container.Image -cne $ImageId -or $container.HostConfig.NetworkMode -cne 'none' -or @($container.HostConfig.PortBindings.PSObject.Properties).Count -ne 0 -or @($container.Mounts).Count -ne 0) { throw 'DASHBOARD_ASSET_FIXTURE_ISOLATION_FAILED' }
    $manifest = (Invoke-AssetDocker @('exec',$fixture,'cat','/app/Memory.Dashboard.staticwebassets.endpoints.json') -join "`n") | ConvertFrom-Json
    $framework = @($manifest.Endpoints | Where-Object { $_.AssetFile -ceq '_framework/blazor.web.js' })
    if ($framework.Count -lt 1) { throw 'DASHBOARD_BLAZOR_BOOTSTRAP_NOT_PUBLISHED' }
    $routes = @(@('/_framework/blazor.web.js') + @($framework | ForEach-Object { '/' + $_.Route.TrimStart('/') }) | Sort-Object -Unique)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    do {
        $previous = $ErrorActionPreference
        try { $ErrorActionPreference = 'Continue'; $status = docker exec $fixture curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:8088/login 2>$null; $code=$LASTEXITCODE }
        finally { $ErrorActionPreference=$previous }
        if ($code -eq 0 -and $status -ceq '200') { break }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    if ($status -cne '200') { throw 'DASHBOARD_ASSET_FIXTURE_NOT_READY' }
    foreach ($route in $routes) {
        $status = Invoke-AssetDocker @('exec',$fixture,'curl','-f','-s','-o','/dev/null','-w','%{http_code}',('http://127.0.0.1:8088'+$route))
        if (($status -join '') -cne '200') { throw 'DASHBOARD_BLAZOR_BOOTSTRAP_HTTP_FAILED' }
    }
    [pscustomobject]@{PublishedImage=$ImageId;BootstrapManifestPresent=$true;BootstrapRoutesVerified=$routes.Count;BootstrapHttp=200;Network='none';PortsPublished=0;ProductionCredentialsUsed=$false;ProductionRuntimeAcceptanceClaimed=$false} | ConvertTo-Json -Compress
} finally {
    if ($created) {
        $container = @(Invoke-AssetDocker @('inspect',$fixture) | ConvertFrom-Json)[0]
        if ($container.Config.Labels.'contexthub.asset-test-owner' -cne $owner -or $container.Image -cne $ImageId -or $container.HostConfig.NetworkMode -cne 'none') { throw 'DASHBOARD_ASSET_FIXTURE_CLEANUP_OWNERSHIP_FAILED' }
        $null = Invoke-AssetDocker @('rm','-f',$fixture)
    }
}
