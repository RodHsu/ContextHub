#Requires -Version 7.4
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
if (-not [OperatingSystem]::IsWindows()) { throw 'WINDOWS_ACL_FIXTURE_REQUIRED' }
$repoRoot=(Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
$identity=[Security.Principal.WindowsIdentity]::GetCurrent().User
$source=Join-Path $PSScriptRoot '../New-PortainerManagementAuthority.ps1'
$parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($source,[ref]$null,[ref]$parseErrors)
if ($parseErrors.Count) { throw 'ENROLLMENT_PARSE_FAILED' }
$functions=@($ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Set-OperatorPrivateAcl'
},$true))
if ($functions.Count -ne 1) { throw 'ACL_FUNCTION_NOT_FOUND' }
# Load only the production ACL function. Never execute enrollment or credential input.
. ([scriptblock]::Create($functions[0].Extent.Text))
$root=Join-Path $repoRoot ('.agent/local/portainer-tests/enrollment-acl-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$passed=0
function Assert-Check([bool]$Condition,[string]$Name) {
    if (-not $Condition) { throw ('ACL_REGRESSION_FAILED: '+$Name) }
    $script:passed++
}
function Assert-PrivateAcl([string]$Path,[bool]$Directory) {
    $acl=Get-Acl -LiteralPath $Path
    $rules=@($acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]))
    Assert-Check ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -eq $identity.Value) 'owner unchanged'
    Assert-Check $acl.AreAccessRulesProtected 'inheritance protected'
    Assert-Check ($rules.Count -eq 2 -and @($rules.IdentityReference.Value|Sort-Object -Unique).Count -eq 2) 'exact two principals'
    Assert-Check (@($rules|Where-Object {$_.IdentityReference.Value -notin @($identity.Value,'S-1-5-18') -or
        $_.IsInherited -or $_.AccessControlType -ne 'Allow' -or $_.FileSystemRights -ne 'FullControl'}).Count -eq 0) 'current identity and SYSTEM only'
    $flags=if($Directory){[Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'}else{[Security.AccessControl.InheritanceFlags]::None}
    Assert-Check (@($rules|Where-Object {$_.InheritanceFlags -ne $flags -or $_.PropagationFlags -ne 'None'}).Count -eq 0) 'exact propagation'
}
$directory=Join-Path $root 'private-directory'
New-Item -ItemType Directory -Path $directory | Out-Null
Set-OperatorPrivateAcl $directory $true
Assert-PrivateAcl $directory $true
$original=(Get-Acl -LiteralPath $directory).Sddl
Set-OperatorPrivateAcl $directory $true
Assert-Check ((Get-Acl -LiteralPath $directory).Sddl -ceq $original) 'directory idempotency'
$file=Join-Path $directory 'no-secret-fixture.txt'
'not-a-credential' | Set-Content -LiteralPath $file
Set-OperatorPrivateAcl $file $false
Assert-PrivateAcl $file $false
$original=(Get-Acl -LiteralPath $file).Sddl
Set-OperatorPrivateAcl $file $false
Assert-Check ((Get-Acl -LiteralPath $file).Sddl -ceq $original) 'file idempotency'
$untouched=Join-Path $root 'whatif-directory'
New-Item -ItemType Directory -Path $untouched | Out-Null
$before=(Get-Acl -LiteralPath $untouched).Sddl
Set-OperatorPrivateAcl $untouched $true -WhatIf
Assert-Check ((Get-Acl -LiteralPath $untouched).Sddl -ceq $before) 'whatif does not mutate'
# Owner mismatch must reject before mutation; run the real function against a separate
# in-memory descriptor. This negative guard test is not real substrate acceptance.
$foreign=[Security.AccessControl.DirectorySecurity]::new()
$foreign.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-18'))
$rejected=& {
    function Get-Acl {
        [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidOverwritingBuiltInCmdlets','',Justification='Isolated owner-mismatch negative fixture; real positive ACL tests use native Get-Acl.')]
        [CmdletBinding()]
        param([string]$LiteralPath)
        if ($LiteralPath -cne $untouched) { throw 'UNEXPECTED_FIXTURE_PATH' }
        return $foreign
    }
    try { Set-OperatorPrivateAcl $untouched $true; return $false }
    catch { return $_.Exception.Message -ceq 'PORTAINER_AUTHORITY_OWNER_MISMATCH' }
}
Assert-Check $rejected 'foreign owner fails closed'
Assert-Check ((Get-Acl -LiteralPath $untouched).Sddl -ceq $before) 'foreign owner rejected without filesystem mutation'
# Read-back validation must not accept a storage layer that fails to persist a private DACL.
$unprotectedSource=Join-Path $root 'unprotected-readback-source'
New-Item -ItemType Directory -Path $unprotectedSource | Out-Null
$rejected=& {
    # The first descriptor is modified in-place by the production function; each read
    # returns a fresh descriptor so the read-back cannot inherit its in-memory changes.
    function Get-Acl {
        [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidOverwritingBuiltInCmdlets','',Justification='Isolated failed-persistence negative fixture, not substrate acceptance.')]
        [CmdletBinding()]
        param([string]$LiteralPath)
        if ($LiteralPath -cne $untouched) { throw 'UNEXPECTED_FIXTURE_PATH' }
        return Microsoft.PowerShell.Security\Get-Acl -LiteralPath $unprotectedSource
    }
    try { Set-OperatorPrivateAcl $untouched $true; return $false }
    catch { return $_.Exception.Message -ceq 'PORTAINER_AUTHORITY_ACL_REJECTED' }
}
Assert-Check $rejected 'unproven ACL persistence rejected'
Assert-PrivateAcl $untouched $true
# Run the exact enrollment source in an isolated, ignored Git fixture. The secure
# prompt is intercepted with a sentinel; no token is supplied or read, even synthetically.
$fixtureRepo=Join-Path $root 'enrollment-repo'
$fixtureTools=Join-Path $fixtureRepo 'tools/deployment'
New-Item -ItemType Directory -Path $fixtureTools -Force | Out-Null
Copy-Item -LiteralPath $source -Destination $fixtureTools
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../Portainer.Management.ps1') -Destination $fixtureTools
& git -C $fixtureRepo init --quiet
Assert-Check ($LASTEXITCODE -eq 0) 'enrollment fixture git initialization'
'.agent/' | Set-Content -LiteralPath (Join-Path $fixtureRepo '.gitignore')
$enrollmentArguments=@{
    PortainerUrl='https://127.0.0.1:1'; EndpointId=2
    OldPasswordInvalidatedAtUtc=[DateTimeOffset]::UtcNow.AddMinutes(-1)
    InvalidationEvidenceRef='no-secret-fixture';TokenSessionReviewEvidenceRef='no-secret-fixture'
    OperatorApprovalEvidenceRef='no-secret-fixture';MachineIdentity='no-secret-fixture';PermissionModel='no-secret-fixture'
}
$fixtureScript=Join-Path $fixtureTools 'New-PortainerManagementAuthority.ps1'
$boundary=& {
    function Read-Host {
        [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidOverwritingBuiltInCmdlets','',Justification='Isolated no-secret prompt interception, never a real enrollment override.')]
        [CmdletBinding()]
        param([string]$Prompt,[switch]$AsSecureString)
        if (-not $AsSecureString -or $Prompt -notmatch 'secure input') { throw 'UNSAFE_PROMPT_REGRESSION' }
        throw 'TEST_SECURE_PAT_PROMPT_BOUNDARY'
    }
    try { & $fixtureScript @enrollmentArguments; return 'UNEXPECTED_ENROLLMENT_COMPLETION' }
    catch { return $_.Exception.Message }
}
Assert-Check ($boundary -ceq 'TEST_SECURE_PAT_PROMPT_BOUNDARY') 'exact enrollment reaches secure prompt without credential'
$fixtureAuthority=Join-Path $fixtureRepo '.agent/local/security/portainer'
Assert-PrivateAcl $fixtureAuthority $true
Assert-Check (-not (Test-Path -LiteralPath (Join-Path $fixtureAuthority 'management-authority.json')) -and
    -not (Test-Path -LiteralPath (Join-Path $fixtureAuthority 'management-token.clixml'))) 'no authority files written before prompt'
foreach ($existingFile in @('management-authority.json','management-token.clixml')) {
    $fixtureFile=Join-Path $fixtureAuthority $existingFile
    'not-a-credential' | Set-Content -LiteralPath $fixtureFile
    $boundary=& {
        function Read-Host {
            [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidOverwritingBuiltInCmdlets','',Justification='Isolated no-overwrite regression; prompt must be unreachable.')]
            [CmdletBinding()]
            param([string]$Prompt,[switch]$AsSecureString)
            if ($Prompt -or $AsSecureString) { throw 'PROMPT_MUST_NOT_BE_REACHED' }
        }
        try { & $fixtureScript @enrollmentArguments; return 'UNEXPECTED_ENROLLMENT_COMPLETION' }
        catch { return $_.Exception.Message }
    }
    Assert-Check ($boundary -ceq 'PORTAINER_AUTHORITY_ALREADY_EXISTS_REFUSING_OVERWRITE') 'existing fixture authority is never overwritten'
    Remove-Item -LiteralPath $fixtureFile
}
'PORTAINER_ENROLLMENT_ACL_PASS '+$passed+'/'+$passed+'; realAclSubstrate='+[IO.Path]::GetPathRoot($repoRoot)+'; realCredentialAccess=0; remoteRequests=0'
exit 0
