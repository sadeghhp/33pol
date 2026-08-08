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

Cross-Origin Resource Sharing applies only to **browser** clients calling the gateway from a **different origin** (scheme, host, or port) than the API. Server-side SDKs, `curl`, and mobile native HTTP are not affected.

| Environment | Policy |
|-------------|--------|
| **Development** | `AllowAnyOrigin` — browser SPAs need no origin list. |
| **Production / Staging** | Only origins in `Gateway:Cors:AllowedOrigins` are allowed. Empty list blocks all cross-origin browser traffic (startup logs a warning). Preflight responses include `Access-Control-Max-Age: 86400`. |

Do not use `AllowAnyOrigin` in production. The built-in admin UI (`/admin`) is same-origin and does not need CORS configuration.

### Browser / SPA clients

1. Set **exact** SPA origins (no path, no trailing slash), e.g. `https://app.example.com`, `http://localhost:5173`.
2. `http://localhost:5173` and `http://127.0.0.1:5173` are different origins — list both if needed.
3. Call inference with `Authorization: Bearer <inference-key>` (or `X-API-Key`). Do not rely on `credentials: 'include'`; the gateway does not enable `AllowCredentials` for CORS.

**Configuration**

| Mechanism | Example |
|-----------|---------|
| Admin UI | **Settings → CORS allowed origins** |
| Admin API | `GET` / `PUT` `/admin/api/cors` — see [runbooks/cors-admin.md](runbooks/cors-admin.md) |
| `appsettings` | `Gateway:Cors:AllowedOrigins: ["https://sadeghhp.github.io"]` |
| Environment | `Gateway__Cors__AllowedOrigins__0=https://sadeghhp.github.io` |
| Docker `.env` | `GATEWAY_CORS_ALLOWED_ORIGIN_0=https://sadeghhp.github.io` (any index; no Compose edits) or `GATEWAY_CORS_ALLOWED_ORIGINS=…` comma-separated |
| Helm | `gateway.cors.allowedOrigins` in `values.yaml` |

Changes via admin UI/API write appsettings and **hot-reload** the CORS policy (no restart).

**Verify preflight**

```bash
curl -i -X OPTIONS 'http://localhost:8080/v1/chat/completions' \
  -H 'Origin: https://sadeghhp.github.io' \
  -H 'Access-Control-Request-Method: POST' \
  -H 'Access-Control-Request-Headers: authorization,content-type'
```

Expect `Access-Control-Allow-Origin: https://sadeghhp.github.io` when that origin is configured (or `*` in Development).

**Alternative:** serve the SPA behind the same host as the gateway (reverse proxy) so requests are same-origin and CORS is unnecessary.

## Secrets

- Do not commit API keys, peppers, or connection strings (see root `.gitignore`: `.env*`, `appsettings.*.local.json`, `*.pem`, `api-keys.json`, operator `config/` files).
- **`deploy/docker/config/models.json` is gitignored** — copy from `models.json.example` locally; never commit internal upstream URLs or production model topology.
- **`deploy/docker/config/upstream-secrets.enc` is gitignored** — copy from `upstream-secrets.enc.example`; never commit encrypted upstream API keys. Rotate provider keys if they ever appeared in Git history.
- Use `Gateway:Security:KeyPepper` from environment or secret store.
- Bootstrap admin key (`Gateway:Bootstrap`) is for first-run only; rotate after provisioning.

### Going public (checklist)

**Repository (Git):**

- Operator paths are gitignored: `models.json`, `upstream-secrets.enc`, `.env`.
- Committed templates only: `models.json.example`, `upstream-secrets.enc.example`.
- History must not contain operator registry or encrypted upstream files (verify with `git log origin/main -- deploy/docker/config/models.json` and `upstream-secrets.enc` — both empty).

**Operators (each deployment):**

1. Copy examples: `models.json.example` → `models.json`, `upstream-secrets.enc.example` → `upstream-secrets.enc`.
2. **Rotate** any provider keys that ever lived in old Git history; prefer `OPENROUTER_API_KEY` in `.env` with `upstreamAuth.envVar` in `models.json`.
3. Replace dev defaults: admin API key, `KeyPepper`.
4. Reapply or restart gateway after secret changes.

**Collaborators after a history rewrite:** `git fetch && git reset --hard origin/main` or re-clone.

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

`CentralPackageTransitivePinningEnabled` is on in `Directory.Packages.props`, so transitive packages can be pinned to patched versions without waiting for the parent to update. Current advisory-driven pins live there with the GHSA id in a comment beside each — read that file rather than a copy here, which drifts:

| Package | Reason |
|---------|--------|
| `OpenTelemetry.Api` | GHSA-g94r-2vxg-569j |
| `System.Security.Cryptography.Xml` | EF transitive XML crypto advisories |
| `SQLitePCLRaw.*` | GHSA-2m69-gcr7-jv3q (native SQLite library) |
| `Microsoft.OpenApi` | GHSA-v5pm-xwqc-g5wc |

## Optional penetration test (external)

Engage a third party on an annual cadence, or before first exposing the gateway to untrusted traffic. Suggested scope:

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
