# Phase 6 — Per-Assembly Review Rubric

**Use with:** [phase-6-production-quality-review.md](./phases/phase-6-production-quality-review.md)  
**Findings log:** [16-phase6-findings.md](./16-phase6-findings.md)  
**Reviewer signs each row:** `Y` (pass), `N` (finding logged), `N/A`, `W` (waived with ref)

**Audit date:** 2026-05-27

---

## Cross-cutting (all assemblies)

| # | Check | Result | Notes |
|---|-------|--------|-------|
| X1 | No secrets in source or default appsettings | Y | Dev admin key in `appsettings.Development.json` only |
| X2 | `dotnet test` green | Y | Release build, all 13 test projects |
| X3 | NetArchTest dependency rules | Y | `33pol.Architecture.Tests` |
| X4 | No vulnerable packages (`dotnet list package --vulnerable`) | Y | 2026-05-27 |
| X5 | Coverage gates for gated assemblies | Y | See G-18 / F-P6-018 |

---

## 33pol.Core

**Normative:** [01-solution-architecture.md](./01-solution-architecture.md), [06-sdk-error-catalog.md](./06-sdk-error-catalog.md)

| # | Check | Result | Notes |
|---|-------|--------|-------|
| C1 | No ASP.NET / EF / YARP references | Y | NetArchTest |
| C2 | `GatewayOptions` validated on startup | Y | `GatewayOptionsValidation` |
| C3 | `GatewayErrorCode` stable strings | Y | Conformance goldens |
| C4 | Provider catalog / URL validators tested | Y | `ProviderCatalogTests`, `ProviderModelsListUrlValidatorTests` |
| C5 | Abstractions remain interface-only | Y | |

**Assembly sign-off:** Y

---

## 33pol.Registry

**Normative:** [13-live-model-registry.md](./13-live-model-registry.md), [09-v1-parity-spec.md](./09-v1-parity-spec.md)

| # | Check | Result | Notes |
|---|-------|--------|-------|
| R1 | Atomic registry swap (readers never see partial state) | Y | `ModelRegistryService` |
| R2 | Watch/poll file reload | Y | Integration tests |
| R3 | Alias map correctness | Y | Unit + integration |
| R4 | Persist + apply single pipeline | Y | `ModelRegistryWriter` |
| R5 | Load test gate R5 (doc 13) | W | Staging k6 — P5 G-01 |

**Assembly sign-off:** Y

---

## 33pol.Proxy (hot path)

**Normative:** [09-v1-parity-spec.md](./09-v1-parity-spec.md), [03-performance-and-load-testing.md](./03-performance-and-load-testing.md)

| # | Check | Result | Notes |
|---|-------|--------|-------|
| P1 | SSE not fully buffered before forward | Y | `StreamingHttpTransformer`, `UsageCapturingStream` |
| P2 | Model JSON rewrite (spacing variants) | Y | Unit + conformance |
| P3 | Health gating before forward | Y | `ModelRouterMiddleware` |
| P4 | Circuit breaker + bulkhead ordering | Y | Registry tests |
| P5 | `IRequestTracker` wired in router | Y | DI → `GatewayRequestTracker` |
| P6 | Dead `RequestTracker` stub removed | Y | F-P6-022 closed |
| P7 | Resilience middleware timeouts | Y | `InferenceResilienceMiddleware` |

**Assembly sign-off:** Y

---

## 33pol.Security

**Normative:** [10-identity-data-model.md](./10-identity-data-model.md), [security.md](../security.md)

| # | Check | Result | Notes |
|---|-------|--------|-------|
| S1 | Inference vs admin auth split | Y | `GatewayAuthorizationMiddleware` |
| S2 | API keys hashed at rest | Y | Persistence |
| S3 | Model grants enforced | Y | `ModelGrantService` |
| S4 | `IAuditLogger` — durable export | N/A | Post-GA G-13; Serilog today |
| S5 | All `/admin/api/*` require admin role | Y | Middleware + explicit policy on groups (F-P6-015) |

**Assembly sign-off:** Y

---

## 33pol.Policy

**Normative:** [10-identity-data-model.md](./10-identity-data-model.md), [12-metrics-and-runtime-contracts.md](./12-metrics-and-runtime-contracts.md)

