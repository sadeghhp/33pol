#!/usr/bin/env python3
"""Cap every API key inside its tenant's bucket, so one key cannot drain it for the rest.

Every key a tenant holds draws on one bucket (docs/rate-limit-guide.en.md, "Tiers"). On a gateway
whose keys were all issued from the console there is one tenant, so the default tier is shared by
the whole deployment and the busiest key decides who else is refused. An `api_key` rule bounds one
key inside that bucket, and rules only tighten, so adding one per key is safe to do one at a time.
This script writes those rules for every key that has none.

It plans by default and changes nothing:

    export POL33_ADMIN_KEY=sk-33pol-...
    python3 scripts/rate-limit-cap-keys.py --gateway https://gateway.example.com --max-share 0.4

That prints what each key would be capped at next to what it sent over the last three hours, and
writes two files: the configuration as it is now (the rollback) and the configuration proposed.
Read the table, then apply exactly that plan:

    python3 scripts/rate-limit-cap-keys.py --gateway https://gateway.example.com \\
        --apply rate-limit-plan.json

The write is conditional on the version the plan was made from, so it is refused if anybody saved
in between. To undo it, apply the rollback file the plan step wrote, the same way.

A cap is a share of the tenant tier: `--max-share 0.4` on a 120 rpm tier caps each key at 48 rpm.
`--tier-rpm`, `--tier-burst` and `--tier-streams` change the default tier in the same plan, and the
caps are then shares of the new numbers. `--rate-only` leaves streams uncapped on the new rules:
use it until each key's peak of open streams is known, because a stream cap set below a key's
normal peak refuses traffic that is admitted today. Keys that already have an `api_key` rule, and revoked or
archived keys, are left alone. Standard library only.
"""

from __future__ import annotations

import argparse
import copy
import datetime
import json
import math
import os
import sys
import urllib.error
import urllib.parse
import urllib.request

USAGE_MINUTES = 180
MAX_RULES = 1000


def plan_caps(config: dict, keys: list[dict], *, max_share: float, floor_rpm: int,
              tier: dict | None = None, rate_only: bool = False) -> tuple[dict, list[dict]]:
    """Return (proposed configuration, one row per key saying what was decided for it).

    Pure: no I/O, so the arithmetic can be tested without a gateway.
    """
    if not 0 < max_share <= 1:
        raise ValueError("max_share must be above 0 and at most 1")

    proposed = copy.deepcopy(config)
    default = proposed.setdefault("default", {})
    for field, value in (tier or {}).items():
        if value is not None:
            default[field] = value

    rpm = int(default.get("rpm", 0))
    burst = int(default.get("burst", 0))
    streams = int(default.get("maxConcurrentStreams", 0))
    if rpm < 1:
        raise ValueError("the default tier has no rpm to take a share of")

    cap = {
        "rpm": max(floor_rpm, math.ceil(rpm * max_share)),
        # A burst is extra tokens above a rate, so it scales with the rate it sits on.
        "burst": math.ceil(burst * max_share),
        # 0 streams means unlimited, and a share of unlimited is unlimited. A rule with 0 leaves
        # the key on the tenant's stream cap, which is what it has without a rule.
        "maxConcurrentStreams": 0 if rate_only or streams <= 0 else max(1, math.ceil(streams * max_share)),
    }

    rules = list(proposed.get("rules") or [])
    already = {str(r.get("target", "")).lower() for r in rules
               if str(r.get("scope", "")).lower() == "api_key"}

    decisions = []
    for key in keys:
        key_id = str(key.get("id", ""))
        label = key.get("label") or key.get("keyPrefix") or key_id
        row = {"id": key_id, "label": label, **cap}
        if key.get("revokedAt") or key.get("archivedAt"):
            row["action"] = "skip: revoked or archived"
        elif key_id.lower() in already:
            row["action"] = "keep: has a rule"
        else:
            row["action"] = "add"
            rules.append({"scope": "api_key", "target": key_id, **cap, "enabled": True})
        decisions.append(row)

    if len(rules) > MAX_RULES:
        raise ValueError(f"{len(rules)} rules would exceed the gateway's limit of {MAX_RULES}")

    proposed["rules"] = rules
    return proposed, decisions


