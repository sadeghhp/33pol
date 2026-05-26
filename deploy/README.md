# Deployment artifacts

| Path | Description |
|------|-------------|
| [docker/](./docker/) | Docker Compose local stack (WP5.4) |
| [grafana/](./grafana/) | `provisioning/` (datasources, providers) + `dashboards/` JSON (Phase 4+) |
| [prometheus/](./prometheus/) | Prometheus alert rules |
| [helm/33pol/](./helm/33pol/) | Helm chart (Deployment, Service, HPA, ServiceMonitor) |
| `otel-collector/` | OpenTelemetry collector sample (Phase 4–5) |

See [docker/README.md](./docker/README.md) for Compose usage.
