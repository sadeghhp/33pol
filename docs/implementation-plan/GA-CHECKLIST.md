# 33pol v2 — GA Checklist

**Release:** 2.0.0  
**Sign-off:** _no-staging local sign-off complete; staging follow-up remains_

---

## Phase completion

| Phase | Exit criteria met | Sign-off | Date |
|-------|-------------------|----------|------|
| P1 Platform | [x] | CI workflow, NetArchTest, health live | 2026-05-26 |
| P2 Data plane | [x] | V1 parity integration tests, live registry, k6 smoke | 2026-05-26 |
| P3 Security & resilience | [x] | Auth, Postgres, SDK errors, resilience, admin key CRUD | 2026-05-26 |
| P4 Policy & observability | [x] | Rate limits, quotas, OTel, control plane (SSE deferred) | 2026-05-26 |
| P5 FinOps & GA | [x] | **No-staging local sign-off complete** (2026-05-28): compose E2E, SDK smoke, k6 local GA suite, local soak substitute, observability checks; staging-only follow-up tracked separately | 2026-05-28 |

---

## Functional

- [x] Live registry: add model via admin API without restart (`POST /admin/api/models`)  
- [x] Admin UI Models page: list / add / delete via `/admin`  
- [x] Operator console `models add/edit/remove`  
- [x] File-only reload `POST /admin/api/config/reload` (authenticated)  
- [x] API key create/revoke (admin API + UI tab)  
- [x] Rate limit returns 429 with stable `code` (integration tests)  
- [x] Quota enforcement (hard and/or soft) + budget hard stop when `HardStopEnabled`  
- [x] FinOps usage export (`GET /admin/api/usage/export`)  
- [x] OpenAI SDK (Python) chat completion against gateway — _local Compose 2026-05-27; staging re-run pending_  
- [x] Streaming SSE chat completion — _sdk-smoke.py step 3 PASS on Compose (WireMock SSE mapping)_  
- [x] Embeddings path — _proxy + conformance tests; SDK optional_  
- [x] Model aliases and canonical rewrite — _covered by integration tests + local manual alias routing check (2026-05-28)_  
- [x] Manual `models.json` edit picked up (watch or ≤2 s poll) — _local Compose ≤3s (alias `poll-test-alias`); staging optional_  
- [x] (Optional) WP4.9 operator-console manual smoke — _deferred: console disabled in Compose; admin UI + Grafana_

---

## Quality

- [x] `dotnet test` green (CI `build-test` job)  
- [x] Coverage ≥ targets for gated assemblies (`build/check-coverage.sh`)  
- [x] No critical/high vulnerabilities in dependencies — _pinned OTel.Api 1.15.3, Cryptography.Xml 10.0.8; CI audit_  
- [x] Architecture tests pass  

---

## Performance

- [x] k6 `smoke.js` CI green (workflow job `k6-smoke`)  
- [x] k6 local substitute (`bash perf/ci/run-ga-compose-k6.sh`) passed on Compose (2026-05-28)  
- [ ] k6 `inference-rps.js` meets thresholds on **staging**  
- [ ] k6 `streaming-concurrent.js` meets thresholds on **staging**  
- [x] Gateway overhead methodology in `perf/reports/ga-2026-05-26.md` + `overhead-compare.js` — _staging numbers pending_  
- [x] Soak local substitute (`bash perf/ci/run-soak-local.sh`, 10m) completed on Compose (2026-05-28)  
- [ ] Soak test completed (4h) without memory growth on staging — use `perf/k6/scripts/soak.js` ([guide](../perf/reports/k6-smoke-ci.md))

---

## Observability

- [x] Prometheus scrape endpoint `/metrics`  
- [x] Grafana dashboard artifact `deploy/grafana/dashboards/33pol-gateway.json`  
- [x] Alert rules validated in CI when `promtool` available (`33pol.yml`, `33pol-writer.yml`)  
- [x] Local observability verification passed (`bash perf/ci/verify-observability-local.sh`, 2026-05-28)  
- [ ] OTel traces end-to-end in staging  
- [x] Runbooks exist (`docs/runbooks/`, `docs/observability.md`)

---

## Security

- [x] No anonymous admin endpoints (admin policy on `/admin/api/**`)  
- [x] API keys stored hashed only  
- [x] TLS validation configurable (`Gateway:Tls`)  
- [x] CORS configurable per environment  
- [x] Secrets not in repository (use `appsettings.Development.local.json`, env vars)

---

## Deployment

- [x] Docker image (`Dockerfile`)  
- [x] Helm chart `deploy/helm/33pol/`  
- [x] Compose stack verified end-to-end — `bash perf/ci/run-compose-e2e.sh` (2026-05-27 local)  
- [x] Liveness/readiness probes (`/health/live`, `/health/ready`)

---

## Documentation

- [x] `docs/errors.md`  
- [x] `docs/integrations.md`  
- [x] `docs/observability.md`  
- [x] `docs/finops.md`  
- [x] `docs/security.md`  
- [x] `docs/architecture.md`  
- [x] `README.md` quick start  
- [x] Inference conformance suite (`33pol.Conformance.Tests`) in CI  

---

## Approvals

| Role | Name | Date |
|------|------|------|
| Engineering | sadegh hp | 2026-05-28 |
| Operations | sadegh hp | 2026-05-28 |
| Product | sadegh hp | 2026-05-28 |
