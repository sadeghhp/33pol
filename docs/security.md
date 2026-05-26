# 33pol Gateway — Security

## Authentication

| Surface | Credential | Notes |
|---------|------------|-------|
| Inference (`/v1/*` POST) | Inference API key | Required when keys configured in DB/bootstrap |
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

## Audit

Admin mutations invoke `IAuditLogger` (structured logs). Durable audit retention is a post-GA enhancement.

## OWASP API (summary)

| Risk | Mitigation |
|------|------------|
| Broken auth | Role-separated keys, hashed storage |
| Excessive data exposure | Key list omits secrets; errors use stable codes |
| Lack of rate limiting | RPM, concurrency, quota layers |
| Security misconfiguration | Options validation on startup, readiness checks |

## Reporting

Report vulnerabilities privately to the maintainers; do not open public issues with exploit details.
