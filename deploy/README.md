# Deployment artifacts

| Path | Description |
|------|-------------|
| [docker/](./docker/) | Compose service definitions (WP5.4); run from repo root via [`docker-compose.yml`](../docker-compose.yml) |
| [grafana/](./grafana/) | `provisioning/` (datasources, providers) + `dashboards/` JSON (Phase 4+) |
| [prometheus/](./prometheus/) | Prometheus alert rules |
| [helm/33pol/](./helm/33pol/) | Helm chart (Deployment, Service, HPA, ServiceMonitor) |
| `otel-collector/` | OpenTelemetry collector sample (Phase 4–5) |

See [docker/README.md](./docker/README.md) for Compose usage.

## Interactive installer (remote GPU / server)

```bash
./scripts/install-33pol.sh install
```

Profiles: **gpu-gateway**, **gpu-observability** (Prometheus + Grafana), or **full-stack** (adds mock upstream). See [docs/deploy-remote-gpu.md](../docs/deploy-remote-gpu.md).

Compose profiles use `COMPOSE_PROFILES` in `.env` (see `.env.example`): `observability` or `full`.

## Helm

```bash
helm template 33pol deploy/helm/33pol
helm upgrade --install 33pol deploy/helm/33pol -f my-values.yaml
```

| Value | Purpose |
|-------|---------|
| `gateway.existingSecret` | **Required** (or inline `gateway.keyPepper` + `gateway.adminApiKey` for dev): Secret with keys `keyPepper`, `adminApiKey` (optional `metricsScrapeToken`). The chart fails to render without one; the pepper must stay stable for the life of the database |
| `gateway.metrics.allowAnonymous` | `false` (default) keeps `/metrics` behind an operator key or the scrape token; the ServiceMonitor sends the token as a Bearer credential |
| `podSecurityContext` / `securityContext` | Restricted-PSS defaults: non-root uid 1654, no privilege escalation, drop ALL, seccomp RuntimeDefault, read-only root fs (`/tmp` is an emptyDir) |
| `probes.startup` | startupProbe on `/health/live` (30 x 5s) before liveness applies |
| `gateway.operatorConsole.enabled` | Keep `false` in Kubernetes |
| `persistence.enabled` | Provision the ReadWriteOnce PVC for the embedded SQLite database (keep `replicaCount: 1`) |
| `serviceMonitor.enabled` | Prometheus Operator scrape of `/metrics` |
| `ingress.enabled` | Expose gateway HTTP (configure SSE timeouts for streaming) |
| `autoscaling.enabled` | Must stay `false` (single-instance SQLite; the chart refuses to render otherwise) |

**Grafana:** not included in the Helm chart. Use [grafana/](./grafana/) JSON + provisioning with your own Grafana/Prometheus stack, or run the full local stack via [docker/README.md](./docker/README.md).

Container images are built from the repo [Dockerfile](../Dockerfile).

| Workflow | When | Image tags |
|----------|------|------------|
| [docker-image.yml](../.github/workflows/docker-image.yml) | Push to `main` | `latest`, branch, commit SHA |
| [release.yml](../.github/workflows/release.yml) | Push tag `v*` | Semver (e.g. `2.0.0`, `2.0`) |

**Production:** pin `image.repository` and `image.tag` to a semver release (see [docs/release.md](../docs/release.md)), not `latest`.
