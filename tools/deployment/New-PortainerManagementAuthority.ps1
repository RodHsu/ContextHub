#Requires -Version 7.4
# Operator-only interactive enrollment. The repo agent must not invoke this script.
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string]$PortainerUrl,
    [Parameter(Mandatory)][ValidateRange(1,2147483647)][int]$EndpointId,
    [Parameter(Mandatory)][DateTimeOffset]$OldPasswordInvalidatedAtUtc,
    [Parameter(Mandatory)][string]$InvalidationEvidenceRef,
    [Parameter(Mandatory)][string]$TokenSessionReviewEvidenceRef,
    [Parameter(Mandatory)][string]$OperatorApprovalEvidenceRef,
    [Parameter(Mandatory)][string]$MachineIdentity,
    [Parameter(Mandatory)][string]$PermissionModel,
    [ValidatePattern('^$|^[0-9a-fA-F]{64}$')][string]$CertificateSha256 = ''
)
$ErrorActionPreference='Stop'
if (-not [OperatingSystem]::IsWindows()) { throw 'PORTAINER_DPAPI_PLATFORM_REQUIRED' }
$repoRoot=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $PSScriptRoot 'Portainer.Management.ps1')
$origin=$null
if (-not [Uri]::TryCreate($PortainerUrl,[UriKind]::Absolute,[ref]$origin) -or
    $origin.Scheme -cne 'https' -or $origin.UserInfo -or $origin.Query -or $origin.Fragment -or $origin.AbsolutePath -ne '/') {
    throw 'PORTAINER_ORIGIN_REJECTED'
}
if ($OldPasswordInvalidatedAtUtc -gt [DateTimeOffset]::UtcNow -or
    $OldPasswordInvalidatedAtUtc -lt [DateTimeOffset]::Parse('2026-09-28T00:00:00Z')) {
    throw 'PORTAINER_OPERATOR_PREREQUISITE_INCOMPLETE'
}
foreach ($reference in @($InvalidationEvidenceRef,$TokenSessionReviewEvidenceRef,$OperatorApprovalEvidenceRef,$MachineIdentity,$PermissionModel)) {
    if ([string]::IsNullOrWhiteSpace($reference)) { throw 'PORTAINER_OPERATOR_PREREQUISITE_INCOMPLETE' }
}
$directory=Join-Path $repoRoot '.agent/local/security/portainer'
$authorityPath=Join-Path $directory 'management-authority.json'
$tokenPath=Join-Path $directory 'management-token.clixml'
if (-not $PSCmdlet.ShouldProcess($directory,'Enroll operator-controlled encrypted Portainer authority')) { return }
if ((Test-Path -LiteralPath $authorityPath) -or (Test-Path -LiteralPath $tokenPath)) {
    throw 'PORTAINER_AUTHORITY_ALREADY_EXISTS_REFUSING_OVERWRITE'
}
foreach ($authorityFilePath in @($authorityPath,$tokenPath)) {
    $relativePath=[IO.Path]::GetRelativePath($repoRoot,$authorityFilePath)
    & git -C $repoRoot check-ignore --quiet -- $relativePath
    if ($LASTEXITCODE -ne 0) { throw 'PORTAINER_AUTHORITY_NOT_IGNORED' }
}
$cursor=Split-Path $directory -Parent
while ($cursor.Length -ge $repoRoot.Length) {
    if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'PORTAINER_AUTHORITY_REPARSE_REJECTED'
    }
    if ($cursor -eq $repoRoot) { break }; $cursor=Split-Path $cursor -Parent
}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent().User
function Set-OperatorPrivateAcl {
    [CmdletBinding(SupportsShouldProcess)]
    param([string]$Path,[bool]$IsDirectory)
    if (-not $PSCmdlet.ShouldProcess($Path,'Set protected operator/SYSTEM ACL')) { return }
    $acl=Get-Acl -LiteralPath $Path
    $acl.SetOwner($identity); $acl.SetAccessRuleProtection($true,$false)
    foreach($existing in @($acl.Access)) { if($existing) { $null=$acl.RemoveAccessRuleSpecific($existing) } }
    foreach ($sid in @($identity,[Security.Principal.SecurityIdentifier]::new('S-1-5-18'))) {
        $rule=if ($IsDirectory) {
            [Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow')
        } else { [Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','Allow') }
        $acl.AddAccessRule($rule)
    }
    if ($IsDirectory) { [IO.FileSystemAclExtensions]::SetAccessControl([IO.DirectoryInfo]::new($Path),$acl) }
    else { [IO.FileSystemAclExtensions]::SetAccessControl([IO.FileInfo]::new($Path),$acl) }
}
New-Item -ItemType Directory -Path $directory -Force | Out-Null
if ((Get-Item -LiteralPath $directory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'PORTAINER_AUTHORITY_REPARSE_REJECTED' }
Set-OperatorPrivateAcl $directory $true
$credential=$null
try {
    $credential=Read-Host 'New Portainer API Access Token (secure input; never paste in chat)' -AsSecureString
    if ($credential.Length -eq 0) { throw 'PORTAINER_TOKEN_MISSING' }
    $credential | Export-Clixml -LiteralPath $tokenPath -ErrorAction Stop
    Set-OperatorPrivateAcl $tokenPath $false
    $authority=[ordered]@{
        projectId='ContextHub'; portainerUrl=$origin.GetLeftPart([UriPartial]::Authority); endpointId=$EndpointId
        tokenFile='management-token.clixml'; certificateSha256=$CertificateSha256
        oldPasswordInvalidatedAtUtc=$OldPasswordInvalidatedAtUtc.ToUniversalTime().ToString('o')
        invalidationEvidenceRef=$InvalidationEvidenceRef; tokenSessionReviewEvidenceRef=$TokenSessionReviewEvidenceRef
        operatorApprovalEvidenceRef=$OperatorApprovalEvidenceRef; machineIdentity=$MachineIdentity; permissionModel=$PermissionModel
    }
    $authority | ConvertTo-Json | Set-Content -LiteralPath $authorityPath -Encoding utf8 -ErrorAction Stop
    Set-OperatorPrivateAcl $authorityPath $false
    $null=Assert-PortainerManagementAuthority -RepoRoot $repoRoot -PortainerUrl $PortainerUrl -EndpointId $EndpointId
    Write-Output 'PORTAINER_AUTHORITY_ENROLLED_NO_REMOTE_REQUEST'
}
finally { if ($credential -is [Security.SecureString]) { $credential.Dispose() }; $credential=$null }
