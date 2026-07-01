[Diagnostics.CodeAnalysis.SuppressMessageAttribute(
    "PSAvoidUsingConvertToSecureStringWithPlainText",
    "",
    Justification = "The script reads bearer/OIDC tokens from existing environment variables and never persists them."
)]
param(
    [string]$Endpoint = "https://context-hub.wjcy.org/mcp-chat",
    [string]$ProjectId = "ContextHub",
    [string]$UnauthorizedProjectId = "ContextHubChatGptGatewayDenied",
    [string]$Query = "ContextHub MCP chat gateway diagnostics",
    [string]$ResourceMetadataUrl = "https://context-hub.wjcy.org/.well-known/oauth-protected-resource/mcp-chat",
    [string]$RootResourceMetadataUrl = "https://context-hub.wjcy.org/.well-known/oauth-protected-resource",
    [string]$AuthorizationServerMetadataUrl = "https://context-hub.wjcy.org/.well-known/oauth-authorization-server/mcp-chat",
    [string]$RootAuthorizationServerMetadataUrl = "https://context-hub.wjcy.org/.well-known/oauth-authorization-server",
    [string]$OpenIdConfigurationUrl = "https://context-hub.wjcy.org/.well-known/openid-configuration/mcp-chat",
    [string]$RootOpenIdConfigurationUrl = "https://context-hub.wjcy.org/.well-known/openid-configuration",
    [string]$UserInfoUrl = "https://context-hub.wjcy.org/userinfo",
    [string]$TokenEnvironmentVariable = "CONTEXTHUB_MCP_CHAT_TOKEN",
    [switch]$RequireAuthorizationToken,
    [switch]$RunProposalSmoke
)

$ErrorActionPreference = "Stop"

