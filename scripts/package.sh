#!/usr/bin/env bash
# Builds the plugin and produces dist/vidy_<version>.zip (+ md5 and a repository manifest.json).
set -euo pipefail

VERSION="0.1.0.0"
TARGET_ABI="12.4.0.0"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

rm -rf out dist
dotnet publish src/Jellyfin.Plugin.Vidy -c Release -o out

mkdir -p dist/pkg
cp out/Jellyfin.Plugin.Vidy.dll out/Jellyfin.Plugin.Vidy.Core.dll dist/pkg/

TIMESTAMP="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
cat > dist/pkg/meta.json <<EOF
{
  "category": "General",
  "guid": "b7c2a4e1-5d3f-4a68-9c1e-2f8d6a0b3e47",
  "name": "Vidy",
  "description": "Search TMDB from Jellyfin and watch movies and TV through the Vidy embed player.",
  "overview": "TMDB search, Vidy playback, per-user progress and Continue Watching for Jellyfin Web.",
  "owner": "vidy-plugin",
  "targetAbi": "${TARGET_ABI}",
  "version": "${VERSION}",
  "timestamp": "${TIMESTAMP}"
}
EOF

ZIP="dist/vidy_${VERSION}.zip"
(cd dist/pkg && zip -qr "../vidy_${VERSION}.zip" .)
MD5="$(md5sum "$ZIP" | cut -d' ' -f1)"
echo "$MD5  vidy_${VERSION}.zip" > "dist/vidy_${VERSION}.zip.md5"

# Repository manifest. sourceUrl must point at wherever you host the ZIP (for example a GitHub Release asset).
# Until you do, the manifest is NOT usable for installation from Jellyfin's plugin catalogue.
SOURCE_URL="${SOURCE_URL:-REPLACE_WITH_PUBLIC_URL_OF_THE_ZIP}"
cat > dist/manifest.json <<EOF
[
  {
    "guid": "b7c2a4e1-5d3f-4a68-9c1e-2f8d6a0b3e47",
    "name": "Vidy",
    "description": "Search TMDB from Jellyfin and watch movies and TV through the Vidy embed player.",
    "overview": "TMDB search, Vidy playback, per-user progress and Continue Watching for Jellyfin Web.",
    "owner": "vidy-plugin",
    "category": "General",
    "versions": [
      {
        "version": "${VERSION}",
        "changelog": "Initial release.",
        "targetAbi": "${TARGET_ABI}",
        "sourceUrl": "${SOURCE_URL}",
        "checksum": "${MD5}",
        "timestamp": "${TIMESTAMP}"
      }
    ]
  }
]
EOF

rm -rf dist/pkg
echo "Built $ZIP (md5 $MD5)"
