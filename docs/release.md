# Release process

This document describes how maintainers cut a **versioned** release of 33pol on GitHub.

## Prerequisites

| Requirement | Notes |
|-------------|--------|
| Merge rights on `main` | Release tags should point at commits on `main` |
| GitHub Actions enabled | Workflows: [ci.yml](../.github/workflows/ci.yml), [release.yml](../.github/workflows/release.yml), [docker-image.yml](../.github/workflows/docker-image.yml) |
| GHCR package access | Images publish to `ghcr.io/<owner>/33pol` using `GITHUB_TOKEN` (`packages: write`) |
| Public package (optional) | For anonymous `docker pull`, set the GHCR package visibility to **public** in GitHub Packages settings |

Recommended: enable branch protection on `main` requiring the **CI / build-test** check (from [ci-reusable.yml](../.github/workflows/ci-reusable.yml)).

## Version source of truth

- Default version in [Directory.Build.props](../Directory.Build.props): `Version` and `InformationalVersion` (currently aligned with GA **2.0.0**).
- **Tagged releases** override MSBuild version at publish time from the git tag (`v2.0.0` → `2.0.0`) so the container, tarball, and `GET /` JSON `version` field match.

## Cutting a release

1. **Bump version** in `Directory.Build.props` if the next release is not already reflected there.
2. **Update** [CHANGELOG.md](../CHANGELOG.md) under `## [x.y.z]` with user-facing changes.
3. Merge to `main` and confirm [CI](../.github/workflows/ci.yml) is green.
4. Create and push an annotated tag:
   ```bash
   git tag -a v2.0.0 -m "Release 2.0.0"
   git push origin v2.0.0
   ```
5. Watch the [Release workflow](../.github/workflows/release.yml) on GitHub Actions.

Tag format must match `vMAJOR.MINOR.PATCH` (optional prerelease suffix, e.g. `v2.0.0-rc.1`).

## What the Release workflow produces

| Artifact | Location |
|----------|----------|
| Container image | `ghcr.io/<owner>/33pol:2.0.0`, `:2.0`, and exact semver tag |
| Binary bundle | GitHub Release asset `33pol-gateway-2.0.0-linux-x64.tar.gz` (framework-dependent; requires [.NET 10 ASP.NET runtime](https://dotnet.microsoft.com/download) on the host — not self-contained) |
| Release notes | [CHANGELOG](../CHANGELOG.md) section for that version + generated notes |

**Rolling `main` builds** (not a formal release) publish `ghcr.io/<owner>/33pol:latest` via [docker-image.yml](../.github/workflows/docker-image.yml) only. Do not rely on `latest` in production; pin a semver tag.

## Verify a release

```bash
# Container
docker pull ghcr.io/<owner>/33pol:2.0.0
docker run --rm -p 8080:8080 ghcr.io/<owner>/33pol:2.0.0
curl -s http://localhost:8080/ | jq .version

# Tarball (from GitHub Releases; install ASP.NET 10 runtime on host first)
tar -xzf 33pol-gateway-2.0.0-linux-x64.tar.gz
cd gateway
export ASPNETCORE_URLS=http://+:8080
export Gateway__ModelsConfigPath=/path/to/models.json
dotnet 33pol.App.dll
```

Helm:

```bash
helm upgrade --install 33pol deploy/helm/33pol \
  --set image.repository=ghcr.io/<owner>/33pol \
  --set image.tag=2.0.0 \
  --set gateway.existingSecret=33pol-gateway   # Secret with keyPepper + adminApiKey (required)
```

## Rollback

- Redeploy the previous semver image tag or reinstall from the prior GitHub Release asset.
- Database migrations are forward-only; rollback planning is operator-specific (restore backup or stay on new schema).

## Troubleshooting

| Symptom | Action |
|---------|--------|
| Release workflow failed on `ci` | Fix tests on `main`, delete the tag locally/remote, re-tag after green CI |
| GHCR 403 | Confirm `packages: write` and org policy allowing Actions to publish |
| Empty release notes body | Ensure `CHANGELOG.md` has a `## [x.y.z]` section matching the tag version |
| Duplicate image push on tag | Only `release.yml` should run on `v*` tags; `docker-image.yml` must not list tag triggers |