| # | Check | Result | Notes |
|---|-------|--------|-------|
| Po1 | 429 stable codes | Y | Integration tests |
| Po2 | Quota reserve/commit semantics | Y | `QuotaMiddleware` |
| Po3 | In-memory rate limit per pod documented | Y | F-P6-010 P1 limitation |
| Po4 | Circuit breaker state exposed to metrics | Y | `GatewayCircuitBreakerMetricsExporter` |

**Assembly sign-off:** Y

---

## 33pol.Observability

**Normative:** [12-metrics-and-runtime-contracts.md](./12-metrics-and-runtime-contracts.md), [08-operator-console.md](./08-operator-console.md)

| # | Check | Result | Notes |
|---|-------|--------|-------|
| O1 | Canonical metric names | Y | `GatewayMeters` |
| O2 | No high-cardinality labels (raw key, request id) | Y | |
| O3 | Usage channel backpressure + drop metrics | Y | WP5.2 |
| O4 | `ControlPlaneCommands` not in Api | Y | D6 remediated |
| O5 | `IRequestTracker` implementation | Y | `GatewayRequestTracker` |
| O6 | Circuit breaker gauge vs doc 12 “post-GA” | Y | Implemented ahead of doc — update doc 12 |

**Assembly sign-off:** Y

---

## 33pol.Billing

| # | Check | Result | Notes |
|---|-------|--------|-------|
| B1 | Idempotent usage by request_id | Y | |
| B2 | NoOp services when DB disabled | Y | Documented in DI |
| B3 | Rate card cost calculator tested | Y | ≥90% coverage |
| B4 | Webhook HMAC | Y | Unit tests |

**Assembly sign-off:** Y

---

## 33pol.Persistence

| # | Check | Result | Notes |
|---|-------|--------|-------|
| Pe1 | Migrations apply cleanly | Y | Testcontainers tests |
| Pe2 | Indexes on hot query paths | Y | Reviewed repositories |
| Pe3 | Not in CI coverage gate | N | F-P6-018 P1 — document exclusion |

**Assembly sign-off:** Y (with G-18 note)

---

## 33pol.Api

| # | Check | Result | Notes |
|---|-------|--------|-------|
| A1 | Admin endpoints use `RequireAuthorization(Admin)` | Y | F-P6-015 — keys/config aligned |
| A2 | Provider models POST (no secrets in URL) | Y | US-P5-10 |
| A3 | Input validation on admin mutations | Y | |
| A4 | Not in CI coverage gate | N | F-P6-018 |

**Assembly sign-off:** Y

---

## 33pol.App

| # | Check | Result | Notes |
|---|-------|--------|-------|
| Ap1 | Middleware order matches architecture doc | Y | `GatewayPipelineExtensions` / host |
| Ap2 | Kestrel limits / max body | Y | Options |
| Ap3 | Graceful shutdown | Y | |
| Ap4 | Static admin UI served | Y | `wwwroot/admin` |

**Assembly sign-off:** Y

---

## 33pol.OperatorConsole

**Normative:** [08-operator-console.md](./08-operator-console.md)

| # | Check | Result | Notes |
|---|-------|--------|-------|
| Oc1 | Disabled by default in Production/Docker | Y | |
| Oc2 | P1–P6 performance contract | Y | No Spectre when disabled |
| Oc3 | References Core only | Y | NetArchTest |

**Assembly sign-off:** Y

---

## wwwroot/admin

**Normative:** [admin-ui.md](../admin-ui.md)

| # | Check | Result | Notes |
|---|-------|--------|-------|
| U1 | API key in localStorage threat model documented | Y | admin-ui.md |
| U2 | Error surfacing near actions | Y | US-P5-10 toasts |
| U3 | Polling interval reasonable | Y | 2s summary |
| U4 | XSS — no unsanitized HTML from API | Y | text bindings |
| U5 | Playwright E2E | N/A | P2 G-20 |

**Assembly sign-off:** Y

---

## WP6.3–6.8 summary

| WP | Result | Notes |
|----|--------|-------|
| 6.3 Data plane perf | Y | Code review; staging numbers P5 |
| 6.4 Security paths | Y | OWASP rows in security.md |
| 6.5 Observability | Y | Metrics in repo; local compose verify needs running stack |
| 6.6 Admin UX | Y | Gaps → US-P5-10 / #613 |
| 6.7 Duplication | Y | F-P6-022 removed dead code |
| 6.8 Test gaps | Y | F-P6-018 documented |

**Phase 6 audit sign-off:** All assemblies Y — zero Open P0 in findings register.
