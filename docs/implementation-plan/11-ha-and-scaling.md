# High Availability & Scaling

**Applies from:** Phase 4 (policy + in-memory state) and Phase 5 (Helm HPA)  
**Design goal:** Stateless **inference** pods; externalize or accept per-replica semantics for control-plane caches.

---

## Stateless vs sticky state

| Component | Replica scope | GA default | Multi-replica requirement |
|-----------|---------------|------------|---------------------------|
| Registry + `models.json` | Per pod (volume or sync) | Shared volume or identical image config | **MUST** use same model config on all pods |
| API key validation cache | Per pod | OK | Stale revoke ≤ TTL (1–5 min) — document |
| Rate limit counters | Per pod (in-memory) | **OK for single replica** | **Redis** (or equivalent) **REQUIRED** for fair global RPM |
| Quota (`QuotaUsage` in DB) | Global | OK | DB is source of truth |
| Circuit breaker | Per pod | OK | Per-pod half-open acceptable |
| `IRecentRequestStore` | Per pod | OK | Admin UI shows **pod-local** recent requests unless sharded |
| Usage writer queue | Per pod | OK | All write same DB; idempotent `request_id` |
| SSE `/admin/api/events/stream` | Per pod | OK | Clients see events for connected pod only |

---

## Deployment postures

### A — Single replica (default GA documentation)

- In-memory rate limiting and ring buffer are **correct**.
- Suitable for dev, staging, and small production.

### B — Horizontal replicas without Redis

- **MAY** scale for availability and CPU, but:
  - Effective RPM ≈ `N × limit` (each pod enforces independently).
  - Recent-requests API and SSE differ per instance.
- **MUST** document in `docs/integrations.md` and operator runbooks.

### C — Production multi-replica (recommended)

- Enable `IDistributedRateLimitStore` (Redis) in Phase 4 WP4.1.
- Pin admin “live tail” to one pod or use aggregated metrics in Grafana instead of per-pod ring buffer.
- HPA on CPU and/or custom metric (see below).

---

## Helm / HPA (Phase 5)

| Signal | Use |
|--------|-----|
| CPU | Baseline HPA |
| `gateway_active_streams` | Scale on streaming pressure (if Prometheus adapter available) |
| `gateway_inference_requests_total` rate | RPS-based scaling (advanced) |

**Probes (MUST):**

- Liveness: `GET /health/live` (no auth)
- Readiness: `GET /health/ready` (no auth) — registry loaded + policy (see Phase 3)

**Graceful shutdown:** Phase 3 `gateway_draining` — load balancer should remove pod on `SIGTERM` before kill; in-flight streams **SHOULD** complete within `TerminationGracePeriodSeconds` (document in Helm values).

---

## Live registry under scale

**Normative:** [13-live-model-registry.md](./13-live-model-registry.md) §9.

- Each pod maintains its own in-memory registry, fed by a **shared** `models.json` (RWX volume) **or** per-pod admin API calls.
- `POST /admin/api/models` on one pod updates **that pod only** unless all pods share the same writable file or an orchestrator fans out CRUD to every replica.
- File watch/poll on a **read-only** ConfigMap does **not** allow in-pod file writes — use **admin API** for mutations in Kubernetes.
- `POST /admin/api/config/reload` on one pod does **not** reload others unless orchestrated. **SHOULD** document runbook: “apply to all replicas” = fan-out admin API, shared volume + watch, or rolling reload job.

---

## PostgreSQL

- Single primary for `GatewayDb` at GA.
- Read replicas **post-GA** for analytics exports.

---

## Operator console (Phase 4)

- Runs **in-process** on one pod when enabled — not a cluster-wide control plane.
- Production: keep `Gateway:OperatorConsole:Enabled=false` ([08-operator-console.md](./08-operator-console.md)).

---

## Related documents

- [10-identity-data-model.md](./10-identity-data-model.md) — quota DB vs per-pod rate limits
- [12-metrics-and-runtime-contracts.md](./12-metrics-and-runtime-contracts.md) — metrics for HPA
- [phases/phase-5-finops-ui-ecosystem-and-ga.md](./phases/phase-5-finops-ui-ecosystem-and-ga.md) — Helm WP5.4
