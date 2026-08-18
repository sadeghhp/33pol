# Docker Compose — local 33pol stack

Runs the **gateway** by default, with an embedded SQLite database persisted on the `gateway-data` volume (no external database service). Optional Compose profile **`full`** adds Prometheus, Grafana, and a WireMock mock upstream.

## Compose profiles

| Profile | `COMPOSE_PROFILES` | Services |
|---------|----------------------|----------|
| **gpu-gateway** (default) | empty / unset | `gateway` (embedded SQLite) |
| **gpu-observability** | `observability` | above + `prometheus`, `grafana` |
| **full-stack** (local demo) | `full` | above + `mock-upstream` |

Set in `.env` (see `.env.example`). Use `observability` on GPU servers that need metrics dashboards without WireMock. Use `full` only for local demo stacks with a mock upstream.

Interactive install: [../../scripts/install-33pol.sh](../../scripts/install-33pol.sh) and [docs/deploy-remote-gpu.md](../../docs/deploy-remote-gpu.md).

## Prerequisites

- Docker Engine 24+ with Compose v2 (`docker compose`)
- No local .NET SDK required (gateway image is built by Compose)

## Gateway image rebuild time

The gateway `Dockerfile` restores NuGet packages in a layer that copies only `*.csproj` files, then copies full `src/` and publishes. That means:

- **First build** (or after `Directory.Packages.props` / project file changes): expect ~1–3 minutes for `dotnet restore` inside the image.
- **Code-only edits** (`.cs`, `wwwroot`, etc.): restore should show **CACHED**; only `publish` runs (~30–90s).
- **Interrupted builds** (`Ctrl+C` before publish finishes) discard progress — the next `--build` pays restore again. Let one full `docker compose build gateway` complete.

NuGet downloads are cached across builds via BuildKit (`--mount=type=cache` on `/root/.nuget/packages`). Do not use `docker compose build --no-cache` for routine dev unless you are debugging the image itself.

## Quick start (recommended — repo root)

From the repository root:

```bash
cp .env.example .env   # includes COMPOSE_PROFILES=full
docker compose up -d --build
bash perf/ci/verify-compose-health.sh
```

For **gpu-gateway only**, omit `COMPOSE_PROFILES` in `.env` (or leave it empty), then use `bash perf/ci/verify-compose-health-gpu.sh`.

| Service        | URL / port (defaults)        |
|----------------|------------------------------|
| Gateway        | http://localhost:8080        |
| Admin UI       | http://localhost:8080/admin (key from `GATEWAY_ADMIN_API_KEY`) |
| Mock upstream  | http://localhost:18080       |
| Prometheus     | http://localhost:9090        |
| Grafana        | http://localhost:3000 (admin / admin) — folder **33pol**: [33pol Gateway](http://localhost:3000/d/33pol-gateway/33pol-gateway) (RED, backends), [Traffic & tokens](http://localhost:3000/d/33pol-gateway-traffic/33pol-gateway-traffic) — see [observability.md](../../docs/observability.md) |
| Database       | embedded SQLite at `/data/gateway.db` on the `gateway-data` volume |

All published ports bind to **127.0.0.1** by default (`GATEWAY_BIND`, `MOCK_UPSTREAM_BIND`, `PROMETHEUS_BIND`, `GRAFANA_BIND`); Prometheus and WireMock have no authentication, so set the `*_BIND` variables to `0.0.0.0` only deliberately. Prometheus scrapes `/metrics` with the Bearer token from `GATEWAY_METRICS_SCRAPE_TOKEN` (compose secret → `authorization.credentials_file`); the dev stack also sets `GATEWAY_METRICS_ALLOW_ANONYMOUS=true` so `curl :8080/metrics` works — set it to `false` (and a strong token) for any non-loopback deploy.

Test the mock:

```bash
curl -s http://localhost:18080/v1/models
curl -s -X POST http://localhost:18080/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d "{\"model\":\"mock-gpt\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}"
```

Test the gateway (via registry → mock):

```bash
curl -s http://localhost:8080/health/live
curl -s http://localhost:8080/v1/models -H "Authorization: Bearer <api-key>"
```

