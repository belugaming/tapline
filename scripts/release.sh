#!/usr/bin/env bash
# Cuts a release.
#
#   scripts/release.sh 0.2.0          # bump, update CHANGELOG, commit, tag v0.2.0
#   scripts/release.sh 0.2.0 --push   # ...and push, which starts the GitHub release build
#
# The version lives in project.yml (MARKETING_VERSION). The build number is the
# commit count and is set by CI at build time. Pushing a v* tag runs
# .github/workflows/release.yml, which builds Tapline and publishes the release
# with the matching CHANGELOG section as notes. Versions with a suffix
# (e.g. 0.3.0-beta.1) are published as pre-releases.
set -euo pipefail
cd "$(dirname "$0")/.."

version="${1:-}"
push="${2:-}"
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$ ]]; then
  echo "usage: $0 <major.minor.patch[-suffix]> [--push]" >&2
  exit 1
fi
tag="v$version"
current="$(scripts/version.sh)"

fail() { echo "error: $*" >&2; exit 1; }
[[ -z "$(git status --porcelain)" ]] || fail "the working tree has uncommitted changes"
[[ "$(git branch --show-current)" == "main" ]] || fail "releases are cut from main"
git rev-parse -q --verify "refs/tags/$tag" >/dev/null && fail "tag $tag already exists"
[[ "$version" != "$current" ]] || fail "project.yml is already at $version"
newest="$(printf '%s\n%s\n' "$current" "$version" | sort -V | tail -n 1)"
[[ "$newest" == "$version" ]] || fail "$version is lower than the current version $current"

unreleased="$(awk '/^## \[Unreleased\]/{f=1;next} /^## \[/{if(f)exit} f' CHANGELOG.md | grep -c '[^[:space:]]' || true)"
[[ "$unreleased" -gt 0 ]] || fail "the [Unreleased] section of CHANGELOG.md is empty"

sed -i '' "s/^\( *MARKETING_VERSION: *\).*/\1\"$version\"/" project.yml

python3 - "$version" "$current" "$(date +%Y-%m-%d)" <<'PY'
import re, sys
version, previous, date = sys.argv[1:4]
path = "CHANGELOG.md"
text = open(path, encoding="utf-8").read()
text = text.replace("## [Unreleased]", f"## [Unreleased]\n\n## [{version}] - {date}", 1)
base = "https://github.com/belugaming/tapline"
text = re.sub(r"^\[Unreleased\]: .*$",
              f"[Unreleased]: {base}/compare/v{version}...HEAD\n[{version}]: {base}/compare/v{previous}...v{version}",
              text, count=1, flags=re.M)
open(path, "w", encoding="utf-8").write(text)
PY

git add project.yml CHANGELOG.md
git commit -q -m "Release $tag"
git tag -a "$tag" -m "Tapline $version"
echo "Tagged $tag ($current -> $version)."

if [[ "$push" == "--push" ]]; then
  git push origin main "$tag"
  echo "Pushed. Follow the build at https://github.com/belugaming/tapline/actions"
else
  echo "Push to publish:  git push origin main $tag"
fi
