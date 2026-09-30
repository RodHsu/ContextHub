[CmdletBinding()]
param([switch]$UseSyntheticAclFixture,[switch]$SkipLoopbackTlsFixture,
    [ValidateSet('Windows','Wsl')][string]$TlsFixtureBackend='Windows')
$ErrorActionPreference='Stop'
$repoRoot=(Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
. (Join-Path $repoRoot 'tools/deployment/Portainer.Management.ps1')
$passed=0
function Assert-Check([bool]$Condition,[string]$Name) {
    if (-not $Condition) { throw "BOUNDARY_TEST_FAILED: $Name" }
    $script:passed++
}
function Assert-Rejected([scriptblock]$Action,[string]$Expected,[string]$Name) {
    $rejected=$false
    try { $null=& $Action } catch { $rejected=$_.Exception.Message -eq $Expected }
    Assert-Check $rejected $Name
}
$testRoot=Join-Path $repoRoot ('.agent/local/portainer-tests/'+[Guid]::NewGuid().ToString('N'))
$directory=Join-Path $testRoot '.agent/local/security/portainer'
$authorityPath=Join-Path $directory 'management-authority.json'
$tokenPath=Join-Path $directory 'management-token.clixml'
$identity=[Security.Principal.WindowsIdentity]::GetCurrent().User
$syntheticAcl=@{}
if ($UseSyntheticAclFixture) {
    # Policy-only fixtures. Never overrides the production script or provisions real authority.
    function Get-Acl {
        [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidOverwritingBuiltInCmdlets','',Justification='Explicit isolated synthetic ACL policy fixture, never a production override.')]
        [CmdletBinding()]
        param([string]$LiteralPath)
        if (-not $syntheticAcl.ContainsKey($LiteralPath)) { throw 'SYNTHETIC_ACL_MISSING' }
        return $syntheticAcl[$LiteralPath]
    }
}
function Set-TestAcl {
    [CmdletBinding(SupportsShouldProcess)]
    param([string]$Path,[bool]$Directory)
    if (-not $PSCmdlet.ShouldProcess($Path,'Configure isolated ACL test fixture')) { return }
    $acl=if($UseSyntheticAclFixture) {
        if($Directory){[Security.AccessControl.DirectorySecurity]::new()}else{[Security.AccessControl.FileSecurity]::new()}
    } else { Get-Acl -LiteralPath $Path }
    $acl.SetOwner($identity);$acl.SetAccessRuleProtection($true,$false)
    foreach($existing in @($acl.Access)) { if($existing) { $null=$acl.RemoveAccessRuleSpecific($existing) } }
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity,'FullControl','Allow'))
    if ($UseSyntheticAclFixture) { $syntheticAcl[$Path]=$acl }
    elseif ($Directory) { [IO.FileSystemAclExtensions]::SetAccessControl([IO.DirectoryInfo]::new($Path),$acl) }
    else { [IO.FileSystemAclExtensions]::SetAccessControl([IO.FileInfo]::new($Path),$acl) }
}
$authority=[ordered]@{
    projectId='ContextHub';portainerUrl='https://127.0.0.1:1';endpointId=2;tokenFile='management-token.clixml'
    certificateSha256='';oldPasswordInvalidatedAtUtc=[DateTimeOffset]::UtcNow.AddMinutes(-1).ToString('o')
    invalidationEvidenceRef='offline-fixture';tokenSessionReviewEvidenceRef='offline-fixture';operatorApprovalEvidenceRef='offline-fixture'
    machineIdentity='offline-fixture';permissionModel='environment-management-fixture'
}
function Save-TestAuthority { $authority | ConvertTo-Json | Set-Content -LiteralPath $authorityPath -Encoding utf8; Set-TestAcl $authorityPath $false }
try {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    & git -C $testRoot init --quiet
    Assert-Check ($LASTEXITCODE -eq 0) 'fixture git initialization'
    '.agent/' | Set-Content -LiteralPath (Join-Path $testRoot '.gitignore')
    Set-TestAcl $directory $true
    Assert-Rejected { Assert-PortainerManagementAuthority -RepoRoot $testRoot } 'PORTAINER_AUTHORITY_MISSING' 'missing authority fails closed'
    # Deliberately synthetic, never a management credential; never output or fingerprint it.
    $fixtureCredential=[Security.SecureString]::new()
    foreach($character in 'offline-test-fixture-not-a-credential'.ToCharArray()) { $fixtureCredential.AppendChar($character) }
    $fixtureCredential | Export-Clixml -LiteralPath $tokenPath
    $fixtureCredential.Dispose()
    Set-TestAcl $tokenPath $false; Save-TestAuthority
    $coordinates=Assert-PortainerManagementAuthority -RepoRoot $testRoot
    Assert-Check ($coordinates.PortainerUrl -eq 'https://127.0.0.1:1' -and $coordinates.EndpointId -eq 2) 'valid private authority'
    Assert-Check (@($coordinates.PSObject.Properties.Name | Where-Object {$_ -match 'token|password|credential|header'}).Count -eq 0) 'authority returns no secret coordinates'
    Assert-Rejected { Assert-PortainerManagementAuthority -RepoRoot $testRoot -CredentialAuthorityPath '../outside.json' } 'PORTAINER_AUTHORITY_PATH_REJECTED' 'out-of-repo authority'
    Assert-Rejected { Assert-PortainerManagementAuthority -RepoRoot $testRoot -PortainerUrl 'https://example.invalid' } 'PORTAINER_ORIGIN_REJECTED' 'origin mismatch'
    Assert-Rejected { Assert-PortainerManagementAuthority -RepoRoot $testRoot -EndpointId 3 } 'PORTAINER_ORIGIN_REJECTED' 'endpoint mismatch'
    foreach($field in @('invalidationEvidenceRef','tokenSessionReviewEvidenceRef','operatorApprovalEvidenceRef','machineIdentity','permissionModel')) {
        $saved=$authority[$field];$authority[$field]='';Save-TestAuthority
        Assert-Rejected { Assert-PortainerManagementAuthority -RepoRoot $testRoot } 'PORTAINER_OPERATOR_PREREQUISITE_INCOMPLETE' "missing $field"
        $authority[$field]=$saved
    }
    $saved=$authority.oldPasswordInvalidatedAtUtc;$authority.oldPasswordInvalidatedAtUtc=[DateTimeOffset]::UtcNow.AddDays(1).ToString('o');Save-TestAuthority
    Assert-Rejected { Assert-PortainerManagementAuthority -RepoRoot $testRoot } 'PORTAINER_OPERATOR_PREREQUISITE_INCOMPLETE' 'future invalidation evidence'
    $authority.oldPasswordInvalidatedAtUtc=$saved
    foreach($origin in @('http://127.0.0.1:1','https://user@127.0.0.1:1','https://127.0.0.1:1/path','https://127.0.0.1:1?x=1')) {
        $authority.portainerUrl=$origin;Save-TestAuthority
        Assert-Rejected { Assert-PortainerManagementAuthority -RepoRoot $testRoot } 'PORTAINER_ORIGIN_REJECTED' 'invalid authority origin'
    }
    $authority.portainerUrl='https://127.0.0.1:1';$authority['unexpectedField']='forbidden';Save-TestAuthority
    Assert-Rejected { Assert-PortainerManagementAuthority -RepoRoot $testRoot } 'PORTAINER_AUTHORITY_FIELDS_REJECTED' 'unexpected authority field'
    $authority.Remove('unexpectedField');Save-TestAuthority
    $boundary=@{RepoRoot=$testRoot;PortainerUrl='https://127.0.0.1:1'}
    foreach($uri in @('https://example.invalid/api/endpoints/2/docker/images/json','https://127.0.0.1:1/api/auth',
        'https://127.0.0.1:1/api/endpoints/3/docker/images/json','https://127.0.0.1:1/api/endpoints/2/docker/../users',
        'https://127.0.0.1:1/api/endpoints/2/docker/images/json?token=forbidden','https://127.0.0.1:1/api/endpoints/2/docker/%2fusers',
        'https://127.0.0.1:1/api/endpoints/2/docker/images/json#fragment')) {
        Assert-Rejected { Invoke-PortainerManagementRequest @boundary -Uri $uri } 'PORTAINER_REQUEST_BOUNDARY_REJECTED' 'request boundary rejects before token import/network'
    }
    foreach($arguments in @(@('-H','Authorization: forbidden','https://127.0.0.1:1/api/endpoints/2/docker/images/json'),
        @('--insecure','https://127.0.0.1:1/api/endpoints/2/docker/images/json'),@('--location','https://127.0.0.1:1/api/endpoints/2/docker/images/json'))) {
        $expected=if($arguments[0] -eq '-H'){'PORTAINER_HEADER_REJECTED'}else{'PORTAINER_LEGACY_ARGUMENT_REJECTED'}
        Assert-Rejected { Invoke-PortainerLegacyRequest @boundary -CredentialAuthorityPath '.agent/local/security/portainer/management-authority.json' -Arguments $arguments } $expected 'legacy credential/TLS/redirect fallback rejected'
    }
    $acl=Get-Acl -LiteralPath $tokenPath;$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-1-0'),'Read','Allow'))
    if ($UseSyntheticAclFixture) { $syntheticAcl[$tokenPath]=$acl }
    else { [IO.FileSystemAclExtensions]::SetAccessControl([IO.FileInfo]::new($tokenPath),$acl) }
    Assert-Rejected { Assert-PortainerManagementAuthority -RepoRoot $testRoot } 'PORTAINER_AUTHORITY_ACL_REJECTED' 'broad token ACL rejected'
    Set-TestAcl $tokenPath $false
    Initialize-PortainerPrivateTransport
    Assert-Check ($null -ne ('ContextHub.Deployment.PortainerPrivateTransport' -as [type])) 'compiled private HTTP transport'
    if ($TlsFixtureBackend -eq 'Windows' -and -not ('ContextHub.Deployment.Tests.PortainerLoopbackFixture' -as [type])) {
        Add-Type -Path (Join-Path $PSScriptRoot 'PortainerLoopbackFixture.cs')
    }
    $modes=if($SkipLoopbackTlsFixture){@()}else{@('normal','reflect','escaped-reflect','redirect','failure','wrong-pin','normal-trust-reject')}
    foreach($mode in $modes) {
        $fixtureProcess=$null
        $server=$null
        try {
          if ($TlsFixtureBackend -eq 'Wsl') {
            # Only public fixture source/mode cross process boundaries, never the synthetic token.
            $start=[Diagnostics.ProcessStartInfo]::new('wsl.exe')
            foreach($argument in @('--distribution','Ubuntu','--exec','python3','-u','-',$mode)) { $start.ArgumentList.Add($argument) }
            $start.UseShellExecute=$false;$start.CreateNoWindow=$true
            $start.RedirectStandardInput=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
            $fixtureProcess=[Diagnostics.Process]::Start($start)
            $fixtureProcess.StandardInput.Write([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'portainer_loopback_fixture.py')))
            $fixtureProcess.StandardInput.Close()
            $ready=$fixtureProcess.StandardOutput.ReadLineAsync()
            if (-not $ready.Wait(15000)) { throw 'LOOPBACK_FIXTURE_START_FAILED' }
            try { $server=$ready.Result | ConvertFrom-Json -ErrorAction Stop }
            catch { throw 'LOOPBACK_FIXTURE_START_FAILED' }
            if (@($server.PSObject.Properties.Name).Count -ne 2 -or
                $server.Port -isnot [long] -and $server.Port -isnot [int] -or
                $server.Port -lt 1 -or $server.Port -gt 65535 -or
                $server.CertificatePin -cnotmatch '^[0-9a-f]{64}$') { throw 'LOOPBACK_FIXTURE_START_FAILED' }
          } else { $server=[ContextHub.Deployment.Tests.PortainerLoopbackFixture]::new($mode) }
            $authority.portainerUrl='https://127.0.0.1:'+$server.Port
            $authority.certificateSha256=if($mode -eq 'wrong-pin'){'0'*64}elseif($mode -eq 'normal-trust-reject'){''}else{$server.CertificatePin}
            Save-TestAuthority
            $localBoundary=@{RepoRoot=$testRoot;PortainerUrl=$authority.portainerUrl;TimeoutSec=5}
            $localUri=$authority.portainerUrl+'/api/endpoints/2/docker/images/json'
            if($mode -eq 'normal') {
                try { $result=Invoke-PortainerManagementRequest @localBoundary -Uri $localUri }
                catch { throw 'LOOPBACK_POSITIVE_CONTROL_FAILED' }
                Assert-Check ($result.Id -eq 'fixture-id') 'successful synthetic API token response'
                if ($TlsFixtureBackend -eq 'Windows') {
                    Assert-Check ($server.ApiKeyPresent -and $server.AuthorizationAbsent) 'X-API-Key only; no password/JWT login'
                }
            } else {
                Assert-Rejected { Invoke-PortainerManagementRequest @localBoundary -Uri $localUri } 'PORTAINER_MANAGEMENT_REQUEST_REJECTED' ('loopback rejects '+$mode)
            }
            if ($fixtureProcess) {
                $observed=$fixtureProcess.StandardOutput.ReadLineAsync()
                if (-not $observed.Wait(10000)) { throw 'LOOPBACK_FIXTURE_RESULT_FAILED' }
                $observation=$observed.Result | ConvertFrom-Json -ErrorAction Stop
                if (@($observation.PSObject.Properties.Name).Count -ne 3 -or
                    $observation.ApiKeyPresent -isnot [bool] -or $observation.AuthorizationAbsent -isnot [bool] -or
                    $observation.RequestReceived -isnot [bool] -or -not $fixtureProcess.WaitForExit(2000) -or
                    $fixtureProcess.ExitCode -ne 0) { throw 'LOOPBACK_FIXTURE_RESULT_FAILED' }
                if ($mode -in @('wrong-pin','normal-trust-reject')) {
                    Assert-Check (-not $observation.ApiKeyPresent -and -not $observation.RequestReceived) 'TLS rejection precedes authentication header'
                } else {
                    Assert-Check ($observation.ApiKeyPresent -and $observation.AuthorizationAbsent -and $observation.RequestReceived) 'X-API-Key only; no password/JWT login'
                }
            }
        } finally {
            if ($fixtureProcess) {
                if (-not $fixtureProcess.HasExited -and -not $fixtureProcess.WaitForExit(2000)) {
                    $fixtureProcess.Kill($true)
                    $null=$fixtureProcess.WaitForExit(2000)
                }
                $fixtureProcess.Dispose()
            } elseif ($server) { $server.Dispose() }
        }
    }
    $authority.portainerUrl='https://127.0.0.1:1';$authority.certificateSha256='';Save-TestAuthority
    & {
        # Test-only fake Docker inventory; no auth import, HTTP call or production override.
        $script:fakeImageInventory=@([pscustomobject]@{Id=('sha256:'+'a'*64);RepoTags=@('registry.example.com/team/helper:latest');RepoDigests=@('registry.example.com/team/helper@sha256:fixture')})
        function Invoke-PortainerManagementRequest {
            [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword','',Justification='Test mock receives a non-secret authority path only.')]
            [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter','',Justification='Test mock intentionally preserves the request interface without sending requests.')]
            [CmdletBinding()]
            param($RepoRoot,$CredentialAuthorityPath,$PortainerUrl,$EndpointId,$Uri)
            return $script:fakeImageInventory
        }
        foreach($reference in @('registry.example.com/team/helper:latest','registry.example.com/team/helper','registry.example.com/team/helper@sha256:fixture',('sha256:'+'a'*64))) {
            $imageId=Resolve-PortainerImageIdentity @boundary -CredentialAuthorityPath '.agent/local/security/portainer/management-authority.json' -ImageReference $reference
            Assert-Check ($imageId -ceq ('sha256:'+'a'*64) -and [Uri]::EscapeDataString($imageId) -notmatch '%2f') 'namespaced tag/digest/ID resolves to separator-free deletion coordinate'
        }
        Assert-Rejected { Resolve-PortainerImageIdentity @boundary -CredentialAuthorityPath '.agent/local/security/portainer/management-authority.json' -ImageReference 'unproven' } 'PORTAINER_IMAGE_IDENTITY_UNPROVEN' 'missing image identity fails closed'
        $script:fakeImageInventory+= [pscustomobject]@{Id=('sha256:'+'b'*64);RepoTags=@('registry.example.com/team/helper:latest');RepoDigests=@()}
        Assert-Rejected { Resolve-PortainerImageIdentity @boundary -CredentialAuthorityPath '.agent/local/security/portainer/management-authority.json' -ImageReference 'registry.example.com/team/helper:latest' } 'PORTAINER_IMAGE_IDENTITY_UNPROVEN' 'ambiguous image identity fails closed'
    }
    'not-a-secure-string' | Export-Clixml -LiteralPath $tokenPath;Set-TestAcl $tokenPath $false
    Assert-Rejected { Invoke-PortainerManagementRequest @boundary -Uri 'https://127.0.0.1:1/api/endpoints/2/docker/images/json' } 'PORTAINER_MANAGEMENT_REQUEST_REJECTED' 'plaintext/nonsecure credential rejected before network'
    Write-Output ("PORTAINER_OFFLINE_POLICY_PASS checks={0}; syntheticAclPolicyOnly={1}; tlsControlsSkipped={2}; tlsBackend={3}; productionRequests=0; realCredentialsRead=0" -f $passed,$UseSyntheticAclFixture.IsPresent,$SkipLoopbackTlsFixture.IsPresent,$TlsFixtureBackend)
}
finally {
    $validated=[IO.Path]::GetFullPath($testRoot)
    $allowed=[IO.Path]::GetFullPath((Join-Path $repoRoot '.agent/local/portainer-tests'))+[IO.Path]::DirectorySeparatorChar
    if ($validated.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase) -and (Split-Path $validated -Leaf) -match '^[0-9a-f]{32}$') {
        Remove-Item -LiteralPath $validated -Recurse -Force
    }
}
