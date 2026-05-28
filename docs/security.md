# 33pol Gateway — Security

## Authentication

| Surface | Credential | Notes |
|---------|------------|-------|
| Inference (`/v1/*` POST) | Inference API key | Required when keys configured in DB/bootstrap, except models with `publicAccess: true` |
| Inference (`GET /v1/models*`) | Optional | Unauthenticated callers see only `publicAccess` models; authenticated callers see public + granted models |
| Admin (`/admin/api/*`) | Admin API key | Separate role; never expose in browser URLs |
| Health / metrics | None | `/health/live`, `/health/ready`, `/metrics` public for probes |

Keys are stored **hashed** (HMAC + pepper). Plaintext secrets are shown only once at creation.

## Transport

- Terminate TLS at ingress (Kubernetes Ingress, reverse proxy).
- Upstream backend TLS validation is configurable (`Gateway:Resilience:ValidateUpstreamTls`); enable in production.

## CORS

Production uses restricted origins via environment-based CORS policy. Do not use `AllowAnyOrigin` in production configs.

## Secrets

- Do not commit API keys, peppers, or connection strings.
- Use `Gateway:Security:KeyPepper` from environment or secret store.
- Bootstrap admin key (`Gateway:Bootstrap`) is for first-run only; rotate after provisioning.

## Admin UI

The static admin UI (`/admin`) stores the API key in **localStorage**. Treat the workstation as trusted; prefer short-lived keys and network isolation.

## Public models (`publicAccess`)

Operators may mark individual registry models with `"publicAccess": true` (admin UI: **Allow use without 33pol API key**). For those models only:

- Clients may call inference with **no** API key or any placeholder `Authorization: Bearer` value.
- A **valid** inference key still works and attributes usage to the tenant (rate limits, quotas, budgets).
- Model grants are **not** enforced for public models.
- Anonymous callers use the same `anonymous` rate-limit and quota partition as other unauthenticated traffic.

**Operational guidance:** Use public access only for local or internal upstreams (e.g. LM Studio). Do not mark paid cloud models public without network isolation and strict default/anonymous rate limits.

## Audit

Admin mutations invoke `IAuditLogger` (structured logs). Durable audit retention is a post-GA enhancement.

## OWASP API Security Top 10 (mapping)

| # | Risk | 33pol control |
|---|------|----------------|
| API1 | Broken object level authorization | Tenant-scoped API keys; admin APIs require admin role |
| API2 | Broken authentication | Hashed keys, pepper, expiry/revoke |
| API3 | Broken object property level authorization | Tenant ceiling + per-key allowlist (`IModelGrantService`; empty key grants = deny all); admin vs inference roles |
| API4 | Unrestricted resource consumption | Rate limits, concurrency, quotas, request body size cap |
| API5 | Broken function level authorization | `GatewayAuthPolicies.Admin` on `/admin/api/**` |
| API6 | Unrestricted access to sensitive business flows | Admin UI/console documented; audit logs on mutations |
| API7 | Server side request forgery | Upstream URLs from operator-controlled registry only |
| API8 | Security misconfiguration | `GatewayOptions` validation; console off in K8s defaults |
| API9 | Improper inventory management | `/v1/models` + admin registry APIs |
| API10 | Unsafe consumption of APIs | Stable error JSON; no stack traces on inference path |

## Dependency audit

CI runs `dotnet list package --vulnerable --include-transitive` on every PR/main build.

Central pins (2026-05-26):

| Package | Version | Reason |
|---------|---------|--------|
| `OpenTelemetry.Api` | 1.15.3 | GHSA-g94r-2vxg-569j |
| `System.Security.Cryptography.Xml` | 10.0.8 | EF transitive XML crypto advisories |

`CentralPackageTransitivePinningEnabled` is on in `Directory.Packages.props`.

## Optional penetration test (external)

Engage a third party before GA or on an annual cadence. Suggested scope:

| In scope | Out of scope (unless agreed) |
|----------|------------------------------|
| Inference API auth bypass, tenant/model grant enforcement | Physical datacenter / network perimeter |
| Admin API authorization (`/admin/api/**`) | Social engineering |
| SSRF via operator-controlled registry upstream URLs | Sustained DDoS (use staging k6 instead) |
| Rate limit, quota, and budget hard-stop bypass | Supply-chain audit beyond dependency scan |
| Secret leakage in logs, metrics, traces, and error JSON | |

Deliverables: written report with severity ratings and remediation tickets; retest after fixes.

## Reporting

Report vulnerabilities privately to the maintainers; do not open public issues with exploit details.
