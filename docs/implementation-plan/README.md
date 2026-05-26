# 33pol v2 — Implementation Plan

This folder contains the **authoritative implementation plan** for 33pol LLM Gateway version 2.0. It translates the product proposal into **five ordered phases**, a **modern .NET 10 solution architecture**, a **unit-test-first quality bar**, and a **performance/load testing program**.

**Status:** Planning authoritative; **Phase 1 not started** for the multi-project gateway. Repo may contain early scaffold only (placeholder `33pol.csproj`, `deploy/docker/`, `perf/k6/thresholds.json`).

**Logging:** Application logs are **not** stored in PostgreSQL (Serilog + OpenTelemetry export only). See [01-solution-architecture.md](./01-solution-architecture.md).

## Documents

| Document | Purpose |
|----------|---------|
| [00-executive-proposal.md](./00-executive-proposal.md) | Condensed v2 proposal (goals, scope, v1 parity, differentiators) |
| [01-solution-architecture.md](./01-solution-architecture.md) | .NET 10 solution layout, modules, dependencies, host design |
| [02-testing-strategy.md](./02-testing-strategy.md) | Unit, integration, contract tests; coverage gates; CI |
| [03-performance-and-load-testing.md](./03-performance-and-load-testing.md) | Benchmarks, k6 scenarios, SLOs, environments |
| [04-phase-overview.md](./04-phase-overview.md) | Why five phases, dependencies, exit criteria summary |
| [05-feature-to-phase-matrix.md](./05-feature-to-phase-matrix.md) | Maps all proposal features to phases |
| [06-sdk-error-catalog.md](./06-sdk-error-catalog.md) | Stable error codes (planning reference) |
| [07-review-findings.md](./07-review-findings.md) | Plan review log (remediated items) |
| [GA-CHECKLIST.md](./GA-CHECKLIST.md) | Production release sign-off template |

## Phases (implementation order)

| Phase | Document | Theme |
|-------|----------|--------|
| **1** | [phases/phase-1-platform-foundation.md](./phases/phase-1-platform-foundation.md) | Solution skeleton, architecture boundaries, test harness, CI |
| **2** | [phases/phase-2-core-data-plane.md](./phases/phase-2-core-data-plane.md) | Registry, proxy, streaming, health, OpenAI models API |
| **3** | [phases/phase-3-security-and-resilience.md](./phases/phase-3-security-and-resilience.md) | Auth, tenants, hardening, SDK errors, persistence foundation |
| **4** | [phases/phase-4-policy-and-observability.md](./phases/phase-4-policy-and-observability.md) | Rate limits, quotas, metrics, OTel, control-plane APIs |
| **5** | [phases/phase-5-finops-ui-ecosystem-and-ga.md](./phases/phase-5-finops-ui-ecosystem-and-ga.md) | Billing, admin UI, integrations, load/perf validation, GA |

## Related references

- v1 behavior spec: [`../old-version/`](../old-version/)
- Workspace test rule: [`../../.cursor/rules/unit-test-coverage.mdc`](../../.cursor/rules/unit-test-coverage.mdc)
- Taiga tracking: project **33pol** (epics should map 1:1 to phases)

## Read order

1. `00-executive-proposal.md` — what we are building  
2. `04-phase-overview.md` — how phases connect  
3. `01-solution-architecture.md` — where code will live  
4. `02-testing-strategy.md` + `03-performance-and-load-testing.md` — quality bars  
5. `phases/phase-1` … `phase-5` — detailed backlog per phase  
