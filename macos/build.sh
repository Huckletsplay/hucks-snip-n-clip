#!/usr/bin/env bash
# Builds Huck's Snip 'n' Clip for macOS into the project-root artifacts folder.
#
# The Swift build directory deliberately lives off this drive: Swift Package Manager creates
# symlinks inside it, and the hub's exFAT volume cannot store a symlink at all.
set -euo pipefail

# exFAT has no native extended attributes, so macOS writes them to "._" sidecar files. One of
# those inside the bundle makes codesign fail outright, so copying never carries them here.
export COPYFILE_DISABLE=1

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
ARTIFACT_DIR="$PROJECT_ROOT/artifacts/macos"
APP_DIR="$ARTIFACT_DIR/Huck's Snip 'n' Clip.app"
LEGACY_APP_DIR="$ARTIFACT_DIR/QSnipAndClip.app"
SCRATCH_DIR="${HUCKS_SNIP_N_CLIP_BUILD_DIR:-$HOME/Library/Caches/HucksSnipNClip/swiftpm}"
CONFIGURATION="${1:-release}"
ICONSET_DIR="$SCRATCH_DIR/AppIcon.iconset"
STAGING_ROOT="$(mktemp -d "${TMPDIR:-/private/tmp}/hucks-snip-n-clip-sign.XXXXXX")"
STAGING_APP_DIR="$STAGING_ROOT/Huck's Snip 'n' Clip.app"
trap 'rm -rf "$STAGING_ROOT"' EXIT

if ! command -v swift >/dev/null 2>&1; then
    echo "The Swift toolchain was not found. Install the Xcode Command Line Tools with: xcode-select --install" >&2
    exit 1
fi

echo "Building ($CONFIGURATION)..."
mkdir -p "$SCRATCH_DIR"
swift build \
    --package-path "$SCRIPT_DIR" \
    --configuration "$CONFIGURATION" \
    --scratch-path "$SCRATCH_DIR"

BINARY="$(swift build --package-path "$SCRIPT_DIR" --configuration "$CONFIGURATION" --scratch-path "$SCRATCH_DIR" --show-bin-path)/HucksSnipNClip"
if [ ! -f "$BINARY" ]; then
    echo "The build reported success but $BINARY is missing." >&2
    exit 1
fi

echo "Generating the H app icon..."
swift "$SCRIPT_DIR/Resources/generate-app-icon.swift" "$ICONSET_DIR"
iconutil --convert icns --output "$SCRATCH_DIR/AppIcon.icns" "$ICONSET_DIR"

echo "Assembling $STAGING_APP_DIR..."
mkdir -p "$STAGING_APP_DIR/Contents/MacOS" "$STAGING_APP_DIR/Contents/Resources"
cp "$BINARY" "$STAGING_APP_DIR/Contents/MacOS/HucksSnipNClip"
cp "$SCRIPT_DIR/Resources/Info.plist" "$STAGING_APP_DIR/Contents/Info.plist"
cp "$SCRATCH_DIR/AppIcon.icns" "$STAGING_APP_DIR/Contents/Resources/AppIcon.icns"
printf 'APPL????' > "$STAGING_APP_DIR/Contents/PkgInfo"
chmod +x "$STAGING_APP_DIR/Contents/MacOS/HucksSnipNClip"

find "$STAGING_APP_DIR" -name '._*' -delete 2>/dev/null || true
xattr -cr "$STAGING_APP_DIR" 2>/dev/null || true
find "$STAGING_APP_DIR" -name '._*' -delete 2>/dev/null || true

# Sign with the local identity when this Mac has one. That gives the app a stable identity, so a
# granted Screen Recording permission survives a rebuild. Without it the signature is a fingerprint
# of the exact binary, every build looks like different software, and the permission has to be
# granted all over again. setup-signing.sh creates the identity, once per Mac.
SIGN_IDENTITY="${HUCKS_SNIP_N_CLIP_SIGN_IDENTITY:-Huck's Snip 'n' Clip Local Signing}"

if security find-identity -v -p codesigning 2>/dev/null | grep -qF "$SIGN_IDENTITY"; then
    echo "Signing as \"$SIGN_IDENTITY\"..."
    codesign --force --sign "$SIGN_IDENTITY" --timestamp=none "$STAGING_APP_DIR" >/dev/null
else
    echo "Signing (unsigned form - no local identity on this Mac)..."
    codesign --force --sign - --timestamp=none "$STAGING_APP_DIR" >/dev/null
    echo
    echo "Tip: run 'bash setup-signing.sh' once to stop macOS asking for Screen Recording"
    echo "     permission again after every rebuild."
fi

codesign --verify --deep --strict "$STAGING_APP_DIR"

# codesign's atomic replacement creates AppleDouble `._*` files when it runs directly on exFAT,
# which can leave a stale `.cstemp` executable and an invalid resource envelope. Sign on the Mac's
# native filesystem first, then copy the complete verified bundle back to the portable drive.
echo "Copying the signed app to $APP_DIR..."
mkdir -p "$ARTIFACT_DIR"
rm -rf "$APP_DIR"
cp -R "$STAGING_APP_DIR" "$APP_DIR"
find "$APP_DIR" -name '._*' -delete 2>/dev/null || true
codesign --verify --deep --strict "$APP_DIR"

if [ -d "$LEGACY_APP_DIR" ]; then
    rm -rf "$LEGACY_APP_DIR"
fi

echo "Built $APP_DIR"
