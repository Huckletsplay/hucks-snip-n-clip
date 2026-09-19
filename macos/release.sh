#!/usr/bin/env bash
# Builds the public macOS distribution: a universal, hardened, signed, notarized DMG.
#
# A free unsigned-beta mode is available for GitHub distribution with documented Gatekeeper steps.
# The optional production mode uses Developer ID and Apple's notary service when the project is
# ready to pay for a public signing identity.
set -euo pipefail

export COPYFILE_DISABLE=1

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
ARTIFACT_DIR="$PROJECT_ROOT/artifacts/macos/release"
INFO_PLIST="$SCRIPT_DIR/Resources/Info.plist"
ENTITLEMENTS="$SCRIPT_DIR/Resources/HucksSnipNClip.entitlements"
APP_NAME="Huck's Snip 'n' Clip.app"
VOLUME_NAME="Huck's Snip 'n' Clip"
EXECUTABLE_NAME="HucksSnipNClip"
PACKAGE_NAME="HucksSnipNClip"
RELEASE_VERSION="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$INFO_PLIST")"
WORK_ROOT="$(mktemp -d "${TMPDIR:-/private/tmp}/hucks-snip-n-clip-release.XXXXXX")"
BUILD_ROOT="${HUCKS_SNIP_N_CLIP_RELEASE_BUILD_DIR:-$HOME/Library/Caches/HucksSnipNClip/release}"
APP_DIR="$WORK_ROOT/$APP_NAME"
DMG_ROOT="$WORK_ROOT/dmg-root"
MOUNT_ROOT="$WORK_ROOT/mount"
ICONSET_DIR="$WORK_ROOT/AppIcon.iconset"
AD_HOC=no
UNSIGNED_BETA=no
MOUNTED=no

usage() {
    cat <<'EOF'
Usage:
  bash release.sh
      Build, Developer ID-sign, notarize, staple, and verify the public DMG.

  bash release.sh --ad-hoc
      Build and verify a universal local test DMG. It is not distributable.

  bash release.sh --unsigned-beta
      Build a universal GitHub beta DMG without paid Apple signing or notarization.
      Users must approve it through System Settings > Privacy & Security > Open Anyway.

Environment for a public release:
  HUCKS_SNIP_N_CLIP_SIGN_IDENTITY   Full Developer ID Application certificate name
  HUCKS_SNIP_N_CLIP_NOTARY_PROFILE notarytool keychain profile name
EOF
}

cleanup() {
    if [ "$MOUNTED" = yes ]; then
        hdiutil detach "$MOUNT_ROOT" -quiet >/dev/null 2>&1 || true
    fi
    rm -rf "$WORK_ROOT"
}
trap cleanup EXIT

case "${1:-}" in
    "") ;;
    --ad-hoc) AD_HOC=yes ;;
    --unsigned-beta) AD_HOC=yes; UNSIGNED_BETA=yes ;;
    --help|-h) usage; exit 0 ;;
    *) usage >&2; exit 2 ;;
esac

case "$RELEASE_VERSION" in
    *[!0-9A-Za-z.-]*|'')
        echo "The bundle version is not safe for a release filename: $RELEASE_VERSION" >&2
        exit 1
        ;;
esac

for tool in swift lipo iconutil codesign hdiutil xcrun shasum ditto security spctl; do
    if ! command -v "$tool" >/dev/null 2>&1; then
        echo "Required release tool is missing: $tool" >&2
        exit 1
    fi
done

if [ "$AD_HOC" = yes ]; then
    SIGN_IDENTITY="-"
    NOTARY_PROFILE=""
    if [ "$UNSIGNED_BETA" = yes ]; then
        DMG_BASENAME="$PACKAGE_NAME-$RELEASE_VERSION-macOS-universal-unsigned-beta.dmg"
        echo "Building an unsigned GitHub beta. Users will need macOS Open Anyway approval."
    else
        DMG_BASENAME="$PACKAGE_NAME-$RELEASE_VERSION-macOS-universal-UNNOTARIZED.dmg"
        echo "Building an ad-hoc local validation package. Do not distribute this output."
    fi
