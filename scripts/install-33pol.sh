#!/usr/bin/env bash
# Entry point for 33pol deployment installer.
exec "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/install/install-33pol.sh" "$@"
