# Cloudflare Edge Rules

This guide describes Cloudflare policy for a public ContextHub deployment. It uses `context-hub.example.com` as a placeholder. Replace it with your hostname.

ContextHub MCP clients are non-browser HTTP clients. They expect raw Streamable HTTP/SSE-compatible behavior and must not receive cached responses, transformed content, or browser challenge pages.

## Public Dynamic Routes

```text
/mcp
/mcp-chat
/.well-known/oauth-protected-resource/mcp-chat
/.well-known/oauth-authorization-server/mcp-chat
/.well-known/openid-configuration/mcp-chat
/oauth/chat/*
/userinfo
/api/*
/_blazor*
/health*
/login*
/account*
```

## Match Expression

For MCP and chat-agent gateway traffic:

```text
(http.host eq "context-hub.example.com" and (
  http.request.uri.path eq "/mcp" or
  http.request.uri.path eq "/mcp-chat" or
  http.request.uri.path eq "/.well-known/oauth-protected-resource/mcp-chat" or
  http.request.uri.path eq "/.well-known/oauth-authorization-server/mcp-chat" or
  http.request.uri.path eq "/.well-known/openid-configuration/mcp-chat" or
  http.request.uri.path eq "/userinfo" or
  starts_with(http.request.uri.path, "/oauth/chat/")
))
```

For dynamic dashboard and REST traffic:

```text
(http.host eq "context-hub.example.com" and (
  starts_with(http.request.uri.path, "/api/") or
  starts_with(http.request.uri.path, "/_blazor") or
  starts_with(http.request.uri.path, "/health") or
  starts_with(http.request.uri.path, "/login") or
  starts_with(http.request.uri.path, "/account")
))
```

## Cache Rules

Create cache bypass rules for:

- `/mcp`
- `/mcp-chat`
- OAuth/OIDC metadata paths
- `/oauth/chat/*`
- `/userinfo`
- `/api/*`
- `/_blazor*`
- `/health*`
- `/login*`
- `/account*`

Do not:

- cache POST responses
- override origin `Cache-Control: no-store`
- override `CDN-Cache-Control: no-store`

Static dashboard assets may still use edge caching.

Expected MCP response behavior:

```text
Cache-Control: no-store, no-cache, max-age=0, must-revalidate, no-transform
CF-Cache-Status: DYNAMIC or BYPASS
```

## Configuration Rules

Disable browser-facing transforms for MCP and OAuth/OIDC routes:

- Rocket Loader
- Auto Minify
- Polish
- Mirage
- Zaraz
- Email Obfuscation
- Browser Integrity Check

If the plan exposes request or response buffering controls, disable buffering for `/mcp` and `/mcp-chat`.

## WAF And Bot Rules

MCP routes should skip interactive challenges:

- Managed Challenge
- JavaScript Challenge
- browser challenge pages

Keep:

- DDoS protection
- explicit allow/block rules
- rate limiting
- bearer token or OAuth validation at the origin

Any Cloudflare HTML challenge response will break MCP client initialization even when the origin server is healthy.

## TLS Client Certificates

Do not require Cloudflare mTLS client certificates on a hostname used by public MCP clients unless every supported client is known to handle it.

Because TLS client certificate negotiation happens before HTTP path routing, a path rule cannot reliably exempt `/mcp` if the hostname itself requests client certificates.

If the dashboard requires mTLS, prefer a separate still-proxied hostname for public MCP:

```text
mcp.context-hub.example.com
```

Then point agents to:

```text
https://mcp.context-hub.example.com/mcp
```

Do not create a DNS-only MCP hostname unless you explicitly accept origin exposure risk.

## Verification

Run raw MCP diagnostics:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-contexthub-mcp.ps1 -Endpoint https://context-hub.example.com/mcp
```

Run chat-agent gateway diagnostics:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-contexthub-mcp-chat.ps1 -Endpoint https://context-hub.example.com/mcp-chat
```

Manual unauthenticated checks:

```powershell
Resolve-DnsName context-hub.example.com

Invoke-WebRequest `
  -Uri https://context-hub.example.com/mcp `
  -Method Get `
  -UseBasicParsing
```

Expected result:

- status is `401`
- response is not an HTML challenge page
- dynamic routes are not cached
- `/mcp-chat` routes to the restricted gateway, not the full `/mcp` server
