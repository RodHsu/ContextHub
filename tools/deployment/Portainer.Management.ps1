#Requires -Version 7.4
# Portainer management credentials are imported only inside the request boundary.
# No environment, command-line token, password login, redirect or curl fallback.
function Assert-PortainerPrivatePath {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$RepoRoot, [Parameter(Mandatory)][string]$Path)

    $root = [IO.Path]::GetFullPath($RepoRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $directory = [IO.Path]::GetFullPath((Join-Path $root '.agent/local/security/portainer'))
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($directory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'PORTAINER_AUTHORITY_PATH_REJECTED'
    }
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw 'PORTAINER_AUTHORITY_MISSING' }
    $cursor = $full
    while ($cursor.Length -ge $root.Length) {
        if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'PORTAINER_AUTHORITY_REPARSE_REJECTED'
        }
        if ($cursor -eq $root) { break }
        $cursor = Split-Path -Parent $cursor
    }
    & git -C $root check-ignore --quiet -- $full
    if ($LASTEXITCODE -ne 0) { throw 'PORTAINER_AUTHORITY_NOT_IGNORED' }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    foreach ($protectedPath in @($directory, $full)) {
        $acl = Get-Acl -LiteralPath $protectedPath
        if (-not $acl.AreAccessRulesProtected -or
            $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne $identity) {
            throw 'PORTAINER_AUTHORITY_ACL_REJECTED'
        }
        $currentUserCanRead = $false
        foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
            if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow) { continue }
            if ($rule.IdentityReference.Value -notin @($identity, 'S-1-5-18')) { throw 'PORTAINER_AUTHORITY_ACL_REJECTED' }
            if ($rule.IdentityReference.Value -eq $identity -and
                ($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::ReadData)) {
                $currentUserCanRead = $true
            }
        }
        if (-not $currentUserCanRead) { throw 'PORTAINER_AUTHORITY_ACL_REJECTED' }
    }
    return $full
}

function Assert-PortainerManagementAuthority {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword','',Justification='CredentialAuthorityPath contains only a non-secret file reference.')]
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [string]$CredentialAuthorityPath = '.agent/local/security/portainer/management-authority.json',
        [string]$PortainerUrl,
        [int]$EndpointId = 2
    )
    Set-StrictMode -Version Latest
    if (-not [OperatingSystem]::IsWindows()) { throw 'PORTAINER_DPAPI_PLATFORM_REQUIRED' }
    $path = if ([IO.Path]::IsPathRooted($CredentialAuthorityPath)) {
        $CredentialAuthorityPath
    } else { Join-Path $RepoRoot $CredentialAuthorityPath }
    $path = Assert-PortainerPrivatePath -RepoRoot $RepoRoot -Path $path
    try { $authority = [IO.File]::ReadAllText($path) | ConvertFrom-Json -ErrorAction Stop }
    catch { throw 'PORTAINER_AUTHORITY_INVALID' }
    $allowed = @('projectId', 'portainerUrl', 'endpointId', 'tokenFile', 'certificateSha256',
        'oldPasswordInvalidatedAtUtc', 'invalidationEvidenceRef', 'tokenSessionReviewEvidenceRef',
        'operatorApprovalEvidenceRef', 'machineIdentity', 'permissionModel')
    if (@($authority.PSObject.Properties.Name | Where-Object { $_ -notin $allowed }).Count -ne 0) {
        throw 'PORTAINER_AUTHORITY_FIELDS_REJECTED'
    }
    foreach ($required in @('projectId','portainerUrl','endpointId','tokenFile','oldPasswordInvalidatedAtUtc',
        'invalidationEvidenceRef','tokenSessionReviewEvidenceRef','operatorApprovalEvidenceRef','machineIdentity','permissionModel')) {
        if (-not $authority.PSObject.Properties[$required] -or [string]::IsNullOrWhiteSpace([string]$authority.$required)) {
            throw 'PORTAINER_OPERATOR_PREREQUISITE_INCOMPLETE'
        }
    }
    $invalidated = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse([string]$authority.oldPasswordInvalidatedAtUtc, [ref]$invalidated) -or
        $invalidated -gt [DateTimeOffset]::UtcNow -or $invalidated -lt [DateTimeOffset]::Parse('2026-09-28T00:00:00Z')) {
        throw 'PORTAINER_OPERATOR_PREREQUISITE_INCOMPLETE'
    }
    $origin = $null
    if ($authority.projectId -cne 'ContextHub' -or
        -not [Uri]::TryCreate([string]$authority.portainerUrl, [UriKind]::Absolute, [ref]$origin) -or
        $origin.Scheme -cne 'https' -or $origin.UserInfo -or $origin.Query -or $origin.Fragment -or
        $origin.AbsolutePath -ne '/' -or [int]$authority.endpointId -ne $EndpointId -or $EndpointId -lt 1) {
        throw 'PORTAINER_ORIGIN_REJECTED'
    }
    if ($PortainerUrl -and $PortainerUrl.TrimEnd('/') -cne $origin.GetLeftPart([UriPartial]::Authority)) {
        throw 'PORTAINER_ORIGIN_REJECTED'
    }
    $pin = if ($authority.PSObject.Properties['certificateSha256']) { [string]$authority.certificateSha256 } else { '' }
    if ($pin -and $pin -cnotmatch '^[0-9a-fA-F]{64}$') { throw 'PORTAINER_CERTIFICATE_AUTHORITY_INVALID' }
    if ([string]$authority.tokenFile -cne 'management-token.clixml') { throw 'PORTAINER_TOKEN_REFERENCE_REJECTED' }
    $null = Assert-PortainerPrivatePath -RepoRoot $RepoRoot -Path (Join-Path (Split-Path $path -Parent) $authority.tokenFile)
    # Return only non-secret coordinates; never the imported credential or HTTP headers.
    return [pscustomobject]@{ AuthorityPath = $path; PortainerUrl = $origin.GetLeftPart([UriPartial]::Authority); EndpointId = $EndpointId; CertificateSha256 = $pin }
}

