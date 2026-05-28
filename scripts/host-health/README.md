# Ubuntu host health check

Read-only Bash toolkit to scan Ubuntu server health (resources, systemd, packages, network, logs). Independent of 33pol; safe to copy to any Ubuntu 22.04/24.04 host.

## Quick start

```bash
cd scripts/host-health
./ubuntu-healthcheck.sh --quick
./ubuntu-healthcheck.sh --full
sudo ./ubuntu-healthcheck.sh --full --log-file /var/log/host-healthcheck.log
```

## Exit codes

| Code | Meaning |
|------|---------|
| 0 | No FAIL (WARN allowed unless `--strict`) |
| 1 | At least one WARN (`--strict` only) |
| 2 | At least one FAIL |

## Options

| Flag | Description |
|------|-------------|
| `--quick` | System, systemd, packages, network (default) |
| `--full` | Includes journal/OOM/dmesg checks |
| `--config PATH` | Extra `KEY=value` config file |
| `--skip-external` | Skip ping to external host (air-gapped) |
| `--json` | Print JSON summary after human-readable output |
| `--strict` | Exit 1 on any WARN |
| `--no-color` | Plain output |
| `--log-file PATH` | Append timestamped lines to a log file |

## Configuration

Load order:

1. `config/defaults.conf`
2. `/etc/host-healthcheck.conf` (optional)
3. File passed to `--config`

Example `/etc/host-healthcheck.conf`:

```ini
DISK_WARN_PCT=80
DISK_FAIL_PCT=90
MEM_AVAIL_WARN_PCT=15
SSH_FAILED_WARN_COUNT=100
```

Only `KEY=value` lines are accepted; shell commands are rejected.

## Cron example

```cron
# Daily full report
0 6 * * * root /opt/host-health/scripts/host-health/ubuntu-healthcheck.sh --full 2>&1 | logger -t host-health

# Every 15 minutes: quick check, alert on FAIL
*/15 * * * * root /opt/host-health/scripts/host-health/ubuntu-healthcheck.sh --quick --skip-external || logger -p daemon.err -t host-health "check failed"
```

## Optional packages

- `curl` — fallback external connectivity check
- `dnsutils` (`host`) — alternative DNS check if `getent` unavailable

## Safety

- **Read-only**: no package installs, service restarts, or config changes (except appending to `--log-file` if you set it).
- **Config whitelist**: only threshold/host keys in `HEALTH_CONFIG_KEYS` can be set from config files; arbitrary variables like `PATH` are ignored.
- **Host tokens**: `DNS_TEST_HOST` and `EXTERNAL_PING_HOST` must be a simple hostname or IPv4 literal (no shell metacharacters).
- **Privileges**: runs as your user; some checks skip without root (e.g. full `dmesg`). Do not run untrusted config files from world-writable paths.

## What this is not

- Not a security auditor (use Lynis, OpenSCAP, etc. for hardening reviews)
- Does not install updates, restart services, or change system state
- Does not check application stacks (Docker, databases, custom apps)
- `dmesg-errors` reads the kernel ring buffer (last 20 err+ lines), not a time window — may include messages from prior boots

Future ideas: SMART disk checks, sensor temperatures, port baselines (plugins under `checks.d/`).

## Development

```bash
# From repo root (requires bats)
bats tests/host-health/

shellcheck -e SC1091 scripts/host-health/ubuntu-healthcheck.sh scripts/host-health/lib/*.sh
```

Run the script on a real Ubuntu host for integration validation; CI runs Bats unit tests only.
