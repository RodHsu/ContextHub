#requires -Version 7.4
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$RetainedSourceSha,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$CandidateSourceSha,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$TargetRevision
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$config = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'work-item-rollback-preparation.json') |
    ConvertFrom-Json -AsHashtable

if ($config['retainedSourceSha'] -cne $RetainedSourceSha -or
    $config['candidateSourceSha'] -cne $CandidateSourceSha -or
    $config['targetRevision'] -cne $TargetRevision) {
    throw 'The requested rollback does not match the configured promotion.'
}

$container = $config['postgresContainerId']
$database = $config['database']
$user = $config['databaseUser']
if ($container -isnot [string] -or $container -cnotmatch '^[0-9a-f]{64}$' -or
    $database -isnot [string] -or $database -cnotmatch '^[a-z_][a-z0-9_]{0,62}$' -or
    $user -isnot [string] -or $user -cnotmatch '^[a-z_][a-z0-9_]{0,62}$') {
    throw 'A pinned PostgreSQL container ID, database and database user are required.'
}

$docker = (Get-Command docker -CommandType Application | Select-Object -First 1).Source
$identity = & $docker inspect --type container --format '{{.Id}}/{{.State.Running}}' $container
if ($LASTEXITCODE -ne 0 -or $identity -cne "$container/true") {
    throw 'The configured PostgreSQL container is unavailable.'
}

$sql = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'work-item-rollback-read-only.sql')
foreach ($mode in 'enable', 'verify') {
    $sql | & $docker exec -i $container psql -X -w -U $user -d $database -At `
        -v ON_ERROR_STOP=1 -v "work_item_rollback_mode=$mode" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Work Item rollback guard failed during $mode."
    }
}

'WorkItemRollbackGuardVerified'
