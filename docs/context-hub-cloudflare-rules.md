# ContextHub Cloudflare Rules

This document defines the Cloudflare edge policy for ContextHub MCP traffic.
The `context-hub.wjcy.org` DNS record must stay Proxied. Do not switch it to
DNS-only to debug MCP, because that exposes the origin IP.

## Match expression

Use the same expression for MCP-specific Cache, Configuration, and WAF rules:

```text
(http.host eq "context-hub.wjcy.org" and starts_with(http.request.uri.path, "/mcp"))
```

For dynamic REST/API traffic, use:

```text
(http.host eq "context-hub.wjcy.org" and (
  starts_with(http.request.uri.path, "/api/") or
  starts_with(http.request.uri.path, "/_blazor") or
  starts_with(http.request.uri.path, "/health") or
  starts_with(http.request.uri.path, "/login") or
  starts_with(http.request.uri.path, "/account")
))
```

## Cache Rules

Create a Cache Rule for `/mcp*`:

```text
Action:
  Bypass cache

Do not:
  Override origin Cache-Control
  Override Cloudflare-CDN-Cache-Control
  Cache POST responses
```

Create or keep a dynamic traffic Cache Rule for `/api/*`, `/_blazor*`,
`/health*`, `/login*`, and `/account*`:

```text
Action:
  Bypass cache
```

Static assets may still be cached by extension or immutable asset path. Do not
turn the whole hostname into bypass mode unless a broader incident requires it.

Expected MCP response headers:

```text
Cache-Control: no-store, no-cache, max-age=0, must-revalidate, no-transform
CF-Cache-Status: DYNAMIC or BYPASS
```

## Configuration Rules

Create a Configuration Rule for `/mcp*` that disables browser-facing features:

```text
Disable:
  Rocket Loader
  Auto Minify
  Polish
  Mirage
  Zaraz
  Email Obfuscation
  Browser Integrity Check
```

If the plan exposes a request or response buffering setting, set buffering to
`none` for `/mcp*`. MCP Streamable HTTP/SSE clients expect raw protocol
responses and should not receive transformed HTML, JavaScript injection, or
buffered event streams.

## WAF and bot rules

Create a WAF exception for `/mcp*`:

```text
Skip:
  Managed Challenge
  JavaScript Challenge
  Interactive challenge pages

Keep:
  DDoS protection
  Bearer token authentication at origin
  Rate limiting
  Explicit allow/block rules
```

MCP clients are non-browser clients. Any rule that returns a Cloudflare HTML
challenge page will break Codex MCP worker initialization even when the origin
server is healthy.

## TLS client certificates / mTLS

Do not enable Cloudflare mTLS client certificate requirements on the hostname
used by public MCP clients. MCP clients such as Codex use non-browser HTTP
stacks and may fail during TLS setup when Cloudflare requests client
certificate renegotiation, even if PowerShell or browser-style clients appear
to continue successfully.

Because TLS client certificate negotiation happens before an HTTP path is
processed, do not rely on a `/mcp*` path rule to fix hostname-level mTLS
behavior. If dashboard or admin routes require mTLS in the future, put MCP on a
dedicated still-Proxied hostname that does not request client certificates:

```text
mcp.context-hub.wjcy.org
```

Bearer token authentication at the ContextHub origin remains mandatory for
MCP. Do not replace bearer tokens with Cloudflare mTLS for public agent access.

## Optional dedicated MCP hostname

If Codex MCP worker still fails after the `/mcp*` rules above, add a dedicated
hostname:

```text
mcp.context-hub.wjcy.org
```

The new hostname must also stay Proxied. Apply only the minimal MCP rules above
to that hostname and point Codex config to:

```text
https://mcp.context-hub.wjcy.org/mcp
```

Do not create a DNS-only MCP hostname without explicit approval.

## Verification

Run the local diagnostic script:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-contexthub-mcp.ps1
```

To include Codex tool-injection verification:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-contexthub-mcp.ps1 -RunCodexExec
```

Manual checks:

```powershell
Resolve-DnsName context-hub.wjcy.org

Invoke-WebRequest `
  -Uri https://context-hub.wjcy.org/mcp `
  -Method Get `
  -UseBasicParsing
```

The unauthenticated MCP request should return `401`, include a `CF-RAY` header,
and must not return a browser challenge HTML page.

The diagnostic script also probes `curl.exe -sv --http1.1` output. If it sees
TLS renegotiation/client-certificate negotiation, Cloudflare mTLS or a related
edge TLS setting must be removed from the MCP hostname before Codex HTTP MCP
worker can be considered supported.