def put_body(config: dict) -> dict:
    """The fields PUT /admin/api/rate-limits reads; the rest of a GET answer is read-only."""
    return {
        "enabled": config.get("enabled", True),
        "adaptiveEnabled": config.get("adaptiveEnabled", False),
        "default": config.get("default", {}),
        "plans": config.get("plans", {}),
        "rules": config.get("rules") or [],
    }


class Gateway:
    def __init__(self, base: str, admin_key: str, timeout: float = 30.0) -> None:
        self._base = base.rstrip("/")
        self._key = admin_key
        self._timeout = timeout

    def call(self, method: str, path: str, body: dict | None = None,
             if_match: int | None = None) -> tuple[int, dict | list | None]:
        data = None if body is None else json.dumps(body).encode()
        request = urllib.request.Request(self._base + path, data=data, method=method)
        request.add_header("Authorization", "Bearer " + self._key)
        if data is not None:
            request.add_header("Content-Type", "application/json")
        if if_match is not None:
            request.add_header("If-Match", f'W/"{if_match}"')
        try:
            with urllib.request.urlopen(request, timeout=self._timeout) as response:
                raw = response.read()
                return response.status, json.loads(raw) if raw else None
        except urllib.error.HTTPError as error:
            raw = error.read()
            try:
                return error.code, json.loads(raw) if raw else None
            except json.JSONDecodeError:
                return error.code, {"message": raw.decode(errors="replace")[:500]}

    def get(self, path: str) -> dict | list:
        status, body = self.call("GET", path)
        if status != 200 or body is None:
            raise SystemExit(f"GET {path} answered {status}: {body}")
        return body


def key_list(answer: dict | list) -> list[dict]:
    if isinstance(answer, list):
        return answer
    for field in ("keys", "items", "data"):
        if isinstance(answer.get(field), list):
            return answer[field]
    raise SystemExit("GET /admin/api/keys answered a shape this script does not know")


def observed_minutes(report: dict) -> float:
    """How many minutes of traffic a usage report really holds.

    The tracker lives in the gateway's memory and starts empty at every restart, while the report
    divides by the window that was asked for. Just after a restart that understates every rate.
    """
    window = float(report.get("windowMinutes") or USAGE_MINUTES)
    try:
        since = datetime.datetime.fromisoformat(
            str(report["tracker"]["trackingSinceUtc"]).replace("Z", "+00:00")[:19] + "+00:00")
        now = datetime.datetime.fromisoformat(
            str(report["generatedUtc"]).replace("Z", "+00:00")[:19] + "+00:00")
    except (KeyError, TypeError, ValueError):
        return window
    return max(1.0, min(window, (now - since).total_seconds() / 60))


def usage_by_key(gateway: Gateway) -> tuple[dict[str, dict], float]:
    """Observed traffic per key id, and the minutes it covers.

    Empty when the tracker is off; the plan does not depend on it.
    """
    status, body = gateway.call("GET", f"/admin/api/rate-limits/usage?minutes={USAGE_MINUTES}&take=500")
    if status != 200 or not isinstance(body, dict):
        return {}, float(USAGE_MINUTES)
    minutes = observed_minutes(body)
    rows = {}
    for row in body.get("byApiKey") or []:
        key_id = str(row.get("apiKeyId") or row.get("key") or "").lower()
        if key_id:
            # Requests over the minutes actually tracked, not over the window asked for.
            if row.get("requests") is not None:
                row = {**row, "requestsPerMinute": row["requests"] / minutes}
            rows[key_id] = row
    return rows, minutes


