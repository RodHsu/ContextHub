# ContextHub Public Nginx Proxy

`context-hub.wjcy.org` should stay behind Cloudflare Proxied DNS and route
through Nginx on the shared Docker network instead of exposing ContextHub
service ports publicly.

Expected Docker network:

```text
host_share
```

Expected upstream aliases from `docker-compose.release.yml`:

```text
context-hub-dashboard:8088
context-hub-mcp-server-a:8080
context-hub-mcp-server-b:8080
context-hub-chatgpt-gateway:8083
```

Origin cache header policy:

```text
Dashboard static assets:
  Cache-Control: public, max-age=31536000, immutable
  Cloudflare-CDN-Cache-Control: public, max-age=31536000
  CDN-Cache-Control: public, max-age=31536000

Dashboard HTML, health checks, Blazor transport, API, MCP:
  Cache-Control: no-store, no-cache, max-age=0, must-revalidate
  Cloudflare-CDN-Cache-Control: no-store
  CDN-Cache-Control: no-store
```

Cloudflare Cache Rules should not override the origin `no-store` policy for
`/api/*`, `/mcp*`, `/mcp-chat*`, `/_blazor*`, `/health*`, `/login*`, or
`/account*`.
Only static asset file extensions should be eligible for edge cache.

Apply the Cloudflare edge rules in
[`context-hub-cloudflare-rules.md`](context-hub-cloudflare-rules.md) before
treating MCP failures as origin or application failures.

Release deploys run two fixed full MCP backends (`mcp-server-a` and
`mcp-server-b`). Nginx should route `/mcp` and `/api/` through the
`contexthub_mcp` upstream so one backend can be recreated while the other
continues serving existing agents. The deployment script performs public MCP
smoke checks between backend updates; do not collapse the upstream back to a
single `context-hub-mcp-server` target.

Public MCP routes:

```text
/mcp       -> Codex/full ContextHub MCP, via mcp-server-a/b
/mcp-chat  -> restricted chat-agent MCP gateway, via chatgpt-gateway:8083/mcp
/.well-known/oauth-protected-resource/mcp-chat
          -> OAuth protected resource metadata for /mcp-chat
/.well-known/oauth-authorization-server/mcp-chat
          -> OAuth authorization server metadata for ContextHub self-hosted OAuth
/oauth/chat/authorize
/oauth/chat/token
          -> ContextHub self-hosted OAuth authorization code flow for chat agents
```

`/mcp-chat` is the public endpoint for ChatGPT custom MCP apps and future chat
agents. It must stay separate from `/mcp`: chat agents only see the gateway
allowlist, project allowlist, OIDC/OAuth checks, rate limit, audit, and
proposal-gated durable writes.

After any release that recreates `dashboard`, `mcp-server-a/b`, or
`chatgpt-gateway`, reload Nginx after `nginx -t`. The current Nginx deployment
resolves Docker DNS at config load time, so a reload is required to avoid stale
upstream container IPs after Docker recreates containers.

Cloudflare rules for `/mcp*` and `/mcp-chat*`:

```text
DNS:
  Keep the record Proxied (orange-cloud). Do not switch to DNS-only just to fix MCP.

Cache:
  Bypass cache for /mcp* and /mcp-chat*
  Do not override origin Cache-Control / CDN-Cache-Control no-store

Transform / challenges:
  Do not apply response transformation, Rocket Loader, JS challenge, or managed challenge
  to /mcp* or /mcp-chat*. MCP clients are non-browser clients and expect raw
  Streamable HTTP/SSE.

Security:
  Enforce access with bearer tokens, WAF allow/block rules, and rate limits.
  If origin hiding needs to be stronger later, prefer Cloudflare Tunnel over DNS-only.
```

Example Nginx server:

