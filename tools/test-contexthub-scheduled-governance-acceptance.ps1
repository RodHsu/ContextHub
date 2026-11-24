[CmdletBinding()]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute("PSReviewUnusedParameter", "TimeoutSec", Justification = "Consumed by the nested HTTP helper through script scope.")]
param(
    [ValidateSet("Readiness", "A0", "A1")]
    [string]$Mode = "A0",
    [string]$Endpoint = "",
    [string]$OAuthResource = "",
    [string]$TokenEnvironmentVariable = "CONTEXTHUB_MCP_AUTOMATION_TOKEN",
    [string]$SpecPath = "",
    [string]$ProtectedInvariantEvidencePath = "",
    [string]$ProtectedInvariantReadbackRequestPath = "",
    [ValidateRange(1, 120)]
    [int]$ProtectedInvariantReadbackTimeoutSec = 30,
    [string]$EvidencePath = "",
    [string]$A1CheckpointPath = "",
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
    if (-not [string]::IsNullOrEmpty($Body)) {
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

function Write-ScheduledGovernanceCanonicalJson {
    param([System.Text.Json.Utf8JsonWriter]$Writer, [System.Text.Json.JsonElement]$Value)

    # Match PublishedToolSchemaHash, including ordinal key order and singleton arrays.
    switch ($Value.ValueKind) {
        'Object' {
            $Writer.WriteStartObject()
            [string[]]$names = @(foreach ($property in $Value.EnumerateObject()) { $property.Name })
            [Array]::Sort($names, [StringComparer]::Ordinal)
            foreach ($name in $names) {
                $Writer.WritePropertyName($name)
                Write-ScheduledGovernanceCanonicalJson -Writer $Writer -Value $Value.GetProperty($name)
            }
            $Writer.WriteEndObject()
        }
        'Array' {
            $Writer.WriteStartArray()
            foreach ($item in $Value.EnumerateArray()) {
                Write-ScheduledGovernanceCanonicalJson -Writer $Writer -Value $item
            }
            $Writer.WriteEndArray()
        }
        default { $Value.WriteTo($Writer) }
    }
}

function Get-ScheduledGovernancePublishedSchemaHash {
    param([Parameter(Mandatory)][object]$Tool)

    $document = [System.Text.Json.JsonDocument]::Parse((ConvertTo-Json -InputObject $Tool -Depth 100 -Compress))
    $buffer = [System.IO.MemoryStream]::new()
    $writer = [System.Text.Json.Utf8JsonWriter]::new($buffer)
    try {
        $writer.WriteStartObject()
        $writer.WriteString('name', $document.RootElement.GetProperty('name').GetString())
        foreach ($schema in @('inputSchema', 'outputSchema')) {
            $writer.WritePropertyName($schema)
            Write-ScheduledGovernanceCanonicalJson -Writer $writer -Value $document.RootElement.GetProperty($schema)
        }
        $writer.WriteEndObject()
        $writer.Flush()
        return Get-ScheduledGovernanceSha256Hex -Text ([Text.Encoding]::UTF8.GetString($buffer.ToArray()))
    }
    finally {
        $writer.Dispose()
        $buffer.Dispose()
        $document.Dispose()
    }
}

function Assert-ScheduledGovernancePublishedToolAuthority {
    param([Parameter(Mandatory)][object]$Tool)

    # The output schema reports facts such as resolved project IDs and restricted-action errors.
    $invocation = @{
        description = Get-ScheduledGovernancePropertyValue $Tool "description"
        inputSchema = Get-ScheduledGovernancePropertyValue $Tool "inputSchema"
    } | ConvertTo-Json -Depth 100 -Compress
    foreach ($forbidden in Get-ScheduledGovernanceForbiddenToken) {
        if ($invocation.IndexOf($forbidden, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "Automation catalog exposed a forbidden authority token in a tool input or description."
        }
    }
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

function Test-ScheduledGovernanceExecutionResult {
    param(
        [AllowNull()][object]$Execution,
        [Parameter(Mandatory)][string]$ExpectedRunId,
        [Parameter(Mandatory)][string]$ExpectedSnapshot,
        [Parameter(Mandatory)][bool]$ExpectedReplay
    )

    $succeeded = Get-ScheduledGovernancePropertyValue $Execution "succeeded"
    $isReplay = Get-ScheduledGovernancePropertyValue $Execution "isReplay"
    $stoppedReason = Get-ScheduledGovernancePropertyValue $Execution "stoppedReason"
    return $succeeded -is [bool] -and $succeeded -and
        $isReplay -is [bool] -and $isReplay -eq $ExpectedReplay -and
        $stoppedReason -is [string] -and -not [string]::IsNullOrWhiteSpace($stoppedReason) -and
        $stoppedReason -cne "UnknownResult" -and
        (Get-ScheduledGovernancePropertyValue $Execution "governanceRunId") -ceq $ExpectedRunId -and
        (Get-ScheduledGovernancePropertyValue $Execution "snapshotToken") -ceq $ExpectedSnapshot
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
    $coverageComplete = Get-ScheduledGovernancePropertyValue $Review "coverageComplete"
    if ($coverageComplete -isnot [bool]) { throw "Review did not return a boolean coverageComplete." }
    $authorized = Get-ScheduledGovernancePropertyValue $invariant "authorizedDurableMemoryCount"
    $covered = Get-ScheduledGovernancePropertyValue $invariant "coveredDurableMemoryCount"
    $scanned = Get-ScheduledGovernancePropertyValue $invariant "scannedDurableMemoryCount"
    $total = Get-ScheduledGovernancePropertyValue $invariant "totalDurableMemoryCount"
    $shared = Get-ScheduledGovernancePropertyValue $invariant "sharedScopeOccurrences"
    $user = Get-ScheduledGovernancePropertyValue $invariant "userScopeOccurrences"
    $separateUser = Get-ScheduledGovernancePropertyValue $invariant "userScopeHandledSeparately"
    $satisfied = Get-ScheduledGovernancePropertyValue $invariant "satisfied"
    if ($coverageComplete) {
        $invalidCount = @($authorized, $covered, $scanned, $total, $shared, $user | Where-Object {
                ($_ -isnot [int] -and $_ -isnot [long]) -or $_ -lt 0
            })
        if ($invalidCount.Count -gt 0 -or $authorized -ne $covered -or $covered -ne $scanned -or $scanned -ne $total -or
            $shared -ne 1 -or $user -ne 0 -or $separateUser -isnot [bool] -or -not $separateUser -or
            $satisfied -isnot [bool] -or -not $satisfied) {
            throw "Complete review did not prove the authorized durable-memory count and scope invariant."
        }
    }

    $humanDecisionCount = Get-ScheduledGovernancePropertyValue $Review "humanDecisionCount"
    $governedExceptionCount = Get-ScheduledGovernancePropertyValue $Review "governedExceptionCount"
    $exceptionDelta = Get-ScheduledGovernancePropertyValue $Review "exceptionDelta"
    $newExceptions = Get-ScheduledGovernancePropertyValue $exceptionDelta "new"
    $escalatedExceptions = Get-ScheduledGovernancePropertyValue $exceptionDelta "escalated"
    $decisionCounts = @($humanDecisionCount, $governedExceptionCount)
    if ($null -ne $exceptionDelta) {
        $decisionCounts += @($newExceptions, $escalatedExceptions)
    }
    if (@($decisionCounts | Where-Object { ($_ -isnot [int] -and $_ -isnot [long]) -or $_ -lt 0 }).Count -gt 0) {
        throw "Review returned an invalid human-decision or exception count."
    }
    $humanDecisionEvidence = if ($null -eq $exceptionDelta) {
        $humanDecisionCount -gt 0 -or $governedExceptionCount -gt 0
    }
    else {
        $newExceptions -gt 0 -or $escalatedExceptions -gt 0
    }

    [ordered]@{
        decision = $decision
        snapshotPresent = -not [string]::IsNullOrWhiteSpace([string](Get-ScheduledGovernancePropertyValue $Review "snapshotToken"))
        coverageComplete = $coverageComplete
        authorized = $authorized
        covered = $covered
        scanned = $scanned
        total = $total
        sharedOccurrences = $shared
        userOccurrences = $user
        userScopeHandledSeparately = $separateUser
        countInvariantSatisfied = $satisfied
        candidateCount = [int](Get-ScheduledGovernancePropertyValue $Review "candidateCount")
        reversibleExecutionCount = [int](Get-ScheduledGovernancePropertyValue $Review "reversibleExecutionCount")
        humanDecisionCount = $humanDecisionCount
        governedExceptionCount = $governedExceptionCount
        newExceptionCount = $newExceptions
        escalatedExceptionCount = $escalatedExceptions
        humanDecisionEvidence = $humanDecisionEvidence
        businessWorkItemActionableCount = [int](Get-ScheduledGovernancePropertyValue $Review "businessWorkItemActionableCount")
        resolvedProjectCount = @(Get-ScheduledGovernanceArrayProperty $Review "resolvedProjectIds").Count
    }
}

function Get-ScheduledGovernanceReceiptEvidence {
    param(
        [AllowNull()][object]$Receipt,
        [Parameter(Mandatory)][string]$ExpectedRunId
    )

    if ($null -eq $Receipt) {
        return [ordered]@{
            present = $false
            runExists = $false
            latestBatchExecuted = $null
            latestBatchStatus = $null
        }
    }

    if ((Get-ScheduledGovernancePropertyValue $Receipt "governanceRunId") -cne $ExpectedRunId) {
        throw "Scheduled governance receipt did not match the requested run."
    }

    [ordered]@{
        present = $true
        runExists = [bool](Get-ScheduledGovernancePropertyValue $Receipt "runExists")
        status = [string](Get-ScheduledGovernancePropertyValue $Receipt "status")
        latestBatchReceived = [bool](Get-ScheduledGovernancePropertyValue $Receipt "latestBatchReceived")
        latestBatchExecuted = $null
        latestBatchStatus = $null
        latestBatchRequiresReReview = $null
        isReplay = [bool](Get-ScheduledGovernancePropertyValue $Receipt "isReplay")
        initialGovernanceActionable = [int](Get-ScheduledGovernancePropertyValue $Receipt "initialGovernanceActionable")
        finalGovernanceActionable = [int](Get-ScheduledGovernancePropertyValue $Receipt "finalGovernanceActionable")
        finalConvergenceStatus = [string](Get-ScheduledGovernancePropertyValue $Receipt "finalConvergenceStatus")
        finalSnapshotPresent = -not [string]::IsNullOrWhiteSpace([string](Get-ScheduledGovernancePropertyValue $Receipt "finalSnapshotToken"))
        requestIdentityHashPresent = [string](Get-ScheduledGovernancePropertyValue $Receipt "requestIdentityHash") -match "^[a-f0-9]{64}$"
        auditCount = @(Get-ScheduledGovernanceArrayProperty $Receipt "auditIds").Count
    }
}

function Get-ScheduledGovernanceReviewReceiptEvidence {
    param(
        [AllowNull()][object]$Receipt,
        [Parameter(Mandatory)][string]$ExpectedRunId,
        [Parameter(Mandatory)][object]$Review,
        [Parameter(Mandatory)][System.Collections.IDictionary]$Contract,
        [switch]$RequireNoExecution
    )

    $evidence = Get-ScheduledGovernanceReceiptEvidence $Receipt -ExpectedRunId $ExpectedRunId
    $receiptId = [Guid]::Empty
    if (-not $evidence.present -or $evidence.status -cne "Completed" -or
        -not [Guid]::TryParse((Get-ScheduledGovernancePropertyValue $Receipt "receiptId"), [ref]$receiptId) -or
        $receiptId -eq [Guid]::Empty) {
        throw "Scheduled governance review did not have a completed receipt."
    }

    foreach ($field in @("runExists", "received", "terminal")) {
        $value = Get-ScheduledGovernancePropertyValue $Receipt $field
        if ($value -isnot [bool] -or -not $value) {
            throw "Scheduled governance review receipt did not confirm $field."
        }
    }
    foreach ($field in @("toolContractVersion", "schemaHash", "publishedCatalogVersion")) {
        if ((Get-ScheduledGovernancePropertyValue $Receipt $field) -cne $Contract[$field]) {
            throw "Scheduled governance review receipt did not match the verified $field."
        }
    }

    $reviewDecision = Get-ScheduledGovernancePropertyValue $Review "decision"
    $reviewCoverage = Get-ScheduledGovernancePropertyValue $Review "coverageComplete"
    $receiptCoverage = Get-ScheduledGovernancePropertyValue $Receipt "coverageComplete"
    if ($reviewCoverage -isnot [bool] -or $receiptCoverage -isnot [bool] -or
        $reviewCoverage -ne ($reviewDecision -cne "CoverageIncomplete") -or
        $receiptCoverage -ne $reviewCoverage) {
        throw "Scheduled governance review receipt did not match the review coverage."
    }

    $reviewSnapshot = Get-ScheduledGovernancePropertyValue $Review "snapshotToken"
    $receiptSnapshot = Get-ScheduledGovernancePropertyValue $Receipt "finalSnapshotToken"
    if ($reviewSnapshot -isnot [string] -or ($reviewCoverage -and [string]::IsNullOrWhiteSpace($reviewSnapshot)) -or
        $receiptSnapshot -cne $reviewSnapshot) {
        throw "Scheduled governance review receipt did not match the review snapshot."
    }

    $decision = Get-ScheduledGovernancePropertyValue $Receipt "decision"
    $outcome = Get-ScheduledGovernancePropertyValue $Receipt "outcome"
    if ($evidence.finalConvergenceStatus -cne $reviewDecision -or $decision -cne $reviewDecision -or $outcome -cne $reviewDecision) {
        throw "Scheduled governance review receipt did not match the persisted review decision."
    }

    $evidence.receiptId = $receiptId.ToString("D")
    $evidence.decision = $decision
    $evidence.outcome = $outcome
    $evidence.coverageComplete = $receiptCoverage
    if ($RequireNoExecution) {
        Assert-ScheduledGovernanceNoExecution $Receipt
        $evidence.noExecutionObserved = $true
    }
    return $evidence
}

function Get-ScheduledGovernanceBatchCounts {
    param([object]$Object, [string]$Suffix = "")

    $counts = [ordered]@{}
    foreach ($field in @("applied", "failed", "quarantined", "semanticAutoResolved")) {
        $value = Get-ScheduledGovernancePropertyValue $Object "$field$Suffix"
        if (($value -isnot [int] -and $value -isnot [long]) -or $value -lt 0) {
            throw "Scheduled governance batch evidence has an invalid $field$Suffix count."
        }
        $counts[$field] = $value
    }
    return $counts
}

function Get-ScheduledGovernanceBatchAuditIds {
    param([object]$Object)

    $property = $Object.PSObject.Properties["auditIds"]
    if ($null -eq $property -or $property.Value -isnot [array]) {
        throw "Scheduled governance batch evidence is missing its audit ID array."
    }
    foreach ($value in $property.Value) {
        $id = [Guid]::Empty
        if ($value -isnot [string] -or -not [Guid]::TryParse($value, [ref]$id) -or $id -eq [Guid]::Empty) {
            throw "Scheduled governance batch evidence contains an invalid audit ID."
        }
        $id.ToString("D")
    }
}

function Assert-ScheduledGovernanceNoExecution {
    param([object]$Receipt)

    $received = Get-ScheduledGovernancePropertyValue $Receipt "latestBatchReceived"
    $counts = Get-ScheduledGovernanceBatchCounts $Receipt
    $auditIds = @(Get-ScheduledGovernanceBatchAuditIds $Receipt)
    if ($received -isnot [bool] -or $received -or $auditIds.Count -ne 0 -or
        @($counts.Values | Where-Object { $_ -ne 0 }).Count -ne 0) {
        throw "Scheduled governance review receipt must confirm no batch, execution totals or audit IDs."
    }
}

function Get-ScheduledGovernanceBatchReceiptEvidence {
    param(
        [AllowNull()][object]$Receipt,
        [object]$Execution,
        [string]$RunId,
        [string]$Snapshot,
        [System.Collections.IDictionary]$Contract,
        [string]$InitialReceiptId,
        [bool]$ExpectedReplay
    )

    $evidence = Get-ScheduledGovernanceReceiptEvidence $Receipt -ExpectedRunId $RunId
    $receiptId = [Guid]::Empty
    if (-not $evidence.present -or $evidence.status -cne "Completed" -or
        -not [Guid]::TryParse((Get-ScheduledGovernancePropertyValue $Receipt "receiptId"), [ref]$receiptId) -or
        $receiptId -eq [Guid]::Empty -or $receiptId.ToString("D") -eq $InitialReceiptId -or
        -not $evidence.requestIdentityHashPresent) {
        throw "A1 requires a distinct completed batch receipt with a request identity."
    }

    foreach ($field in @("runExists", "received", "terminal", "latestBatchReceived", "coverageComplete", "isReplay")) {
        $value = Get-ScheduledGovernancePropertyValue $Receipt $field
        $expected = $field -cne "isReplay" -or $ExpectedReplay
        if ($value -isnot [bool] -or $value -ne $expected) {
            throw "A1 batch receipt did not confirm $field."
        }
    }
    foreach ($field in @("toolContractVersion", "schemaHash", "publishedCatalogVersion")) {
        if ((Get-ScheduledGovernancePropertyValue $Receipt $field) -cne $Contract[$field]) {
            throw "A1 batch receipt did not match the verified $field."
        }
    }

    $requiresReReview = Get-ScheduledGovernancePropertyValue $Execution "requiresReReview"
    $expectedOutcome = if ($requiresReReview) { "ReReviewRequired" } else { "ReversibleExecutionRequired" }
    $expectedDecision = if (-not $requiresReReview) { $expectedOutcome }
    if ($requiresReReview -isnot [bool] -or
        (Get-ScheduledGovernancePropertyValue $Receipt "decision") -cne $expectedDecision -or
        (Get-ScheduledGovernancePropertyValue $Receipt "outcome") -cne $expectedOutcome -or
        (Get-ScheduledGovernancePropertyValue $Receipt "finalConvergenceStatus") -cne "ReversibleExecutionRequired" -or
        (Get-ScheduledGovernancePropertyValue $Receipt "initialSnapshotToken") -cne $Snapshot -or
        (Get-ScheduledGovernancePropertyValue $Receipt "finalSnapshotToken") -cne $Snapshot -or
        (Get-ScheduledGovernancePropertyValue $Receipt "stoppedReason") -cne (Get-ScheduledGovernancePropertyValue $Execution "stoppedReason")) {
        throw "A1 batch receipt did not match the execution snapshot and outcome."
    }

    # The runner checked that the initial receipt has no earlier batch. These
    # run totals can therefore be compared directly with the first execution.
    $counts = Get-ScheduledGovernanceBatchCounts $Receipt
    $batchCounts = Get-ScheduledGovernanceBatchCounts $Execution -Suffix "Count"
    foreach ($field in $counts.Keys) {
        if ($counts[$field] -ne $batchCounts[$field]) {
            throw "A1 batch receipt did not match the execution $field count."
        }
    }
    $receiptAudits = @(Get-ScheduledGovernanceBatchAuditIds $Receipt)
    $batchAudits = @(Get-ScheduledGovernanceBatchAuditIds $Execution)
    if ($counts.failed -ne 0 -or $receiptAudits.Count -eq 0 -or
        (Get-ScheduledGovernancePropertyValue $Execution "stoppedReason") -ceq "ItemFailed" -or
        -not (Test-ScheduledGovernanceIdsEqual $receiptAudits $batchAudits)) {
        throw "A1 batch receipt did not establish a failure-free execution and matching audit IDs."
    }

    $evidence.receiptId = $receiptId.ToString("D")
    $evidence.outcome = $expectedOutcome
    $evidence.counts = $counts
    $evidence.requestIdentityHash = Get-ScheduledGovernancePropertyValue $Receipt "requestIdentityHash"
    $evidence.auditIds = $receiptAudits
    return $evidence
}

function Assert-ScheduledGovernanceFinalBatchContinuity {
    param([object]$Receipt, [System.Collections.IDictionary]$Evidence)

    $prior = $Evidence.receipt.afterReplay
    $final = $Evidence.receipt.final
    $latestBatch = Get-ScheduledGovernancePropertyValue $Receipt "latestBatchReceived"
    $requestIdentity = Get-ScheduledGovernancePropertyValue $Receipt "requestIdentityHash"
    $auditIds = @(Get-ScheduledGovernanceBatchAuditIds $Receipt)
    if ($latestBatch -isnot [bool] -or -not $latestBatch -or
        $final.receiptId -in @($Evidence.receipt.initial.receiptId, $prior.receiptId) -or
        $requestIdentity -cne $prior.requestIdentityHash -or
        -not (Test-ScheduledGovernanceIdsEqual $auditIds $prior.auditIds)) {
        throw "Final review receipt did not preserve the executed batch request and audit identity."
    }
    $counts = Get-ScheduledGovernanceBatchCounts $Receipt
    foreach ($field in $counts.Keys) {
        if ($counts[$field] -ne $prior.counts[$field]) {
            throw "Final review receipt changed the executed batch $field count."
        }
    }
}

function Invoke-ScheduledGovernanceVerifiedReplay {
    param(
        [string]$Token,
        [hashtable]$ExecuteArguments,
        [object]$FirstExecution,
        [System.Collections.IDictionary]$Contract,
        [System.Collections.IDictionary]$Evidence
    )

    $runId = $ExecuteArguments.request.governanceRunId
    $snapshot = $ExecuteArguments.request.snapshotToken
    if (-not (Test-ScheduledGovernanceExecutionResult $FirstExecution $runId $snapshot $false)) {
        throw "First execution did not return a successful result bound to this run and snapshot."
    }

    $receiptArguments = @{ governanceRunId = $runId; includeReliabilityHistory = $false }
    $initialReceiptId = $Evidence.receipt.initial.receiptId
    $beforeJson = Invoke-ScheduledGovernanceTool -Token $Token -ToolName "scheduled_governance_run_get" -Arguments $receiptArguments -RequestId 42
    $before = Get-ScheduledGovernanceToolData -Json $beforeJson -Operation "scheduled_governance_run_get-before-replay"
    $Evidence.receipt.beforeReplay = Get-ScheduledGovernanceBatchReceiptEvidence `
        $before $FirstExecution $runId $snapshot $Contract $initialReceiptId $false

    $Evidence.outcome.executorCalls++
    $Evidence.replay.exercised = $true
    try {
        $replayJson = Invoke-ScheduledGovernanceTool -Token $Token -ToolName "scheduled_governance_execute" -Arguments $ExecuteArguments -RequestId 43
        $replay = Get-ScheduledGovernanceToolData -Json $replayJson -Operation "scheduled_governance_execute-exact-replay"
        if (-not (Test-ScheduledGovernanceExecutionResult $replay $runId $snapshot $true) -or
            (Get-ScheduledGovernancePropertyValue $replay "requiresReReview") -ne (Get-ScheduledGovernancePropertyValue $FirstExecution "requiresReReview") -or
            (Get-ScheduledGovernancePropertyValue $replay "stoppedReason") -cne (Get-ScheduledGovernancePropertyValue $FirstExecution "stoppedReason")) {
            throw "Exact execution replay did not preserve the original result identity."
        }
    }
    catch {
        Read-ScheduledGovernanceUnknownOutcome -Token $Token -RunId $runId -Contract $Contract -Evidence $Evidence
        throw
    }

    $afterJson = Invoke-ScheduledGovernanceTool -Token $Token -ToolName "scheduled_governance_run_get" -Arguments $receiptArguments -RequestId 46
    $after = Get-ScheduledGovernanceToolData -Json $afterJson -Operation "scheduled_governance_run_get-after-replay"
    $Evidence.receipt.afterReplay = Get-ScheduledGovernanceBatchReceiptEvidence `
        $after $replay $runId $snapshot $Contract $initialReceiptId $true
    $batchFields = $Evidence.receipt.beforeReplay.counts.Keys
    foreach ($field in $batchFields) {
        if ($Evidence.receipt.beforeReplay.counts[$field] -ne $Evidence.receipt.afterReplay.counts[$field]) {
            throw "Exact execution replay changed the durable $field count."
        }
    }
    if ($Evidence.receipt.beforeReplay.receiptId -eq $Evidence.receipt.afterReplay.receiptId -or
        (Get-ScheduledGovernancePropertyValue $before "requestIdentityHash") -cne (Get-ScheduledGovernancePropertyValue $after "requestIdentityHash") -or
        -not (Test-ScheduledGovernanceIdsEqual @(Get-ScheduledGovernanceBatchAuditIds $before) @(Get-ScheduledGovernanceBatchAuditIds $after))) {
        throw "Exact execution replay changed the durable request or audit identity."
    }
    $Evidence.replay.exact = $true
}

function Read-ScheduledGovernanceProtectedInvariantEvidence {
    param(
        [string]$Path,
        [Parameter(Mandatory)][System.Collections.IDictionary]$Binding
    )

    $result = [ordered]@{
        status = "required-missing"
        unchanged = $false
        source = "external-read-only-manifest"
        bindingMatched = $false
        displayNameUnchanged = $false
        businessWorkItemsUnchanged = $false
        businessWorkItemCount = 0
    }
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $result
    }

    try {
        $manifest = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
        $observedBinding = Get-ScheduledGovernancePropertyValue $manifest "binding"
        foreach ($field in @("governanceRunId", "resource", "initialReceiptId", "finalReceiptId")) {
            $value = Get-ScheduledGovernancePropertyValue $observedBinding $field
            if ($value -isnot [string] -or [string]::IsNullOrWhiteSpace($value) -or $value -cne $Binding[$field]) {
                $result.status = "binding-mismatch"
                return $result
            }
        }

        $result.bindingMatched = $true
        $displayName = Get-ScheduledGovernancePropertyValue $manifest "displayName"
        $displayNameBefore = Get-ScheduledGovernancePropertyValue $displayName "before"
        $displayNameAfter = Get-ScheduledGovernancePropertyValue $displayName "after"
        if ($displayNameBefore -isnot [string] -or [string]::IsNullOrWhiteSpace($displayNameBefore) -or
            $displayNameAfter -isnot [string] -or [string]::IsNullOrWhiteSpace($displayNameAfter)) {
            throw "displayName requires nonempty before and after strings."
        }

        $workItems = $manifest.PSObject.Properties["businessWorkItems"].Value
        if ($workItems -isnot [array]) {
            throw "businessWorkItems must be an explicit array."
        }

        $displayNameUnchanged = $displayNameBefore -ceq $displayNameAfter
        $workItemsUnchanged = $true
        $observedIds = [System.Collections.Generic.HashSet[Guid]]::new()
        $validStatuses = @("Pending", "InProgress", "Blocked", "Completed", "Cancelled")
        foreach ($item in $workItems) {
            $id = [Guid]::Empty
            $rawId = Get-ScheduledGovernancePropertyValue $item "id"
            if ($rawId -isnot [string] -or -not [Guid]::TryParse($rawId, [ref]$id) -or
                $id -eq [Guid]::Empty -or -not $observedIds.Add($id)) {
                throw "businessWorkItems requires distinct nonempty Work Item IDs."
            }

            $before = Get-ScheduledGovernancePropertyValue $item "before"
            $after = Get-ScheduledGovernancePropertyValue $item "after"
            $beforeStatus = Get-ScheduledGovernancePropertyValue $before "status"
            $afterStatus = Get-ScheduledGovernancePropertyValue $after "status"
            $beforeArchived = Get-ScheduledGovernancePropertyValue $before "isArchived"
            $afterArchived = Get-ScheduledGovernancePropertyValue $after "isArchived"
            if ($beforeStatus -isnot [string] -or $beforeStatus -cnotin $validStatuses -or
                $afterStatus -isnot [string] -or $afterStatus -cnotin $validStatuses -or
                $beforeArchived -isnot [bool] -or $afterArchived -isnot [bool]) {
                throw "businessWorkItems requires an observed status and boolean isArchived before and after."
            }
            if ($beforeStatus -cne $afterStatus -or $beforeArchived -ne $afterArchived) {
                $workItemsUnchanged = $false
            }
        }

        $unchanged = $displayNameUnchanged -and $workItemsUnchanged
        $result.status = if ($unchanged) { "verified" } else { "changed" }
        $result.unchanged = $unchanged
        $result.displayNameUnchanged = $displayNameUnchanged
        $result.businessWorkItemsUnchanged = $workItemsUnchanged
        $result.businessWorkItemCount = $workItems.Count
    }
    catch {
        $result.status = "invalid"
    }
    return $result
}

function Assert-ScheduledGovernanceReadbackPaths {
    param([string]$RequestPath, [string]$ManifestPath, [string]$EvidencePath, [string]$CheckpointPath, [switch]$RecoveryOnly)

    $paths = @($RequestPath, $ManifestPath, $EvidencePath, $CheckpointPath | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($_) })
    if (@($paths | Sort-Object -CaseSensitive:(!$IsWindows) -Unique).Count -ne $paths.Count) {
        throw "The protected readback request, manifest, final evidence and A1 checkpoint require distinct paths."
    }
    if (-not $RecoveryOnly -and -not [string]::IsNullOrWhiteSpace($RequestPath)) {
        if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
            throw "A protected readback request requires a manifest path."
        }
        if ((Test-Path -LiteralPath $RequestPath) -or (Test-Path -LiteralPath $ManifestPath)) {
            throw "Use fresh paths for the protected readback request and manifest."
        }
        $requestFile = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($RequestPath)
        $manifestFile = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ManifestPath)
        if (-not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($requestFile)) -or
            -not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($manifestFile))) {
            throw "The protected readback request and manifest parent directories must exist."
        }
    }
}

