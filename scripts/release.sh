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
#
# Tapline for Windows ships with the same version. When windows/CHANGELOG.md has
# entries under "## Unreleased", they become "## <version>", Tapline.csproj gets
# the version and windows-v<version> is tagged on the same commit, which runs
# .github/workflows/release-windows.yml.
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

windows_tag=""
windows_unreleased="$(awk '/^## Unreleased/{f=1;next} /^## /{if(f)exit} f' windows/CHANGELOG.md | grep -c '[^[:space:]]' || true)"
if [[ "$windows_unreleased" -gt 0 ]]; then
  windows_tag="windows-$tag"
  git rev-parse -q --verify "refs/tags/$windows_tag" >/dev/null && fail "tag $windows_tag already exists"
fi

# Whether version $1 is newer than $2. Compares major.minor.patch first; with the same
# numbers, a version without a suffix is newer than its pre-releases
# (0.3.0 > 0.3.0-rc.1 > 0.3.0-beta.2). sort -V alone ranks 0.3.0-beta.1 above 0.3.0.
newer() {
  local a="${1%%-*}" b="${2%%-*}"
  local a_pre="${1#"$a"}" b_pre="${2#"$b"}"
  if [[ "$a" != "$b" ]]; then
    [[ "$(printf '%s\n%s\n' "$a" "$b" | sort -V | tail -n 1)" == "$a" ]]
    return
  fi
  [[ -z "$b_pre" ]] && return 1
  [[ -z "$a_pre" ]] && return 0
  [[ "$(printf '%s\n%s\n' "${a_pre#-}" "${b_pre#-}" | sort -V | tail -n 1)" == "${a_pre#-}" ]]
}
newer "$version" "$current" || fail "$version is lower than the current version $current"

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

if [[ -n "$windows_tag" ]]; then
  python3 - "$version" <<'PY'
import re, sys
version = sys.argv[1]
path = "windows/CHANGELOG.md"
text = open(path, encoding="utf-8").read()
text = re.sub(r"^## Unreleased$", f"## Unreleased\n\n## {version}", text, count=1, flags=re.M)
open(path, "w", encoding="utf-8").write(text)
# The csproj version is the default for local and CI builds; it must be numeric.
path = "windows/src/Tapline/Tapline.csproj"
text = open(path, encoding="utf-8").read()
text, n = re.subn(r"(<Version Condition=\"'\$\(Version\)' == ''\">)[^<]*(</Version>)",
                  rf"\g<1>{version.split('-')[0]}\g<2>", text, count=1)
if n != 1:
    sys.exit("error: no default <Version> in Tapline.csproj")
open(path, "w", encoding="utf-8").write(text)
PY
  git add windows/CHANGELOG.md windows/src/Tapline/Tapline.csproj
fi

git commit -q -m "Release $tag"
git tag -a "$tag" -m "Tapline $version"
echo "Tagged $tag ($current -> $version)."
tags=("$tag")
if [[ -n "$windows_tag" ]]; then
  git tag -a "$windows_tag" -m "Tapline for Windows $version"
  echo "Tagged $windows_tag."
  tags+=("$windows_tag")
else
  echo "windows/CHANGELOG.md has nothing under Unreleased: no Windows release."
fi

if [[ "$push" == "--push" ]]; then
  git push origin main "${tags[@]}"
  echo "Pushed. Follow the build at https://github.com/belugaming/tapline/actions"
else
  echo "Push to publish:  git push origin main ${tags[*]}"
fi
