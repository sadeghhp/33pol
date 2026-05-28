#!/usr/bin/env bash
# HTTP GET /health/live without curl/wget (avoids apt during image build on restricted networks).
set -euo pipefail
port="${GATEWAY_HEALTH_PORT:-8080}"
exec 3<>"/dev/tcp/127.0.0.1/${port}"
printf 'GET /health/live HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n' >&3
read -r status_line <&3
exec 3<&-
exec 3>&-
[[ "${status_line}" == *" 200 "* ]]
