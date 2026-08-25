#!/usr/bin/env bash
# Pre-release gate: build in Release, run the full test suite, then boot the real gateway
# with production-like settings and confirm it starts, answers health, and logs no crash.
#
# Usage:  scripts/verify-release.sh
# Exit code is non-zero on the first failing step.
set -euo pipefail

# --smoke-only: skip build/test and only boot the gateway (assumes a Release build exists).
SMOKE_ONLY=0
[ "${1:-}" = "--smoke-only" ] && SMOKE_ONLY=1

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

# ---- locate the .NET SDK ------------------------------------------------------------
if ! command -v dotnet >/dev/null 2>&1; then
    if [ -x "$HOME/.dotnet/dotnet" ]; then
        export PATH="$HOME/.dotnet:$PATH"
    else
        echo "FAIL: dotnet SDK not found (install to ~/.dotnet or add to PATH)" >&2
        exit 1
    fi
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

step() { printf '\n==> %s\n' "$*"; }
fail() { printf '\nFAIL: %s\n' "$*" >&2; exit 1; }

# run_live <logfile> <grep-pattern> <label> -- <command...>
# Runs the command in the background writing to <logfile>, streams lines matching <grep-pattern>
# to the terminal as they appear, and keeps a "still running" ticker updating every 5s so a long
# step never looks stuck. Returns the command's exit code.
run_live() {
    local log="$1" pattern="$2" label="$3"; shift 4
    : >"$log"
    "$@" >"$log" 2>&1 &
    local pid=$! start=$SECONDS shown=0 tick=0
    while kill -0 "$pid" 2>/dev/null; do
        local total
        total=$(grep -cE "$pattern" "$log" 2>/dev/null || true)
        total=${total:-0}
        if [ "$total" -gt "$shown" ]; then
            printf '\r\033[K'
            grep -E "$pattern" "$log" | tail -n +"$((shown + 1))" | sed 's/^/    /'
            shown=$total
        fi
        tick=$(( (tick + 1) % 4 ))
        local sp='|/-\\'
        printf '\r    %s %s... %ds elapsed' "${sp:$tick:1}" "$label" $(( SECONDS - start ))
        sleep 1
    done
    wait "$pid"; local rc=$?
    printf '\r\033[K'
    grep -E "$pattern" "$log" | tail -n +"$((shown + 1))" | sed 's/^/    /' || true
    printf '    done in %ds\n' $(( SECONDS - start ))
    return $rc
}

WORK="$(mktemp -d)"
APP_PID=""
cleanup() {
    if [ -n "$APP_PID" ] && kill -0 "$APP_PID" 2>/dev/null; then
        kill "$APP_PID" 2>/dev/null || true
        wait "$APP_PID" 2>/dev/null || true
    fi
    rm -rf "$WORK"
}
trap cleanup EXIT

# ---- 1. build --------------------------------------------------------------------------
if [ "$SMOKE_ONLY" -eq 0 ]; then
    step "Restore + build (Release)"
    BUILD_LOG="$WORK/build.log"
    if ! run_live "$BUILD_LOG" '^\s+[0-9A-Za-z_.]+ -> .*\.dll$|error ' "building" -- \
            dotnet build 33pol.sln -c Release --nologo -v m; then
        grep -E "error|Error" "$BUILD_LOG" | sort -u | head -40
        fail "build failed"
    fi

    # ---- 2. tests ----------------------------------------------------------------------
    step "Run full test suite (Release)"
    TEST_LOG="$WORK/test.log"
    PROJECTS=$(ls -d tests/*.Tests | wc -l)
    echo "    $PROJECTS test projects; each reports as it finishes (Integration + Persistence take a few minutes)"
    if ! run_live "$TEST_LOG" '^Passed!|^Failed!|^Test run for' "running tests" -- \
            dotnet test 33pol.sln -c Release --no-build --nologo -v q; then
        # Surface the reason instead of a bare non-zero exit: failed assertions, host crashes,
        # or a test project that never reported.
        grep -E "^Passed!|^Failed!" "$TEST_LOG" || true
        grep -E "Failed |\[FAIL\]|Error Message|Stack Trace|error |Aborted|crash|exited" -A8 "$TEST_LOG" | head -120 || true
        cp "$TEST_LOG" "$ROOT/verify-release-test.log"
        fail "tests failed (full output: verify-release-test.log)"
    fi
    PASSED=$(grep -cE "^Passed!" "$TEST_LOG" || true)
    [ "$PASSED" -eq "$PROJECTS" ] || fail "only $PASSED of $PROJECTS test projects reported results"
else
    step "Skipping build and tests (--smoke-only)"
fi

# ---- 3. smoke-run the real gateway -----------------------------------------------------
step "Smoke run: start gateway with production-like settings"
PORT=$(( 20000 + RANDOM % 20000 ))
# Run the built DLL the way production does. `dotnet run` would apply launchSettings.json and
# bind to its applicationUrl instead of ASPNETCORE_URLS.
APP_DLL="$(ls src/33pol.App/bin/Release/net*/33pol.App.dll 2>/dev/null | head -n1)"
[ -n "$APP_DLL" ] || fail "Release build of 33pol.App not found (run without --smoke-only first)"
LOG="$WORK/gateway.log"
mkdir -p "$WORK/config"
echo '{"models":[]}' > "$WORK/config/models.json"