`models.json` is **local operator config** (gitignored). Copy the committed template before first run:

```bash
cp deploy/docker/config/models.json.example deploy/docker/config/models.json
```

The default points at `http://mock-upstream:8080` (full-stack profile). Edit on the host for your upstreams — **do not commit** internal URLs or production topology.

`host.docker.internal` is configured so backends running on the host machine are reachable from the gateway container.

The whole `deploy/docker/config/` directory is mounted at `/app/config` (including `models.json`) so the Admin UI (**Models** tab) can persist registry changes. A single-file bind mount cannot be atomically replaced on Docker Desktop (EBUSY).

## First boot (fresh database)

33pol deploys **greenfield**: it starts from an empty SQLite database and there is no import
from any prior datastore. EF migrations create the schema only — they never carry data across.
On the first boot of an empty `gateway.db`, `GatewayDbBootstrap` seeds the intended starting
state, **once** (skipped forever after, so this is not a re-sync):

- **Model routes** ← `models.json` (if present at `Gateway:ModelsConfigPath`).
- **CORS origins, rate limits, quota scalars** ← `appsettings`.
- **One admin API key** ← `GATEWAY_ADMIN_API_KEY`, plus the default tenant.

There are **no pre-existing inference keys, tenants, model grants, or usage history** — you
issue inference keys through the Admin UI / `POST /admin/api/keys` after first boot. If you are
ever migrating from an external datastore instead of starting fresh, that is a separate one-off
import (not provided here); a plain redeploy never imports data.

## Versioned deploy & instant rollback

For managed deployments, use **[`33pol-deploy.sh`](33pol-deploy.sh)** instead of raw `docker compose up --build`. It builds an immutable, version-tagged gateway image (`33pol-gateway:<git-sha>-<utc>`), snapshots the database before every rollout, health-gates the deploy, and **auto-rolls-back** if the gateway does not come up. Rolling back to a prior version is a seconds-long image swap — no rebuild.

```bash
cd deploy/docker
./33pol-deploy.sh deploy                 # snapshot DB → build tagged image → roll out → verify
./33pol-deploy.sh status                 # current/previous version + health
./33pol-deploy.sh rollback               # instant swap to the previous version
./33pol-deploy.sh versions               # list built image versions
./33pol-deploy.sh history                # deploy/rollback audit log
./33pol-deploy.sh backup                 # database snapshot on demand
./33pol-deploy.sh restore <file>         # restore a database snapshot
./33pol-deploy.sh help                   # all commands and flags
```

Key flags: `--version <v>` (explicit tag, e.g. a release semver), `--profiles observability|full`, `--to <v>` (rollback target), `--restore-db <file>` (restore DB during rollback), `--no-backup`, `--timeout <sec>`, `--yes`, `--dry-run`.

**Rollback and the database:** the app image rolls back instantly, but EF Core migrations auto-apply on startup and are **not** auto-reverted. The pre-deploy snapshot is your schema rollback — for a release that changed the schema, roll back with `./33pol-deploy.sh rollback --to <prev> --restore-db <pre-deploy-snapshot>`. Keeping migrations backward-compatible (add nullable columns; avoid drop/rename in the same release the code needs) lets an image-only rollback always be safe.

Runtime state (versions, snapshots, the generated image-pin override) lives in the gitignored `deploy/docker/.deploy/` directory.

### OpenRouter (cloud)

**Recommended (no provider key in `.env`):**

1. Open http://localhost:8080/admin → sign in with `GATEWAY_ADMIN_API_KEY`.
2. **Routing → Add model** — model name (e.g. `anthropic/claude-3.5-sonnet`), URL `https://openrouter.ai/api`, paste your OpenRouter API key.
3. **API keys → Create** an inference key for clients.

The gateway writes `config/upstream-secrets.enc` on the mounted `config/` volume (writable). Restart preserves secrets. **`upstream-secrets.enc` is gitignored** — copy from `upstream-secrets.enc.example` on first run; Admin UI creates encrypted entries at runtime. **Never commit** this file (historical ciphertext + default dev pepper = weak protection).

