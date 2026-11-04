
#!/usr/bin/env bash
# Builds the Vidy Jellyfin plugin and creates:
# dist/vidy_<version>.zip
# dist/vidy_<version>.zip.md5
# dist/manifest.json

set -euo pipefail

# --------------------------------------------------
# Configuration
# --------------------------------------------------

VERSION="${PLUGIN_VERSION:-0.1.0.0}"
TARGET_ABI="${TARGET_ABI:-12.4.0.0}"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

# Remove Windows line endings and accidental newlines.
VERSION="$(printf '%s' "$VERSION" | tr -d '\r\n')"
TARGET_ABI="$(printf '%s' "$TARGET_ABI" | tr -d '\r\n')"

# --------------------------------------------------
# Debug information
# --------------------------------------------------

echo "========================================"
echo "Vidy Plugin Build"
echo "========================================"

echo "Repository: $ROOT"
echo "PLUGIN_VERSION: '${PLUGIN_VERSION-<unset>}'"
echo "VERSION: '$VERSION'"
echo "TARGET_ABI: '$TARGET_ABI'"

printf 'VERSION bytes: '
printf '%s' "$VERSION" | od -An -tx1

echo "========================================"

# --------------------------------------------------
# Validate version
# --------------------------------------------------

if [[ ! "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "ERROR: Invalid plugin version: '$VERSION'" >&2
    echo "Expected four numeric components (example: 0.1.0.0)." >&2
    exit 1
fi

if [[ ! "$TARGET_ABI" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "ERROR: Invalid target ABI: '$TARGET_ABI'" >&2
    echo "Expected four numeric components (example: 12.4.0.0)." >&2
    exit 1
fi

# --------------------------------------------------
# Check build dependencies
# --------------------------------------------------

if ! command -v dotnet >/dev/null 2>&1; then
    echo "ERROR: .NET SDK is not installed." >&2
    exit 1
fi

if ! command -v zip >/dev/null 2>&1; then
    echo "ERROR: zip is not installed." >&2
    exit 1
fi

if ! command -v md5sum >/dev/null 2>&1; then
    echo "ERROR: md5sum is not installed." >&2
    exit 1
fi

# --------------------------------------------------
# Clean previous build
# --------------------------------------------------

echo ""
echo "Cleaning previous build..."

rm -rf out dist
mkdir -p dist/pkg

# --------------------------------------------------
# Build plugin
# --------------------------------------------------

echo ""
echo "Building Jellyfin plugin..."

dotnet publish \
    src/Jellyfin.Plugin.Vidy \
    -c Release \
    -o out

# --------------------------------------------------
# Verify build output
# --------------------------------------------------

echo ""
echo "Checking build output..."

REQUIRED_FILES=(
    "Jellyfin.Plugin.Vidy.dll"
    "Jellyfin.Plugin.Vidy.Core.dll"
)

for FILE in "${REQUIRED_FILES[@]}"; do
    if [[ ! -f "out/$FILE" ]]; then
        echo "ERROR: Required build file missing: out/$FILE" >&2
        echo "Available build files:"
        find out -maxdepth 2 -type f -print || true
        exit 1
    fi

    echo "Found: $FILE"
done

# --------------------------------------------------
# Prepare plugin package
# --------------------------------------------------

echo ""
echo "Preparing plugin package..."

cp out/Jellyfin.Plugin.Vidy.dll dist/pkg/
cp out/Jellyfin.Plugin.Vidy.Core.dll dist/pkg/

TIMESTAMP="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

# --------------------------------------------------
# Generate plugin metadata
# --------------------------------------------------

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

# --------------------------------------------------
# Create ZIP package
# --------------------------------------------------

echo ""
echo "Creating plugin ZIP..."

ZIP="dist/vidy_${VERSION}.zip"

(
    cd dist/pkg
    zip -qr "../vidy_${VERSION}.zip" .
)

if [[ ! -s "$ZIP" ]]; then
    echo "ERROR: ZIP package was not created correctly." >&2
    exit 1
fi

# --------------------------------------------------
# Generate MD5 checksum
# --------------------------------------------------

echo ""
echo "Generating MD5 checksum..."

MD5="$(md5sum "$ZIP" | cut -d' ' -f1)"

echo "$MD5  vidy_${VERSION}.zip" \
    > "dist/vidy_${VERSION}.zip.md5"

# --------------------------------------------------
# Generate Jellyfin repository manifest
# --------------------------------------------------

echo ""
echo "Generating repository manifest..."

SOURCE_URL="${SOURCE_URL:-https://github.com/silentwander-afk/jellyfin-vidy/releases/download/v${VERSION}/vidy_${VERSION}.zip}"

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

# --------------------------------------------------
# Clean temporary package directory
# --------------------------------------------------

rm -rf dist/pkg

# --------------------------------------------------
# Final verification
# --------------------------------------------------

echo ""
echo "========================================"
echo "BUILD COMPLETED SUCCESSFULLY"
echo "========================================"

echo "Version: $VERSION"
echo "Target ABI: $TARGET_ABI"
echo "Timestamp: $TIMESTAMP"
echo ""
echo "Generated files:"

ls -lh \
    "$ZIP" \
    "dist/vidy_${VERSION}.zip.md5" \
    "dist/manifest.json"

echo ""
echo "MD5: $MD5"
echo "Source URL: $SOURCE_URL"
echo ""
echo "========================================"
