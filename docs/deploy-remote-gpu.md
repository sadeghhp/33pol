# Deploy 33pol on a remote GPU server

This guide installs **33pol** on a Linux host with Docker, using the **gpu-gateway** profile: Postgres and the gateway run in containers; your GPU inference server (vLLM, Ollama, TGI, etc.) runs on the **host** and is reached via `host.docker.internal`.

## Prerequisites

| Requirement | Notes |
|-------------|--------|
| Linux server with SSH | Ubuntu 22.04+ or similar |
| Docker Engine 24+ | `docker compose version` must work |
| Git | To clone the repository |
| Open port for gateway | Default `8080` (configurable) |
| Inference server on host | vLLM, Ollama, or any OpenAI-compatible HTTP API |

No .NET SDK is required on the server; the gateway image is built inside Docker.

## Quick install

```bash
git clone https://github.com/sadeghhp/33pol.git
cd 33pol
chmod +x scripts/install-33pol.sh
./scripts/install-33pol.sh install
```

Non-interactive (generated secrets, Production environment):

```bash
./scripts/install-33pol.sh install --yes --profile gpu-gateway
```

## Architecture

```text
Clients → http://<server>:8080/v1/...  →  33pol gateway (Docker)
                                              │
                                              ▼
                         http://host.docker.internal:<port>/v1/...
                                              │
                                              ▼
                                    vLLM / Ollama (host, GPU)
```

## Configure host LLM

1. Start your inference server on the host (example: vLLM on port `8000`).
2. During install, pick the matching preset or custom port.
3. The installer seeds `deploy/docker/config/models.json` with `host.docker.internal`.

Verify from the server:

```bash
curl -s http://127.0.0.1:8000/v1/models
```

From inside the gateway container, the same upstream is `http://host.docker.internal:8000`.

## Admin and inference keys

| Item | Location |
|------|----------|
| Admin UI | `http://<server-ip>:<GATEWAY_PORT>/admin` |
| Bootstrap admin key | `.env` → `GATEWAY_ADMIN_API_KEY` |
| Inference keys | Admin → **API keys** → Create |

Rotate the bootstrap admin key after first login per [security.md](./security.md).

## Binding and firewall

- **Public access:** `GATEWAY_BIND=0.0.0.0` (installer default).
- **Local / SSH tunnel only:** set `GATEWAY_BIND=127.0.0.1` in `.env`, then `ssh -L 8080:127.0.0.1:8080 user@server`.

Open only the gateway port in your firewall; do not expose Postgres publicly unless required.

## Lifecycle commands

```bash
./scripts/install-33pol.sh status
./scripts/install-33pol.sh upgrade
./scripts/install-33pol.sh reapply                  # apply .env changes (quota, ports, profiles)
./scripts/install-33pol.sh reapply --service gateway  # gateway-only (faster for quota/env)
./scripts/install-33pol.sh logs gateway
./scripts/install-33pol.sh doctor
./scripts/install-33pol.sh uninstall          # stop containers
./scripts/install-33pol.sh uninstall --volumes  # also remove DB volumes
```

State file (no secrets): `~/.33pol/install.state.json`  
Install log: `~/.33pol/install-YYYYMMDD.log`

## Health checks

```bash
bash perf/ci/verify-compose-health-gpu.sh
curl -s http://127.0.0.1:8080/health/live
```

## Observability on the GPU host

For Prometheus and Grafana without a mock upstream, use **gpu-observability**:

```bash
# In .env: COMPOSE_PROFILES=observability
./scripts/install-33pol.sh install --yes --profile gpu-observability
```

For mock upstream plus observability (local demo only), use **full-stack**:

```bash
cp .env.example .env   # COMPOSE_PROFILES=full
docker compose up -d --build
```

Or:

```bash
./scripts/install-33pol.sh install --yes --profile full-stack
```

## Troubleshooting

| Issue | What to check |
|-------|----------------|
| Gateway unhealthy after install | `docker compose logs gateway`; first build can take several minutes |
| Upstream unreachable | Host LLM must listen on `0.0.0.0`, not only `127.0.0.1`, for Docker to reach it |
| Port in use | Change `GATEWAY_PORT` in `.env` and `docker compose up -d` |
| Old compose without optional depends | Docker Compose v2.20+ required |

See also [deploy/docker/README.md](../deploy/docker/README.md) and [lm-studio-with-33pol.md](./lm-studio-with-33pol.md) (same `host.docker.internal` pattern on desktop).
