# Deployment artifacts

| Path | Description |
|------|-------------|
| [docker/](./docker/) | Compose service definitions (WP5.4); run from repo root via [`docker-compose.yml`](../docker-compose.yml) |
| [grafana/](./grafana/) | `provisioning/` (datasources, providers) + `dashboards/` JSON (Phase 4+) |
| [prometheus/](./prometheus/) | Prometheus alert rules |
| [helm/33pol/](./helm/33pol/) | Helm chart (Deployment, Service, HPA, ServiceMonitor) |
| `otel-collector/` | OpenTelemetry collector sample (Phase 4–5) |

See [docker/README.md](./docker/README.md) for Compose usage.

## Helm

```bash
helm template 33pol deploy/helm/33pol
helm upgrade --install 33pol deploy/helm/33pol -f my-values.yaml
```

| Value | Purpose |
|-------|---------|
| `gateway.operatorConsole.enabled` | Keep `false` in Kubernetes |
| `postgresql.enabled` | Wire `ConnectionStrings__GatewayDb` from a secret |
| `serviceMonitor.enabled` | Prometheus Operator scrape of `/metrics` |
| `ingress.enabled` | Expose gateway HTTP (configure SSE timeouts for streaming) |
| `autoscaling.enabled` | HPA on CPU |

Container images are built from the repo [Dockerfile](../Dockerfile); CI publishes to `ghcr.io/<repository>` on `main` (see `.github/workflows/docker-image.yml`).
