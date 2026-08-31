[CmdletBinding()]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute("PSReviewUnusedParameter", "EvidencePath", Justification = "Consumed by the nested evidence writer through script scope.")]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute("PSReviewUnusedParameter", "TimeoutSec", Justification = "Consumed by the nested HTTP helper through script scope.")]
param(
    [ValidateSet("Readiness", "A0", "A1")]
    [string]$Mode = "A0",
    [string]$Endpoint = "",
    [string]$OAuthResource = "",
    [string]$TokenEnvironmentVariable = "CONTEXTHUB_MCP_AUTOMATION_TOKEN",
    [string]$SpecPath = "",
    [string]$ProtectedInvariantEvidencePath = "",
    [string]$EvidencePath = "",
    [ValidateSet("Production", "Isolated")]
    [string]$A1Environment = "Production",
    [switch]$AllowIsolatedSyntheticFixtures,
    [switch]$RequireAuthorizationToken,
    [ValidateRange(5, 180)]
    [int]$TimeoutSec = 45
)

$ErrorActionPreference = "Stop"

$automationDirectory = Join-Path $PSScriptRoot "automation"
. (Join-Path $automationDirectory "ScheduledGovernanceAutomation.Common.ps1")

function Get-ScheduledGovernanceEnvironmentValue {
    param([Parameter(Mandatory)][string]$Name)

    foreach ($scope in @("Process", "User", "Machine")) {
        $value = [Environment]::GetEnvironmentVariable($Name, $scope)
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            return $value.Trim()
        }
    }

    return ""
}

function Get-ScheduledGovernancePropertyValue {
    param(
        [AllowNull()][object]$Object,
        [Parameter(Mandatory)][string]$Name
    )

    return Get-ScheduledGovernanceProperty -Object $Object -Name $Name
}

function Get-ScheduledGovernanceMcpHeaderMap {
    param(
        [string]$Token,
        [Parameter(Mandatory)][string]$Method,
        [string]$ToolName = ""
    )

    $headers = @{
        Accept = "application/json, text/event-stream"
        "Content-Type" = "application/json"
        "MCP-Protocol-Version" = "2026-07-28"
        "Mcp-Method" = $Method
    }
    if (-not [string]::IsNullOrWhiteSpace($Token)) {
        $headers.Authorization = "Bearer $Token"
    }
    if (-not [string]::IsNullOrWhiteSpace($ToolName)) {
        $headers["Mcp-Name"] = $ToolName
    }

    return $headers
}

function Get-ScheduledGovernanceMcpMeta {
    return @{
        "io.modelcontextprotocol/protocolVersion" = "2026-07-28"
        "io.modelcontextprotocol/clientInfo" = @{
            name = "contexthub-scheduled-governance-acceptance"
            version = "1.0"
        }
        "io.modelcontextprotocol/clientCapabilities" = @{}
    }
}

function ConvertTo-ScheduledGovernanceHeaderMap {
    param([AllowNull()][object]$Headers)

    $result = @{}
    if ($null -eq $Headers) {
        return $result
    }

    try {
        if ($Headers.AllKeys) {
            foreach ($key in $Headers.AllKeys) {
                $result[[string]$key] = [string]$Headers[$key]
            }
            return $result
        }
    }
    catch {
        Write-Verbose "The response header collection does not expose AllKeys."
    }

    try {
        foreach ($header in $Headers.GetEnumerator()) {
            $value = $header.Value
            if ($value -is [Array]) {
                $value = $value -join ", "
            }
            $result[[string]$header.Key] = [string]$value
        }
    }
    catch {
        Write-Verbose "The response header collection could not be enumerated."
    }

    return $result
}

function Get-ScheduledGovernanceResponseContent {
    param([AllowNull()][object]$Response)

    if ($null -eq $Response) {
        return ""
    }
    try {
        if ($Response.Content -is [string]) {
            return [string]$Response.Content
        }
    }
    catch {
        Write-Verbose "The response content is not exposed as a string."
    }
    try {
        if ($Response.Content -and $Response.Content.ReadAsStringAsync) {
            return $Response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        }
    }
    catch {
        Write-Verbose "The response content does not support ReadAsStringAsync."
    }
    try {
        if ($Response.GetResponseStream) {
            $stream = $Response.GetResponseStream()
            if ($stream) {
                $reader = [System.IO.StreamReader]::new($stream)
                try {
                    return $reader.ReadToEnd()
                }
                finally {
                    $reader.Dispose()
                }
            }
        }
    }
    catch {
        Write-Verbose "The response does not expose a readable response stream."
    }

    return ""
}

function Invoke-ScheduledGovernanceHttp {
    param(
        [Parameter(Mandatory)][string]$Uri,
        [Parameter(Mandatory)][string]$Method,
        [hashtable]$Headers = @{},
        [AllowNull()][string]$Body = $null
    )

    $parameters = @{
        Uri = $Uri
        Method = $Method
        Headers = $Headers
        UseBasicParsing = $true
        TimeoutSec = $TimeoutSec
    }
    if ($null -ne $Body) {
        $parameters.Body = $Body
        $parameters.ContentType = "application/json"
    }

    try {
        $supportsSkipHttpErrorCheck = (Get-Command Invoke-WebRequest).Parameters.ContainsKey("SkipHttpErrorCheck")
        if ($supportsSkipHttpErrorCheck) {
            $response = Invoke-WebRequest @parameters -SkipHttpErrorCheck
        }
        else {
            $response = Invoke-WebRequest @parameters
        }
        return [pscustomobject]@{
            StatusCode = [int]$response.StatusCode
            Headers = ConvertTo-ScheduledGovernanceHeaderMap $response.Headers
            Content = Get-ScheduledGovernanceResponseContent $response
            TransportError = $false
            Error = ""
        }
    }
    catch {
        $exception = $_.Exception
        $response = $null
        try { $response = $exception.Response } catch { Write-Verbose "The transport exception does not expose a response." }
        if ($null -eq $response) {
            return [pscustomobject]@{
                StatusCode = 0
                Headers = @{}
                Content = ""
                TransportError = $true
                Error = $exception.Message
            }
        }

        $statusCode = 0
        try { $statusCode = [int]$response.StatusCode } catch { Write-Verbose "The error response does not expose a numeric status code." }
        return [pscustomobject]@{
            StatusCode = $statusCode
            Headers = ConvertTo-ScheduledGovernanceHeaderMap $response.Headers
            Content = Get-ScheduledGovernanceResponseContent $response
            TransportError = $false
            Error = ""
        }
    }
}