else
    SIGN_IDENTITY="${HUCKS_SNIP_N_CLIP_SIGN_IDENTITY:-}"
    NOTARY_PROFILE="${HUCKS_SNIP_N_CLIP_NOTARY_PROFILE:-}"
    DMG_BASENAME="$PACKAGE_NAME-$RELEASE_VERSION-macOS-universal.dmg"

    if [ -z "$SIGN_IDENTITY" ]; then
        echo "Set HUCKS_SNIP_N_CLIP_SIGN_IDENTITY to the full Developer ID Application certificate name." >&2
        exit 1
    fi
    AVAILABLE_IDENTITIES="$(security find-identity -v -p codesigning 2>/dev/null || true)"
    MATCHING_IDENTITY="$(printf '%s\n' "$AVAILABLE_IDENTITIES" \
        | grep -F "Developer ID Application" \
        | grep -F "$SIGN_IDENTITY" || true)"
    if [ -z "$MATCHING_IDENTITY" ]; then
        echo "A matching Developer ID Application identity was not found in the keychain:" >&2
        echo "  $SIGN_IDENTITY" >&2
        exit 1
    fi
    if [ -z "$NOTARY_PROFILE" ]; then
        echo "Set HUCKS_SNIP_N_CLIP_NOTARY_PROFILE to a notarytool keychain profile name." >&2
        exit 1
    fi
fi

echo "Building arm64 release..."
mkdir -p "$BUILD_ROOT"
swift build \
    --package-path "$SCRIPT_DIR" \
    --configuration release \
    --scratch-path "$BUILD_ROOT/swiftpm-arm64" \
    --triple arm64-apple-macosx14.0
ARM_BIN="$(swift build --package-path "$SCRIPT_DIR" --configuration release --scratch-path "$BUILD_ROOT/swiftpm-arm64" --triple arm64-apple-macosx14.0 --show-bin-path)/$EXECUTABLE_NAME"

echo "Building x86_64 release..."
swift build \
    --package-path "$SCRIPT_DIR" \
    --configuration release \
    --scratch-path "$BUILD_ROOT/swiftpm-x86_64" \
    --triple x86_64-apple-macosx14.0
INTEL_BIN="$(swift build --package-path "$SCRIPT_DIR" --configuration release --scratch-path "$BUILD_ROOT/swiftpm-x86_64" --triple x86_64-apple-macosx14.0 --show-bin-path)/$EXECUTABLE_NAME"

if [ ! -f "$ARM_BIN" ] || [ ! -f "$INTEL_BIN" ]; then
    echo "One or both architecture builds did not produce $EXECUTABLE_NAME." >&2
    exit 1
fi

echo "Assembling universal app..."
mkdir -p "$APP_DIR/Contents/MacOS" "$APP_DIR/Contents/Resources"
lipo -create "$ARM_BIN" "$INTEL_BIN" -output "$APP_DIR/Contents/MacOS/$EXECUTABLE_NAME"
cp "$INFO_PLIST" "$APP_DIR/Contents/Info.plist"
swift "$SCRIPT_DIR/Resources/generate-app-icon.swift" "$ICONSET_DIR"
iconutil --convert icns --output "$APP_DIR/Contents/Resources/AppIcon.icns" "$ICONSET_DIR"
printf 'APPL????' > "$APP_DIR/Contents/PkgInfo"
chmod +x "$APP_DIR/Contents/MacOS/$EXECUTABLE_NAME"
xattr -cr "$APP_DIR" 2>/dev/null || true

ARCHITECTURES="$(lipo -archs "$APP_DIR/Contents/MacOS/$EXECUTABLE_NAME")"
if [ "$ARCHITECTURES" != "x86_64 arm64" ] && [ "$ARCHITECTURES" != "arm64 x86_64" ]; then
    echo "Expected a universal x86_64 + arm64 executable, found: $ARCHITECTURES" >&2
    exit 1
fi

echo "Signing the app with Hardened Runtime..."
if [ "$AD_HOC" = yes ]; then
    codesign --force --sign - --options runtime --timestamp=none \
        --entitlements "$ENTITLEMENTS" "$APP_DIR"