function Initialize-PortainerPrivateTransport {
    if ('ContextHub.Deployment.PortainerPrivateTransport' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Net.Http;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
namespace ContextHub.Deployment {
    public sealed class PortainerSafeResponse {
        public int StatusCode { get; set; }
        public byte[] Content { get; set; }
    }
    public static class PortainerPrivateTransport {
        private static bool JsonContainsCredential(JsonElement element, string token) {
            if (element.ValueKind == JsonValueKind.String)
                return element.GetString().Contains(token, StringComparison.Ordinal);
            if (element.ValueKind == JsonValueKind.Array) {
                foreach (var item in element.EnumerateArray())
                    if (JsonContainsCredential(item, token)) return true;
            }
            if (element.ValueKind == JsonValueKind.Object) {
                foreach (var property in element.EnumerateObject())
                    if (property.Name.Contains(token, StringComparison.Ordinal) || JsonContainsCredential(property.Value, token)) return true;
            }
            return false;
        }
        public static PortainerSafeResponse Send(SecureString credential, string certificatePin,
            Uri uri, string method, string body, string inFile, string contentType, int timeout) {
            IntPtr buffer = IntPtr.Zero;
            string token = null;
            try {
                using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false };
                if (!string.IsNullOrEmpty(certificatePin)) {
                    handler.ServerCertificateCustomValidationCallback = (request, certificate, chain, errors) =>
                        certificate != null && certificate.NotBefore.ToUniversalTime() <= DateTime.UtcNow &&
                        certificate.NotAfter.ToUniversalTime() >= DateTime.UtcNow &&
                        string.Equals(Convert.ToHexString(SHA256.HashData(certificate.RawData)), certificatePin, StringComparison.OrdinalIgnoreCase);
                }
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeout) };
                using var request = new HttpRequestMessage(new HttpMethod(method), uri);
                buffer = Marshal.SecureStringToBSTR(credential);
                token = Marshal.PtrToStringBSTR(buffer);
                if (string.IsNullOrWhiteSpace(token) || token.Contains("\r") || token.Contains("\n")) throw new Exception();
                request.Headers.Add("X-API-Key", token);
                if (!string.IsNullOrEmpty(inFile)) request.Content = new StreamContent(File.OpenRead(inFile));
                else if (body != null) request.Content = new StringContent(body, Encoding.UTF8);
                if (request.Content != null && !string.IsNullOrEmpty(contentType))
                    request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
                using var response = client.Send(request);
                int status = (int)response.StatusCode;
                if (status < 200 || status >= 300) return new PortainerSafeResponse { StatusCode = status, Content = Array.Empty<byte>() };
                var bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                bool reflectsCredential = Encoding.UTF8.GetString(bytes).Contains(token, StringComparison.Ordinal);
                try {
                    using var document = JsonDocument.Parse(bytes);
                    reflectsCredential |= JsonContainsCredential(document.RootElement, token);
                } catch (JsonException) { /* Non-JSON bytes still receive the raw credential check. */ }
                if (reflectsCredential) {
                    Array.Clear(bytes, 0, bytes.Length);
                    throw new Exception();
                }
                return new PortainerSafeResponse { StatusCode = status, Content = bytes };
            } catch { throw new InvalidOperationException("PORTAINER_REQUEST_FAILED"); }
            finally {
                token = null;
                if (buffer != IntPtr.Zero) Marshal.ZeroFreeBSTR(buffer);
            }
        }
    }
}
'@ -ErrorAction Stop
}

