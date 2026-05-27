# Docker Compose — local 33pol stack

Runs **gateway**, **Postgres**, **Prometheus**, **Grafana**, and a **WireMock** OpenAI-compatible mock upstream in one command.

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
cp .env.example .env
docker compose up -d --build
bash perf/ci/verify-compose-health.sh
```

| Service        | URL / port (defaults)        |
|----------------|------------------------------|
| Gateway        | http://localhost:8080        |
| Admin UI       | http://localhost:8080/admin (key from `GATEWAY_ADMIN_API_KEY`) |
| Mock upstream  | http://localhost:18080       |
| Prometheus     | http://localhost:9090        |
| Grafana        | http://localhost:3000 (admin / admin) — folder **33pol**: [33pol Gateway](http://localhost:3000/d/33pol-gateway/33pol-gateway) (RED, backends), [Traffic & tokens](http://localhost:3000/d/33pol-gateway-traffic/33pol-gateway-traffic) — see [observability.md](../../docs/observability.md) |
| PostgreSQL     | localhost:5432               |

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

`models.json` is mounted from `deploy/docker/config/models.json` and points at `http://mock-upstream:8080`.

`host.docker.internal` is configured so backends running on the host machine are reachable from the gateway container.

The whole `deploy/docker/config/` directory is mounted at `/app/config` (including `models.json`) so the Admin UI (**Models** tab) can persist registry changes. A single-file bind mount cannot be atomically replaced on Docker Desktop (EBUSY).

### OpenRouter (cloud)

**Recommended (no provider key in `.env`):**

1. Open http://localhost:8080/admin → sign in with `GATEWAY_ADMIN_API_KEY`.
2. **Routing → Add model** — model name (e.g. `anthropic/claude-3.5-sonnet`), URL `https://openrouter.ai/api`, paste your OpenRouter API key.
3. **API keys → Create** an inference key for clients.

The gateway writes `config/upstream-secrets.enc` on the mounted `config/` volume (writable). Restart preserves secrets.

**Optional GitOps:** set `OPENROUTER_API_KEY` in `.env` and use `upstreamAuth.envVar` in `models.json` instead of the admin API key field. Provider discovery HTTP API is still available for automation (not exposed in the UI).

See [docs/integrations.md](../../docs/integrations.md#openrouter).

### LM Studio (host LLM)

Full walkthrough (LM Studio setup, admin UI, inference keys, curl + Python, troubleshooting):

**[docs/lm-studio-with-33pol.md](../../docs/lm-studio-with-33pol.md)**

**Operator console:** Disabled in the gateway container (`Gateway:OperatorConsole:Enabled=false`). Use HTTP `/admin` and Grafana. For a TTY-only local experiment, see [docs/implementation-plan/08-operator-console.md](../../docs/implementation-plan/08-operator-console.md).

## Alternate path (this directory)

Equivalent stack when you prefer to run Compose from `deploy/docker/`:

```bash
cd deploy/docker
cp .env.example .env
docker compose up -d --build
```

## Layout

| Path | Purpose |
|------|---------|
| `docker-compose.yml` | Service definitions (also included from repo root) |
| `config/models.json` | Registry sample (volume-mounted into gateway) |
| `config/prometheus.yml` | Scrape config (gateway job) |
| `wiremock/` | Mock upstream mappings |
| `../grafana/` | Provisioning + dashboard JSON (Phase 4+) |
| `../prometheus/alerts/` | Alert rules for `promtool` / Prometheus |
| `../../docker-compose.yml` | Root entry point (`include` of this file) |

## Stop and reset

```bash
docker compose down
docker compose down -v   # also removes volumes
```

Run from repo root or `deploy/docker/` depending on where you started the stack.