function Get-OptionalBearerToken {
    param([string]$Name)

    $processValue = [Environment]::GetEnvironmentVariable($Name, "Process")
    if (-not [string]::IsNullOrWhiteSpace($processValue)) {
        return $processValue.Trim()
    }

    $userValue = [Environment]::GetEnvironmentVariable($Name, "User")
    if (-not [string]::IsNullOrWhiteSpace($userValue)) {
        return $userValue.Trim()
    }

    $machineValue = [Environment]::GetEnvironmentVariable($Name, "Machine")
    if (-not [string]::IsNullOrWhiteSpace($machineValue)) {
        return $machineValue.Trim()
    }

    return ""
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

function Invoke-McpJsonRpc {
    param(
        [string]$Endpoint,
        [hashtable]$Headers,
        [object]$Payload
    )

    $body = $Payload | ConvertTo-Json -Depth 40
    Invoke-WebRequest -Uri $Endpoint -Method Post -Headers $Headers -Body $body -UseBasicParsing -TimeoutSec 45
}

function Read-SseDataJson {
    param([string]$Content)

    $line = $Content -split "(`r`n|`n|`r)" | Where-Object { $_ -like "data: *" } | Select-Object -First 1
    if (-not $line) {
        throw "MCP response did not contain an SSE data line."
    }

    return $line.Substring(6) | ConvertFrom-Json
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

function Assert-ToolCallSucceeded {
    param(
        [object]$Json,
        [string]$ToolName
    )

    if ($Json.error) {
        throw "$ToolName returned JSON-RPC error: $($Json.error.message)"
    }

    if ($Json.result.isError -eq $true) {
        $content = @($Json.result.content | ForEach-Object { $_.text }) -join "`n"
        throw "$ToolName returned MCP tool error: $content"
    }
}

function Assert-ToolCallRejected {
    param(
        [object]$Json,
        [string]$Scenario
    )

    if ($Json.error -or $Json.result.isError -eq $true) {
        return
    }

    throw "Expected rejection for $Scenario, but the call succeeded."
}

Write-Host "1/12 unauthenticated MCP chat gateway should return 401 without browser challenge"
$unauthResponse = Invoke-WebRequestAllowError -Uri $Endpoint -Method Get -TimeoutSec 15
if ([int]$unauthResponse.StatusCode -ne 401) {
    throw "Expected 401 from unauthenticated MCP chat request, got $($unauthResponse.StatusCode)."
}

Assert-HeaderContains -Response $unauthResponse -HeaderName "Cache-Control" -ExpectedValue "no-store"
Assert-HeaderContains -Response $unauthResponse -HeaderName "WWW-Authenticate" -ExpectedValue "resource_metadata=`"$ResourceMetadataUrl`""
Assert-NoBrowserChallenge -Response $unauthResponse
Write-Host "Unauthenticated /mcp-chat returned 401 with no-store."

Write-Host "2/12 OAuth protected resource metadata should describe MCP chat gateway"
$metadataResponse = Invoke-WebRequestAllowError -Uri $ResourceMetadataUrl -Method Get -TimeoutSec 15
if ([int]$metadataResponse.StatusCode -ne 200) {
    throw "Expected 200 from OAuth protected resource metadata, got $($metadataResponse.StatusCode)."
}

Assert-NoBrowserChallenge -Response $metadataResponse
$metadata = $metadataResponse.Content | ConvertFrom-Json
if ([string]$metadata.resource -ne $Endpoint) {
    throw "Expected OAuth protected resource metadata resource '$Endpoint', got '$($metadata.resource)'."
}

if (-not $metadata.authorization_servers -or $metadata.authorization_servers.Count -lt 1) {
    throw "OAuth protected resource metadata must include at least one authorization server."
}

if (@($metadata.bearer_methods_supported) -notcontains "header") {
    throw "OAuth protected resource metadata must include bearer_methods_supported=header."
}

Write-Host "3/12 root OAuth protected resource metadata should also be public"
$rootMetadataResponse = Invoke-WebRequestAllowError -Uri $RootResourceMetadataUrl -Method Get -TimeoutSec 15
if ([int]$rootMetadataResponse.StatusCode -ne 200) {
    throw "Expected 200 from root OAuth protected resource metadata, got $($rootMetadataResponse.StatusCode)."
}

Assert-NoBrowserChallenge -Response $rootMetadataResponse
$rootMetadata = $rootMetadataResponse.Content | ConvertFrom-Json
if ([string]$rootMetadata.resource -ne $Endpoint) {
    throw "Expected root OAuth protected resource metadata resource '$Endpoint', got '$($rootMetadata.resource)'."
}

Write-Host "4/12 OAuth authorization server metadata should expose authorization code endpoints"
$authorizationMetadataResponse = Invoke-WebRequestAllowError -Uri $AuthorizationServerMetadataUrl -Method Get -TimeoutSec 15
if ([int]$authorizationMetadataResponse.StatusCode -ne 200) {
    throw "Expected 200 from OAuth authorization server metadata, got $($authorizationMetadataResponse.StatusCode)."
}

Assert-NoBrowserChallenge -Response $authorizationMetadataResponse
$authorizationMetadata = $authorizationMetadataResponse.Content | ConvertFrom-Json
if (-not $authorizationMetadata.issuer) {
    throw "OAuth authorization server metadata must include issuer."
}

if ([string]$authorizationMetadata.authorization_endpoint -notmatch "/oauth/chat/authorize$") {
    throw "OAuth authorization server metadata must expose /oauth/chat/authorize, got '$($authorizationMetadata.authorization_endpoint)'."
}

if ([string]$authorizationMetadata.token_endpoint -notmatch "/oauth/chat/token$") {
    throw "OAuth authorization server metadata must expose /oauth/chat/token, got '$($authorizationMetadata.token_endpoint)'."
}

if (@($authorizationMetadata.code_challenge_methods_supported) -notcontains "S256") {
    throw "OAuth authorization server metadata must include PKCE S256 support."
}

Write-Host "5/12 root OAuth authorization server metadata should also be public"
$rootAuthorizationMetadataResponse = Invoke-WebRequestAllowError -Uri $RootAuthorizationServerMetadataUrl -Method Get -TimeoutSec 15
if ([int]$rootAuthorizationMetadataResponse.StatusCode -ne 200) {
    throw "Expected 200 from root OAuth authorization server metadata, got $($rootAuthorizationMetadataResponse.StatusCode)."
}

Assert-NoBrowserChallenge -Response $rootAuthorizationMetadataResponse
$rootAuthorizationMetadata = $rootAuthorizationMetadataResponse.Content | ConvertFrom-Json
if ([string]$rootAuthorizationMetadata.authorization_endpoint -ne [string]$authorizationMetadata.authorization_endpoint) {
    throw "Root OAuth authorization metadata must expose the same authorization endpoint."
}

Write-Host "6/12 OpenID Connect discovery should expose userinfo and id_token support"
$oidcMetadataResponse = Invoke-WebRequestAllowError -Uri $OpenIdConfigurationUrl -Method Get -TimeoutSec 15
if ([int]$oidcMetadataResponse.StatusCode -ne 200) {
    throw "Expected 200 from OpenID Connect metadata, got $($oidcMetadataResponse.StatusCode)."
}

Assert-NoBrowserChallenge -Response $oidcMetadataResponse
$oidcMetadata = $oidcMetadataResponse.Content | ConvertFrom-Json
if (-not $oidcMetadata.issuer) {
    throw "OpenID Connect metadata must include issuer."
}

if ([string]$oidcMetadata.authorization_endpoint -notmatch "/oauth/chat/authorize$") {
    throw "OpenID Connect metadata must expose /oauth/chat/authorize, got '$($oidcMetadata.authorization_endpoint)'."
}

if ([string]$oidcMetadata.token_endpoint -notmatch "/oauth/chat/token$") {
    throw "OpenID Connect metadata must expose /oauth/chat/token, got '$($oidcMetadata.token_endpoint)'."
}

if ([string]$oidcMetadata.userinfo_endpoint -notmatch "/userinfo$") {
    throw "OpenID Connect metadata must expose /userinfo, got '$($oidcMetadata.userinfo_endpoint)'."
}

if (@($oidcMetadata.id_token_signing_alg_values_supported) -notcontains "HS256") {
    throw "OpenID Connect metadata must include HS256 id_token support."
}

if (@($oidcMetadata.scopes_supported) -notcontains "offline_access") {
    throw "OpenID Connect metadata must advertise offline_access refresh-token support."
}

if (@($oidcMetadata.claims_supported) -notcontains "sub") {
    throw "OpenID Connect metadata must include sub claim support."
}

Write-Host "7/12 root OpenID Connect discovery should also be public"
$rootOidcMetadataResponse = Invoke-WebRequestAllowError -Uri $RootOpenIdConfigurationUrl -Method Get -TimeoutSec 15
if ([int]$rootOidcMetadataResponse.StatusCode -ne 200) {
    throw "Expected 200 from root OpenID Connect metadata, got $($rootOidcMetadataResponse.StatusCode)."
}

Assert-NoBrowserChallenge -Response $rootOidcMetadataResponse
$rootOidcMetadata = $rootOidcMetadataResponse.Content | ConvertFrom-Json
if ([string]$rootOidcMetadata.userinfo_endpoint -ne [string]$oidcMetadata.userinfo_endpoint) {
    throw "Root OpenID Connect metadata must expose the same userinfo endpoint."
}

$token = Get-OptionalBearerToken -Name $TokenEnvironmentVariable
if ([string]::IsNullOrWhiteSpace($token)) {
    if ($RequireAuthorizationToken) {
        throw "$TokenEnvironmentVariable is required for authorized MCP chat gateway verification."
    }

    Write-Warning "No $TokenEnvironmentVariable token was found. Authorized ChatGPT simulation was skipped."
    return
}

$baseHeaders = New-McpHeaders -Token $token

Write-Host "8/12 userinfo should accept the OAuth bearer token"
$userInfoResponse = Invoke-WebRequestAllowError -Uri $UserInfoUrl -Method Get -Headers @{ Authorization = "Bearer $token"; Accept = "application/json" } -TimeoutSec 15
if ([int]$userInfoResponse.StatusCode -ne 200) {
    throw "Expected 200 from userinfo endpoint, got $($userInfoResponse.StatusCode)."
}

Assert-NoBrowserChallenge -Response $userInfoResponse
$userInfo = $userInfoResponse.Content | ConvertFrom-Json
if (-not $userInfo.sub) {
    throw "Userinfo response must include sub."
}

Write-Host "9/12 initialize MCP chat gateway session"
$initResponse = Invoke-McpJsonRpc -Endpoint $Endpoint -Headers $baseHeaders -Payload @{
    jsonrpc = "2.0"
    id = 1
    method = "initialize"
    params = @{
        protocolVersion = "2025-06-18"
        capabilities = @{}
        clientInfo = @{
            name = "contexthub-mcp-chat-diagnostics"
            version = "1.0"
        }
    }
}
$sessionId = [string]$initResponse.Headers["Mcp-Session-Id"]
if (-not $sessionId) {
    throw "MCP chat initialize did not return Mcp-Session-Id."
}
$sessionHeaders = New-McpHeaders -Token $token -SessionId $sessionId

Write-Host "10/12 tools/list should expose only restricted chat gateway tools"
$toolsResponse = Invoke-McpJsonRpc -Endpoint $Endpoint -Headers $sessionHeaders -Payload @{
    jsonrpc = "2.0"
    id = 2
    method = "tools/list"
    params = @{}
}
$toolsJson = Read-SseDataJson -Content $toolsResponse.Content
$toolNames = @($toolsJson.result.tools | ForEach-Object { $_.name })
$requiredTools = @(
    "describe_context_hub",
    "build_working_context",
    "memory_search",
    "memory_get",
    "log_search",
    "log_read",
    "conversation_ingest",
    "memory_upsert",
    "memory_update",
    "user_preference_upsert",
    "promote_log_slice_to_memory",
    "chatgpt_proposals_list",
    "chatgpt_proposal_approve",
    "chatgpt_proposal_reject"
)
$forbiddenTools = @(
    "enqueue_reindex",
    "enqueue_summary_refresh",
    "conversation_sessions_list",
    "maintenance_status",
    "maintenance_lease_acquire",
    "project_artifacts_prune_expired_objects"
)

foreach ($name in $requiredTools) {
    if ($toolNames -notcontains $name) {
        throw "Expected MCP chat gateway to expose '$name'."
    }
}

foreach ($name in $forbiddenTools) {
    if ($toolNames -contains $name) {
        throw "MCP chat gateway must not expose '$name'."
    }
}
Write-Host "Restricted tool allowlist verified ($($toolNames.Count) tools)."

Write-Host "11/12 authorized read tools should work for allowed project"
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
            }
        }
    }
}
$contextJson = Read-SseDataJson -Content $contextResponse.Content
Assert-ToolCallSucceeded -Json $contextJson -ToolName "build_working_context"

$searchResponse = Invoke-McpJsonRpc -Endpoint $Endpoint -Headers $sessionHeaders -Payload @{
    jsonrpc = "2.0"
    id = 4
    method = "tools/call"
    params = @{
        name = "memory_search"
        arguments = @{
            projectId = $ProjectId
            query = $Query
            limit = 3
        }
    }
}
$searchJson = Read-SseDataJson -Content $searchResponse.Content
Assert-ToolCallSucceeded -Json $searchJson -ToolName "memory_search"
Write-Host "Allowed project read tools completed."

Write-Host "12/12 unauthorized project and unknown tool should be rejected"
$deniedProjectResponse = Invoke-McpJsonRpc -Endpoint $Endpoint -Headers $sessionHeaders -Payload @{
    jsonrpc = "2.0"
    id = 5
    method = "tools/call"
    params = @{
        name = "memory_search"
        arguments = @{
            projectId = $UnauthorizedProjectId
            query = "should be rejected"
            limit = 1
        }
    }
}
$deniedProjectJson = Read-SseDataJson -Content $deniedProjectResponse.Content
Assert-ToolCallRejected -Json $deniedProjectJson -Scenario "unauthorized project '$UnauthorizedProjectId'"

$unknownToolResponse = Invoke-McpJsonRpc -Endpoint $Endpoint -Headers $sessionHeaders -Payload @{
    jsonrpc = "2.0"
    id = 6
    method = "tools/call"
    params = @{
        name = "enqueue_reindex"
        arguments = @{
            projectId = $ProjectId
        }
    }
}
$unknownToolJson = Read-SseDataJson -Content $unknownToolResponse.Content
Assert-ToolCallRejected -Json $unknownToolJson -Scenario "forbidden tool enqueue_reindex"
Write-Host "Boundary checks completed."

if (-not $RunProposalSmoke) {
    Write-Host "Proposal smoke skipped. Pass -RunProposalSmoke to create and reject a test proposal."
    return
}

Write-Host "Proposal smoke: proposal write should create pending proposal and allow rejection"
$proposalKey = "mcp-chat-smoke-" + (Get-Date -Format "yyyyMMddHHmmss")
$proposalResponse = Invoke-McpJsonRpc -Endpoint $Endpoint -Headers $sessionHeaders -Payload @{
    jsonrpc = "2.0"
    id = 7
    method = "tools/call"
    params = @{
        name = "memory_upsert"
        arguments = @{
            request = @{
                projectId = $ProjectId
                externalKey = $proposalKey
                scope = "Project"
                memoryType = "Fact"
                title = "MCP chat gateway smoke proposal"
                summary = "MCP chat gateway smoke proposal"
                content = "Temporary proposal created by tools/test-contexthub-mcp-chat.ps1 and rejected during verification."
                sourceType = "diagnostic"
                sourceRef = "tools/test-contexthub-mcp-chat.ps1"
                tags = @("mcp-chat-smoke")
                importance = 0.1
                confidence = 0.1
                metadataJson = "{}"
            }
        }
    }
}
$proposalJson = Read-SseDataJson -Content $proposalResponse.Content
Assert-ToolCallSucceeded -Json $proposalJson -ToolName "memory_upsert proposal"

$proposalText = @($proposalJson.result.content | ForEach-Object { $_.text }) -join "`n"
$proposalId = [regex]::Match($proposalText, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}").Value
if (-not $proposalId) {
    $proposalListResponse = Invoke-McpJsonRpc -Endpoint $Endpoint -Headers $sessionHeaders -Payload @{
        jsonrpc = "2.0"
        id = 8
        method = "tools/call"
        params = @{
            name = "chatgpt_proposals_list"
            arguments = @{
                request = @{
                    projectId = $ProjectId
                    status = "Pending"
                    limit = 20
                }
            }
        }
    }
    $proposalListJson = Read-SseDataJson -Content $proposalListResponse.Content
    Assert-ToolCallSucceeded -Json $proposalListJson -ToolName "chatgpt_proposals_list"
    $proposalListText = @($proposalListJson.result.content | ForEach-Object { $_.text }) -join "`n"
    $proposalId = [regex]::Match($proposalListText, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}").Value
}

if (-not $proposalId) {
    throw "Could not resolve pending proposal id for '$proposalKey'."
}

$rejectResponse = Invoke-McpJsonRpc -Endpoint $Endpoint -Headers $sessionHeaders -Payload @{
    jsonrpc = "2.0"
    id = 9
    method = "tools/call"
    params = @{
        name = "chatgpt_proposal_reject"
        arguments = @{
            request = @{
                proposalId = $proposalId
                note = "MCP chat gateway smoke rejection"
            }
        }
    }
}
$rejectJson = Read-SseDataJson -Content $rejectResponse.Content
Assert-ToolCallSucceeded -Json $rejectJson -ToolName "chatgpt_proposal_reject"
Write-Host "Proposal smoke completed and rejected proposal $proposalId."
