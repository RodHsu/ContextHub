# ContextHub Public Nginx Proxy

`context-hub.wjcy.org` should route through Nginx on the shared Docker network
instead of exposing ContextHub service ports publicly.

Expected Docker network:

```text
host_share
```

Expected upstream aliases from `docker-compose.release.yml`:

```text
context-hub-dashboard:8088
context-hub-mcp-server:8080
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

Example Nginx server:

```nginx
map $http_upgrade $connection_upgrade {
    default upgrade;
    '' close;
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
        proxy_buffering off;
        proxy_read_timeout 3600s;
        proxy_send_timeout 3600s;
        proxy_pass http://context-hub-mcp-server:8080;
    }

    location /api/ {
        proxy_pass http://context-hub-mcp-server:8080;
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