def print_table(decisions: list[dict], usage: dict[str, dict], default: dict,
                minutes: float = USAGE_MINUTES) -> None:
    print(f"default tier: {default.get('rpm')} rpm, {default.get('burst')} burst, "
          f"{default.get('maxConcurrentStreams')} streams")
    print(f"observed columns cover the last {minutes:.0f} minutes, and are an average: "
          "a key that bursts is busier at its peak than the rpm shown.")
    if minutes < USAGE_MINUTES:
        print(f"The gateway restarted {minutes:.0f} minutes ago and its usage history restarted with it. "
              "That is too little to judge a cap by: plan again once it has run for a few hours.")
    print()
    header = f"{'key':<34} {'action':<26} {'cap rpm':>7} {'burst':>5} {'streams':>7} {'seen rpm':>8} {'refused':>8}"
    print(header)
    print("-" * len(header))
    over = []
    for row in decisions:
        seen = usage.get(row["id"].lower(), {})
        seen_rpm = seen.get("requestsPerMinute")
        print(f"{str(row['label'])[:34]:<34} {row['action']:<26} {row['rpm']:>7} {row['burst']:>5} "
              f"{row['maxConcurrentStreams']:>7} "
              f"{'' if seen_rpm is None else format(seen_rpm, '.1f'):>8} {seen.get('rejected', ''):>8}")
        if row["action"] == "add" and seen_rpm is not None and seen_rpm > row["rpm"]:
            over.append(row["label"])
    added = sum(1 for r in decisions if r["action"] == "add")
    print(f"\n{added} rule(s) to add, {len(decisions) - added} key(s) left as they are.")
    if over:
        print("Already averaging above the proposed cap, so these would be refused more than now: "
              + ", ".join(map(str, over)))


def write_json(path: str, value: dict) -> None:
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(value, handle, indent=2)
        handle.write("\n")


def do_plan(gateway: Gateway, args: argparse.Namespace) -> int:
    config = gateway.get("/admin/api/rate-limits")
    keys = key_list(gateway.get("/admin/api/keys"))
    tier = {"rpm": args.tier_rpm, "burst": args.tier_burst, "maxConcurrentStreams": args.tier_streams}
    proposed, decisions = plan_caps(config, keys, max_share=args.max_share,
                                    floor_rpm=args.floor_rpm, tier=tier, rate_only=args.rate_only)

    usage, minutes = usage_by_key(gateway)
    print_table(decisions, usage, proposed["default"], minutes)
    version = config.get("version")
    write_json(args.rollback, {"basedOnVersion": None, "config": put_body(config)})
    write_json(args.out, {"basedOnVersion": version, "config": put_body(proposed)})
    print(f"\nNothing was changed. Current configuration (version {version}) saved to {args.rollback}; "
          f"proposal saved to {args.out}.")
    print(f"Apply with: --apply {args.out}")
    return 0


def do_apply(gateway: Gateway, path: str) -> int:
    with open(path, encoding="utf-8") as handle:
        plan = json.load(handle)
    status, body = gateway.call("PUT", "/admin/api/rate-limits", plan["config"],
                                if_match=plan.get("basedOnVersion"))
    message = body.get("message") if isinstance(body, dict) else body
    if status == 409:
        print(f"Refused: the configuration changed since this plan was made. Plan again. ({message})")
        return 1
    if status != 200:
        print(f"Refused with {status}: {message}")
        return 1
    print(f"Applied. The gateway is now at version {body.get('version')}.")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--gateway", required=True, help="base URL, e.g. https://gateway.example.com")
    parser.add_argument("--max-share", type=float, default=0.4,
                        help="largest share of the tenant tier one key may take (default 0.4)")
    parser.add_argument("--floor-rpm", type=int, default=5, help="no cap is set below this (default 5)")
    parser.add_argument("--tier-rpm", type=int, help="also set the default tier's rpm")
    parser.add_argument("--tier-burst", type=int, help="also set the default tier's burst")
    parser.add_argument("--tier-streams", type=int, help="also set the default tier's stream cap")
    parser.add_argument("--rate-only", action="store_true",
                        help="cap rpm and burst only; new rules leave streams uncapped")
    parser.add_argument("--out", default="rate-limit-plan.json")
    parser.add_argument("--rollback", default="rate-limit-rollback.json")
    parser.add_argument("--apply", metavar="PLAN", help="write this plan file to the gateway")
    args = parser.parse_args(argv)

    admin_key = os.environ.get("POL33_ADMIN_KEY", "")
    if not admin_key:
        print("Set POL33_ADMIN_KEY to an admin API key.", file=sys.stderr)
        return 2

    gateway = Gateway(args.gateway, admin_key)
    return do_apply(gateway, args.apply) if args.apply else do_plan(gateway, args)


if __name__ == "__main__":
    sys.exit(main())
