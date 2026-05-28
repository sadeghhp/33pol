# Security Policy

## Reporting vulnerabilities

If you discover a security issue, please report it privately to the maintainers (do not open a public issue with exploit details).

## Secrets and configuration

- **Never commit** `.env`, `deploy/docker/config/models.json`, or `deploy/docker/config/upstream-secrets.enc`.
- Use the committed templates: `models.json.example`, `upstream-secrets.enc.example`.
- Override dev defaults before any internet-facing deployment:
  - `GATEWAY_ADMIN_API_KEY` / bootstrap admin key
  - `Gateway:Security:KeyPepper`
  - Postgres password and provider API keys (`OPENROUTER_API_KEY`, etc.)

Full guidance: [docs/security.md](docs/security.md).

## Self-hosted deployment

33pol is **open source** for inspection and self-hosting. Running a gateway on the public internet requires TLS, strong secrets, CORS configuration, network isolation for `/admin`, and your own security review. See the README disclaimer and [GA checklist](docs/implementation-plan/GA-CHECKLIST.md) for maturity notes.

## Repository hygiene

Maintainers: before publishing or after history rewrites, verify Git history contains no operator registry files or encrypted upstream stores. See **Going public** in [docs/security.md](docs/security.md#going-public-checklist).
