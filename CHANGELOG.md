# Changelog

All notable changes to this project are documented here. Version tags follow [SemVer](https://semver.org/) (`vMAJOR.MINOR.PATCH`).

## [2.0.0] — 2026-05-28

### Added

- Multi-project gateway (Phases 1–5): OpenAI-compatible proxy, Postgres persistence, rate limits, quotas, FinOps, operator admin UI.
- Docker Compose stack, Helm chart, host install script (`install-33pol.sh`), Prometheus/Grafana provisioning.
- CI: Release build, coverage gate, k6 smoke; GHCR images; tag-gated GitHub Releases with gateway tarball.

### Security

- Operator registry and upstream secrets are gitignored; see [docs/security.md](docs/security.md).

### Notes

- GA sign-off: local Compose E2E and k6 complete; staging perf items tracked in [GA-CHECKLIST.md](docs/implementation-plan/GA-CHECKLIST.md).
