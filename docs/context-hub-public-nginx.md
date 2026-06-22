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
`/api/*`, `/mcp*`, `/_blazor*`, `/health*`, `/login*`, or `/account*`.
Only static asset file extensions should be eligible for edge cache.

Apply the Cloudflare edge rules in
[`context-hub-cloudflare-rules.md`](context-hub-cloudflare-rules.md) before
treating MCP failures as origin or application failures.

Release deploys run two fixed MCP backends (`mcp-server-a` and `mcp-server-b`).
Nginx should route `/mcp` and `/api/` through the `contexthub_mcp` upstream so
one backend can be recreated while the other continues serving existing agents.
The deployment script performs public MCP smoke checks between backend updates;
do not collapse the upstream back to a single `context-hub-mcp-server` target.

Cloudflare rules for `/mcp*`:

```text
DNS:
  Keep the record Proxied (orange-cloud). Do not switch to DNS-only just to fix MCP.

Cache:
  Bypass cache for /mcp*
  Do not override origin Cache-Control / CDN-Cache-Control no-store

Transform / challenges:
  Do not apply response transformation, Rocket Loader, JS challenge, or managed challenge
  to /mcp*. MCP clients are non-browser clients and expect raw Streamable HTTP/SSE.

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
