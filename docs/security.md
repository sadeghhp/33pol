# 33pol Gateway — Security

## Authentication

| Surface | Credential | Notes |
|---------|------------|-------|
| Inference (`/v1/*` POST) | Inference API key | Required when keys configured in DB/bootstrap, except models with `publicAccess: true` |
| Inference (`GET /v1/models*`) | Optional | Callers with no key — or an unrecognised placeholder one — see only `publicAccess` models; authenticated callers see public + granted models. A revoked or expired key is still `401` |
| Admin, per-tenant (`/admin/api/keys*`, `model-grants`, `usage`) | Admin API key | Scoped to the caller's own tenant |
| Admin, gateway-wide (models, providers, CORS, rate limits, config, backup, `/stats`, requests/logs) | **Operator-tenant** Admin API key | Admin role alone is per-tenant; these surfaces additionally require the key to belong to the operator tenant (`Gateway:Security:OperatorTenantSlug`, defaulting to the bootstrap tenant). Never expose keys in browser URLs |
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

- Clients may call inference with **no** API key, or with any placeholder `Authorization: Bearer` value the gateway does not recognise (`lm-studio`, `not-needed`, …). This matters in practice: OpenAI-compatible SDKs refuse to construct a client with an empty `api_key`, so most callers of a public model send a dummy one.
- A key the gateway **does** recognise but will not honour — revoked, expired, or belonging to a deactivated tenant — is still rejected with `401`, on public models and on `GET /v1/models` alike. Serving those anonymously would answer `200` to a caller whose credential had been withdrawn, leaving no signal anywhere that it had stopped working.
- A **valid** inference key still works and attributes usage to the tenant (rate limits, quotas, budgets).
- Model grants are **not** enforced for public models.
- Anonymous callers are partitioned for rate limits and quotas by client IP, not pooled into one bucket. Behind a proxy this requires `Gateway:ForwardedHeaders` (below) — without it every anonymous caller shares the proxy's address and one client can exhaust the limit for all of them.

**Operational guidance:** Use public access only for local or internal upstreams (e.g. LM Studio). Do not mark paid cloud models public without network isolation and strict default/anonymous rate limits.

## Client IP behind a proxy (`Gateway:ForwardedHeaders`)

Anonymous rate limits, quotas and stream slots are counted per client address. Behind an ingress, a load balancer, or docker's userland proxy, every request arrives from the proxy, so all anonymous traffic collapses into a single partition unless the gateway is told to read `X-Forwarded-For`.

This is **off by default and never inferred**. The header is written by whoever sent the request, so trusting it from an untrusted peer is worse than ignoring it — a caller could put a fresh fake address on every request and mint unlimited partitions, bypassing anonymous rate limiting entirely rather than merely sharing it.

| Setting | Default | Notes |
|---------|---------|-------|
| `Gateway:ForwardedHeaders:Enabled` | `false` | Honours `X-Forwarded-For` and `X-Forwarded-Proto` from trusted peers. |
| `Gateway:ForwardedHeaders:KnownProxies` | `[]` | Proxy IP addresses to trust. Loopback is always trusted, which covers a reverse proxy on the same host. |
| `Gateway:ForwardedHeaders:KnownNetworks` | `[]` | Proxy networks in CIDR form, e.g. `10.0.0.0/8`. Host bits are masked off. |
| `Gateway:ForwardedHeaders:ForwardLimit` | `1` | Number of trusted proxies in front of the gateway. Do not set it higher than the real chain — each hop consumes one entry from the right, so an inflated limit lets the client's own spoofed entries be read as the origin. |
| `Gateway:ForwardedHeaders:TrustAllProxies` | `false` | Trusts any peer. Only safe when nothing but the proxy can reach the gateway's port. Logs a warning at startup. |

Enabling it without naming a proxy trusts loopback only, so an ingress on another host is still ignored; startup logs a warning when that combination is configured. Invalid addresses or CIDR entries fail startup rather than being skipped silently.

## Audit

Every admin mutation calls `IAuditLogger`, implemented by `FileAuditLogger`, which writes **two** records:

1. A structured `ILogger` event, so whatever Serilog sinks are configured keep collecting what they do today.
2. An append-only **JSON Lines** trail on disk — one object per action, with `timestampUtc`, `action`, `tenantId`, `apiKeyId` and the action's `details`.

| Setting | Default | Notes |
|---------|---------|-------|
| `Gateway:Security:AuditLogPath` | `config/audit-log.jsonl` | Relative paths resolve against the app base directory. Same writable volume as `models.json` and `upstream-secrets.enc` — back it up with them. |
| `Gateway:Security:AuditLogMaxBytes` | `8388608` (8 MB) | At the cap the file rolls to `<path>.1`, keeping one generation. Floor: 64 KB. |

Actions recorded: `api_key.create`, `api_key.update`, `api_key.revoke`, `api_key.revoke_batch`,
`api_key.model_grants.replace`, `tenant.model_grants.replace`, `cors.update`, `rate_limits.update`,
`config.reload`, `maintenance.backup`, `model.renamed`, `model.pricing.update`, and the
`upstream_secret.*` lifecycle events.

Call sites pass key **ids and prefixes**, model ids and counts — never a secret — which is what makes
the file safe to retain and ship to a log collector. A write failure (read-only `config/` mount) is
logged once as a warning and never fails the admin action that produced it, so a deployment with an
unwritable volume degrades to structured logs only rather than rejecting valid changes.

**Reading the trail:** `jq -c 'select(.action | startswith("api_key"))' config/audit-log.jsonl`. It is
deliberately not exposed over the admin API — the console's Logs tab is an in-memory diagnostics ring
(warnings and errors, 500 entries), not the audit trail.

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