```nginx
map $http_upgrade $connection_upgrade {
    default upgrade;
    '' close;
}

upstream contexthub_mcp {
    ip_hash;
    server context-hub-mcp-server-a:8080 max_fails=2 fail_timeout=10s;
    server context-hub-mcp-server-b:8080 max_fails=2 fail_timeout=10s;
    keepalive 32;
}

server {
    listen 443 ssl http2;
    server_name context-hub.wjcy.org;

    ssl_certificate /etc/nginx/certs/context-hub.wjcy.org/fullchain.pem;
    ssl_certificate_key /etc/nginx/certs/context-hub.wjcy.org/privkey.pem;

    client_max_body_size 25m;

    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header X-Real-IP $remote_addr;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Host $host;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_set_header X-Forwarded-Port $server_port;

    location = /mcp {
        add_header Cache-Control "no-store, no-cache, max-age=0, must-revalidate, no-transform" always;
        add_header Cloudflare-CDN-Cache-Control "no-store" always;
        add_header CDN-Cache-Control "no-store" always;
        add_header X-Accel-Buffering "no" always;
        gzip off;
        proxy_cache off;
        proxy_buffering off;
        proxy_request_buffering off;
        proxy_read_timeout 3600s;
        proxy_send_timeout 3600s;
        proxy_pass http://contexthub_mcp;
    }

    location = /mcp-chat {
        add_header Cache-Control "no-store, no-cache, max-age=0, must-revalidate, no-transform" always;
        add_header Cloudflare-CDN-Cache-Control "no-store" always;
        add_header CDN-Cache-Control "no-store" always;
        add_header X-Accel-Buffering "no" always;
        gzip off;
        proxy_cache off;
        proxy_buffering off;
        proxy_request_buffering off;
        proxy_read_timeout 3600s;
        proxy_send_timeout 3600s;
        proxy_set_header Authorization $http_authorization;
        proxy_set_header Accept $http_accept;
        proxy_set_header Mcp-Session-Id $http_mcp_session_id;
        proxy_set_header MCP-Protocol-Version $http_mcp_protocol_version;
        proxy_pass http://context-hub-chatgpt-gateway:8083/mcp;
    }

    location = /.well-known/oauth-protected-resource/mcp-chat {
        add_header Cache-Control "no-store, no-cache, max-age=0, must-revalidate, no-transform" always;
        add_header Cloudflare-CDN-Cache-Control "no-store" always;
        add_header CDN-Cache-Control "no-store" always;
        add_header X-Accel-Buffering "no" always;
        proxy_cache off;
        proxy_buffering off;
        proxy_request_buffering off;
        proxy_pass http://context-hub-chatgpt-gateway:8083/.well-known/oauth-protected-resource/mcp-chat;
    }

    location = /.well-known/oauth-authorization-server/mcp-chat {
        add_header Cache-Control "no-store, no-cache, max-age=0, must-revalidate, no-transform" always;
        add_header Cloudflare-CDN-Cache-Control "no-store" always;
        add_header CDN-Cache-Control "no-store" always;
        add_header X-Accel-Buffering "no" always;
        proxy_cache off;
        proxy_buffering off;
        proxy_pass http://context-hub-chatgpt-gateway:8083/.well-known/oauth-authorization-server/mcp-chat;
    }

    location /oauth/chat/ {
        add_header Cache-Control "no-store, no-cache, max-age=0, must-revalidate, no-transform" always;
        add_header Cloudflare-CDN-Cache-Control "no-store" always;
        add_header CDN-Cache-Control "no-store" always;
        add_header X-Accel-Buffering "no" always;
        proxy_cache off;
        proxy_buffering off;
        proxy_request_buffering off;
        proxy_pass http://context-hub-chatgpt-gateway:8083;
    }

    location /api/ {
        proxy_pass http://contexthub_mcp;
    }

    location /_blazor {
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection $connection_upgrade;
        proxy_read_timeout 3600s;
        proxy_pass http://context-hub-dashboard:8088;
    }

    location / {
        proxy_pass http://context-hub-dashboard:8088;
    }
}

server {
    listen 80;
    server_name context-hub.wjcy.org;
    return 301 https://$host$request_uri;
}
```

Post-deploy smoke checks:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-contexthub-mcp.ps1 -RunCodexExec
powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-contexthub-mcp-chat.ps1
```

For full ChatGPT simulation, set `CONTEXTHUB_MCP_CHAT_TOKEN` to a valid OAuth
access token for the ChatGPT/custom chat-agent client and rerun:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-contexthub-mcp-chat.ps1 -RequireAuthorizationToken
```

For ChatGPT Developer Mode, create the custom MCP app with this MCP URL:

```text
https://context-hub.wjcy.org/mcp-chat
```

The gateway returns `WWW-Authenticate` with:

```text
resource_metadata="https://context-hub.wjcy.org/.well-known/oauth-protected-resource/mcp-chat"
```

If using ContextHub self-hosted OAuth, the authorization server metadata endpoint
must expose `/oauth/chat/authorize`, `/oauth/chat/token`, and `/userinfo`, while
OpenID discovery must advertise `offline_access`. In ChatGPT Developer Mode, set
scopes to:

```text
openid profile email offline_access
```

Prefer ChatGPT default registration, Dynamic Client Registration, or CIMD. If the
UI is configured with a user-defined OAuth client instead, use:

```text
OAuth client ID: contexthub-chatgpt-gateway
OAuth client secret: leave empty
Token endpoint authentication method: none
```

Click `Scan Tools`, complete OAuth, wait for the scan to finish, then click
`Create`. The app must then be selected from the tools menu in a new chat, unless
it has been published and connected for the workspace. If using an external OIDC
provider, add the ChatGPT-provided OAuth callback URL to the configured OIDC
client allowlist, then rerun the authorized smoke check with a token from the same
OIDC client.

Expected OAuth access sequence for the user-defined client mode is
`GET /oauth/chat/authorize`, `POST /oauth/chat/authorize`,
`POST /oauth/chat/token`, then `POST /mcp-chat`. Dynamic Client Registration adds
`POST /oauth/chat/register` before the authorize request.
