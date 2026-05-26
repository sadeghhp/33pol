#!/usr/bin/env bash
set -euo pipefail

RESULTS_DIR="${1:-TestResults}"
THRESHOLDS="${COVERAGE_THRESHOLDS:-33pol.Registry=90,33pol.Proxy=90,33pol.Security=85,33pol.Policy=85,33pol.Observability=85}"

if [[ ! -d "$RESULTS_DIR" ]]; then
  echo "Coverage results directory not found: $RESULTS_DIR" >&2
  exit 1
fi

python3 - "$RESULTS_DIR" "$THRESHOLDS" <<'PY'
import sys
import xml.etree.ElementTree as ET
from pathlib import Path
from collections import defaultdict

results_dir = Path(sys.argv[1])
thresholds = {}
for item in sys.argv[2].split(","):
    name, pct = item.split("=")
    thresholds[name] = float(pct)

files = list(results_dir.rglob("coverage.cobertura.xml"))
if not files:
    print(f"No coverage.cobertura.xml files under {results_dir}", file=sys.stderr)
    sys.exit(1)

merged: dict[tuple[str, str, int], int] = {}
for file in files:
    root = ET.parse(file).getroot()
    for pkg in root.findall("packages/package"):
        name = pkg.get("name", "")
        if name not in thresholds:
            continue
        for cls in pkg.findall("classes/class"):
            fn = cls.get("filename", "")
            for line in cls.findall("lines/line"):
                num = int(line.get("number"))
                hits = int(line.get("hits", 0))
                key = (name, fn, num)
                merged[key] = max(merged.get(key, 0), hits)

failed = False
for asm, min_pct in sorted(thresholds.items()):
    keys = [k for k in merged if k[0] == asm]
    total = len(keys)
    covered = sum(1 for k in keys if merged[k] > 0)
    pct = 100.0 * covered / total if total else 0.0
    status = "OK" if pct >= min_pct else "FAIL"
    print(f"{asm}: {pct:.1f}% ({covered}/{total}) threshold {min_pct:.0f}% [{status}]")
    if pct < min_pct:
        failed = True

sys.exit(1 if failed else 0)
PY
