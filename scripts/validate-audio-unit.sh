#!/usr/bin/env bash
# Validates the Audio Unit inside a built Tapline.app the way hosts load it: registers the app's
# plug-in with the system, runs auval and scripts/check-audio-unit.swift, then unregisters it.
#
#   scripts/validate-audio-unit.sh build/Build/Products/Release/Tapline.app [--test-preset]
#
# --test-preset first saves a preset in Tapline's preferences, so that the check proves the
# sandboxed extension reads them. CI only: it writes to the app's preferences domain.
set -euo pipefail
cd "$(dirname "$0")/.."

app="${1:?usage: $0 <path to Tapline.app> [--test-preset]}"
app="$(cd "$(dirname "$app")" && pwd)/$(basename "$app")"
appex="$app/Contents/PlugIns/TaplineAU.appex"
lsregister=/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister
if [[ ! -d "$appex" ]]; then
  echo "::error::No Audio Unit in $app"
  exit 1
fi

test_preset=""
if [[ "${2:-}" == "--test-preset" ]]; then
  if [[ "${CI:-}" != "true" ]]; then
    echo "--test-preset writes to Tapline's preferences: CI only" >&2
    exit 1
  fi
  test_preset="CI test preset"
  json="$(printf '[{"id":"%s","name":"%s","bands":[{"id":"%s","type":"peak","frequency":1000,"gain":3,"q":1,"isEnabled":true,"channels":"both"}],"preampDB":0,"autoPreamp":true,"tone":{"bass":0,"warmth":0,"clarity":0,"air":0},"isBuiltIn":false}]' \
    "$(uuidgen)" "$test_preset" "$(uuidgen)")"
  defaults write io.github.belugaming.Tapline userPresets -data "$(printf '%s' "$json" | xxd -p | tr -d '\n')"
fi

echo "Entitlements of $(basename "$appex"):"
codesign --display --entitlements - --xml "$appex" | plutil -p -

cleanup() {
  pluginkit -r "$appex" 2>/dev/null || true
  "$lsregister" -u "$app" 2>/dev/null || true
}
trap cleanup EXIT

# Opening Tapline registers it; here LaunchServices and PlugInKit are told directly.
"$lsregister" -f -R -trusted "$app"
pluginkit -a "$appex"
for attempt in $(seq 1 30); do
  if pluginkit -m -i io.github.belugaming.Tapline.AudioUnit | grep -q "io.github.belugaming.Tapline.AudioUnit"; then break; fi
  if [[ "$attempt" -eq 30 ]]; then
    echo "::error::PlugInKit did not register the Audio Unit"
    exit 1
  fi
  sleep 2
done
pluginkit -m -v -i io.github.belugaming.Tapline.AudioUnit

# The component registry follows PlugInKit after a moment: retry while auval cannot find the
# component or its name yet (a validation that fails otherwise is not retried).
log="$(mktemp)"
for attempt in $(seq 1 10); do
  sleep 3
  if auval -v aufx Tpln Tpln > "$log" 2>&1; then
    cat "$log"
    break
  fi
  if { grep -q "AU VALIDATION" "$log" && ! grep -q "Cannot get Component's Name strings" "$log"; } || [[ "$attempt" -eq 10 ]]; then
    cat "$log"
    echo "::error::auval failed"
    exit 1
  fi
done
rm -f "$log"

swift scripts/check-audio-unit.swift ${test_preset:+--expect-preset "$test_preset"}
