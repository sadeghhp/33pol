# 33pol v2 — GA Checklist

**Release:** 2.0.0  
**Sign-off:** _pending staging perf + approvals_

---

## Phase completion

| Phase | Exit criteria met | Sign-off | Date |
|-------|-------------------|----------|------|
| P1 Platform | [x] | CI workflow, NetArchTest, health live | 2026-05-26 |
| P2 Data plane | [x] | V1 parity integration tests, live registry, k6 smoke | 2026-05-26 |
| P3 Security & resilience | [x] | Auth, Postgres, SDK errors, resilience, admin key CRUD | 2026-05-26 |
| P4 Policy & observability | [x] | Rate limits, quotas, OTel, control plane (SSE deferred) | 2026-05-26 |
| P5 FinOps & GA | [ ] | Code largely complete; staging k6 GA + soak + approvals remain | |

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
- [ ] OpenAI SDK (Python) chat completion against gateway — _staging manual_  
- [ ] Streaming SSE chat completion — _staging manual_  
- [ ] Embeddings path — _if in scope for 2.0.0_  
- [ ] Model aliases and canonical rewrite — _covered by integration tests; SDK manual optional_  
- [ ] Manual `models.json` edit picked up (watch or ≤2 s poll) — _manual on staging_  
- [ ] (Optional) WP4.9 operator-console manual smoke — _or defer with sign-off note_

---

## Quality

- [x] `dotnet test` green (CI `build-test` job)  
- [x] Coverage ≥ targets for gated assemblies (`build/check-coverage.sh`)  
- [ ] No critical/high vulnerabilities in dependencies — _audit pending_  
- [x] Architecture tests pass  

---

## Performance

- [x] k6 `smoke.js` CI green (workflow job `k6-smoke`)  
- [ ] k6 `inference-rps.js` meets thresholds on **staging**  
- [ ] k6 `streaming-concurrent.js` meets thresholds on **staging**  
- [ ] Gateway overhead report in `perf/reports/`  
- [ ] Soak test completed (4h) without memory growth — use `perf/k6/scripts/soak.js` ([guide](../perf/reports/k6-smoke-ci.md))

---

## Observability

- [x] Prometheus scrape endpoint `/metrics`  
- [x] Grafana dashboard artifact `deploy/grafana/dashboards/33pol-gateway.json`  
- [x] Alert rules validated in CI when `promtool` available (`33pol.yml`, `33pol-writer.yml`)  
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
- [ ] Compose stack verified end-to-end — _manual on test host_  
- [x] Liveness/readiness probes (`/health/live`, `/health/ready`)

---

## Documentation

- [x] `docs/errors.md`  
- [x] `docs/integrations.md`  
- [x] `docs/observability.md`  
- [x] `docs/finops.md`  
- [x] `docs/security.md`  
- [x] `README.md` quick start  

---

## Approvals

| Role | Name | Date |
|------|------|------|
| Engineering | | |
| Operations | | |
| Product | | |
