# Factorio Server Manager

คู่มือ Docker แบบ HTML: [docs/docker-guide.html](docs/docker-guide.html)

One Docker container manages one Linux `amd64` Factorio dedicated server. It contains an ASP.NET Core 10 control plane and a React dashboard; all durable state lives in the host-mounted `./data` directory.

## Run

```sh
docker compose up --build -d
docker compose logs factorio-manager
```

Copy the **first-run setup code** from the logs, then open [http://localhost:8080](http://localhost:8080). The first-run page uses that code once to create the admin account and optionally save the Factorio username/token needed to download headless server versions and install Mod Portal content.

The default compose mapping deliberately binds the dashboard to localhost. To use it on a private LAN, replace `127.0.0.1:8080:8080` with `8080:8080`, restrict it with a host firewall, and use a reverse proxy with TLS before exposing it to the internet.

### Reverse proxy and TLS

Keep the compose mapping bound to `127.0.0.1:8080:8080`; the proxy and the manager should be on the same host. The proxy must pass both `/api` and the SignalR endpoint `/hubs/status`, including WebSocket upgrade headers. Use a trusted certificate authority and allow inbound TCP 80/443 only as needed for HTTP/TLS; keep the manager’s TCP 8080 rule limited to localhost or the proxy host.

Caddy (automatic HTTPS):

```caddyfile
manager.example.com {
    reverse_proxy 127.0.0.1:8080
}
```

Nginx (the `map` belongs in the `http` block):

```nginx
map $http_upgrade $connection_upgrade {
    default upgrade;
    ''      close;
}

server {
    listen 443 ssl;
    server_name manager.example.com;
    ssl_certificate /etc/letsencrypt/live/manager.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/manager.example.com/privkey.pem;

    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection $connection_upgrade;
        proxy_read_timeout 3600s;
    }
}
```

Do not send the Factorio game port through this HTTP proxy: `34197/udp` is a separate multiplayer port and should be exposed/firewalled independently.

## Persistent data

`./data` is a bind mount and contains the SQLite state database, Factorio version cache, saves, backups, mods, configuration, logs, and locally stored Factorio credentials. Back it up from the host; do not commit it to source control.

The game port is UDP `34197`. The dashboard controls the Factorio process inside the container; stopping the container itself remains a Docker operation.

The dashboard's **System Health** page shows runtime memory, uptime, disk capacity, and the inventory of saves, backups, versions, and mods. Changes that affect the running server (server settings, save selection/upload/restore, or mod installation/toggle) are rejected until the server is stopped, preventing a partial live configuration.

Applying a different Factorio version requires the selected save to be backed up first. If the server was running, the manager stops it, applies the version, and attempts to restart it; a failed restart restores the previous version configuration.

## Development

```sh
# Terminal 1
dotnet run --project src/FactorioManager.Api --urls http://localhost:8080

# Terminal 2
cd src/FactorioManager.Web
bun install
bun run dev
```

Vite proxies `/api` and `/hubs` to the backend. Use an alternate local `DataRoot` environment variable when running outside Docker.
