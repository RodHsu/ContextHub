param(
    [string]$Endpoint = "https://context-hub.wjcy.org/mcp",
    [string]$ProjectId = "ContextHub",
    [string]$Query = "ContextHub MCP connectivity diagnostics",
    [string]$CodexModel = "gpt-5.5",
    [switch]$RunCodexExec
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

function New-McpHeaders {
    param([string]$Token, [string]$SessionId)

    $headers = @{
        Authorization = "Bearer $Token"
        Accept = "application/json, text/event-stream"
        "Content-Type" = "application/json"
        "MCP-Protocol-Version" = "2025-06-18"
    }

    if ($SessionId) {
        $headers["Mcp-Session-Id"] = $SessionId
    }

    return $headers
}

function Invoke-McpJsonRpc {
    param(
        [string]$Endpoint,
        [hashtable]$Headers,
        [object]$Payload
    )

    $body = $Payload | ConvertTo-Json -Depth 30
    Invoke-WebRequest -Uri $Endpoint -Method Post -Headers $Headers -Body $body -UseBasicParsing -TimeoutSec 45
}

function ConvertTo-HeaderMap {
    param([object]$Headers)

    $map = @{}
    if (-not $Headers) {
        return $map
    }

    if ($Headers.AllKeys) {
        foreach ($key in $Headers.AllKeys) {
            $map[$key] = [string]$Headers[$key]
        }

        return $map
    }

    foreach ($header in $Headers.GetEnumerator()) {
        $value = $header.Value
        if ($value -is [System.Array]) {
            $value = $value -join ", "
        }

        $map[$header.Key] = [string]$value
    }

    return $map
}

function New-HttpResponseRecord {
    param([object]$Response)

    $headers = ConvertTo-HeaderMap -Headers $Response.Headers
    $content = ""

    if ($Response.Content) {
        if ($Response.Content.Headers) {
            $contentHeaders = ConvertTo-HeaderMap -Headers $Response.Content.Headers
            foreach ($key in $contentHeaders.Keys) {
                $headers[$key] = $contentHeaders[$key]
            }
        }

        if ($Response.Content -is [string]) {
            $content = $Response.Content
        }
        elseif ($Response.Content.ReadAsStringAsync) {
            $content = $Response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        }
    }
    elseif ($Response.GetResponseStream) {
        $stream = $Response.GetResponseStream()
        if ($stream) {
            $reader = [System.IO.StreamReader]::new($stream)
            $content = $reader.ReadToEnd()
        }
    }

    [pscustomobject]@{
        StatusCode = [int]$Response.StatusCode
        Headers = $headers
        Content = $content
    }
}

function Invoke-WebRequestAllowError {
    param(
        [string]$Uri,
        [string]$Method = "Get",
        [hashtable]$Headers,
        [object]$Body,
        [int]$TimeoutSec = 45
    )

    try {
        $response = Invoke-WebRequest -Uri $Uri -Method $Method -Headers $Headers -Body $Body -UseBasicParsing -TimeoutSec $TimeoutSec
        return New-HttpResponseRecord -Response $response
    }
    catch {
        if ($_.Exception.Response) {
            return New-HttpResponseRecord -Response $_.Exception.Response
        }

        throw
    }
}

function Assert-HeaderContains {
    param(
        [object]$Response,
        [string]$HeaderName,
        [string]$ExpectedValue
    )

    $value = [string]$Response.Headers[$HeaderName]
    if ($value -notmatch [regex]::Escape($ExpectedValue)) {
        throw "Expected response header '$HeaderName' to contain '$ExpectedValue', got '$value'."
    }
}

function Assert-NoBrowserChallenge {
    param([object]$Response)

    $contentType = [string]$Response.Headers["Content-Type"]
    $body = [string]$Response.Content
    if ($contentType -match "text/html" -or $body -match "(?i)cf-chl|challenge-platform|Just a moment") {
        throw "Endpoint returned an HTML/browser challenge response instead of raw MCP HTTP."
    }
}

function Assert-NoTlsRenegotiation {
    param([string]$Endpoint)

    $curlPath = Get-Command curl.exe -ErrorAction SilentlyContinue
    if (-not $curlPath) {
        Write-Warning "curl.exe not found; skipping TLS renegotiation probe."
        return
    }

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $curlOutput = & curl.exe -sv --http1.1 $Endpoint -o NUL 2>&1 | Out-String
        $curlExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    if ($curlExitCode -ne 0) {
        throw "curl.exe TLS probe failed with exit code $curlExitCode."
    }

    if ($curlOutput -match "(?i)remote party requests renegotiation|renegotiating SSL/TLS connection") {
        Write-Warning "curl.exe reported TLS renegotiation. This is advisory only; MCP protocol and Codex worker checks below determine pass/fail."
    }
}

function Read-SseDataJson {
    param([string]$Content)

    $line = $Content -split "(`r`n|`n|`r)" | Where-Object { $_ -like "data: *" } | Select-Object -First 1
    if (-not $line) {
        throw "MCP response did not contain an SSE data line."
    }

    return $line.Substring(6) | ConvertFrom-Json
}

$token = Get-ContextHubToken
$baseHeaders = New-McpHeaders -Token $token

Write-Host "1/7 codex mcp get contexthub"
codex mcp get contexthub

Write-Host "2/7 Cloudflare edge must not request TLS renegotiation/client certificates"
Assert-NoTlsRenegotiation -Endpoint $Endpoint

Write-Host "3/7 Cloudflare edge should pass raw unauthenticated MCP 401 without cache or challenge"
$unauthResponse = Invoke-WebRequestAllowError -Uri $Endpoint -Method Get -TimeoutSec 15
$statusCode = [int]$unauthResponse.StatusCode
if ($statusCode -ne 401) {
    throw "Expected 401 from unauthenticated MCP request, got $statusCode."
}
Assert-HeaderContains -Response $unauthResponse -HeaderName "Cache-Control" -ExpectedValue "no-store"
$cfCacheStatus = [string]$unauthResponse.Headers["CF-Cache-Status"]
if ($cfCacheStatus -and $cfCacheStatus -notin @("DYNAMIC", "BYPASS")) {
    throw "Expected CF-Cache-Status to be DYNAMIC or BYPASS, got '$cfCacheStatus'."
}
if (-not $unauthResponse.Headers["CF-RAY"]) {
    throw "Expected Cloudflare CF-RAY header on MCP response."
}
Assert-NoBrowserChallenge -Response $unauthResponse
Write-Host "Unauthenticated request returned 401 with Cache-Control no-store and CF-Cache-Status $cfCacheStatus."

Write-Host "4/7 initialize MCP session"
$initPayload = @{
    jsonrpc = "2.0"
    id = 1
    method = "initialize"
    params = @{
        protocolVersion = "2025-06-18"
        capabilities = @{}
        clientInfo = @{
            name = "contexthub-diagnostics"
            version = "1.0"
        }
    }
}
$initResponse = Invoke-McpJsonRpc -Endpoint $Endpoint -Headers $baseHeaders -Payload $initPayload
$sessionId = [string]$initResponse.Headers["Mcp-Session-Id"]
if (-not $sessionId) {
    throw "MCP initialize did not return Mcp-Session-Id."
}
$initJson = Read-SseDataJson -Content $initResponse.Content
Write-Host "Initialized $($initJson.result.serverInfo.name) $($initJson.result.serverInfo.version)."

Write-Host "5/7 tools/list should expose ContextHub tools"
$sessionHeaders = New-McpHeaders -Token $token -SessionId $sessionId
$toolsPayload = @{
    jsonrpc = "2.0"
    id = 2
    method = "tools/list"
    params = @{}
}
$toolsResponse = Invoke-McpJsonRpc -Endpoint $Endpoint -Headers $sessionHeaders -Payload $toolsPayload
$toolsJson = Read-SseDataJson -Content $toolsResponse.Content
$toolNames = @($toolsJson.result.tools | ForEach-Object { $_.name })
foreach ($requiredTool in @("memory_search", "build_working_context", "conversation_ingest")) {
    if ($toolNames -notcontains $requiredTool) {
        throw "Required ContextHub MCP tool '$requiredTool' was not listed."
    }
}
Write-Host "Tools listed: $($toolNames -join ', ')"

Write-Host "6/7 build_working_context should succeed for ProjectId=$ProjectId"
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
                limit = 3
                recentLogLimit = 3
            }
        }
    }
}
$contextResponse = Invoke-McpJsonRpc -Endpoint $Endpoint -Headers $sessionHeaders -Payload $contextPayload
$contextJson = Read-SseDataJson -Content $contextResponse.Content
$contextText = [string]$contextJson.result.content[0].text
if ($contextText -notmatch "userPreferences|facts|decisions|recentLogs") {
    throw "build_working_context returned an unexpected payload."
}
Write-Host "build_working_context succeeded."

if ($RunCodexExec) {
    Write-Host "7/7 optional codex exec verification"
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

    if ($codexText -match "https://context-hub\.wjcy\.org/mcp" -and $codexText -match "http/request failed|rmcp::transport::worker|MCP startup failed") {
        throw "codex exec reported ContextHub MCP worker transport failure."
    }

    $hasCompletedToolCall = $codexText.Contains("mcp: contexthub/memory_search") -and $codexText.Contains("(completed)")
    $hasSucceededAnswer = $codexText -match "(?i)\bsucceeded\b"
    if (-not $hasCompletedToolCall -or -not $hasSucceededAnswer) {
        throw "codex exec did not show a completed contexthub/memory_search tool call."
    }
}
else {
    Write-Host "7/7 optional codex exec verification skipped. Re-run with -RunCodexExec to include it."
}

Write-Host "ContextHub MCP diagnostics passed."