function Invoke-PortainerManagementRequest {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword','',Justification='CredentialAuthorityPath contains only a non-secret file reference.')]
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [string]$CredentialAuthorityPath = '.agent/local/security/portainer/management-authority.json',
        [Parameter(Mandatory)][string]$PortainerUrl,
        [int]$EndpointId = 2,
        [Parameter(Mandatory)][string]$Uri,
        [ValidateSet('GET','POST','DELETE','PUT','PATCH')][string]$Method = 'GET',
        [AllowNull()][string]$Body = $null,
        [string]$InFile,
        [string]$ContentType = 'application/json',
        [ValidateRange(1,7200)][int]$TimeoutSec = 900,
        [ValidateSet('Json','Text','Bytes')][string]$ResponseFormat = 'Json',
        [switch]$IgnoreHttpFailure
    )
    $coordinates = Assert-PortainerManagementAuthority -RepoRoot $RepoRoot -CredentialAuthorityPath $CredentialAuthorityPath -PortainerUrl $PortainerUrl -EndpointId $EndpointId
    $destination = $null
    if (-not [Uri]::TryCreate($Uri, [UriKind]::Absolute, [ref]$destination) -or
        $destination.GetLeftPart([UriPartial]::Authority) -cne $coordinates.PortainerUrl -or
        $destination.UserInfo -or $destination.Fragment -or
        -not $destination.AbsolutePath.StartsWith("/api/endpoints/$EndpointId/docker/", [StringComparison]::Ordinal) -or
        $destination.OriginalString -match '(?i)(%2f|%5c|\.\.|[?&](token|key|password|authorization)=)') {
        throw 'PORTAINER_REQUEST_BOUNDARY_REJECTED'
    }
    $credential = $null
    try {
        # DPAPI CLIXML is bound to this Windows user and machine, not a plaintext secret file.
        $credential = Import-Clixml -LiteralPath (Join-Path (Split-Path $coordinates.AuthorityPath -Parent) 'management-token.clixml') -ErrorAction Stop
        if ($credential -isnot [Security.SecureString] -or $credential.Length -eq 0) { throw 'PORTAINER_TOKEN_FORMAT_REJECTED' }
        Initialize-PortainerPrivateTransport
        $response = [ContextHub.Deployment.PortainerPrivateTransport]::Send($credential, $coordinates.CertificateSha256,
            $destination, $Method, $Body, $InFile, $ContentType, $TimeoutSec)
        if ($response.StatusCode -lt 200 -or $response.StatusCode -ge 300) {
            if ($IgnoreHttpFailure) { return $null }
            throw 'PORTAINER_HTTP_FAILED'
        }
        if ($ResponseFormat -eq 'Bytes') { return ,([byte[]]$response.Content) }
        $content = [Text.Encoding]::UTF8.GetString($response.Content)
        if ($ResponseFormat -eq 'Text') { return $content }
        if (-not [string]::IsNullOrWhiteSpace($content)) { return ($content | ConvertFrom-Json -ErrorAction Stop) }
    }
    catch { throw 'PORTAINER_MANAGEMENT_REQUEST_REJECTED' }
    finally { if ($credential -is [Security.SecureString]) { $credential.Dispose() }; $credential = $null }
}