function Write-ScheduledGovernanceA1Checkpoint {
    param([string]$Path, [string]$BindingHash, [string]$RunId)

    $temporary = "$Path.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        $journal = @{ version = 1; bindingHash = $BindingHash; governanceRunId = $RunId } | ConvertTo-Json -Compress
        [IO.File]::WriteAllText($temporary, $journal)
        [IO.File]::Move($temporary, $Path)
    }
    finally {
        if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
    }
}

function Invoke-ScheduledGovernanceA1Recovery {
    param([string]$Path, [string]$BindingHash, [string]$Token, [object]$Spec, [object]$Evidence)

    $journal = [IO.File]::ReadAllText($Path) | ConvertFrom-Json -AsHashtable
    if ($journal -isnot [System.Collections.IDictionary] -or $journal['version'] -ne 1 -or
        $journal['bindingHash'] -cne $BindingHash -or
        $journal['governanceRunId'] -cnotmatch '^scheduled-acceptance-[a-f0-9]{32}$') {
        throw "A1 checkpoint is malformed or belongs to another endpoint, resource, environment or contract."
    }
    $runId = $journal.governanceRunId
    $Evidence.governanceRunId = $runId
    $Evidence.outcome.status = "a1-recovery-read-only"
    $contract = @{
        toolContractVersion = $Spec.contract.toolContractVersion
        schemaHash = $Spec.contract.schemaHash
        publishedCatalogVersion = $Spec.catalog.publishedCatalogVersion
    }
    Read-ScheduledGovernanceUnknownOutcome -Token $Token -RunId $runId -Contract $contract -Evidence $Evidence
}

