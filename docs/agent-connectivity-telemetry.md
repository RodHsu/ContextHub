# Agent Connectivity Telemetry

ContextHub can collect agent-to-ContextHub MCP connectivity telemetry without sending data through an LLM prompt or tool result. The stdio bridge measures client-observed latency locally and uploads sampled observations to `/api/agent-connectivity/observations` in the background.

## Collection Policy

- Default profile: `Balanced`
- Raw observation retention: 7 days
- Aggregated summary retention: 14 days
- Success sampling default: 20%
- Failure sampling default: 100%
- Upload interval default: 15 seconds

The telemetry payload intentionally excludes prompts, responses, tool arguments, request bodies, tokens, secrets, full exception stacks, and local file paths.

## Bridge Configuration

Environment variables:

```powershell
$env:CONTEXTHUB_AGENT_TELEMETRY_ENABLED = "true"
$env:CONTEXTHUB_AGENT_TELEMETRY_PROFILE = "Balanced"
$env:CONTEXTHUB_AGENT_TELEMETRY_SUCCESS_SAMPLE_RATE = "0.2"
$env:CONTEXTHUB_AGENT_TELEMETRY_FAILURE_SAMPLE_RATE = "1.0"
$env:CONTEXTHUB_AGENT_TELEMETRY_UPLOAD_INTERVAL_SECONDS = "15"
$env:CONTEXTHUB_AGENT_TELEMETRY_MAX_BATCH_SIZE = "100"
$env:CONTEXTHUB_AGENT_ID = "stdio-bridge"
$env:CONTEXTHUB_AGENT_NAME = "ContextHub MCP stdio bridge"
$env:CONTEXTHUB_PROJECT_ID = "ContextHub"
```

Set `CONTEXTHUB_AGENT_TELEMETRY_ENABLED=false` or `CONTEXTHUB_AGENT_TELEMETRY_PROFILE=Off` to disable agent-side collection.

## Server Configuration

`AgentConnectivityTelemetry` controls server-side policy:

```json
{
  "AgentConnectivityTelemetry": {
    "Enabled": true,
    "Profile": "Balanced",
    "SuccessSampleRate": 0.2,
    "FailureSampleRate": 1.0,
    "ProbeIntervalSeconds": 60,
    "UploadIntervalSeconds": 15,
    "MaxBatchSize": 100,
    "MaxSamplesPerAgentMethodPerMinute": 60,
    "RawRetentionDays": 7,
    "SummaryRetentionDays": 14
  }
}
```

The dashboard exposes the active settings on `/connectivity`.