function Resolve-PortainerImageIdentity {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword','',Justification='CredentialAuthorityPath contains only a non-secret file reference.')]
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$RepoRoot,[string]$CredentialAuthorityPath,
        [Parameter(Mandatory)][string]$PortainerUrl,[int]$EndpointId=2,[Parameter(Mandatory)][string]$ImageReference)
    $normalizedReference=if (-not $ImageReference.Contains('@') -and -not ($ImageReference.Split('/')[-1]).Contains(':')) {
        $ImageReference+':latest'
    } else { $ImageReference }
    $images=Invoke-PortainerManagementRequest -RepoRoot $RepoRoot -CredentialAuthorityPath $CredentialAuthorityPath -PortainerUrl $PortainerUrl -EndpointId $EndpointId -Uri "$PortainerUrl/api/endpoints/$EndpointId/docker/images/json"
    $identities=@($images | Where-Object {
        $_.Id -ceq $normalizedReference -or $normalizedReference -cin @($_.RepoTags) -or $normalizedReference -cin @($_.RepoDigests)
    } | ForEach-Object Id | Sort-Object -Unique)
    if ($identities.Count -ne 1 -or $identities[0] -cnotmatch '^sha256:[0-9a-f]{64}$') {
        throw 'PORTAINER_IMAGE_IDENTITY_UNPROVEN'
    }
    return $identities[0]
}

function Invoke-PortainerLegacyRequest {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword','',Justification='CredentialAuthorityPath contains only a non-secret file reference.')]
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$RepoRoot, [string]$CredentialAuthorityPath,
        [Parameter(Mandatory)][string]$PortainerUrl, [int]$EndpointId = 2,
        [Parameter(Mandatory)][string[]]$Arguments, [switch]$IgnoreExitCode)

    $parameters = @{ RepoRoot=$RepoRoot; CredentialAuthorityPath=$CredentialAuthorityPath; PortainerUrl=$PortainerUrl;
        EndpointId=$EndpointId; ResponseFormat='Text'; IgnoreHttpFailure=$IgnoreExitCode; Method='GET' }
    for ($index=0; $index -lt $Arguments.Count; $index++) {
        $argument = $Arguments[$index]
        switch ($argument) {
            { $_ -in @('--silent','--show-error','--fail') } { continue }
            '-X' { $parameters.Method=$Arguments[++$index]; continue }
            '-H' {
                $header=$Arguments[++$index]
                if ($header -cnotmatch '^Content-Type: (application/json|application/x-tar)$') { throw 'PORTAINER_HEADER_REJECTED' }
                $parameters.ContentType=$header.Substring(14); continue
            }
            '--data-binary' {
                $data=$Arguments[++$index]
                if (-not $data.StartsWith('@')) { throw 'PORTAINER_UPLOAD_REFERENCE_REJECTED' }
                $parameters.InFile=$data.Substring(1); continue
            }
            default {
                if ($argument.StartsWith('https://') -and -not $parameters.ContainsKey('Uri')) { $parameters.Uri=$argument; continue }
                throw 'PORTAINER_LEGACY_ARGUMENT_REJECTED'
            }
        }
    }
    if (-not $parameters.ContainsKey('Uri')) { throw 'PORTAINER_REQUEST_BOUNDARY_REJECTED' }
    $result = Invoke-PortainerManagementRequest @parameters
    # Existing image cleanup checks this non-secret success coordinate.
    $global:LASTEXITCODE = if ($null -eq $result -and $IgnoreExitCode) { 1 } else { 0 }
    return $result
}