function ConvertFrom-ScheduledGovernanceMcpResponse {
    param([Parameter(Mandatory)][string]$Content)

    if ([string]::IsNullOrWhiteSpace($Content)) {
        throw "MCP response body was empty."
    }

    $dataLine = $Content -split "(`r`n|`n|`r)" |
        Where-Object { $_ -match "^data:\s*" } |
        Select-Object -First 1
    if ($dataLine) {
        return ($dataLine -replace "^data:\s*", "") | ConvertFrom-Json
    }

    return $Content | ConvertFrom-Json
}

function Invoke-ScheduledGovernanceMcpJson {
    param(
        [Parameter(Mandatory)][string]$Token,
        [Parameter(Mandatory)][string]$Method,
        [Parameter(Mandatory)][hashtable]$Params,
        [Parameter(Mandatory)][int]$RequestId,
        [string]$ToolName = ""
    )

    $body = [ordered]@{
        jsonrpc = "2.0"
        id = $RequestId
        method = $Method
        params = $Params
    } | ConvertTo-Json -Depth 40 -Compress
    $response = Invoke-ScheduledGovernanceHttp `
        -Uri $Endpoint `
        -Method "Post" `
        -Headers (Get-ScheduledGovernanceMcpHeaderMap -Token $Token -Method $Method -ToolName $ToolName) `
        -Body $body
    if ($response.TransportError) {
        throw "MCP transport error: $($response.Error)"
    }
    if ($response.StatusCode -lt 200 -or $response.StatusCode -ge 300) {
        throw "MCP HTTP status $($response.StatusCode)."
    }

    return ConvertFrom-ScheduledGovernanceMcpResponse -Content $response.Content
}

