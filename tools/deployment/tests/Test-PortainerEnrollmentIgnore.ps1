#Requires -Version 7.4
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$repoRoot=(Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
$source=Join-Path $PSScriptRoot '../New-PortainerManagementAuthority.ps1'
$parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($source,[ref]$null,[ref]$parseErrors)
if ($parseErrors.Count) { throw 'ENROLLMENT_PARSE_FAILED' }
# Execute only the actual ignore gate, never the enrollment/credential/ACL path.
$loops=@($ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.ForEachStatementAst] -and
        $node.Variable.VariablePath.UserPath -ceq 'authorityFilePath'
},$true))
if ($loops.Count -ne 1) { throw 'ENROLLMENT_IGNORE_GATE_NOT_FOUND' }
$gate=[scriptblock]::Create($loops[0].Extent.Text)
$fixtureRoot=Join-Path $repoRoot ('.agent/local/portainer-tests/enrollment-ignore-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null
& git -C $fixtureRoot init --quiet
if ($LASTEXITCODE) { throw 'FIXTURE_GIT_INIT_FAILED' }
$relativeAuthority='.agent/local/security/portainer/management-authority.json'
$relativeToken='.agent/local/security/portainer/management-token.clixml'
$cases=@(
    @{Name='both independently ignored';Rules=@($relativeAuthority,$relativeToken);Accept=$true},
    @{Name='authority only ignored';Rules=@($relativeAuthority);Accept=$false},
    @{Name='token only ignored';Rules=@($relativeToken);Accept=$false},
    @{Name='neither ignored';Rules=@('# no authority ignore');Accept=$false},
    @{Name='directory ignored';Rules=@('.agent/');Accept=$true}
)
$passed=0
foreach ($case in $cases) {
    $case.Rules | Set-Content -LiteralPath (Join-Path $fixtureRoot '.gitignore') -Encoding utf8
    $accepted=$false
    try {
        & {
            $repoRoot=$fixtureRoot
            $authorityPath=Join-Path $repoRoot $relativeAuthority
            $tokenPath=Join-Path $repoRoot $relativeToken
            if ([IO.Path]::GetRelativePath($repoRoot,$authorityPath) -ne $relativeAuthority.Replace('/',[IO.Path]::DirectorySeparatorChar) -or
                [IO.Path]::GetRelativePath($repoRoot,$tokenPath) -ne $relativeToken.Replace('/',[IO.Path]::DirectorySeparatorChar)) {
                throw 'FIXTURE_PATH_MISMATCH'
            }
            & $gate
        }
        $accepted=$true
    } catch {
        if ($_.Exception.Message -cne 'PORTAINER_AUTHORITY_NOT_IGNORED') { throw 'UNEXPECTED_IGNORE_GATE_FAILURE' }
    }
    if ($accepted -ne $case.Accept) { throw ('IGNORE_GATE_REGRESSION: '+$case.Name) }
    $passed++
}
# Missing Git repository is also fail-closed; suppress Git diagnostics, not its status.
$nonRepo=Join-Path $fixtureRoot 'missing-directory'
$rejected=$false
try {
    & {
        $repoRoot=$nonRepo
        $authorityPath=Join-Path $repoRoot $relativeAuthority
        $tokenPath=Join-Path $repoRoot $relativeToken
        if (-not [IO.Path]::IsPathFullyQualified($authorityPath) -or -not [IO.Path]::IsPathFullyQualified($tokenPath)) {
            throw 'FIXTURE_PATH_MISMATCH'
        }
        & $gate 2>$null
    }
} catch {
    if ($_.Exception.Message -cne 'PORTAINER_AUTHORITY_NOT_IGNORED') { throw 'UNEXPECTED_GIT_FAILURE' }
    $rejected=$true
}
if (-not $rejected) { throw 'GIT_ERROR_ACCEPTED' }
$passed++
& git -C $fixtureRoot check-ignore --quiet -- $relativeAuthority $relativeToken 2>$null
if ($LASTEXITCODE -ne 128) { throw 'LEGACY_MULTI_PATH_REPRODUCTION_CHANGED' }
$passed++
'PORTAINER_ENROLLMENT_IGNORE_PASS '+$passed+'/'+$passed+'; credentialAccess=0; remoteRequests=0'
exit 0
