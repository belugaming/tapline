#!/usr/bin/env bash
# Prints the app version (MARKETING_VERSION in project.yml, the single source of truth).
set -euo pipefail
cd "$(dirname "$0")/.."
sed -n 's/^ *MARKETING_VERSION: *"\{0,1\}\([^"]*\)"\{0,1\} *$/\1/p' project.yml | head -n 1
