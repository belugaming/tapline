#!/usr/bin/env bash
# Signs Tapline.app with Tapline's self-signed certificate, so macOS keeps the audio and
# microphone permissions across updates: an ad-hoc signature is a new identity with every
# build, a certificate stays the same. Not notarization: Gatekeeper still asks once.
# The Audio Unit extension inside is signed first, with its sandbox entitlements.
#
#   scripts/sign.sh build/Build/Products/Release/Tapline.app
#
# Reads the certificate from MACOS_CERT_P12 (the .p12, base64) and MACOS_CERT_PASSWORD,
# the repository's Actions secrets. Without them (a fork, a local build) the app stays
# ad-hoc signed.
set -euo pipefail

app="${1:?usage: $0 <path to Tapline.app>}"
if [[ -z "${MACOS_CERT_P12:-}" || -z "${MACOS_CERT_PASSWORD:-}" ]]; then
  echo "No signing certificate: $app stays ad-hoc signed."
  exit 0
fi

work="$(mktemp -d)"
keychain="$work/signing.keychain-db"
keychain_password="$(uuidgen)"
cleanup() {
  security delete-keychain "$keychain" 2>/dev/null || true
  rm -rf "$work"
}
trap cleanup EXIT

# Secrets pasted from a file or from base64 output keep its line breaks; a password with
# a newline at the end fails the import with "MAC verification failed".
cert_password="${MACOS_CERT_PASSWORD//[$'\r\n']/}"
printf '%s' "$MACOS_CERT_P12" | tr -d '[:space:]' | base64 --decode > "$work/cert.p12"
security create-keychain -p "$keychain_password" "$keychain"
security set-keychain-settings -lut 3600 "$keychain"
security unlock-keychain -p "$keychain_password" "$keychain"
security import "$work/cert.p12" -k "$keychain" -P "$cert_password" -T /usr/bin/codesign
security set-key-partition-list -S apple-tool:,apple: -s -k "$keychain_password" "$keychain" >/dev/null
# codesign looks up the identity in the search list.
security list-keychains -d user -s "$keychain" $(security list-keychains -d user | tr -d '"')

# The keychain holds this one identity. It is self-signed, so not trusted: list it
# without -v, which would leave it out.
identity="$(security find-identity -p codesigning "$keychain" | awk '/^ *[0-9]+\)/ { print $2; exit }')"
if [[ -z "$identity" ]]; then
  echo "::error::No code signing identity in MACOS_CERT_P12."
  exit 1
fi

# Embedded extensions first (the Audio Unit, Contents/PlugIns/TaplineAU.appex), each with the
# entitlements it was built with: an Audio Unit extension without its sandbox does not load.
for appex in "$app"/Contents/PlugIns/*.appex; do
  [[ -d "$appex" ]] || continue
  entitlements="$work/$(basename "$appex").entitlements"
  codesign --display --entitlements - --xml "$appex" > "$entitlements"
  if ! grep -q "com.apple.security.app-sandbox" "$entitlements"; then
    echo "::error::$(basename "$appex") is not sandboxed: build it with its entitlements."
    exit 1
  fi
  # A debugging allowance has no place in a release.
  /usr/libexec/PlistBuddy -c "Delete :com.apple.security.get-task-allow" "$entitlements" >/dev/null 2>&1 || true
  codesign --force --sign "$identity" --keychain "$keychain" --timestamp=none --entitlements "$entitlements" "$appex"
  codesign --verify --strict --verbose=2 "$appex"
  codesign --display --entitlements - "$appex"
done

codesign --force --sign "$identity" --keychain "$keychain" --timestamp=none "$app"
codesign --verify --strict --deep --verbose=2 "$app"
# The designated requirement names the certificate; it is what macOS remembers permissions by.
codesign --display --requirements - "$app"