else
    codesign --force --sign "$SIGN_IDENTITY" --options runtime --timestamp \
        --entitlements "$ENTITLEMENTS" "$APP_DIR"
fi
codesign --verify --deep --strict --verbose=2 "$APP_DIR"

SIGNATURE_DETAILS="$(codesign -d --verbose=4 "$APP_DIR" 2>&1)"
if [[ "$SIGNATURE_DETAILS" != *"runtime"* ]]; then
    echo "The app signature does not have Hardened Runtime enabled." >&2
    exit 1
fi
ENTITLEMENT_DETAILS="$(codesign -d --entitlements - "$APP_DIR" 2>/dev/null)"
if [[ "$ENTITLEMENT_DETAILS" != *"com.apple.security.device.audio-input"* ]]; then
    echo "The signed app is missing its audio-input entitlement." >&2
    exit 1
fi

echo "Creating the drag-to-Applications DMG..."
mkdir -p "$DMG_ROOT"
ditto "$APP_DIR" "$DMG_ROOT/$APP_NAME"
ln -s /Applications "$DMG_ROOT/Applications"
STAGED_DMG="$WORK_ROOT/$DMG_BASENAME"
hdiutil create -quiet -ov -format UDZO -volname "$VOLUME_NAME" -srcfolder "$DMG_ROOT" "$STAGED_DMG"

if [ "$AD_HOC" = no ]; then
    echo "Signing the DMG..."
    codesign --force --sign "$SIGN_IDENTITY" --timestamp "$STAGED_DMG"
    codesign --verify --verbose=2 "$STAGED_DMG"

    echo "Submitting to Apple's notary service..."
    xcrun notarytool submit "$STAGED_DMG" --keychain-profile "$NOTARY_PROFILE" --wait

    echo "Stapling the notarization ticket..."
    xcrun stapler staple "$STAGED_DMG"
    xcrun stapler validate "$STAGED_DMG"
    spctl --assess --type open --context context:primary-signature --verbose=4 "$STAGED_DMG"
fi

hdiutil verify "$STAGED_DMG" >/dev/null

echo "Verifying the mounted package..."
mkdir -p "$MOUNT_ROOT"
hdiutil attach -quiet -readonly -nobrowse -mountpoint "$MOUNT_ROOT" "$STAGED_DMG"
MOUNTED=yes
MOUNTED_APP="$MOUNT_ROOT/$APP_NAME"
codesign --verify --deep --strict --verbose=2 "$MOUNTED_APP"
MOUNTED_ARCHITECTURES="$(lipo -archs "$MOUNTED_APP/Contents/MacOS/$EXECUTABLE_NAME")"
if [ "$MOUNTED_ARCHITECTURES" != "$ARCHITECTURES" ]; then
    echo "The mounted DMG does not contain the verified universal executable." >&2
    exit 1
fi
if [ "$AD_HOC" = no ]; then
    spctl --assess --type execute --verbose=4 "$MOUNTED_APP"
fi
hdiutil detach "$MOUNT_ROOT" -quiet
MOUNTED=no

mkdir -p "$ARTIFACT_DIR"
FINAL_DMG="$ARTIFACT_DIR/$DMG_BASENAME"
FINAL_HASH="$FINAL_DMG.sha256.txt"
cp "$STAGED_DMG" "$FINAL_DMG"
(cd "$ARTIFACT_DIR" && shasum -a 256 "$DMG_BASENAME" > "$DMG_BASENAME.sha256.txt")
hdiutil verify "$FINAL_DMG" >/dev/null

echo
if [ "$UNSIGNED_BETA" = yes ]; then
    echo "Unsigned GitHub beta validation passed. Publish it only with the Gatekeeper instructions:"
elif [ "$AD_HOC" = yes ]; then
    echo "Local package validation passed. This DMG is UNNOTARIZED and must not be distributed:"
else
    echo "Public release validation passed:"
fi
echo "  $FINAL_DMG"
echo "  $FINAL_HASH"
echo "  Architectures: $ARCHITECTURES"
