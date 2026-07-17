# Public Nginx Proxy

This guide shows a generic HTTPS reverse-proxy setup for a public ContextHub deployment. Replace all example hostnames, certificate paths, and upstream names with your own deployment values.

## Recommended Topology

```text
Public HTTPS hostname
  -> reverse proxy
  -> dashboard:8088
  -> mcp-server:8080
  -> chatgpt-gateway:8083
```

Keep PostgreSQL, Redis, `embedding-service`, and `worker` on private networks.

## Public Routes

| Route | Upstream | Purpose |
| --- | --- | --- |
| `/` | `dashboard:8088` | Dashboard UI |
| `/_blazor*` | `dashboard:8088` | Blazor transport |
| `/health*` | dashboard or MCP server, depending on route policy | Readiness checks |
| `/api/*` | `mcp-server:8080` | REST APIs |
| `/mcp` | `mcp-server:8080` | Full ContextHub MCP endpoint |
| `/mcp-chat` | `chatgpt-gateway:8083/mcp` | Restricted chat-agent MCP gateway |
| `/.well-known/*/mcp-chat` | `chatgpt-gateway:8083` | OAuth/OIDC metadata for chat-agent gateway |
| `/oauth/chat/*` | `chatgpt-gateway:8083` | OAuth authorization code flow |
| `/userinfo` | `chatgpt-gateway:8083` | OIDC userinfo |

`/mcp-chat` must stay separate from `/mcp`. Chat agents should see only the gateway allowlist, project allowlist, OAuth/OIDC checks, rate limits, audit trails, and proposal-gated durable writes.

## Cache Policy

Dynamic routes must not be cached:

```text
/api/*
/mcp
/mcp-chat
/.well-known/*
/oauth/chat/*
/userinfo
/_blazor*
/health*
/login*
/account*
```

Recommended dynamic response headers:

```text
Cache-Control: no-store, no-cache, max-age=0, must-revalidate, no-transform
CDN-Cache-Control: no-store
```

Static dashboard assets may be cached with immutable asset headers.

## Nginx Example

```nginx
map $http_upgrade $connection_upgrade {
    default upgrade;
    '' close;
}

upstream contexthub_mcp {
    server context-hub-mcp-server:8080 max_fails=2 fail_timeout=10s;
    keepalive 32;
}

upstream contexthub_dashboard {
    server context-hub-dashboard:8088 max_fails=2 fail_timeout=10s;
    keepalive 16;
}

upstream contexthub_chat_gateway {
    server context-hub-chatgpt-gateway:8083 max_fails=2 fail_timeout=10s;
    keepalive 16;
}

server {
    listen 443 ssl http2;
    server_name context-hub.example.com;

    ssl_certificate /etc/nginx/certs/context-hub.example.com/fullchain.pem;
    ssl_certificate_key /etc/nginx/certs/context-hub.example.com/privkey.pem;

    client_max_body_size 100m;

    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header X-Real-IP $remote_addr;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Host $host;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_set_header X-Forwarded-Port $server_port;

    location = /mcp {
        add_header Cache-Control "no-store, no-cache, max-age=0, must-revalidate, no-transform" always;
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
        proxy_pass http://contexthub_chat_gateway/mcp;
    }

    location ~ ^/\.well-known/(oauth-protected-resource|oauth-authorization-server|openid-configuration)/mcp-chat$ {
        add_header Cache-Control "no-store, no-cache, max-age=0, must-revalidate, no-transform" always;
        add_header CDN-Cache-Control "no-store" always;
        add_header X-Accel-Buffering "no" always;
        proxy_cache off;
        proxy_buffering off;
        proxy_pass http://contexthub_chat_gateway;
    }

    location /oauth/chat/ {
        add_header Cache-Control "no-store, no-cache, max-age=0, must-revalidate, no-transform" always;
        add_header CDN-Cache-Control "no-store" always;
        add_header X-Accel-Buffering "no" always;
        proxy_cache off;
        proxy_buffering off;
        proxy_request_buffering off;
        proxy_pass http://contexthub_chat_gateway;
    }

    location = /userinfo {
        add_header Cache-Control "no-store, no-cache, max-age=0, must-revalidate, no-transform" always;
        add_header CDN-Cache-Control "no-store" always;
        proxy_cache off;
        proxy_pass http://contexthub_chat_gateway;
    }

    location /api/ {
        add_header Cache-Control "no-store, no-cache, max-age=0, must-revalidate, no-transform" always;
        add_header CDN-Cache-Control "no-store" always;
        proxy_cache off;
        proxy_pass http://contexthub_mcp;
    }

    location /_blazor {
        add_header Cache-Control "no-store, no-cache, max-age=0, must-revalidate, no-transform" always;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection $connection_upgrade;
        proxy_read_timeout 3600s;
        proxy_pass http://contexthub_dashboard;
    }

    location / {
        proxy_pass http://contexthub_dashboard;
    }
}

server {
    listen 80;
    server_name context-hub.example.com;
    return 301 https://$host$request_uri;
}
```

## Load-Balanced MCP Backends

If you run multiple `mcp-server` containers for rolling updates, route `/mcp` and `/api/` through a shared upstream:

```nginx
upstream contexthub_mcp {
    ip_hash;
    server context-hub-mcp-server-a:8080 max_fails=2 fail_timeout=10s;
    server context-hub-mcp-server-b:8080 max_fails=2 fail_timeout=10s;
    keepalive 32;
}
```

Reload Nginx after containers are recreated if your proxy resolves Docker DNS only at config load time.

## Smoke Checks

After deployment:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-contexthub-mcp.ps1 -Endpoint https://context-hub.example.com/mcp
powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-contexthub-mcp-chat.ps1 -Endpoint https://context-hub.example.com/mcp-chat
```

For authorized chat-agent simulation, provide a valid OAuth access token through `CONTEXTHUB_MCP_CHAT_TOKEN` and run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-contexthub-mcp-chat.ps1 -Endpoint https://context-hub.example.com/mcp-chat -RequireAuthorizationToken
```

Expected unauthenticated behavior:

- `/mcp` returns `401`
- `/mcp-chat` returns `401`
- neither endpoint returns HTML challenge pages
- both endpoints keep dynamic `no-store` behavior
