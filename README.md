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