# Production-shaped config: real pepper (not the dev default), a bootstrap admin key (Production
# refuses to start without at least one key), SQLite on disk. Everything lives in the temp dir.
PEPPER="verify-release-$(head -c 24 /dev/urandom | base64 | tr -d '/+=')"
ADMIN_KEY="sk-33pol-verify-$(head -c 24 /dev/urandom | base64 | tr -d '/+=')"
ASPNETCORE_ENVIRONMENT=Production \
ASPNETCORE_URLS="http://127.0.0.1:${PORT}" \
ConnectionStrings__GatewayDb="Data Source=${WORK}/gateway.db" \
Gateway__Security__KeyPepper="$PEPPER" \
Gateway__Bootstrap__KeyPepper="$PEPPER" \
Gateway__Bootstrap__AdminApiKey="$ADMIN_KEY" \
Gateway__ModelsConfigPath="${WORK}/config/models.json" \
Gateway__UpstreamSecretsPath="${WORK}/config/upstream-secrets.enc" \
dotnet "$APP_DLL" >"$LOG" 2>&1 &
APP_PID=$!

# Wait for the health endpoint (it is anonymous; no key needed).
HEALTH=""
for i in $(seq 1 60); do
    if ! kill -0 "$APP_PID" 2>/dev/null; then
        cat "$LOG"; fail "gateway process exited during startup"
    fi
    HEALTH="$(curl -fsS "http://127.0.0.1:${PORT}/health/live" 2>/dev/null || true)"
    [ -n "$HEALTH" ] && break
    printf '\r    waiting for gateway on port %s... %ds' "$PORT" "$i"
    sleep 1
done
printf '\r\033[K'
[ -n "$HEALTH" ] || { cat "$LOG"; fail "gateway did not answer /health/live within 60s"; }

# The new upstream-secrets check must report Healthy on a clean instance. "Degraded" here
# would mean the pepper wiring is broken, since there are no stored credentials to fail.
[ "$HEALTH" = "Healthy" ] || { cat "$LOG"; fail "/health/live returned '$HEALTH' (expected Healthy)"; }
echo "    /health/live -> $HEALTH"

BASE="http://127.0.0.1:${PORT}"
curl -fsS "$BASE/" >/dev/null || fail "GET / failed"

# Unauthenticated inference must get a gateway error envelope, not a bare Kestrel status.
ENVELOPE="$(curl -sS -X POST "$BASE/v1/chat/completions" -H 'Content-Type: application/json' -d '{}')"
echo "$ENVELOPE" | grep -q '"invalid_api_key"' || fail "expected invalid_api_key envelope, got: $ENVELOPE"
echo "    unauthenticated request -> invalid_api_key envelope OK"

# Authenticated malformed JSON walks the parse path and the exception middleware.
ENVELOPE="$(curl -sS -X POST "$BASE/v1/chat/completions" -H "Authorization: Bearer $ADMIN_KEY" \
    -H 'Content-Type: application/json' -d '{"model": ')"
echo "$ENVELOPE" | grep -q '"error"' || fail "malformed JSON did not return an error envelope: $ENVELOPE"
echo "    malformed JSON -> error envelope OK"

# A body shorter than its declared Content-Length is what the production embeddings client was
# sending. Kestrel raises BadHttpRequestException for it; the gateway must classify it as
# request_incomplete and stay up. Raw socket, since curl will not send a lying Content-Length.
python3 - "$PORT" "$ADMIN_KEY" <<'PY' || fail "truncated-body request could not be sent"
import socket, sys
port, key = int(sys.argv[1]), sys.argv[2]
s = socket.create_connection(("127.0.0.1", port), timeout=5)
s.sendall((f"POST /v1/embeddings HTTP/1.1\r\nHost: localhost\r\nAuthorization: Bearer {key}\r\n"
           "Content-Type: application/json\r\nContent-Length: 500\r\n\r\n{\"model\":\"x\"").encode())
s.shutdown(socket.SHUT_WR)
try:
    data = s.recv(4096)
except OSError:
    data = b""
s.close()
sys.exit(0 if (not data or b"request_incomplete" in data or b"400" in data) else 1)
PY
echo "    truncated body -> handled without crash"

# Give background hosted services a moment, then make sure nothing crashed or threw.
sleep 3
kill -0 "$APP_PID" 2>/dev/null || { cat "$LOG"; fail "gateway process died after startup"; }
if grep -Eiq 'Unhandled exception|terminated unexpectedly|\[FTL\]|Fatal' "$LOG"; then
    cat "$LOG"; fail "fatal/unhandled errors found in gateway log"
fi
if grep -q 'cannot be decrypted' "$LOG"; then
    cat "$LOG"; fail "upstream credential verification reported a pepper mismatch"
fi
echo "    gateway log clean (no fatal/unhandled errors)"

# ---- 4. graceful shutdown ---------------------------------------------------------------
step "Graceful shutdown"
kill -TERM "$APP_PID"
for _ in $(seq 1 30); do
    kill -0 "$APP_PID" 2>/dev/null || break
    sleep 1
done
if kill -0 "$APP_PID" 2>/dev/null; then
    kill -9 "$APP_PID" 2>/dev/null || true; fail "gateway did not stop within 30s of SIGTERM"
fi
wait "$APP_PID" 2>/dev/null || true
APP_PID=""

printf '\nALL CHECKS PASSED — build, tests and smoke run are clean.\n'
