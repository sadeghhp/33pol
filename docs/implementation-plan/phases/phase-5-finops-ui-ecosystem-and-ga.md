# Phase 5 — FinOps, Admin UI, Ecosystem & GA

**Epic:** `EPIC-P5-finops-ga`  
**Duration (guide):** 2–4 weeks  
**Prerequisite:** Phase 4 complete  
**Blocks:** Production release  

---

## Objective

Complete **FinOps and advanced billing**, ship the **minimal admin UI**, deliver **integration artifacts** (Helm, Compose, docs), run **performance and load test GA gates**, and sign off the **GA checklist**.

---

## Outcomes

- Rate cards, plans, budgets, exports, webhooks  
- Admin UI at `/admin`  
- Integration guides (OpenAI SDK, LangChain, K8s)  
- k6 GA suite passes on staging  
- GA checklist complete  
- **90%+ unit coverage** on Billing assembly  

---

## Work packages

### WP5.1 — FinOps & billing (`33pol.Billing`)

| Task | Details |
|------|---------|
| Schema | `RateCard`, `Plan`, `Budget`, `BillingEvent` |
| Rate card engine | Price per 1M input/output tokens per model |
| Cost attribution | `cost_center` metadata on usage events |
| Aggregates | Daily rollups per tenant/model |
| APIs | `GET /admin/api/usage`, export CSV/JSON |
| Forecast API | Projected spend from trailing window |
| Webhooks | `quota.warning`, `usage.daily` (HMAC signed) |
| Idempotency | Unique `request_id` on usage rows |

**Unit tests:**

- Cost calculation per rate card  
- Budget 80% warning event  
- Budget 100% hard stop (if enabled)  
- Export format golden files  
- Webhook payload signing  

### WP5.2 — Usage writer hardening

| Task | Details |
|------|---------|
| Batched writer | Channel 10k, DropOldest, batch 100 / 1s |
| Metrics | `gateway_usage_writer_queue_depth`, `dropped_total` |
| Alerts | `deploy/prometheus/alerts/33pol-writer.yml` — writer queue depth, dropped events |
| Read APIs | Paginated history for admin |
| Retention job | Configurable TTL (document only or background job) |

**Integration tests:**

- Event persisted under load (Testcontainers)  
- Drop policy when saturated (unit with fake channel)  

### WP5.3 — Admin UI (`wwwroot/admin`)

| Task | Details |
|------|---------|
| Stack | Alpine.js + fetch (or Petite-Vue) |
| Pages | Dashboard, backends, keys (create/revoke via WP3.8 APIs), usage chart (no in-app log history — link to observability stack in `docs/observability.md`) |
| Auth | Admin session cookie or token in localStorage (document threat model) |
| Polling | Summary every 2s; optional SSE for requests |
| Styling | Minimal modern CSS (no heavy framework) |

**Manual test checklist:**

- Login / API key entry  
- Dashboard shows live metrics from `/admin/api/summary`  
- Config reload button calls API  

*UI E2E optional (Playwright) — not blocking if manual script signed.*

### WP5.4 — Integration & ecosystem

| Deliverable | Location |
|-------------|----------|
| Docker Compose | `deploy/docker/docker-compose.yml` — gateway, Postgres, Prometheus, Grafana, mock upstream |
| Helm chart | `deploy/helm/33pol/` — Deployment, Service, HPA, ServiceMonitor, probes |
| OpenAI SDK guide | `docs/integrations.md` |
| LangChain / LiteLLM notes | `docs/integrations.md` |
| Ingress SSE guide | `docs/integrations.md` |
| OTel collector sample | `deploy/otel-collector/` |
| GitHub Actions | Publish image, nightly k6 |

### WP5.5 — Performance & load GA gates

Execute [03-performance-and-load-testing.md](../03-performance-and-load-testing.md):

| Script | Gate |
|--------|------|
| `smoke.js` | CI on `main` |
| `inference-rps.js` | Staging thresholds |
| `streaming-concurrent.js` | Concurrency target |
| `rate-limit-storm.js` | 429 behavior |
| Soak 4h | Memory stable (staging) |
| Overhead report | `perf/reports/ga-{date}.md` |

### WP5.6 — Documentation & GA checklist

| Document | Action |
|----------|--------|
| `README.md` | Quick start with Compose |
| `docs/architecture.md` | Final architecture |
| `docs/errors.md` | Complete code catalog |
| `docs/finops.md` | Rate cards, budgets |
| `docs/observability.md` | Dashboards, alerts |
| `docs/runbooks/` | High error rate, all backends down, writer backlog |
| `implementation-plan/GA-CHECKLIST.md` | Sign-off template |

### WP5.8 — Inference conformance suite

| Task | Details |
|------|---------|
| Project | `tests/33pol.Conformance/` or tagged tests in `33pol.Integration.Tests` |
| Scope | OpenAI request/response shapes for chat, completions, embeddings, models list |
| Fixtures | Golden files for error JSON per `06-sdk-error-catalog.md` |
| CI | Runs on `main`; required for GA (executive proposal §8) |

**Tests:** Each official SDK smoke scenario documented in `docs/integrations.md`; suite passes against mock upstream and optional staging vLLM.

### WP5.7 — Security & compliance review

| Task | Details |
|------|---------|
| Dependency scan | `dotnet list package --vulnerable` |
| OWASP API checklist | Document in `docs/security.md` |
| Secret scanning | No keys in repo |
| Pen test | Optional external — document scope |

---

## GA checklist (summary)

- [ ] All Phase 1–5 exit criteria met  
- [ ] `dotnet test` green; coverage gates met all assemblies  
- [ ] k6 GA thresholds pass on staging  
- [ ] Helm deploy successful on test cluster  
- [ ] Inference conformance suite passes (WP5.8)  
- [ ] Docker Compose stack loads Grafana dashboard (WP5.4)  
- [ ] OpenAI Python SDK smoke against gateway  
- [ ] FinOps export validated by sample spreadsheet  
- [ ] Runbooks linked from alerts  
- [ ] Taiga epic P5 closed  

---

## Unit test checklist (Phase 5)

- [ ] Billing cost engine all model/tier combinations  
- [ ] Budget evaluator edge cases (timezone, month boundary)  
- [ ] Webhook HMAC validation  
- [ ] Export CSV column contract  
- [ ] Coverage ≥ 90% Billing  

---

## Post-GA (backlog, not blocking)

- Stripe metered billing adapter  
- Anomaly detection on token spikes  
- Native AOT publish evaluation  
- Multi-destination load balancing per model  

---

## Taiga story seeds

1. As a finance user, I export monthly usage by cost center.  
2. As an operator, I deploy with Helm and see ServiceMonitor scraped.  
3. As a product owner, I sign GA checklist after load tests pass.  
