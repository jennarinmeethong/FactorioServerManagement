# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A single Docker container that manages one Linux `amd64` Factorio dedicated server. It has two parts:

- **`src/FactorioManager.Api`** — ASP.NET Core 10 (net10.0) minimal-API control plane. Owns the Factorio process lifecycle, settings, mods, saves, backups, versions, users/auth, and a SignalR hub for live status/log streaming.
- **`src/FactorioManager.Web`** — React 19 + TypeScript + Tailwind dashboard, built with Vite and Bun. Built assets are copied into the API's `wwwroot` and served as static files (`app.MapFallbackToFile("index.html")` in [Program.cs](src/FactorioManager.Api/Program.cs)).

All durable state (SQLite DB, Factorio version binaries, saves, backups, mods, config, logs, secrets) lives under a host-mounted `./data` directory, never in the image.

## Commands

Backend (from repo root):
```sh
dotnet build FactorioServerManager.sln
dotnet test FactorioServerManager.sln
dotnet test FactorioServerManager.sln --filter FullyQualifiedName~NativeRuntimeTests   # single test class
dotnet run --project src/FactorioManager.Api --urls http://localhost:8080
```

Frontend (from `src/FactorioManager.Web`):
```sh
bun install
bun run dev      # Vite dev server; proxies /api and /hubs (incl. WebSocket) to http://localhost:8080
bun run build    # tsc -b && vite build; outputs directly into ../FactorioManager.Api/wwwroot
bun run test     # bun test
```

Run backend and frontend dev servers in separate terminals; there is no combined dev script. When running the API outside Docker, set the `DataRoot` environment variable to an alternate local path (default is `./data` relative to the app base directory).

Docker (production-shaped build/run):
```sh
docker compose up --build -d
docker compose logs factorio-manager
docker compose exec factorio-manager cat /data/setup-code   # read first-run setup code
```

CI ([.github/workflows/ci.yml](.github/workflows/ci.yml)) runs `dotnet test` on the solution, then `bun install --frozen-lockfile` + `bun run build` for the web project, then (on push, not PRs) builds the Docker image.

`TreatWarningsAsErrors` is set solution-wide in [Directory.Build.props](Directory.Build.props) — a build with warnings fails.

## Backend architecture

Everything is wired up in [Program.cs](src/FactorioManager.Api/Program.cs) as a single minimal-API file: DI registrations at the top, then endpoint groups. There's no MVC/controllers layer — read this file top-to-bottom to find any endpoint.

**Core services (all singletons, see DI block in Program.cs):**
- `DataPaths` — resolves every on-disk location under the data root (`versions/`, `saves/`, `backups/`, `mods/`, `config/`, `logs/`, `app.db`, `setup-code`). Add new persisted data here, not with ad-hoc paths.
- `StateStore` — SQLite-backed key/value store (`app_state` table, JSON values) for settings/mods/profiles, plus the `users` and `audit_events` tables. `GetAsync<T>`/`SetAsync<T>` are the read/write primitives used everywhere instead of a traditional repository layer.
- `ServerSupervisor` — owns the actual Factorio `Process`: start/stop/restart, save creation, log tailing, and automatic-restart-with-backoff on unexpected exit. Guards all state transitions with a single `SemaphoreSlim` gate. Pushes `status`/`log` events to connected clients over the `StatusHub` SignalR hub. Mod-settings must be freshly materialized and validated against current settings/mods before every start (fail-closed).
- `NativeRuntimeDetector` / `NativeMapExchangeService` / `NativeMapRoutes` — the "native runtime" path: verifies a version directory actually contains a host-compatible Factorio executable (checks the PE/ELF header, not just the file suffix — a version directory can contain a binary copied from another OS) and drives native, out-of-process operations (e.g. map exchange string import/export) against it.
- `VersionService`, `ModService`, `ModSettingsService`, `MapControlCatalog`, `MapGeneration`, `BackupService` — version download/apply, Mod Portal search/install/update, mod-settings discovery/validation/materialization into Factorio's binary mod-settings format, map-generation/map-control catalogs (vanilla vs Space Age expansion), and backup create/list/restore.
- `AccountService` / `Security.cs` / `SecretStore` — cookie-auth accounts with roles (`owner` > `admin` > `viewer`), CSRF token issued at sign-in and required on mutations (`CsrfFilter.Validate`), and encrypted-at-rest secrets (Factorio Portal credentials, Discord/Telegram notification settings) via ASP.NET Data Protection keys stored in `config/keys`.
- `SafeDiagnostics.Redact` — every log line, error detail, and downloaded log file is passed through this before it can reach a client or disk; it strips auth headers, tokens, passwords, secrets, and webhook URLs via regex. Always route new error/log surfaces through this rather than emitting raw exception text.
- `MaintenanceWorker` (hosted service) + `MaintenanceStatusService`/`MaintenanceHistoryService`/`ServerEventHistoryService`/`HealthHistoryService` — scheduled backups/update checks/restarts and their audit trails, surfaced via `/api/maintenance`, `/api/server-history`, `/api/health-history`.

**Endpoint groups in Program.cs**, all under `/api`:
- `/api/auth/*` — unauthenticated except `logout`/`me`/`password`; setup and login are rate-limited (`login` policy: 5/5min).
- `owner` group — `RequireAuthorization()` + owner-only policy filter (`OwnerAuthorizeAsync`, audits denials): user management, notification settings, config export/import.
- `api` group — `RequireAuthorization()` + CSRF filter + `ApiMutationAuthorizationFilter` (role gating on mutations): settings, mod-settings, map-controls, control (start/stop/restart), logs, system-health, saves, backups, versions, mods, players, live-players/live-chat (rate-limited via `live-control` policy: 20/min).

**Authorization pattern to follow:** most write endpoints check `supervisor.IsRunning` and reject with `ApiErrors.Conflict` if the server must be stopped first (settings, mod installation/toggle, mod-settings, save selection/upload) — this is a deliberate invariant preventing partial live configuration, not an oversight to work around.

**Cookie/session invariant:** the auth cookie embeds a `security_stamp` and `role` claim that are re-validated against the DB on every request (`OnValidatePrincipal`); changing a user's password or role invalidates their existing sessions. If you change what's embedded in the principal, bump the cookie name (see the comment in Program.cs) so stale cookies from an older build can't be replayed.

## Frontend

Minimal so far: [App.tsx](src/FactorioManager.Web/src/App.tsx), `main.tsx`, and shared UI primitives under `src/components/ui` (shadcn-style, built on `class-variance-authority`/`tailwind-merge`/`lucide-react`). Uses `@microsoft/signalr` to consume the `/hubs/status` hub for live status/log updates. Path alias `@` maps into `src/` (see `tsconfig.app.json`/`vite.config.ts`) if present when adding imports.

## Persistent data layout (`./data`, mounted at `/data` in the container)

`versions/` (downloaded Factorio builds), `saves/`, `backups/`, `mods/`, `config/` (server-settings.json, map-gen/map-settings.json, secrets.json, `keys/` for Data Protection), `logs/`, `app.db` (SQLite), `setup-code` (deleted once first-run setup succeeds). Never commit anything from `./data` — it holds live secrets and save data.

Reverse-proxy note if touching networking: the proxy must forward both `/api` and the SignalR `/hubs/status` endpoint including WebSocket upgrade headers; the Factorio game port `34197/udp` is separate and must never go through the HTTP proxy.
