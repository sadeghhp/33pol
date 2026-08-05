# Security Policy

## Reporting vulnerabilities

If you discover a security issue, please report it privately to the maintainers (do not open a public issue with exploit details).

## Secrets and configuration

- **Never commit** `.env`, `deploy/docker/config/models.json`, or `deploy/docker/config/upstream-secrets.enc`.
- Use the committed templates: `models.json.example`, `upstream-secrets.enc.example`.
- Override dev defaults before any internet-facing deployment:
  - `GATEWAY_ADMIN_API_KEY` / bootstrap admin key
  - `Gateway:Security:KeyPepper`
  - Provider API keys (`OPENROUTER_API_KEY`, etc.)

Full guidance: [docs/security.md](docs/security.md).

## Self-hosted deployment

33pol is **open source** for inspection and self-hosting. Running a gateway on the public internet requires TLS, strong secrets, CORS configuration, network isolation for `/admin`, and your own security review. See the README disclaimer and [GA checklist](docs/implementation-plan/GA-CHECKLIST.md) for maturity notes.

## Credential revocation and cache propagation

Revoking an API key, or removing a model grant, takes effect **immediately on the gateway instance that processed the change** — that instance invalidates its cache entry directly.

Validation results are cached in process memory, and that invalidation does **not** cross process boundaries. On a multi-replica deployment a revoked key therefore keeps being accepted by the other replicas until their cached entry expires.

- The propagation window equals `Gateway:Security:CacheTtlMinutes` (default **2 minutes**).
- Startup validation rejects any value above 5 minutes, so the window can never be configured to exceed the revocation SLA.
- For an immediate, cluster-wide revocation, restart the remaining replicas or route the revocation through every instance.

Removing this limitation requires a shared invalidation channel (for example Redis pub/sub) so that a revocation on one replica evicts the entry on all of them. The gateway does not ship one today; the bounded TTL is what contains the exposure in the meantime.

The same single-process caveat applies to rate limits, token quotas and budget reservations, which are all tracked per process. Run a single gateway instance unless you have accounted for this.

## Repository hygiene

Maintainers: before publishing or after history rewrites, verify Git history contains no operator registry files or encrypted upstream stores. See **Going public** in [docs/security.md](docs/security.md#going-public-checklist).
