param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$CodexArgs
)

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$mcpUrl = "https://context-hub.wjcy.org/mcp"
$bridgeProject = Join-Path $repoRoot "tools\ContextHub.McpStdioBridge\ContextHub.McpStdioBridge.csproj"
$bridgeExe = Join-Path $repoRoot "tools\ContextHub.McpStdioBridge\bin\Debug\net10.0\ContextHub.McpStdioBridge.exe"

if (-not $env:CONTEXTHUB_MCP_TOKEN) {
    $userToken = [Environment]::GetEnvironmentVariable('CONTEXTHUB_MCP_TOKEN', 'User')
    if ($userToken) {
        $env:CONTEXTHUB_MCP_TOKEN = $userToken
    }
}

if (-not $env:CONTEXTHUB_MCP_TOKEN) {
    Write-Warning "CONTEXTHUB_MCP_TOKEN is not set. ContextHub MCP authentication will fail until the token is configured."
}

Write-Host "Building ContextHub MCP stdio bridge..."
dotnet build $bridgeProject --nologo -v:minimal
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$env:CONTEXTHUB_MCP_ENDPOINT = $mcpUrl
$mcpConfig = "{ enabled = true, command = '$bridgeExe', args = [], env = { CONTEXTHUB_MCP_ENDPOINT = '$mcpUrl' } }"

& codex `
    -C $repoRoot `
    -c "mcp_servers.contexthub=$mcpConfig" `
    @CodexArgs
exit $LASTEXITCODE
