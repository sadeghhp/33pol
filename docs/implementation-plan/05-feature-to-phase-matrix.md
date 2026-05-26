# Feature-to-Phase Matrix

Maps proposal capabilities to implementation phases. Use for Taiga tagging (`phase:P1` … `phase:P5`).

## v1 admin URL migration (breaking)

| v1 path | v2 path | Phase |
|---------|---------|-------|
| `POST /admin/reload` | `POST /admin/api/config/reload` | P2 (open) → P3 (secured) |
| `GET /admin/status` | `GET /admin/api/config/status` | P2 → P3 |
| WebSocket `GET /hubs/admin` (SignalR) | `GET /admin/api/events/stream` (SSE, optional) | P4 → P5 (UI) |

## v1 parity

| Feature | Phase |
|---------|-------|
| OpenAI POST inference paths | P2 |
| Body-based model routing | P2 |
| SSE streaming | P2 |
| `models.json` registry + aliases | P2 |
| Hot reload | P2 (unauthenticated) → P3 (secured) |
| Backend health probes | P2 |
| `GET /v1/models` | P2 |
| `GET /health`, `/stats` | P2 (basic) → P3/P4 (expanded) |
| Kestrel streaming config | P2 |
| Optional Postgres | P3 (identity) → P4/P5 (usage) |

## v2 core

| Feature | Phase |
|---------|-------|
| .NET 10 solution architecture | P1 |
| Modular projects + NetArchTest | P1 |
| Massive unit test coverage | P1–P5 (continuous) |
| Hashed API keys + tenants | P3 |
| API key create/revoke/list (admin) | P3 (WP3.8) |
| Model grants / scopes | P3 |
| Admin vs inference auth split | P3 |
| Rate limiting | P4 |
| Quotas | P4 |
| Usage metering hooks | P4 |
| Admin REST APIs | P4 |
| Admin UI | P5 |

## Proposal additions

### Resilience & production hardening

| Feature | Phase |
|---------|-------|
| Request timeouts | P3 |
| Circuit breaker | P3 |
| Bulkhead / max concurrent forwards | P3 |
| Max body size | P3 |
| Graceful shutdown / draining | P3 |
| Strict readiness | P3 |
| Config validation on startup | P3 |
| Upstream TLS validation | P3 |
| Environment-based CORS | P3 |
| HA / stateless design doc | P5 (Helm HPA) |
| Chaos runbook | P5 |

### SDK-friendly error codes

| Feature | Phase |
|---------|-------|
| `GatewayErrorCode` enum | P1 (all codes as stable strings) → P3 (P3 rows) → P4 (429 rows) |
| OpenAI error envelope + `details` | P3 |
| `X-Request-Id`, `X-33pol-Error-Code` | P3 |
| `Retry-After` on 429 | P4 |
| `docs/errors.md` | P3 (start) → P5 (complete) |

### FinOps & advanced billing

| Feature | Phase |
|---------|-------|
| Usage events table | P4 |
| Token parsing | P4 |
| Rate cards | P5 |
| Plans & budgets | P5 |
| Cost center metadata | P5 |
| Export CSV/JSON | P5 |
| Webhooks | P5 |
| Forecast API | P5 |
| Stripe adapter | Post-GA |

### Observability++ (beyond metrics)

| Feature | Phase |
|---------|-------|
| Prometheus full catalog | P4 |
| OpenTelemetry traces | P4 |
| TTFT histogram | P4 |
| Serilog trace correlation | P4 |
| SLO metric hooks (latency/error SLIs) | P4 |
| Grafana dashboards | P4 |
| Alertmanager rules | P4 |
| `/admin/api/summary` | P4 |
| SSE admin event stream | P4 (optional) → P5 (UI) |
| SLO / Prometheus recording rules | P5 |
| Audit logs | P3 (`IAuditLogger` interface) → P4 (admin channel wiring) → P5 (durable retention/export) |
| Application logs in PostgreSQL | **Not in v2** — Serilog + OTel export only |
| Request history read API | P5 |

### Integration & ecosystem

| Feature | Phase |
|---------|-------|
| `perf/k6` smoke | P2 |
| k6 GA suite | P5 |
| Inference conformance suite | P5 (WP5.8, `33pol.Conformance.Tests`) |
| Docker Compose stack | P5 |
| Helm chart | P5 |
| OpenAPI control plane | P4 (start) → P5 (publish) |
| `docs/integrations.md` | P5 |
| ServiceMonitor | P5 |
| OTel collector sample | P4 (sample) → P5 (compose) |
| OpenAI / LangChain guides | P5 |

## Testing & performance

| Activity | Phase |
|----------|-------|
| Test project scaffold | P1 |
| Unit tests per library | P1–P5 |
| Integration tests (proxy) | P2 |
| Testcontainers Postgres | P3 |
| Coverage CI gates | P2 (enforce) |
| k6 smoke baseline | P2 |
| k6 GA + soak | P5 |
| BenchmarkDotNet (optional) | P5 |
