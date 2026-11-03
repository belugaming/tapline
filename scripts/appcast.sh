#!/usr/bin/env bash
# Writes the Sparkle appcast for a release: one item for the .dmg, signed with the EdDSA
# key, with that version's CHANGELOG section as the release notes (Markdown).
#
#   SPARKLE_ED_PRIVATE_KEY=... scripts/appcast.sh <sign_update> <dmg> <version> <build> > appcast.xml
#
# The app reads it from https://github.com/belugaming/Tapline/releases/latest/download/appcast.xml,
# which GitHub points at the newest release that is not a pre-release.
set -euo pipefail
cd "$(dirname "$0")/.."

sign_update="${1:?usage: $0 <sign_update> <dmg> <version> <build>}"
dmg="${2:?}"
version="${3:?}"
build="${4:?}"
[[ -n "${SPARKLE_ED_PRIVATE_KEY:-}" ]] || { echo "error: SPARKLE_ED_PRIVATE_KEY is not set" >&2; exit 1; }

# sign_update prints: sparkle:edSignature="..." length="..."
attributes="$(printf '%s' "$SPARKLE_ED_PRIVATE_KEY" | tr -d '[:space:]' | "$sign_update" --ed-key-file - "$dmg")"
[[ "$attributes" == *'sparkle:edSignature="'* && "$attributes" == *'length="'* ]] || { echo "error: sign_update printed: $attributes" >&2; exit 1; }

name="$(basename "$dmg")"
url="https://github.com/belugaming/Tapline/releases/download/v$version/$name"
minimum="$(awk -F'"' '/^ *MACOSX_DEPLOYMENT_TARGET:/ { print $2; exit }' project.yml)"
notes="$(scripts/changelog-section.sh "$version")"
# A CDATA section ends at "]]>"; split it if the notes ever contain one.
notes="${notes//]]>/]]]]><![CDATA[>}"
date="$(LC_ALL=C date -u '+%a, %d %b %Y %H:%M:%S +0000')"

cat <<XML
<?xml version="1.0" encoding="utf-8"?>
<rss version="2.0" xmlns:sparkle="http://www.andymatuschak.org/xml-namespaces/sparkle">
  <channel>
    <title>Tapline</title>
    <link>https://github.com/belugaming/Tapline</link>
    <item>
      <title>Tapline $version</title>
      <pubDate>$date</pubDate>
      <sparkle:version>$build</sparkle:version>
      <sparkle:shortVersionString>$version</sparkle:shortVersionString>
      <sparkle:minimumSystemVersion>$minimum</sparkle:minimumSystemVersion>
      <description sparkle:format="markdown"><![CDATA[$notes]]></description>
      <enclosure url="$url" type="application/x-apple-diskimage" $attributes />
    </item>
  </channel>
</rss>
XML
