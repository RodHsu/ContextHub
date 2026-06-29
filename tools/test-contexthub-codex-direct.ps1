param(
    [string]$Endpoint = "https://context-hub.wjcy.org/mcp",
    [string]$ProjectId = "ContextHub",
    [string]$Query = "ContextHub Codex native remote MCP diagnostics",
    [string]$CodexModel = "gpt-5.5",
    [switch]$ApplyUserConfig
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

function Read-SseDataJson {
    param([string]$Content)

    $line = $Content -split "(`r`n|`n|`r)" | Where-Object { $_ -like "data: *" } | Select-Object -First 1
    if (-not $line) {
        throw "MCP response did not contain an SSE data line."
    }

    return $line.Substring(6) | ConvertFrom-Json
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

function Assert-NoBrowserChallenge {
    param([object]$Response)

    $contentType = [string]$Response.Headers["Content-Type"]
    $body = [string]$Response.Content
    if ($contentType -match "text/html" -or $body -match "(?i)cf-chl|challenge-platform|Just a moment") {
        throw "Endpoint returned an HTML/browser challenge response instead of raw MCP HTTP."
    }
}

function Get-DirectMcpConfig {
    param([string]$Endpoint)

    return "{ enabled = true, url = '$Endpoint', bearer_token_env_var = 'CONTEXTHUB_MCP_TOKEN' }"
}

function Assert-CodexMcpCallSucceeded {
    param([string]$Output)

    if ($Output -match "rmcp::transport::worker|MCP startup failed|http/request failed|Transport closed") {
        throw "codex exec reported native remote MCP transport failure."
    }

    if ($Output -match "invalid_grant|TokenRefreshFailed|Auth required|AuthRequired") {
        throw "codex exec reported stale OAuth or unauthenticated remote MCP noise."
    }

    $hasCompletedToolCall = $Output.Contains("mcp: contexthub/memory_search") -and $Output.Contains("(completed)")
    $hasSucceededAnswer = $Output -match "(?i)\bsucceeded\b"
    if (-not $hasCompletedToolCall -or -not $hasSucceededAnswer) {
        throw "codex exec did not show a completed contexthub/memory_search tool call."
    }
}

$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$token = Get-ContextHubToken
$baseHeaders = New-McpHeaders -Token $token

Write-Host "1/5 unauthenticated remote MCP should return 401 without browser challenge"
$unauthResponse = Invoke-WebRequestAllowError -Uri $Endpoint -Method Get -TimeoutSec 15
if ([int]$unauthResponse.StatusCode -ne 401) {
    throw "Expected 401 from unauthenticated MCP request, got $($unauthResponse.StatusCode)."
}
Assert-NoBrowserChallenge -Response $unauthResponse
Write-Host "Remote MCP returned 401 as expected."

Write-Host "2/5 raw remote MCP initialize and tools/list"
$initPayload = @{
    jsonrpc = "2.0"
    id = 1
    method = "initialize"
    params = @{
        protocolVersion = "2025-06-18"
        capabilities = @{}
        clientInfo = @{
            name = "contexthub-codex-direct-diagnostics"
            version = "1.0"
        }
    }
}
$initResponse = Invoke-McpJsonRpc -Endpoint $Endpoint -Headers $baseHeaders -Payload $initPayload
$sessionId = [string]$initResponse.Headers["Mcp-Session-Id"]
if (-not $sessionId) {
    throw "MCP initialize did not return Mcp-Session-Id."
}
$sessionHeaders = New-McpHeaders -Token $token -SessionId $sessionId

$toolsResponse = Invoke-McpJsonRpc -Endpoint $Endpoint -Headers $sessionHeaders -Payload @{
    jsonrpc = "2.0"
    id = 2
    method = "tools/list"
    params = @{}
}
$toolsJson = Read-SseDataJson -Content $toolsResponse.Content
$toolNames = @($toolsJson.result.tools | ForEach-Object { $_.name })
foreach ($requiredTool in @("memory_search", "build_working_context", "conversation_ingest")) {
    if ($toolNames -notcontains $requiredTool) {
        throw "Required ContextHub MCP tool '$requiredTool' was not listed."
    }
}
Write-Host "Raw remote MCP tools/list succeeded."

Write-Host "3/5 raw remote MCP build_working_context"
$contextResponse = Invoke-McpJsonRpc -Endpoint $Endpoint -Headers $sessionHeaders -Payload @{
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
$contextJson = Read-SseDataJson -Content $contextResponse.Content
$contextText = [string]$contextJson.result.content[0].text
if ($contextText -notmatch "userPreferences|facts|decisions|recentLogs") {
    throw "build_working_context returned an unexpected payload."
}
Write-Host "Raw remote MCP build_working_context succeeded."

Write-Host "4/5 isolated Codex native remote MCP verification"
$directConfig = Get-DirectMcpConfig -Endpoint $Endpoint
$prompt = "Use ContextHub MCP memory_search with projectId=$ProjectId, query=$Query, limit=1. Do not use shell or raw HTTP. Reply only whether the direct MCP tool call succeeded."
$previousErrorActionPreference = $ErrorActionPreference
$ErrorActionPreference = "Continue"
try {
    $codexOutput = & codex exec `
        --ignore-user-config `
        --ephemeral `
        -C $repoRoot `
        -m $CodexModel `
        -s read-only `
        -c "mcp_servers.contexthub=$directConfig" `
        -c 'mcp_servers.contexthub.tools.memory_search.approval_mode="approve"' `
        $prompt 2>&1
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
Assert-CodexMcpCallSucceeded -Output $codexText
Write-Host "Isolated Codex native remote MCP verification passed."

if ($ApplyUserConfig) {
    Write-Host "5/5 applying native remote MCP to user Codex config"
    codex mcp remove contexthub 2>$null
    codex mcp add contexthub --url $Endpoint --bearer-token-env-var CONTEXTHUB_MCP_TOKEN
    if ($LASTEXITCODE -ne 0) {
        throw "codex mcp add failed with exit code $LASTEXITCODE."
    }

    codex mcp get contexthub
}
else {
    Write-Host "5/5 user Codex config unchanged. Re-run with -ApplyUserConfig after isolated verification if you want to switch the global config."
}

Write-Host "ContextHub Codex native remote MCP diagnostics passed."
