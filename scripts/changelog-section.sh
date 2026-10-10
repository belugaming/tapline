#!/usr/bin/env bash
# Prints the CHANGELOG.md section of one version (without its heading).
# Usage: scripts/changelog-section.sh 0.2.0
set -euo pipefail
cd "$(dirname "$0")/.."
version="${1:?usage: $0 <version>}"
awk -v v="$version" '
  /^## \[/ { if (found) exit; if (index($0, "## [" v "]") == 1) { found = 1; next } }
  /^\[[^]]+\]: / { if (found) exit }
  found { print }
' CHANGELOG.md | sed '/./,$!d' | sed -e :a -e '/^\n*$/{$d;N;ba' -e '}'