function Invoke-ScheduledGovernanceTool {
    param(
        [Parameter(Mandatory)][string]$Token,
        [Parameter(Mandatory)][string]$ToolName,
        [hashtable]$Arguments = @{},
        [Parameter(Mandatory)][int]$RequestId
    )

    return Invoke-ScheduledGovernanceMcpJson `
        -Token $Token `
        -Method "tools/call" `
        -ToolName $ToolName `
        -RequestId $RequestId `
        -Params @{
            name = $ToolName
            arguments = $Arguments
            _meta = Get-ScheduledGovernanceMcpMeta
        }
}

function Assert-ScheduledGovernanceRpcSuccess {
    param(
        [Parameter(Mandatory)][object]$Json,
        [Parameter(Mandatory)][string]$Operation
    )

    $rpcError = Get-ScheduledGovernancePropertyValue $Json "error"
    if ($null -ne $rpcError) {
        throw "$Operation returned a JSON-RPC error."
    }
    $result = Get-ScheduledGovernancePropertyValue $Json "result"
    if ((Get-ScheduledGovernancePropertyValue $result "isError") -eq $true) {
        throw "$Operation returned an MCP tool error."
    }
}

function Get-ScheduledGovernanceToolData {
    param(
        [Parameter(Mandatory)][object]$Json,
        [Parameter(Mandatory)][string]$Operation
    )

    Assert-ScheduledGovernanceRpcSuccess -Json $Json -Operation $Operation
    $result = Get-ScheduledGovernancePropertyValue $Json "result"
    $structured = Get-ScheduledGovernancePropertyValue $result "structuredContent"
    if ($null -ne $structured) {
        return $structured
    }

    $content = @(ConvertTo-ScheduledGovernanceArray (Get-ScheduledGovernancePropertyValue $result "content"))
    foreach ($item in $content) {
        if ((Get-ScheduledGovernancePropertyValue $item "type") -ne "text") {
            continue
        }
        $text = [string](Get-ScheduledGovernancePropertyValue $item "text")
        if ([string]::IsNullOrWhiteSpace($text)) {
            continue
        }
        try {
            return $text | ConvertFrom-Json
        }
        catch {
            return $text
        }
    }

    return $result
}

function Assert-ScheduledGovernanceEqual {
    param(
        [AllowNull()][object]$Actual,
        [AllowNull()][object]$Expected,
        [Parameter(Mandatory)][string]$Message
    )

    if ($Actual -is [bool] -or $Expected -is [bool]) {
        if ([bool]$Actual -ne [bool]$Expected) {
            throw $Message
        }
        return
    }
    if ([string]$Actual -cne [string]$Expected) {
        throw $Message
    }
}

function Get-ScheduledGovernanceArrayProperty {
    param(
        [AllowNull()][object]$Object,
        [Parameter(Mandatory)][string]$Name
    )

    return @(ConvertTo-ScheduledGovernanceArray (Get-ScheduledGovernancePropertyValue $Object $Name))
}

function ConvertTo-ScheduledGovernanceCanonicalObject {
    param([AllowNull()][object]$Value)

    if ($null -eq $Value) {
        return $null
    }
    if ($Value -is [string] -or $Value -is [bool] -or $Value -is [ValueType]) {
        return $Value
    }
    if ($Value -is [System.Collections.IDictionary]) {
        $ordered = [ordered]@{}
        foreach ($key in @($Value.Keys | ForEach-Object { [string]$_ } | Sort-Object)) {
            $ordered[$key] = ConvertTo-ScheduledGovernanceCanonicalObject $Value[$key]
        }
        return $ordered
    }
    if ($Value -is [System.Collections.IEnumerable]) {
        return @($Value | ForEach-Object { ConvertTo-ScheduledGovernanceCanonicalObject $_ })
    }

    $properties = @($Value.PSObject.Properties | Where-Object { $_.MemberType -eq "NoteProperty" } |
        Sort-Object Name)
    if ($properties.Count -gt 0) {
        $ordered = [ordered]@{}
        foreach ($property in $properties) {
            $ordered[$property.Name] = ConvertTo-ScheduledGovernanceCanonicalObject $property.Value
        }
        return $ordered
    }

    return $Value
}

function Get-ScheduledGovernancePublishedSchemaHash {
    param([Parameter(Mandatory)][object]$Tool)

    $canonical = [ordered]@{
        name = [string](Get-ScheduledGovernancePropertyValue $Tool "name")
        inputSchema = ConvertTo-ScheduledGovernanceCanonicalObject (Get-ScheduledGovernancePropertyValue $Tool "inputSchema")
        outputSchema = ConvertTo-ScheduledGovernanceCanonicalObject (Get-ScheduledGovernancePropertyValue $Tool "outputSchema")
    }
    $json = $canonical | ConvertTo-Json -Depth 100 -Compress
    return Get-ScheduledGovernanceSha256Hex -Text $json
}

function Test-ScheduledGovernanceIdsEqual {
    param(
        [AllowNull()][object]$Left,
        [AllowNull()][object]$Right
    )

    $leftValues = @(ConvertTo-ScheduledGovernanceArray $Left | ForEach-Object { [string]$_ } | Sort-Object)
    $rightValues = @(ConvertTo-ScheduledGovernanceArray $Right | ForEach-Object { [string]$_ } | Sort-Object)
    return $leftValues.Count -eq $rightValues.Count -and (($leftValues -join "`n") -ceq ($rightValues -join "`n"))
}

function Get-ScheduledGovernanceReviewEvidence {
    param(
        [Parameter(Mandatory)][object]$Review,
        [Parameter(Mandatory)][string]$ExpectedRunId,
        [Parameter(Mandatory)][bool]$ExpectedReReview
    )

    Assert-ScheduledGovernanceEqual (Get-ScheduledGovernancePropertyValue $Review "governanceRunId") $ExpectedRunId "Review run identifier changed."
    Assert-ScheduledGovernanceEqual (Get-ScheduledGovernancePropertyValue $Review "isReReview") $ExpectedReReview "Review re-review flag changed."
    $decision = [string](Get-ScheduledGovernancePropertyValue $Review "decision")
    if ($decision -notin (Get-ScheduledGovernanceExpectedDecision)) {
        throw "Review returned an unknown server decision."
    }

    $invariant = Get-ScheduledGovernancePropertyValue $Review "countInvariant"
    $coverageComplete = [bool](Get-ScheduledGovernancePropertyValue $Review "coverageComplete")
    [ordered]@{
        decision = $decision
        snapshotPresent = -not [string]::IsNullOrWhiteSpace([string](Get-ScheduledGovernancePropertyValue $Review "snapshotToken"))
        coverageComplete = $coverageComplete
        authorized = [int](Get-ScheduledGovernancePropertyValue $invariant "authorizedDurableMemoryCount")
        covered = [int](Get-ScheduledGovernancePropertyValue $invariant "coveredDurableMemoryCount")
        scanned = [int](Get-ScheduledGovernancePropertyValue $invariant "scannedDurableMemoryCount")
        total = [int](Get-ScheduledGovernancePropertyValue $invariant "totalDurableMemoryCount")
        sharedOccurrences = [int](Get-ScheduledGovernancePropertyValue $invariant "sharedScopeOccurrences")
        userOccurrences = [int](Get-ScheduledGovernancePropertyValue $invariant "userScopeOccurrences")
        userScopeHandledSeparately = [bool](Get-ScheduledGovernancePropertyValue $invariant "userScopeHandledSeparately")
        countInvariantSatisfied = [bool](Get-ScheduledGovernancePropertyValue $invariant "satisfied")
        candidateCount = [int](Get-ScheduledGovernancePropertyValue $Review "candidateCount")
        reversibleExecutionCount = [int](Get-ScheduledGovernancePropertyValue $Review "reversibleExecutionCount")
        humanDecisionCount = [int](Get-ScheduledGovernancePropertyValue $Review "humanDecisionCount")
        governedExceptionCount = [int](Get-ScheduledGovernancePropertyValue $Review "governedExceptionCount")
        businessWorkItemActionableCount = [int](Get-ScheduledGovernancePropertyValue $Review "businessWorkItemActionableCount")
        resolvedProjectCount = (Get-ScheduledGovernanceArrayProperty $Review "resolvedProjectIds").Count
    }
}

function Get-ScheduledGovernanceReceiptEvidence {
    param([AllowNull()][object]$Receipt)

    if ($null -eq $Receipt) {
        return [ordered]@{
            present = $false
            runExists = $false
            latestBatchExecuted = $false
            latestBatchStatus = ""
        }
    }

    $latestBatch = Get-ScheduledGovernancePropertyValue $Receipt "latestBatch"
    [ordered]@{
        present = $true
        runExists = [bool](Get-ScheduledGovernancePropertyValue $Receipt "runExists")
        status = [string](Get-ScheduledGovernancePropertyValue $Receipt "status")
        latestBatchReceived = [bool](Get-ScheduledGovernancePropertyValue $Receipt "latestBatchReceived")
        latestBatchExecuted = [bool](Get-ScheduledGovernancePropertyValue $latestBatch "executed")
        latestBatchStatus = [string](Get-ScheduledGovernancePropertyValue $latestBatch "status")
        latestBatchRequiresReReview = [bool](Get-ScheduledGovernancePropertyValue $latestBatch "requiresReReview")
        isReplay = [bool](Get-ScheduledGovernancePropertyValue $Receipt "isReplay")
        initialGovernanceActionable = [int](Get-ScheduledGovernancePropertyValue $Receipt "initialGovernanceActionable")
        finalGovernanceActionable = [int](Get-ScheduledGovernancePropertyValue $Receipt "finalGovernanceActionable")
        finalConvergenceStatus = [string](Get-ScheduledGovernancePropertyValue $Receipt "finalConvergenceStatus")
        finalSnapshotPresent = -not [string]::IsNullOrWhiteSpace([string](Get-ScheduledGovernancePropertyValue $Receipt "finalSnapshotToken"))
        requestIdentityHashPresent = [string](Get-ScheduledGovernancePropertyValue $Receipt "requestIdentityHash") -match "^[a-f0-9]{64}$"
        auditCount = (Get-ScheduledGovernanceArrayProperty $Receipt "auditIds").Count
    }
}

function Read-ScheduledGovernanceProtectedInvariantEvidence {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return [ordered]@{
            status = "required-missing"
            unchanged = $false
            source = "external-read-only-manifest"
            displayNameUnchanged = $false
            businessWorkItemsUnchanged = $false
            businessWorkItemCount = 0
        }
    }

    try {
        $manifest = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
        $displayName = Get-ScheduledGovernancePropertyValue $manifest "displayName"
        $displayNameBefore = [string](Get-ScheduledGovernancePropertyValue $displayName "before")
        $displayNameAfter = [string](Get-ScheduledGovernancePropertyValue $displayName "after")
        if ([string]::IsNullOrWhiteSpace($displayNameBefore) -or $displayNameBefore -cne $displayNameAfter) {
            throw "displayName before/after values are missing or changed."
        }

        $workItems = @(ConvertTo-ScheduledGovernanceArray (Get-ScheduledGovernancePropertyValue $manifest "businessWorkItems"))
        $workItemsUnchanged = $true
        foreach ($item in $workItems) {
            if ([string]::IsNullOrWhiteSpace([string](Get-ScheduledGovernancePropertyValue $item "id"))) {
                throw "businessWorkItems entry has no id."
            }
            $before = Get-ScheduledGovernancePropertyValue $item "before"
            $after = Get-ScheduledGovernancePropertyValue $item "after"
            foreach ($field in @("status", "isArchived")) {
                if ([string](Get-ScheduledGovernancePropertyValue $before $field) -cne [string](Get-ScheduledGovernancePropertyValue $after $field)) {
                    $workItemsUnchanged = $false
                }
            }
        }

        return [ordered]@{
            status = if ($workItemsUnchanged) { "verified" } else { "changed" }
            unchanged = $workItemsUnchanged
            source = "external-read-only-manifest"
            displayNameUnchanged = $true
            businessWorkItemsUnchanged = $workItemsUnchanged
            businessWorkItemCount = $workItems.Count
        }
    }
    catch {
        return [ordered]@{
            status = "invalid"
            unchanged = $false
            source = "external-read-only-manifest"
            displayNameUnchanged = $false
            businessWorkItemsUnchanged = $false
            businessWorkItemCount = 0
        }
    }
}

function Get-ScheduledGovernanceBaseEvidence {
    param(
        [Parameter(Mandatory)][AllowNull()][object]$Spec,
        [Parameter(Mandatory)][string]$ObservedMode,
        [Parameter(Mandatory)][bool]$TokenPresent
    )

    $catalog = Get-ScheduledGovernancePropertyValue $Spec "catalog"
    $contract = Get-ScheduledGovernancePropertyValue $Spec "contract"
    [ordered]@{
        observedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        mode = $ObservedMode
        surface = "/mcp-automation"
        oauthGate = [ordered]@{
            status = if ($TokenPresent) { "authorized" } else { "OAuthRequired" }
            tokenPresent = $TokenPresent
            tokenValueOmitted = $true
            tokenEnvironmentVariable = $TokenEnvironmentVariable
            missingTokenMode = "discovery-contract-readiness-only"
            requiredScope = [string](Get-ScheduledGovernancePropertyValue (Get-ScheduledGovernancePropertyValue $Spec "oauth") "requiredScope")
        }
        catalog = [ordered]@{
            expectedToolCount = [int](Get-ScheduledGovernancePropertyValue $catalog "toolCount")
            expectedToolNames = @(Get-ScheduledGovernanceArrayProperty $catalog "toolNames")
            observedToolCount = 0
            observedToolNames = @()
            forbiddenAbsent = $false
        }
        contract = [ordered]@{
            expectedToolContractVersion = [string](Get-ScheduledGovernancePropertyValue $contract "toolContractVersion")
            expectedSchemaHash = [string](Get-ScheduledGovernancePropertyValue $contract "schemaHash")
            expectedPublishedCatalogVersion = [string](Get-ScheduledGovernancePropertyValue $catalog "publishedCatalogVersion")
            expectedPublishedCatalogHash = [string](Get-ScheduledGovernancePropertyValue $catalog "publishedCatalogHash")
            observed = $null
        }
        runtimeIdentity = [ordered]@{
            expectedServerName = [string](Get-ScheduledGovernancePropertyValue (Get-ScheduledGovernancePropertyValue $catalog "runtimeIdentity") "serverName")
            expectedServerVersion = [string](Get-ScheduledGovernancePropertyValue (Get-ScheduledGovernancePropertyValue $catalog "runtimeIdentity") "serverVersion")
            observedServerName = ""
            observedServerVersion = ""
            matched = $false
        }
        governanceRunId = $null
        decision = $null
        coverage = $null
        receipt = [ordered]@{}
        replay = [ordered]@{
            exercised = $false
            exact = $false
            outcomeRecovery = "not-exercised"
        }
        protectedInvariants = [ordered]@{
            status = "not-run"
            unchanged = $false
            source = "external-read-only-manifest"
        }
        outcome = [ordered]@{
            status = "not-run"
            accepted = $false
            executorCalls = 0
            syntheticFixtureCreated = $false
        }
        errors = @()
    }
}

function Write-ScheduledGovernanceEvidence {
    param(
        [Parameter(Mandatory)][object]$Evidence,
        [Parameter(Mandatory)][int]$ExitCode
    )

    $json = $Evidence | ConvertTo-Json -Depth 30 -Compress
    if (-not [string]::IsNullOrWhiteSpace($EvidencePath)) {
        Set-Content -LiteralPath $EvidencePath -Value $json -Encoding UTF8 -NoNewline
    }
    Write-Output $json
    exit $ExitCode
}

$exitCode = 0
$specValidation = $null
$evidence = $null

try {
    if ([string]::IsNullOrWhiteSpace($SpecPath)) {
        $SpecPath = Get-ScheduledGovernanceAutomationDefaultSpecPath
    }
    if ([string]::IsNullOrWhiteSpace($Endpoint)) {
        $Endpoint = Get-ScheduledGovernanceEnvironmentValue -Name "CONTEXTHUB_MCP_AUTOMATION_ENDPOINT"
        if ([string]::IsNullOrWhiteSpace($Endpoint)) {
            $Endpoint = "http://localhost:8095/mcp"
        }
    }
    if ([string]::IsNullOrWhiteSpace($OAuthResource)) {
        $OAuthResource = Get-ScheduledGovernanceEnvironmentValue -Name "CHATGPT_GATEWAY_OAUTH_SCHEDULED_GOVERNANCE_RESOURCE"
        if ([string]::IsNullOrWhiteSpace($OAuthResource)) {
            $OAuthResource = "https://context-hub.example.com/mcp-automation"
        }
    }
    if ($Mode -eq "A1" -and $A1Environment -eq "Isolated" -and -not $AllowIsolatedSyntheticFixtures) {
        throw "A1 isolated mode requires explicit AllowIsolatedSyntheticFixtures; the runner never creates fixtures."
    }
    if ($Mode -eq "A1" -and $A1Environment -eq "Production" -and $AllowIsolatedSyntheticFixtures) {
        throw "Synthetic fixtures are forbidden in Production A1 mode."
    }

    $specValidation = Test-ScheduledGovernanceAutomationSpec -Path $SpecPath
    $token = Get-ScheduledGovernanceEnvironmentValue -Name $TokenEnvironmentVariable
    $evidence = Get-ScheduledGovernanceBaseEvidence `
        -Spec $specValidation.Spec `
        -ObservedMode $Mode `
        -TokenPresent (-not [string]::IsNullOrWhiteSpace($token))
    if (-not $specValidation.Valid) {
        $evidence.outcome.status = "offline-spec-invalid"
        $evidence.errors = @($specValidation.Errors)
        $exitCode = 1
    }
    else {
        $endpointUri = [Uri]$Endpoint
        $origin = "$($endpointUri.Scheme)://$($endpointUri.Authority)"
        $metadataUri = "$origin/.well-known/oauth-protected-resource/mcp-automation"
        $metadataResponse = Invoke-ScheduledGovernanceHttp -Uri $metadataUri -Method "Get"
        $metadataEvidence = [ordered]@{
            uri = $metadataUri
            statusCode = $metadataResponse.StatusCode
            resourceMatches = $false
            requiredScopePresent = $false
        }
        if (-not $metadataResponse.TransportError -and $metadataResponse.StatusCode -eq 200) {
            try {
                $metadata = $metadataResponse.Content | ConvertFrom-Json
                $metadataEvidence.resourceMatches = [string](Get-ScheduledGovernancePropertyValue $metadata "resource") -ceq $OAuthResource
                $metadataEvidence.requiredScopePresent =
                    (Get-ScheduledGovernanceArrayProperty $metadata "scopes_supported") -contains "governance:scheduled"
            }
            catch {
                $metadataEvidence.parseError = $true
            }
        }
        $evidence.oauthGate.discovery = $metadataEvidence
        if ($metadataResponse.TransportError -or $metadataResponse.StatusCode -ne 200 -or
            -not $metadataEvidence.resourceMatches -or -not $metadataEvidence.requiredScopePresent) {
            $evidence.errors += "OAuth protected-resource discovery did not satisfy the automation resource contract."
            if ($null -eq $exitCode -or $exitCode -eq 0) { $exitCode = 1 }
        }

        if ([string]::IsNullOrWhiteSpace($token)) {
            if ($RequireAuthorizationToken) {
                throw "Authorization token is required for this invocation."
            }
            $evidence.outcome.status = if ($exitCode -eq 0) { "OAuthRequired" } else { "discovery-not-ready" }
            $evidence.outcome.accepted = $false
        }
        elseif ($exitCode -eq 0) {
            $initializeJson = Invoke-ScheduledGovernanceMcpJson `
                -Token $token `
                -Method "initialize" `
                -Params @{
                    protocolVersion = "2026-07-28"
                    capabilities = @{}
                    clientInfo = @{ name = "contexthub-scheduled-governance-acceptance"; version = "1.0" }
                } `
                -RequestId 1
            Assert-ScheduledGovernanceRpcSuccess -Json $initializeJson -Operation "initialize"
            $initializeResult = Get-ScheduledGovernancePropertyValue $initializeJson "result"
            $serverInfo = Get-ScheduledGovernancePropertyValue $initializeResult "serverInfo"
            $protocolVersion = [string](Get-ScheduledGovernancePropertyValue $initializeResult "protocolVersion")
            $evidence.runtimeIdentity.observedServerName = [string](Get-ScheduledGovernancePropertyValue $serverInfo "name")
            $evidence.runtimeIdentity.observedServerVersion = [string](Get-ScheduledGovernancePropertyValue $serverInfo "version")
            $evidence.runtimeIdentity.protocolVersion = $protocolVersion
            $evidence.runtimeIdentity.matched =
                $evidence.runtimeIdentity.observedServerName -ceq $evidence.runtimeIdentity.expectedServerName -and
                $evidence.runtimeIdentity.observedServerVersion -ceq $evidence.runtimeIdentity.expectedServerVersion -and
                $protocolVersion -ceq "2026-07-28"
            if (-not $evidence.runtimeIdentity.matched) {
                throw "Automation runtime identity did not match the canonical catalog identity."
            }

            $toolsJson = Invoke-ScheduledGovernanceMcpJson `
                -Token $token `
                -Method "tools/list" `
                -Params @{ _meta = Get-ScheduledGovernanceMcpMeta } `
                -RequestId 2
            Assert-ScheduledGovernanceRpcSuccess -Json $toolsJson -Operation "tools/list"
            $toolsResult = Get-ScheduledGovernancePropertyValue $toolsJson "result"
            $publishedTools = @(ConvertTo-ScheduledGovernanceArray (Get-ScheduledGovernancePropertyValue $toolsResult "tools"))
            $observedNames = @($publishedTools | ForEach-Object { [string](Get-ScheduledGovernancePropertyValue $_ "name") })
            $evidence.catalog.observedToolCount = $observedNames.Count
            $evidence.catalog.observedToolNames = $observedNames
            if ($observedNames.Count -ne 4 -or
                -not (Test-ScheduledGovernanceSetEqual $observedNames (Get-ScheduledGovernanceExpectedToolName))) {
                throw "Automation tools/list did not expose exactly the four canonical tools."
            }

            foreach ($tool in $publishedTools) {
                $annotations = Get-ScheduledGovernancePropertyValue $tool "annotations"
                foreach ($annotation in @("readOnlyHint", "destructiveHint", "idempotentHint", "openWorldHint")) {
                    if ($null -eq (Get-ScheduledGovernancePropertyValue $annotations $annotation)) {
                        throw "Automation tool annotations omitted $annotation."
                    }
                }
                if ((Get-ScheduledGovernancePropertyValue $annotations "destructiveHint") -ne $false -or
                    (Get-ScheduledGovernancePropertyValue $annotations "idempotentHint") -ne $true -or
                    (Get-ScheduledGovernancePropertyValue $annotations "openWorldHint") -ne $false) {
                    throw "Automation tool annotations violate the least-privilege contract."
                }
                $toolJson = $tool | ConvertTo-Json -Depth 30 -Compress
                foreach ($forbidden in Get-ScheduledGovernanceForbiddenToken) {
                    if ($toolJson.IndexOf($forbidden, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                        throw "Automation catalog exposed a forbidden authority token."
                    }
                }
            }
            $evidence.catalog.forbiddenAbsent = $true

            $forbiddenProbeResults = @()
            $requestId = 10
            foreach ($forbiddenTool in Get-ScheduledGovernanceForbiddenToolName) {
                try {
                    $forbiddenJson = Invoke-ScheduledGovernanceTool `
                        -Token $token `
                        -ToolName $forbiddenTool `
                        -Arguments @{} `
                        -RequestId $requestId
                    $rejected = $null -ne (Get-ScheduledGovernancePropertyValue $forbiddenJson "error") -or
                        (Get-ScheduledGovernancePropertyValue (Get-ScheduledGovernancePropertyValue $forbiddenJson "result") "isError") -eq $true
                    if (-not $rejected) {
                        throw "Forbidden tool invocation was accepted."
                    }
                    $forbiddenProbeResults += [ordered]@{ name = $forbiddenTool; rejected = $true }
                }
                catch {
                    if ($_.Exception.Message -notmatch "^MCP HTTP status (400|401|403|404|405|422)") {
                        throw
                    }
                    $forbiddenProbeResults += [ordered]@{ name = $forbiddenTool; rejected = $true }
                }
                $requestId++
            }
            $evidence.forbiddenProbes = $forbiddenProbeResults

            $contractJson = Invoke-ScheduledGovernanceTool `
                -Token $token `
                -ToolName "scheduled_governance_contract_get" `
                -Arguments @{} `
                -RequestId 20
            $contractData = Get-ScheduledGovernanceToolData -Json $contractJson -Operation "scheduled_governance_contract_get"
            $expectedContract = Get-ScheduledGovernancePropertyValue $specValidation.Spec "contract"
            $expectedCatalog = Get-ScheduledGovernancePropertyValue $specValidation.Spec "catalog"
            $observedContract = [ordered]@{
                reviewToolName = [string](Get-ScheduledGovernancePropertyValue $contractData "reviewToolName")
                executeToolName = [string](Get-ScheduledGovernancePropertyValue $contractData "executeToolName")
                receiptToolName = [string](Get-ScheduledGovernancePropertyValue $contractData "receiptToolName")
                toolContractVersion = [string](Get-ScheduledGovernancePropertyValue $contractData "toolContractVersion")
                schemaHash = [string](Get-ScheduledGovernancePropertyValue $contractData "schemaHash")
                publishedCatalogVersion = [string](Get-ScheduledGovernancePropertyValue $contractData "publishedCatalogVersion")
                irreversibleRetentionOwner = [string](Get-ScheduledGovernancePropertyValue $contractData "irreversibleRetentionOwner")
            }
            foreach ($pair in @(
                @("reviewToolName", "scheduled_governance_review"),
                @("executeToolName", "scheduled_governance_execute"),
                @("receiptToolName", "scheduled_governance_run_get"),
                @("toolContractVersion", (Get-ScheduledGovernancePropertyValue $expectedContract "toolContractVersion")),
                @("schemaHash", (Get-ScheduledGovernancePropertyValue $expectedContract "schemaHash")),
                @("publishedCatalogVersion", (Get-ScheduledGovernancePropertyValue $expectedCatalog "publishedCatalogVersion")),
                @("irreversibleRetentionOwner", "ContextHubInternalRetentionWorker"))) {
                Assert-ScheduledGovernanceEqual $observedContract[$pair[0]] $pair[1] "Automation contract field mismatch."
            }
            $observedDecisions = @(Get-ScheduledGovernanceArrayProperty $contractData "decisions")
            if (-not (Test-ScheduledGovernanceSetEqual $observedDecisions (Get-ScheduledGovernanceExpectedDecision))) {
                throw "Automation contract decision enum changed."
            }
            $fixedActions = @(Get-ScheduledGovernanceArrayProperty $contractData "fixedReversibleActions")
            foreach ($forbiddenAction in @("MaturedDelete", "DeleteProposal", "memory_delete")) {
                if ($fixedActions -contains $forbiddenAction) {
                    throw "Automation contract exposed a forbidden irreversible action."
                }
            }
            $evidence.contract.observedDecisionCount = $observedDecisions.Count
            $evidence.contract.observedFixedReversibleActionCount = $fixedActions.Count
            $evidence.contract.observed = $observedContract

            $executeTool = @($publishedTools | Where-Object {
                    [string](Get-ScheduledGovernancePropertyValue $_ "name") -ceq "scheduled_governance_execute"
                })[0]
            $requestSchema = Get-ScheduledGovernancePropertyValue (
                Get-ScheduledGovernancePropertyValue (Get-ScheduledGovernancePropertyValue $executeTool "inputSchema") "properties") "request"
            $requestFieldNames = @($requestSchema.properties.PSObject.Properties.Name)
            if (-not (Test-ScheduledGovernanceSetEqual $requestFieldNames @(
                        "governanceRunId", "snapshotToken", "cursor", "maxMutations", "maxDurationSeconds",
                        "isReReview", "toolContractVersion", "schemaHash"))) {
                throw "Scheduled execute request schema changed."
            }
            $observedSchemaHash = Get-ScheduledGovernancePublishedSchemaHash -Tool $executeTool
            $evidence.contract.observedExecuteSchemaHash = $observedSchemaHash
            $evidence.contract.schemaHashMatches = $observedSchemaHash -ceq [string](Get-ScheduledGovernancePropertyValue $expectedContract "schemaHash")
            if (-not $evidence.contract.schemaHashMatches) {
                throw "Scheduled execute input/output schema hash did not match the published contract."
            }

            if ($Mode -eq "Readiness") {
                $evidence.outcome.status = "authorized-readiness-complete"
                $evidence.outcome.accepted = $false
            }
            else {
                $runId = "scheduled-acceptance-$([Guid]::NewGuid().ToString('N'))"
                $evidence.governanceRunId = $runId
                $reviewJson = Invoke-ScheduledGovernanceTool `
                    -Token $token `
                    -ToolName "scheduled_governance_review" `
                    -Arguments @{ request = @{ governanceRunId = $runId; isReReview = $false } } `
                    -RequestId 30
                $review = Get-ScheduledGovernanceToolData -Json $reviewJson -Operation "scheduled_governance_review"
                $initialReviewEvidence = Get-ScheduledGovernanceReviewEvidence `
                    -Review $review -ExpectedRunId $runId -ExpectedReReview $false
                $evidence.decision = $initialReviewEvidence.decision
                $evidence.coverage = $initialReviewEvidence

                $initialReceiptJson = Invoke-ScheduledGovernanceTool `
                    -Token $token `
                    -ToolName "scheduled_governance_run_get" `
                    -Arguments @{ governanceRunId = $runId } `
                    -RequestId 31
                $initialReceipt = Get-ScheduledGovernanceToolData -Json $initialReceiptJson -Operation "scheduled_governance_run_get"
                $evidence.receipt.initial = Get-ScheduledGovernanceReceiptEvidence $initialReceipt
                if (-not $evidence.receipt.initial.present -or -not $evidence.receipt.initial.runExists) {
                    throw "Initial scheduled governance review receipt was not readable."
                }

                if ($Mode -eq "A0") {
                    $evidence.outcome.executorCalls = 0
                    if ($initialReviewEvidence.decision -ne "NoOpConverged") {
                        $evidence.outcome.status = "server-decision-requires-a1-or-human-stop"
                        $exitCode = 2
                    }
                    else {
                        $finalReviewJson = Invoke-ScheduledGovernanceTool `
                            -Token $token `
                            -ToolName "scheduled_governance_review" `
                            -Arguments @{ request = @{ governanceRunId = $runId; isReReview = $true } } `
                            -RequestId 32
                        $finalReview = Get-ScheduledGovernanceToolData -Json $finalReviewJson -Operation "scheduled_governance_review"
                        $finalReviewEvidence = Get-ScheduledGovernanceReviewEvidence `
                            -Review $finalReview -ExpectedRunId $runId -ExpectedReReview $true
                        $evidence.coverage.final = $finalReviewEvidence
                        $evidence.decision = $finalReviewEvidence.decision

                        $finalReceiptJson = Invoke-ScheduledGovernanceTool `
                            -Token $token `
                            -ToolName "scheduled_governance_run_get" `
                            -Arguments @{ governanceRunId = $runId } `
                            -RequestId 33
                        $finalReceipt = Get-ScheduledGovernanceToolData -Json $finalReceiptJson -Operation "scheduled_governance_run_get"
                        $evidence.receipt.final = Get-ScheduledGovernanceReceiptEvidence $finalReceipt
                        if (-not $evidence.receipt.final.present -or -not $evidence.receipt.final.runExists) {
                            throw "Final A0 scheduled governance receipt was not readable."
                        }
                        $evidence.protectedInvariants = Read-ScheduledGovernanceProtectedInvariantEvidence -Path $ProtectedInvariantEvidencePath
                        $evidence.outcome.status = if (
                            $finalReviewEvidence.decision -eq "NoOpConverged" -and
                            $evidence.protectedInvariants.unchanged) {
                            "A0-accepted"
                        }
                        else {
                            "protected-readback-required-or-final-review-not-converged"
                        }
                        $evidence.outcome.accepted = $evidence.outcome.status -eq "A0-accepted"
                        if (-not $evidence.outcome.accepted) { $exitCode = 2 }
                    }
                }
                else {
                    $evidence.outcome.environment = $A1Environment
                    $evidence.outcome.productionNaturalCandidateOnly = $A1Environment -eq "Production"
                    $evidence.outcome.isolatedSyntheticFixtureOptIn = $A1Environment -eq "Isolated" -and $AllowIsolatedSyntheticFixtures
                    if ($initialReviewEvidence.decision -ne "ReversibleExecutionRequired") {
                        $evidence.outcome.status = "server-decision-stops-a1-without-execute"
                        $exitCode = 2
                    }
                    else {
                        $executeArguments = @{
                            request = @{
                                governanceRunId = $runId
                                snapshotToken = [string](Get-ScheduledGovernancePropertyValue $review "snapshotToken")
                                maxMutations = 100
                                maxDurationSeconds = 120
                                isReReview = $false
                                toolContractVersion = [string](Get-ScheduledGovernancePropertyValue $expectedContract "toolContractVersion")
                                schemaHash = [string](Get-ScheduledGovernancePropertyValue $expectedContract "schemaHash")
                            }
                        }
                        $firstExecutionJson = $null
                        $firstExecution = $null
                        $unknownOutcome = $false
                        try {
                            $evidence.outcome.executorCalls++
                            $firstExecutionJson = Invoke-ScheduledGovernanceTool `
                                -Token $token `
                                -ToolName "scheduled_governance_execute" `
                                -Arguments $executeArguments `
                                -RequestId 40
                            if ($null -ne (Get-ScheduledGovernancePropertyValue $firstExecutionJson "error") -or
                                (Get-ScheduledGovernancePropertyValue (Get-ScheduledGovernancePropertyValue $firstExecutionJson "result") "isError") -eq $true) {
                                $evidence.outcome.status = "execute-rejected"
                                $exitCode = 2
                            }
                            else {
                                $firstExecution = Get-ScheduledGovernanceToolData `
                                    -Json $firstExecutionJson -Operation "scheduled_governance_execute"
                            }
                        }
                        catch {
                            $unknownOutcome = $true
                            $evidence.replay.outcomeRecovery = "receipt-readback-required"
                        }

                        if ($unknownOutcome) {
                            $recoveryReceiptJson = Invoke-ScheduledGovernanceTool `
                                -Token $token `
                                -ToolName "scheduled_governance_run_get" `
                                -Arguments @{ governanceRunId = $runId } `
                                -RequestId 41
                            $recoveryReceipt = Get-ScheduledGovernanceToolData `
                                -Json $recoveryReceiptJson -Operation "scheduled_governance_run_get"
                            $recoveryEvidence = Get-ScheduledGovernanceReceiptEvidence $recoveryReceipt
                            $evidence.receipt.afterUnknownOutcome = $recoveryEvidence
                            $latestBatch = Get-ScheduledGovernancePropertyValue $recoveryReceipt "latestBatch"
                            if ([bool](Get-ScheduledGovernancePropertyValue $latestBatch "executed")) {
                                $unknownOutcome = $false
                                $evidence.replay.outcomeRecovery = "recovered-from-receipt-no-retry"
                                $firstExecution = $null
                            }
                            elseif ([string](Get-ScheduledGovernancePropertyValue $latestBatch "status") -ne "Running" -and
                                [bool](Get-ScheduledGovernancePropertyValue $latestBatch "received")) {
                                $evidence.outcome.executorCalls++
                                $firstExecutionJson = Invoke-ScheduledGovernanceTool `
                                    -Token $token `
                                    -ToolName "scheduled_governance_execute" `
                                    -Arguments $executeArguments `
                                    -RequestId 42
                                $firstExecution = Get-ScheduledGovernanceToolData `
                                    -Json $firstExecutionJson -Operation "scheduled_governance_execute-replay-after-recovery"
                                $evidence.replay.outcomeRecovery = "exact-replay-after-receipt-confirmed-no-execution"
                            }
                            else {
                                $evidence.outcome.status = "outcome-unknown-no-retry"
                                $exitCode = 2
                            }
                        }

                        $replayExecution = $null
                        if ($null -ne $firstExecution -and
                            [bool](Get-ScheduledGovernancePropertyValue $firstExecution "succeeded")) {
                            $evidence.outcome.executorCalls++
                            $replayJson = Invoke-ScheduledGovernanceTool `
                                -Token $token `
                                -ToolName "scheduled_governance_execute" `
                                -Arguments $executeArguments `
                                -RequestId 43
                            $replayExecution = Get-ScheduledGovernanceToolData `
                                -Json $replayJson -Operation "scheduled_governance_execute-exact-replay"
                            $auditMatches = Test-ScheduledGovernanceIdsEqual `
                                (Get-ScheduledGovernancePropertyValue $firstExecution "auditIds") `
                                (Get-ScheduledGovernancePropertyValue $replayExecution "auditIds")
                            $appliedMatches = [int](Get-ScheduledGovernancePropertyValue $firstExecution "appliedCount") -eq
                                [int](Get-ScheduledGovernancePropertyValue $replayExecution "appliedCount")
                            $evidence.replay.exercised = $true
                            $evidence.replay.exact =
                                [bool](Get-ScheduledGovernancePropertyValue $replayExecution "isReplay") -and
                                $auditMatches -and $appliedMatches
                            if (-not $evidence.replay.exact) {
                                throw "Exact execution replay did not preserve result and audit identity."
                            }
                        }

                        if ($null -ne $firstExecution -and $exitCode -eq 0) {
                            $finalReviewJson = Invoke-ScheduledGovernanceTool `
                                -Token $token `
                                -ToolName "scheduled_governance_review" `
                                -Arguments @{ request = @{ governanceRunId = $runId; isReReview = $true } } `
                                -RequestId 44
                            $finalReview = Get-ScheduledGovernanceToolData `
                                -Json $finalReviewJson -Operation "scheduled_governance_review-after-execute"
                            $finalReviewEvidence = Get-ScheduledGovernanceReviewEvidence `
                                -Review $finalReview -ExpectedRunId $runId -ExpectedReReview $true
                            $evidence.coverage.final = $finalReviewEvidence
                            $evidence.decision = $finalReviewEvidence.decision
                            $finalReceiptJson = Invoke-ScheduledGovernanceTool `
                                -Token $token `
                                -ToolName "scheduled_governance_run_get" `
                                -Arguments @{ governanceRunId = $runId } `
                                -RequestId 45
                            $finalReceipt = Get-ScheduledGovernanceToolData `
                                -Json $finalReceiptJson -Operation "scheduled_governance_run_get-after-execute"
                            $evidence.receipt.final = Get-ScheduledGovernanceReceiptEvidence $finalReceipt
                            if (-not $evidence.receipt.final.present -or -not $evidence.receipt.final.runExists) {
                                throw "Final A1 scheduled governance receipt was not readable."
                            }
                            $evidence.protectedInvariants = Read-ScheduledGovernanceProtectedInvariantEvidence -Path $ProtectedInvariantEvidencePath
                            $finalConverged = $finalReviewEvidence.decision -eq "NoOpConverged" -or
                                ($finalReviewEvidence.decision -eq "HumanDecisionOnly" -and
                                    ($finalReviewEvidence.governedExceptionCount -gt 0 -or $finalReviewEvidence.humanDecisionCount -gt 0))
                            $evidence.outcome.status = if (
                                $evidence.replay.exact -and $finalConverged -and $evidence.protectedInvariants.unchanged) {
                                "A1-accepted"
                            }
                            else {
                                "final-review-or-protected-readback-incomplete"
                            }
                            $evidence.outcome.accepted = $evidence.outcome.status -eq "A1-accepted"
                            if (-not $evidence.outcome.accepted) { $exitCode = 2 }
                        }
                        elseif ($exitCode -eq 0) {
                            $evidence.outcome.status = "execute-outcome-not-accepted"
                            $exitCode = 2
                        }
                    }
                }
            }
        }
    }
}
catch {
    if ($null -eq $evidence) {
        $evidence = [ordered]@{
            observedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
            mode = $Mode
            surface = "/mcp-automation"
            oauthGate = [ordered]@{ status = "error"; tokenValueOmitted = $true }
            catalog = [ordered]@{}
            contract = [ordered]@{}
            runtimeIdentity = [ordered]@{}
            governanceRunId = $null
            decision = $null
            coverage = $null
            receipt = [ordered]@{}
            replay = [ordered]@{}
            protectedInvariants = [ordered]@{}
            outcome = [ordered]@{ status = "error"; accepted = $false }
            errors = @()
        }
    }
    $evidence.outcome.status = "error"
    $evidence.outcome.accepted = $false
    $evidence.errors += $_.Exception.Message
    $exitCode = 1
}

Write-ScheduledGovernanceEvidence -Evidence $evidence -ExitCode $exitCode
