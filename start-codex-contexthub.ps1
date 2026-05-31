param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$CodexArgs
)

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$mcpUrl = "https://context-hub.wjcy.org/mcp"

if (-not $env:CONTEXTHUB_MCP_TOKEN) {
    $userToken = [Environment]::GetEnvironmentVariable('CONTEXTHUB_MCP_TOKEN', 'User')
    if ($userToken) {
        $env:CONTEXTHUB_MCP_TOKEN = $userToken
    }
}

if (-not $env:CONTEXTHUB_MCP_TOKEN) {
    Write-Warning "CONTEXTHUB_MCP_TOKEN is not set. ContextHub MCP authentication will fail until the token is configured."
}

& codex `
    -C $repoRoot `
    -c "mcp_servers.contexthub.url=`"$mcpUrl`"" `
    -c "mcp_servers.contexthub.bearer_token_env_var=`"CONTEXTHUB_MCP_TOKEN`"" `
    @CodexArgs
exit $LASTEXITCODE
