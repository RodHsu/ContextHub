param(
    [switch]$UseStdioBridge,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$CodexArgs
)

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$mcpUrl = if ($env:CONTEXTHUB_MCP_ENDPOINT) { $env:CONTEXTHUB_MCP_ENDPOINT } else { "http://localhost:8092/mcp" }
$bridgeProject = Join-Path $repoRoot "tools\ContextHub.McpStdioBridge\ContextHub.McpStdioBridge.csproj"
$bridgeExe = Join-Path $repoRoot "tools\ContextHub.McpStdioBridge\bin\Debug\net10.0\ContextHub.McpStdioBridge.exe"
$toolApprovalConfig = "tools = { memory_search = { approval_mode = 'approve' }, build_working_context = { approval_mode = 'approve' }, conversation_ingest = { approval_mode = 'approve' }, memory_upsert = { approval_mode = 'approve' }, memory_update = { approval_mode = 'approve' }, user_preference_upsert = { approval_mode = 'approve' }, maintenance_status = { approval_mode = 'approve' } }"

if (-not $env:CONTEXTHUB_MCP_TOKEN) {
    $userToken = [Environment]::GetEnvironmentVariable('CONTEXTHUB_MCP_TOKEN', 'User')
    if ($userToken) {
        $env:CONTEXTHUB_MCP_TOKEN = $userToken
    }
}

if (-not $env:CONTEXTHUB_MCP_TOKEN) {
    Write-Warning "CONTEXTHUB_MCP_TOKEN is not set. ContextHub MCP authentication will fail until the token is configured."
}

if ($UseStdioBridge) {
    Write-Host "Building ContextHub MCP stdio bridge..."
    dotnet build $bridgeProject --nologo -v:minimal
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    $env:CONTEXTHUB_MCP_ENDPOINT = $mcpUrl
    $mcpConfig = "{ enabled = true, command = '$bridgeExe', args = [], env = { CONTEXTHUB_MCP_ENDPOINT = '$mcpUrl' }, $toolApprovalConfig }"
}
else {
    $mcpConfig = "{ enabled = true, url = '$mcpUrl', bearer_token_env_var = 'CONTEXTHUB_MCP_TOKEN', $toolApprovalConfig }"
}

& codex `
    -C $repoRoot `
    -c "mcp_servers.contexthub=$mcpConfig" `
    @CodexArgs
exit $LASTEXITCODE
