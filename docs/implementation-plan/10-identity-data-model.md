# Identity & Data Model (Phase 3+)

**Database:** `ConnectionStrings:GatewayDb` (default single DB for identity + usage; see [01-solution-architecture.md](./01-solution-architecture.md))  
**ORM:** EF Core 10 + Npgsql  
**Phase:** Schema frozen in Phase 3 WP3.1; extended in Phase 4 (quotas) and Phase 5 (FinOps)

---

## Entity relationship (logical)

```text
Tenant 1──* ApiKey
Tenant 1──* ModelGrant
Tenant 1──* QuotaAllocation (Phase 4)
Plan (Phase 5) ── referenced by Tenant.PlanSlug (string, Phase 3–4)
```

---

## Tenant

| Column / field | Type | Notes |
|----------------|------|-------|
| `Id` | `uuid` PK | Stable `tenant_id` on usage events |
| `Slug` | string unique | Human id for config and logs |
| `Name` | string | Display |
| `PlanSlug` | string nullable | **Phase 4 rate limits** until `Plan` entity (Phase 5) — see § Rate limit source |
| `CostCenter` | string nullable | FinOps label (Phase 5 export) |
| `IsActive` | bool | Inactive → 403 on inference |
| `CreatedAt` | timestamptz | |
| `UpdatedAt` | timestamptz | |

**Bootstrap (MUST):** Migration or seed documents how the **first admin key** and tenant are created on empty DB (install CLI, env bootstrap secret, or one-time setup endpoint disabled after use).

---

## ApiKey

| Column / field | Type | Notes |
|----------------|------|-------|
| `Id` | `uuid` PK | |
| `TenantId` | FK → Tenant | |
| `KeyHash` | string | HMAC-SHA256 or ASP.NET PasswordHasher + **pepper** from config |
| `KeyPrefix` | string | Display only, e.g. `sk-33pol-abc…` (first 8–12 chars of id/prefix) |
| `Role` | enum/string | `Inference`, `Admin`, or `Both` — see § Credential types |
| `Scopes` | jsonb or separate table | Optional coarse scopes (e.g. `inference`, `admin`) |
| `ExpiresAt` | timestamptz nullable | → `expired_api_key` |
| `RevokedAt` | timestamptz nullable | → `invalid_api_key` after cache TTL |
| `CreatedAt` | timestamptz | |
| `LastUsedAt` | timestamptz nullable | Optional |

**Storage rules (MUST):**

- Never store plaintext secret after create response.
- Create response returns full secret **once** (WP3.8).
- Compare keys in **constant-time** after normalization (trim, scheme).

**Pepper rotation:** Document operational procedure in `docs/security.md` (Phase 5): new pepper + re-hash on next login not required for API keys — prefer **issue new key, revoke old**.

---

## Credential types (inference vs admin)

| Role | Use | Policy |
|------|-----|--------|
| `Inference` | `POST /v1/*` inference, `GET /v1/models*` | Default for tenant API keys |
| `Admin` | `POST/GET /admin/api/**` | Operator automation |
| `Both` | Rare; dev only | **SHOULD** disallow in Production via validation |

**Phase 3 GA default:** Admin and inference are **separate keys** (not v1 shared `ApiKeys` list). v1 flat `Gateway:ApiKeys` **BREAKING** — replaced by DB-backed keys.

**JWT (optional):** If enabled for admin, same `Admin` policy; inference **SHOULD** remain API-key-only at GA unless explicitly extended.

---

## ModelGrant

| Column / field | Type | Notes |
|----------------|------|-------|
| `Id` | `uuid` PK | |
| `TenantId` | FK | |
| `ModelPattern` | string | Canonical id or alias pattern; start with exact match |
| `Effect` | enum | `Allow` (default), future `Deny` |

**Evaluation (MUST):**

1. Resolve client `model` to canonical id via registry.
2. If tenant has **no grants** → **MUST** allow any model in registry (v1 parity — v1 had no grant table). Deny-all only when at least one grant row exists and none match.
3. If grants exist → must match at least one `Allow` for canonical id (and alias if stored).

Failure → 403 `insufficient_scope` ([06-sdk-error-catalog.md](./06-sdk-error-catalog.md)).

`model_not_allowed` (400) reserved for plan/feature blocks (Phase 4/5).

---

## Rate limit source (Phase 4, before `Plan` entity)

Until Phase 5 `Plan` table drives limits, resolve RPM / concurrency / burst from **first match**:

1. `Tenant.PlanSlug` → `RateLimiting:Plans:{slug}` in configuration (JSON section).
2. Else `RateLimiting:Default`.
3. Optional per-tenant override: `RateLimiting:Tenants:{tenantSlug}`.

Example configuration:

```json
{
  "RateLimiting": {
    "Default": { "Rpm": 60, "Burst": 10, "MaxConcurrentStreams": 5 },
    "Plans": {
      "standard": { "Rpm": 120, "Burst": 20, "MaxConcurrentStreams": 10 },
      "enterprise": { "Rpm": 600, "Burst": 100, "MaxConcurrentStreams": 50 }
    }
  }
}
```

`IRateLimitPolicyResolver` (Policy) reads resolved policy; **no** FinOps `Plan` entity required in Phase 4.

Phase 5: `Plan` entity may sync into the same resolver or replace config-file tiers.

---

## Quota tables (Phase 4)

| Table | Purpose |
|-------|---------|
| `QuotaAllocation` | Budget per tenant/period (tokens, requests) |
| `QuotaUsage` | Rolling counters |

Semantics: [12-metrics-and-runtime-contracts.md](./12-metrics-and-runtime-contracts.md) § Quota.

---

## Usage events (Phase 4–5)

| Column | Notes |
|--------|-------|
| `RequestId` | Unique — idempotency |
| `TenantId`, `ApiKeyId` | |
| `Model` | Canonical |
| `InputTokens`, `OutputTokens` | From upstream `usage` when present |
| `DurationMs` | |
| `RecordedAt` | |

Writer batching: Phase 4 hook; Phase 5 hardening WP5.2.

---

## Caching

| Data | Cache | TTL |
|------|-------|-----|
| ApiKey validation | `IMemoryCache` | 1–5 min (configurable) |

**Revoke (MUST):** Revoke API clears cache entry for that key id immediately.

---

## Related documents

- [09-v1-parity-spec.md](./09-v1-parity-spec.md) — auth timing Phase 2 vs 3
- [11-ha-and-scaling.md](./11-ha-and-scaling.md) — multi-replica keys/quotas
- [phases/phase-3-security-and-resilience.md](./phases/phase-3-security-and-resilience.md) — WP3.1 implementation
