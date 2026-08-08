#!/usr/bin/env python3
"""Restore admin access to a deployed gateway without wiping the database.

`GATEWAY_ADMIN_API_KEY` is read by the bootstrap seeder only, and the seeder is a
no-op once the `tenants` table is non-empty (GatewayDbBootstrap.EnsureInitializedAsync).
So editing .env after the first boot leaves the stored key untouched and every
/admin/api/* call answers 401 invalid_api_key.

This script writes the key straight into the SQLite database with the pepper the
RUNNING container is configured with, so the value matches what ApiKeyValidator
computes. Existing tenants, keys, and usage history are left in place.

Usage (on the deploy host, from the repo/deploy directory holding docker-compose.yml):

    python3 scripts/reset-admin-key.py                      # key + pepper from ./.env
    python3 scripts/reset-admin-key.py --key sk-33pol-...   # explicit key
    python3 scripts/reset-admin-key.py --dry-run            # show what would change
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import hmac
import json
import os
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import time
import uuid

# Ticks between 0001-01-01 and the Unix epoch. Timestamps are stored as
# DateTimeOffset.UtcTicks (INTEGER) — see GatewayDbContext.DateTimeOffsetToUtcTicksConverter.
TICKS_AT_EPOCH = 62_135_596_800
# ApiKeyHashing.PrefixLength, plus the legacy length that pre-widening databases still store.
# Keep in sync with ApiKeyHashing.LookupPrefixLengths.
PREFIX_LENGTH = 20
LOOKUP_PREFIX_LENGTHS = (20, 12)
DB_FILES = ("gateway.db", "gateway.db-wal", "gateway.db-shm")


def run(cmd: list[str], **kwargs) -> subprocess.CompletedProcess:
    return subprocess.run(cmd, check=True, capture_output=True, text=True, **kwargs)


def parse_env_file(path: str) -> dict[str, str]:
    values: dict[str, str] = {}
    if not os.path.exists(path):
        return values
    with open(path, encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            name, _, value = line.partition("=")
            value = value.strip()
            # docker compose keeps surrounding quotes out of the container env; mirror that.
            if len(value) >= 2 and value[0] == value[-1] and value[0] in "\"'":
                value = value[1:-1]
            values[name.strip()] = value
    return values


def resolve_container(explicit: str | None, service: str) -> str:
    if explicit:
        return explicit
    result = subprocess.run(
        ["docker", "compose", "ps", "-q", service],
        capture_output=True,
        text=True,
    )
    container = result.stdout.strip().splitlines()
    if result.returncode == 0 and container:
        return container[0]
    sys.exit(
        f"Could not find a running '{service}' container. Run this from the directory "
        f"holding docker-compose.yml, or pass --container <name-or-id>."
    )


def container_env(container: str) -> dict[str, str]:
    out = run(["docker", "inspect", "--format", "{{json .Config.Env}}", container]).stdout
    env: dict[str, str] = {}
    for entry in json.loads(out):
        name, _, value = entry.partition("=")
        env[name] = value
    return env


def hash_key(secret: str, pepper: str) -> str:
    """HMAC-SHA256(pepper, secret) base64 — mirrors ApiKeyHashing.Hash."""
    digest = hmac.new(pepper.encode("utf-8"), secret.strip().encode("utf-8"), hashlib.sha256).digest()
    return base64.b64encode(digest).decode("ascii")


def copy_db_out(container: str, workdir: str) -> str:
    local_db = os.path.join(workdir, "gateway.db")
    for name in DB_FILES:
        # -wal/-shm are absent on a cleanly stopped database; that is fine.
        subprocess.run(
            ["docker", "cp", f"{container}:/data/{name}", os.path.join(workdir, name)],
            capture_output=True,
            text=True,
        )
    if not os.path.exists(local_db):
        sys.exit(f"No /data/gateway.db inside container {container}.")
    return local_db


def copy_db_in(container: str, workdir: str) -> None:
    for name in DB_FILES:
        source = os.path.join(workdir, name)
        if os.path.exists(source):
            run(["docker", "cp", source, f"{container}:/data/{name}"])


def upsert_admin_key(
    db_path: str,
    key: str,
    pepper: str,
    role: str,
    tenant_slug: str,
    tenant_name: str,
) -> str:
    prefix = key.strip()[:PREFIX_LENGTH]
    key_hash = hash_key(key, pepper)
    now_ticks = int((time.time() + TICKS_AT_EPOCH) * 10_000_000)

    connection = sqlite3.connect(db_path)
    try:
        connection.execute("PRAGMA foreign_keys = ON")
        cursor = connection.cursor()

        cursor.execute("SELECT Id FROM tenants WHERE IsActive = 1 ORDER BY CreatedAt LIMIT 1")
        row = cursor.fetchone()
        if row:
            tenant_id = row[0]
            action = f"reusing tenant {tenant_id}"
        else:
            tenant_id = str(uuid.uuid4())
            cursor.execute(
                "INSERT INTO tenants (Id, Slug, Name, PlanSlug, CostCenter, IsActive, CreatedAt, UpdatedAt) "
                "VALUES (?, ?, ?, NULL, NULL, 1, ?, ?)",
                (tenant_id, tenant_slug, tenant_name, now_ticks, now_ticks),
            )
            action = f"created tenant {tenant_slug} ({tenant_id})"

        # Rewrite any row already stored for this key rather than adding a duplicate — including rows
        # written before the prefix was widened from 12 to 20 characters.
        candidate_prefixes = []
        for length in LOOKUP_PREFIX_LENGTHS:
            candidate = key.strip()[:length]
            if candidate not in candidate_prefixes:
                candidate_prefixes.append(candidate)
        placeholders = ",".join("?" * len(candidate_prefixes))
        cursor.execute(
            f"SELECT Id FROM api_keys WHERE KeyPrefix IN ({placeholders})", candidate_prefixes
        )
        existing = cursor.fetchall()
        if existing:
            cursor.execute(
                f"UPDATE api_keys SET KeyHash = ?, KeyPrefix = ?, Role = ?, Scopes = '[\"admin\"]', "
                f"RevokedAt = NULL, ExpiresAt = NULL, TenantId = ? WHERE KeyPrefix IN ({placeholders})",
                (key_hash, prefix, role, tenant_id, *candidate_prefixes),
            )
            action += f"; rewrote {len(existing)} existing key row(s) to prefix {prefix}"
        else:
            cursor.execute(
                "INSERT INTO api_keys (Id, TenantId, KeyHash, KeyPrefix, Role, Scopes, ExpiresAt, "
                "RevokedAt, CreatedAt, LastUsedAt, Label, Assignee, Description, CostCenter) "
                "VALUES (?, ?, ?, ?, ?, '[\"admin\"]', NULL, NULL, ?, NULL, ?, NULL, ?, NULL)",
                (
                    str(uuid.uuid4()),
                    tenant_id,
                    key_hash,
                    prefix,
                    role,
                    now_ticks,
                    "recovery-admin",
                    "Inserted by scripts/reset-admin-key.py",
                ),
            )
            action += f"; inserted new {role} key with prefix {prefix}"

        connection.commit()
        return action
    finally:
        connection.close()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--key", help="Admin API key to install (default: GATEWAY_ADMIN_API_KEY from --env-file)")
    parser.add_argument("--pepper", help="Override the pepper (default: the running container's Gateway__Security__KeyPepper)")
    parser.add_argument("--env-file", default=".env", help="Path to the deploy .env (default: ./.env)")
    parser.add_argument("--container", help="Gateway container name or id (default: docker compose ps -q <service>)")
    parser.add_argument("--service", default="gateway", help="Compose service name (default: gateway)")
    parser.add_argument("--role", default="Admin", choices=["Admin", "Both"], help="Key role (default: Admin)")
    parser.add_argument("--tenant-slug", default="default", help="Slug used only if no tenant exists yet")
    parser.add_argument("--tenant-name", default="Default Tenant", help="Name used only if no tenant exists yet")
    parser.add_argument("--dry-run", action="store_true", help="Report what would change and exit")
    args = parser.parse_args()

    env_file = parse_env_file(args.env_file)
    key = args.key or env_file.get("GATEWAY_ADMIN_API_KEY", "").strip()
    if not key:
        return print_error("No admin key. Pass --key, or set GATEWAY_ADMIN_API_KEY in " + args.env_file)

    container = resolve_container(args.container, args.service)
    live_env = container_env(container)

    security_pepper = live_env.get("Gateway__Security__KeyPepper", "")
    bootstrap_pepper = live_env.get("Gateway__Bootstrap__KeyPepper", "")
    if security_pepper and bootstrap_pepper and security_pepper != bootstrap_pepper:
        print(
            "WARNING: the container's Security and Bootstrap peppers differ. Set both from one "
            "GATEWAY_KEY_PEPPER value, or bootstrap-seeded keys can never authenticate.",
            file=sys.stderr,
        )

    # The validator hashes with Gateway__Security__KeyPepper, so that is the only pepper that
    # can produce a matching hash — not whatever .env says if the container was never recreated.
    pepper = args.pepper or security_pepper
    if not pepper:
        return print_error("Container exposes no Gateway__Security__KeyPepper; pass --pepper explicitly.")

    env_pepper = env_file.get("GATEWAY_KEY_PEPPER", "").strip()
    if env_pepper and env_pepper != pepper and not args.pepper:
        print(
            f"NOTE: {args.env_file} sets a different GATEWAY_KEY_PEPPER than the running container. "
            "Using the container's value so the key works right now; recreate the container "
            "(docker compose up -d --force-recreate gateway) only if you intend to invalidate every "
            "existing key, and re-run this script afterwards.",
            file=sys.stderr,
        )

    print(f"container : {container}")
    print(f"key prefix: {key.strip()[:PREFIX_LENGTH]}")
    print(f"role      : {args.role}")

    if args.dry_run:
        print("dry-run: no changes written.")
        return 0

    workdir = tempfile.mkdtemp(prefix="33pol-admin-key-")
    try:
        # Stop the gateway so SQLite is not written underneath the copy, and so the in-process
        # validator cache is dropped on restart.
        print("stopping gateway ...")
        run(["docker", "stop", container])

        db_path = copy_db_out(container, workdir)
        backup = os.path.join(workdir, "gateway.db.bak")
        shutil.copy2(db_path, backup)

        action = upsert_admin_key(
            db_path, key, pepper, args.role, args.tenant_slug, args.tenant_name
        )
        print(f"database  : {action}")

        copy_db_in(container, workdir)
        print("starting gateway ...")
        run(["docker", "start", container])
    finally:
        print(f"working copy (incl. pre-change backup): {workdir}")

    print("\nDone. Verify with:")
    print(f'  curl -si -H "X-API-Key: {key.strip()[:PREFIX_LENGTH]}..." http://localhost:8080/admin/api/summary | head -1')
    return 0


def print_error(message: str) -> int:
    print(message, file=sys.stderr)
    return 1


if __name__ == "__main__":
    sys.exit(main())
