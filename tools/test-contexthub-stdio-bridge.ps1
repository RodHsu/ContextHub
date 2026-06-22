param(
    [string]$ProjectPath = "tools\ContextHub.McpStdioBridge\ContextHub.McpStdioBridge.csproj",
    [string]$Endpoint = "https://context-hub.wjcy.org/mcp",
    [string]$ProjectId = "ContextHub",
    [string]$Query = "ContextHub MCP stdio bridge diagnostics",
    [switch]$RunReconnectRegression,
    [switch]$RunCodexExec,
    [string]$CodexModel = "gpt-5.5"
)

$ErrorActionPreference = "Stop"

function Get-ContextHubToken {
    if ($env:CONTEXTHUB_MCP_TOKEN) {
        return $env:CONTEXTHUB_MCP_TOKEN
    }

    $userToken = [Environment]::GetEnvironmentVariable("CONTEXTHUB_MCP_TOKEN", "User")
    if ($userToken) {
        $env:CONTEXTHUB_MCP_TOKEN = $userToken
        return $userToken
    }

    $machineToken = [Environment]::GetEnvironmentVariable("CONTEXTHUB_MCP_TOKEN", "Machine")
    if ($machineToken) {
        $env:CONTEXTHUB_MCP_TOKEN = $machineToken
        return $machineToken
    }

    throw "CONTEXTHUB_MCP_TOKEN is not set in process, user, or machine environment."
}

function Invoke-StdioBridge {
    param(
        [string]$BridgeDllPath,
        [string[]]$Messages
    )

    $env:CONTEXTHUB_MCP_ENDPOINT = $Endpoint
    $inputPath = [System.IO.Path]::GetTempFileName()
    $outputPath = [System.IO.Path]::GetTempFileName()
    try {
        [System.IO.File]::WriteAllLines($inputPath, $Messages, [System.Text.UTF8Encoding]::new($false))
        $command = 'type "{0}" | dotnet "{1}" > "{2}"' -f $inputPath, $BridgeDllPath, $outputPath
        & cmd.exe /d /c $command
        if ($LASTEXITCODE -ne 0) {
            throw "ContextHub stdio bridge exited with code $LASTEXITCODE."
        }

        $output = Get-Content -LiteralPath $outputPath
    }
    finally {
        Remove-Item -LiteralPath $inputPath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $outputPath -Force -ErrorAction SilentlyContinue
    }

    foreach ($line in $output) {
        if (-not [string]::IsNullOrWhiteSpace($line) -and $line.TrimStart().StartsWith("{")) {
            Write-Output ([string]$line)
        }
    }
}

$null = Get-ContextHubToken

$buildOutputPath = Join-Path ([System.IO.Path]::GetTempPath()) ("contexthub-stdio-bridge-" + [System.Guid]::NewGuid().ToString("N"))
$bridgeDllPath = Join-Path $buildOutputPath "ContextHub.McpStdioBridge.dll"

Write-Host "1/6 build stdio bridge"
dotnet build $ProjectPath --output $buildOutputPath -p:UseAppHost=false
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE."
}
if (-not (Test-Path -LiteralPath $bridgeDllPath)) {
    throw "Expected bridge DLL was not produced at '$bridgeDllPath'."
}

try {
    if ($RunReconnectRegression) {
        Write-Host "2/6 run stdio bridge reconnect regression tests"
        dotnet test "tests\Memory.UnitTests\Memory.UnitTests.csproj" --no-restore --filter McpStdioBridgeTests
        if ($LASTEXITCODE -ne 0) {
            throw "stdio bridge reconnect regression tests failed with exit code $LASTEXITCODE."
        }
    }
    else {
        Write-Host "2/6 reconnect regression tests skipped. Re-run with -RunReconnectRegression to validate retry/no-retry behavior."
    }

    Write-Host "3/6 initialize bridge and list tools"
    $messages = @(
        '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"stdio-bridge-diagnostics","version":"1.0"}}}',
        '{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}',
        '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'
    )
    $responseLines = @(Invoke-StdioBridge -BridgeDllPath $bridgeDllPath -Messages $messages)
    $responseText = $responseLines -join "`n"
    if ($responseText -notmatch "ContextHub\.McpStdioBridge") {
        throw "Bridge initialize response did not include ContextHub.McpStdioBridge."
    }

    foreach ($requiredTool in @("memory_search", "build_working_context", "conversation_ingest")) {
        if ($responseText -notmatch [regex]::Escape($requiredTool)) {
            throw "Required ContextHub MCP tool '$requiredTool' was not listed through stdio bridge. Response prefix: $($responseText.Substring(0, [Math]::Min(500, $responseText.Length)))"
        }
    }
    Write-Host "Required tools listed through stdio bridge."

    Write-Host "4/6 build_working_context through stdio bridge"
    $contextPayload = @{
        jsonrpc = "2.0"
        id = 3
        method = "tools/call"
        params = @{
            name = "build_working_context"
            arguments = @{
                request = @{
                    projectId = $ProjectId
                    query = $Query
                    limit = 2
                    recentLogLimit = 2
                }
            }
        }
    } | ConvertTo-Json -Depth 30 -Compress
    $responseLines = @(Invoke-StdioBridge -BridgeDllPath $bridgeDllPath -Messages @(
        '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"stdio-bridge-diagnostics","version":"1.0"}}}',
        '{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}',
        $contextPayload
     ))
    $contextText = $responseLines -join "`n"
    if ($contextText -notmatch "userPreferences|facts|decisions|recentLogs") {
        throw "build_working_context through stdio bridge returned an unexpected payload."
    }
    Write-Host "build_working_context succeeded through stdio bridge."

    Write-Host "5/6 current Codex MCP config"
    codex mcp get contexthub

    if ($RunCodexExec) {
        Write-Host "6/6 optional codex exec verification using current config"
        $previousErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        try {
            $codexOutput = & codex exec -m $CodexModel "Use ContextHub MCP memory_search with projectId=$ProjectId, query=$Query, limit=1. Do not use shell or raw HTTP. Reply only whether the direct MCP tool call succeeded." 2>&1
            $codexExitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }

        $codexText = ($codexOutput | Out-String).Trim()
        Write-Host $codexText
        if ($codexExitCode -ne 0) {
            throw "codex exec failed with exit code $codexExitCode."
        }

        if ($codexText -match "http/request failed|rmcp::transport::worker|MCP startup failed") {
            throw "codex exec reported remote MCP worker transport/startup failure. Disable or fix noisy remote MCP servers before treating ContextHub diagnostics as clean."
        }

        if ($codexText -match "invalid_grant|TokenRefreshFailed|Auth required|AuthRequired") {
            throw "codex exec reported stale OAuth or unauthenticated remote MCP noise. Clear or disable unused remote MCP/plugin credentials before treating ContextHub diagnostics as clean."
        }

        $hasCompletedToolCall = $codexText.Contains("mcp: contexthub/memory_search") -and $codexText.Contains("(completed)")
        $hasSucceededAnswer = $codexText -match "(?i)\bsucceeded\b"
        if (-not $hasCompletedToolCall -or -not $hasSucceededAnswer) {
            throw "codex exec did not show a completed contexthub/memory_search tool call. Raw MCP and stdio bridge may be healthy while this specific Codex session path is stale."
        }
    }
    else {
        Write-Host "6/6 optional codex exec verification skipped. Re-run with -RunCodexExec after switching Codex config to stdio."
    }
}
finally {
    Remove-Item -LiteralPath $buildOutputPath -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "ContextHub stdio bridge diagnostics passed."
