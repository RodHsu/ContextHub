param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$CodexArgs
)

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$mcpUrl = "http://127.0.0.1:8080/mcp"

& codex -C $repoRoot -c "mcp_servers.contexthub.url=`"$mcpUrl`"" @CodexArgs
exit $LASTEXITCODE
