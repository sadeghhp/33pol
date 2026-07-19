# Runbook: SQLite backup & recovery

33pol stores all operational state (tenants, API keys, model grants, usage/billing,
and the CORS / rate-limit / model-route / quota configuration) in a single embedded
SQLite database at `/data/gateway.db` on the `gateway-data` volume. It runs
single-instance, so that file is the only copy of your state — back it up.

There are three layers of protection, in increasing durability:

1. **Snapshot on deploy** (automatic) — every `33pol-deploy.sh deploy` snapshots the DB
   before building/rolling out, because EF migrations auto-apply on startup and are not
   auto-reverted. This is rollback insurance, not a backup schedule.
2. **Scheduled backups** (you must set this up) — periodic consistent copies copied off
   the volume. Covered below.
3. **Continuous replication / PITR** (optional) — Litestream, for point-in-time recovery.
   Covered below.

## Taking a backup

### Hot backup (no downtime) — preferred for scheduled backups

```bash
deploy/docker/33pol-deploy.sh backup --hot
```

This asks the running gateway to run `VACUUM INTO` — a transactionally-consistent copy
made while the gateway keeps serving under WAL — then verifies it with
`PRAGMA integrity_check` and copies the single file to
`deploy/docker/.deploy/backups/db-manual-<UTC>/gateway.db`. It authenticates with
`GATEWAY_ADMIN_API_KEY` from `.env`.

The same operation is exposed directly for external schedulers:

```bash
curl -fsS -X POST -H "Authorization: Bearer $GATEWAY_ADMIN_API_KEY" \
  http://127.0.0.1:8080/admin/api/maintenance/backup
# => {"succeeded":true,"path":"/data/backups/gateway-<UTC>.db","sizeBytes":...,"integrityCheck":"ok","error":null}
```

Note the endpoint writes into `/data/backups` **inside the volume** — durable against a
container restart but not against loss of the volume itself. Always copy the file off the
volume (the `--hot` deploy command does this for you).

### Cold snapshot (brief restart) — when the gateway is unhealthy

```bash
deploy/docker/33pol-deploy.sh backup
```

Briefly stops the gateway, copies `gateway.db` (+ any `-wal`/`-shm`) out of the volume,
and restarts. Use this if the gateway can't serve the hot-backup request.

## Scheduling

Run a hot backup on a timer from the host. Example cron (daily 02:15 UTC, prune to keep
14 days handled separately):

```cron
15 2 * * *  cd /opt/33pol/deploy/docker && ./33pol-deploy.sh backup --hot >> /var/log/33pol-backup.log 2>&1
```

Or a systemd timer:

```ini
# /etc/systemd/system/33pol-backup.service
[Service]
Type=oneshot
WorkingDirectory=/opt/33pol/deploy/docker
ExecStart=/opt/33pol/deploy/docker/33pol-deploy.sh backup --hot

# /etc/systemd/system/33pol-backup.timer
[Timer]
OnCalendar=*-*-* 02:15:00 UTC
Persistent=true
[Install]
WantedBy=timers.target
```

```bash
systemctl enable --now 33pol-backup.timer
```

Copy backups off-host (rsync/S3) for real disaster recovery; a backup on the same host as
the volume does not survive host loss.

## Retention

```bash
deploy/docker/33pol-deploy.sh list-backups
deploy/docker/33pol-deploy.sh prune --keep 14   # also trims old images
```

## Restoring

```bash
deploy/docker/33pol-deploy.sh list-backups
deploy/docker/33pol-deploy.sh restore .deploy/backups/db-manual-<UTC>
```

Restore stops the gateway, removes any stale `gateway.db`/`-wal`/`-shm` (so a leftover WAL
cannot replay over the restored file), copies the snapshot in, fixes ownership for the
non-root runtime user, and restarts. It accepts either a snapshot directory or a bare
`gateway.db` file. Verify a backup before relying on it:

```bash
# integrity of a copied-out backup (needs a sqlite3 available on the host, or reuse the app):
python3 -c "import sqlite3,sys; print(sqlite3.connect(sys.argv[1]).execute('PRAGMA integrity_check').fetchone()[0])" \
  .deploy/backups/db-manual-<UTC>/gateway.db
```

## Optional: continuous replication / point-in-time recovery (Litestream)

`VACUUM INTO` gives you consistent point-in-*backup* snapshots. For point-in-*time*
recovery (replay to any second, minimal data loss), add [Litestream](https://litestream.io)
as a sidecar that streams the WAL to object storage. It fits single-instance SQLite
exactly (one writer). Sketch:

```yaml
# docker-compose override — one Litestream sidecar per gateway, sharing the data volume.
litestream:
  image: litestream/litestream:0.3
  restart: unless-stopped
  volumes:
    - gateway-data:/data
    - ./litestream.yml:/etc/litestream.yml:ro
  command: ["replicate"]
  environment:
    LITESTREAM_ACCESS_KEY_ID: ${LITESTREAM_ACCESS_KEY_ID}
    LITESTREAM_SECRET_ACCESS_KEY: ${LITESTREAM_SECRET_ACCESS_KEY}
```

```yaml
# litestream.yml
dbs:
  - path: /data/gateway.db
    replicas:
      - url: s3://my-bucket/33pol/gateway.db
```

Restore with `litestream restore -o /data/gateway.db s3://my-bucket/33pol/gateway.db`
while the gateway is stopped. Litestream pulls in a small binary/image, so it is not
air-gap-friendly out of the box — keep the scheduled hot backups above as the baseline and
layer Litestream on where the extra durability is worth the dependency.

## See also

- [deploy/docker/README.md](../../deploy/docker/README.md) — deploy tooling
- Single-instance constraint: the gateway must run one replica (the Helm chart enforces it).