function Read-ScheduledGovernanceUnknownOutcome {
    param([string]$Token, [string]$RunId, [System.Collections.IDictionary]$Contract, [System.Collections.IDictionary]$Evidence)

    $Evidence.replay.outcomeRecovery = "receipt-readback-unavailable"
    $Evidence.receipt.afterUnknownOutcome = $null
    try {
        $json = Invoke-ScheduledGovernanceTool -Token $Token -ToolName "scheduled_governance_run_get" `
            -Arguments @{ governanceRunId = $RunId; includeReliabilityHistory = $false } -RequestId 41
        $receipt = Get-ScheduledGovernanceToolData -Json $json -Operation "scheduled_governance_run_get"
        foreach ($field in @("toolContractVersion", "schemaHash", "publishedCatalogVersion")) {
            if ((Get-ScheduledGovernancePropertyValue $receipt $field) -cne $Contract[$field]) {
                throw "Recovery receipt did not match the verified $field."
            }
        }
        if ((Get-ScheduledGovernancePropertyValue $receipt "runExists") -isnot [bool]) {
            throw "Recovery receipt did not confirm whether the run exists."
        }
        $Evidence.receipt.afterUnknownOutcome = Get-ScheduledGovernanceReceiptEvidence $receipt -ExpectedRunId $RunId
        $Evidence.replay.outcomeRecovery = if ($Evidence.receipt.afterUnknownOutcome.runExists) {
            "receipt-readback-insufficient-for-exact-replay"
        }
        else { "receipt-run-not-found" }
    }
    catch {
        $Evidence.errors += $_.Exception.Message
    }
}

function Invoke-ScheduledGovernanceInitialExecution {
    param([string]$Token, [hashtable]$ExecuteArguments, [System.Collections.IDictionary]$Contract, [System.Collections.IDictionary]$Evidence)

    $runId = $ExecuteArguments.request.governanceRunId
    $snapshot = $ExecuteArguments.request.snapshotToken
    try {
        $Evidence.outcome.executorCalls++
        $json = Invoke-ScheduledGovernanceTool -Token $Token -ToolName "scheduled_governance_execute" -Arguments $ExecuteArguments -RequestId 40
        $execution = Get-ScheduledGovernanceToolData -Json $json -Operation "scheduled_governance_execute"
        if (Test-ScheduledGovernanceExecutionResult -Execution $execution -ExpectedRunId $runId -ExpectedSnapshot $snapshot -ExpectedReplay $false) {
            return $execution
        }
    }
    catch {
        $Evidence.errors += $_.Exception.Message
    }

    $Evidence.outcome.status = "outcome-unknown-no-retry"
    Read-ScheduledGovernanceUnknownOutcome -Token $Token -RunId $runId -Contract $Contract -Evidence $Evidence
}

function Complete-ScheduledGovernanceProtectedReadback {
    param(
        [string]$ManifestPath,
        [string]$RequestPath,
        [int]$TimeoutSec,
        [Parameter(Mandatory)][System.Collections.IDictionary]$Binding
    )

    if ($Binding.initialReceiptId -ceq $Binding.finalReceiptId) {
        throw "The initial and final review receipts must be distinct for protected readback."
    }
    if (-not [string]::IsNullOrWhiteSpace($RequestPath)) {
        $target = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($RequestPath)
        $temporary = "$target.$([Guid]::NewGuid().ToString('N')).tmp"
        try {
            @{ binding = $Binding } | ConvertTo-Json -Depth 5 -Compress |
                Set-Content -LiteralPath $temporary -Encoding UTF8 -NoNewline
            [IO.File]::Move($temporary, $target)
        }
        finally {
            if ([IO.File]::Exists($temporary)) {
                [IO.File]::Delete($temporary)
            }
        }

        $elapsed = [Diagnostics.Stopwatch]::StartNew()
        while ($elapsed.Elapsed.TotalSeconds -lt $TimeoutSec -and
            -not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
            Start-Sleep -Milliseconds 100
        }
    }

    $readback = Read-ScheduledGovernanceProtectedInvariantEvidence -Path $ManifestPath -Binding $Binding
    if (-not [string]::IsNullOrWhiteSpace($RequestPath) -and $readback.status -ceq "required-missing") {
        $readback.status = "readback-timeout"
    }
    return $readback
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
    if (-not [string]::IsNullOrWhiteSpace($validatedEvidencePath)) {
        Set-Content -LiteralPath $validatedEvidencePath -Value $json -Encoding UTF8 -NoNewline
    }
    Write-Output $json
    exit $ExitCode
}

$exitCode = 0
$specValidation = $null
$evidence = $null
$validatedEvidencePath = ""

try {
    if ($Mode -eq "A1" -and [string]::IsNullOrWhiteSpace($A1CheckpointPath)) {
        if ([string]::IsNullOrWhiteSpace($EvidencePath)) { throw "A1 requires A1CheckpointPath or EvidencePath." }
        $A1CheckpointPath = "$EvidencePath.a1-checkpoint.json"
    }
    if ($Mode -eq "A1") {
        $A1CheckpointPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($A1CheckpointPath)
    }
    $recoveringA1 = $Mode -eq "A1" -and [IO.File]::Exists($A1CheckpointPath)
    Assert-ScheduledGovernanceReadbackPaths -RequestPath $ProtectedInvariantReadbackRequestPath `
        -ManifestPath $ProtectedInvariantEvidencePath -EvidencePath $EvidencePath -CheckpointPath $A1CheckpointPath -RecoveryOnly:$recoveringA1
    if ($Mode -ne "A1") { $validatedEvidencePath = $EvidencePath }
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
        if ($Mode -eq "A1") {
            if (-not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($A1CheckpointPath)) -or [IO.Directory]::Exists($A1CheckpointPath)) {
                throw "A1 checkpoint requires a file path in an existing directory."
            }
            $checkpointBinding = [ordered]@{
                endpoint = $endpointUri.AbsoluteUri
                resource = $OAuthResource
                environment = $A1Environment
                contract = $specValidation.Spec.contract.toolContractVersion
                schema = $specValidation.Spec.contract.schemaHash
                catalog = $specValidation.Spec.catalog.publishedCatalogVersion
            }
            $checkpointBindingHash = Get-ScheduledGovernanceSha256Hex ($checkpointBinding | ConvertTo-Json -Compress)
        }
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
            if ($metadataResponse.TransportError) {
                $evidence.errors += "OAuth protected-resource discovery transport failed: $($metadataResponse.Error)"
            }
            else {
                $evidence.errors += "OAuth protected-resource discovery did not satisfy the automation resource contract."
            }
            if ($null -eq $exitCode -or $exitCode -eq 0) { $exitCode = 1 }
        }

        if ([string]::IsNullOrWhiteSpace($token)) {
            if ($RequireAuthorizationToken) {
                throw "Authorization token is required for this invocation."
            }
            $evidence.outcome.status = if ($exitCode -eq 0) { "OAuthRequired" } else { "discovery-not-ready" }
            $evidence.outcome.accepted = $false
        }
        elseif ($exitCode -eq 0 -and $recoveringA1) {
            Invoke-ScheduledGovernanceA1Recovery -Path $A1CheckpointPath -BindingHash $checkpointBindingHash `
                -Token $token -Spec $specValidation.Spec -Evidence $evidence
            $exitCode = 2
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

            $receiptTool = @($publishedTools | Where-Object {
                    [string](Get-ScheduledGovernancePropertyValue $_ "name") -ceq "scheduled_governance_run_get"
                })[0]
            $receiptInput = Get-ScheduledGovernancePropertyValue $receiptTool "inputSchema"
            $receiptProperties = Get-ScheduledGovernancePropertyValue $receiptInput "properties"
            $historySchema = Get-ScheduledGovernancePropertyValue $receiptProperties "includeReliabilityHistory"
            if ((Get-ScheduledGovernancePropertyValue $historySchema "type") -cne "boolean" -or
                (Get-ScheduledGovernanceArrayProperty $receiptInput "required") -ccontains "includeReliabilityHistory") {
                throw "Scheduled receipt discovery did not expose optional boolean includeReliabilityHistory."
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
                Assert-ScheduledGovernancePublishedToolAuthority -Tool $tool
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
                if ($Mode -eq "A1") {
                    Write-ScheduledGovernanceA1Checkpoint -Path $A1CheckpointPath -BindingHash $checkpointBindingHash -RunId $runId
                    $validatedEvidencePath = $EvidencePath
                }
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
                    -Arguments @{ governanceRunId = $runId; includeReliabilityHistory = $false } `
                    -RequestId 31
                $initialReceipt = Get-ScheduledGovernanceToolData -Json $initialReceiptJson -Operation "scheduled_governance_run_get"
                $evidence.receipt.initial = Get-ScheduledGovernanceReviewReceiptEvidence `
                    -Receipt $initialReceipt -ExpectedRunId $runId -Review $review -Contract $observedContract -RequireNoExecution

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
                            -Arguments @{ governanceRunId = $runId; includeReliabilityHistory = $true } `
                            -RequestId 33
                        $finalReceipt = Get-ScheduledGovernanceToolData -Json $finalReceiptJson -Operation "scheduled_governance_run_get"
                        $evidence.receipt.final = Get-ScheduledGovernanceReviewReceiptEvidence `
                            -Receipt $finalReceipt -ExpectedRunId $runId -Review $finalReview -Contract $observedContract -RequireNoExecution
                        $evidence.protectedInvariants = Complete-ScheduledGovernanceProtectedReadback `
                            -ManifestPath $ProtectedInvariantEvidencePath -RequestPath $ProtectedInvariantReadbackRequestPath `
                            -TimeoutSec $ProtectedInvariantReadbackTimeoutSec -Binding ([ordered]@{
                                governanceRunId = $runId
                                resource = $OAuthResource
                                initialReceiptId = $evidence.receipt.initial.receiptId
                                finalReceiptId = $evidence.receipt.final.receiptId
                            })
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
                        $firstExecution = Invoke-ScheduledGovernanceInitialExecution -Token $token `
                            -ExecuteArguments $executeArguments -Contract $observedContract -Evidence $evidence

                        if ($null -ne $firstExecution) {
                            Invoke-ScheduledGovernanceVerifiedReplay $token $executeArguments $firstExecution $observedContract $evidence
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
                                -Arguments @{ governanceRunId = $runId; includeReliabilityHistory = $true } `
                                -RequestId 45
                            $finalReceipt = Get-ScheduledGovernanceToolData `
                                -Json $finalReceiptJson -Operation "scheduled_governance_run_get-after-execute"
                            $evidence.receipt.final = Get-ScheduledGovernanceReviewReceiptEvidence `
                                -Receipt $finalReceipt -ExpectedRunId $runId -Review $finalReview -Contract $observedContract
                            Assert-ScheduledGovernanceFinalBatchContinuity -Receipt $finalReceipt -Evidence $evidence
                            $evidence.protectedInvariants = Complete-ScheduledGovernanceProtectedReadback `
                                -ManifestPath $ProtectedInvariantEvidencePath -RequestPath $ProtectedInvariantReadbackRequestPath `
                                -TimeoutSec $ProtectedInvariantReadbackTimeoutSec -Binding ([ordered]@{
                                    governanceRunId = $runId
                                    resource = $OAuthResource
                                    initialReceiptId = $evidence.receipt.initial.receiptId
                                    finalReceiptId = $evidence.receipt.final.receiptId
                                })
                            $finalConverged = $finalReviewEvidence.decision -eq "NoOpConverged" -or
                                ($finalReviewEvidence.decision -eq "HumanDecisionOnly" -and $finalReviewEvidence.humanDecisionEvidence)
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