**Optional GitOps:** set provider keys in `.env` (see `.env.example` for `OPENROUTER_API_KEY`, `OPENAI_API_KEY`, `TOGETHER_API_KEY`, `GROQ_API_KEY`, `DEEPSEEK_API_KEY`, `MISTRAL_API_KEY`, `FIREWORKS_API_KEY`, `DASHSCOPE_API_KEY`) and reference the matching `upstreamAuth.envVar` in `models.json` instead of the admin API key field. Provider discovery HTTP API is still available for automation (not exposed in the UI).

See [docs/integrations.md](../../docs/integrations.md#openrouter).

### LM Studio (host LLM)

Full walkthrough (LM Studio setup, admin UI, inference keys, curl + Python, troubleshooting):

**[docs/lm-studio-with-33pol.md](../../docs/lm-studio-with-33pol.md)**

**Operator console:** Disabled in the gateway container (`Gateway:OperatorConsole:Enabled=false`). Use HTTP `/admin` and Grafana. For a TTY-only local experiment, see [docs/operator-console.md](../../docs/operator-console.md).

## Alternate path (this directory)

Equivalent stack when you prefer to run Compose from `deploy/docker/`:

```bash
cd deploy/docker
cp .env.example .env
cp config/models.json.example config/models.json
cp config/upstream-secrets.enc.example config/upstream-secrets.enc
docker compose up -d --build
```

## Layout

| Path | Purpose |
|------|---------|
| `docker-compose.yml` | Service definitions (also included from repo root) |
| `config/upstream-secrets.enc.example` | Empty encrypted store template (copy locally; gitignored at runtime) |
| `config/models.json.example` | Registry template (copy to `models.json` locally) |
| `config/models.json` | Operator registry (gitignored; volume-mounted into gateway) |
| `config/prometheus.yml` | Scrape config (gateway job) |
| `wiremock/` | Mock upstream mappings |
| `../grafana/` | Provisioning + dashboard JSON (Phase 4+) |
| `../prometheus/alerts/` | Alert rules for `promtool` / Prometheus |
| `../../docker-compose.yml` | Root entry point (`include` of this file) |

## Browser applications (CORS)

| `ASPNETCORE_ENVIRONMENT` | Configuration |
|--------------------------|---------------|
| **Development** (default in `.env.example`) | Any browser origin allowed; no origin list needed. |
| **Production** | Allowlist exact SPA origins (no path, no trailing slash). `localhost` ≠ `127.0.0.1`. |

**Preferred:** Admin UI **Settings → CORS**, or `GET`/`PUT` `/admin/api/cors` (writes `Gateway:Cors:AllowedOrigins` in appsettings and hot-reloads).

**`.env` (Docker):** Add origins directly in repo-root `.env` — any number, no Compose mapping:

```bash
ASPNETCORE_ENVIRONMENT=Production
GATEWAY_CORS_ALLOWED_ORIGIN_0=https://sadeghhp.github.io
GATEWAY_CORS_ALLOWED_ORIGIN_1=http://localhost:5173
GATEWAY_CORS_ALLOWED_ORIGIN_2=http://localhost
# or: GATEWAY_CORS_ALLOWED_ORIGINS=https://sadeghhp.github.io,http://localhost:5173
docker compose up -d --force-recreate gateway
```

The gateway loads `.env` via `env_file` and reads `GATEWAY_CORS_ALLOWED_ORIGIN_*` / `GATEWAY_CORS_ALLOWED_ORIGINS` at startup.

Verify after changing config / restarting the gateway:

```bash
docker compose up -d gateway
curl -i -X OPTIONS "http://localhost:${GATEWAY_PORT:-8080}/v1/chat/completions" \
  -H "Origin: https://sadeghhp.github.io" \
  -H "Access-Control-Request-Method: POST" \
  -H "Access-Control-Request-Headers: authorization,content-type"
```

Production with a configured origin should return `Access-Control-Allow-Origin`. See [docs/security.md](../../docs/security.md) and [docs/runbooks/cors-admin.md](../../docs/runbooks/cors-admin.md).

## Stop and reset

```bash
docker compose down
docker compose down -v   # also removes volumes
```

Run from repo root or `deploy/docker/` depending on where you started the stack.
