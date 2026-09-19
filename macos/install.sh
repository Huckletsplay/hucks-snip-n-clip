#!/usr/bin/env bash
# Installs Huck's Snip 'n' Clip into ~/Applications, away from the source tree.
set -euo pipefail

export COPYFILE_DISABLE=1

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
SOURCE_APP="$PROJECT_ROOT/artifacts/macos/Huck's Snip 'n' Clip.app"
TARGET_DIR="$HOME/Applications"
TARGET_APP="$TARGET_DIR/Huck's Snip 'n' Clip.app"
STAGED_APP="$TARGET_DIR/.HucksSnipNClip.installing.app"
BACKUP_APP="$TARGET_DIR/.HucksSnipNClip.previous.app"
NEW_EXECUTABLE="$TARGET_APP/Contents/MacOS/HucksSnipNClip"
LEGACY_PUBLIC_APP="$TARGET_DIR/Q's Snip 'n' Clip.app"
LEGACY_INTERNAL_APP="$TARGET_DIR/QSnipAndClip.app"
NEW_BUNDLE_ID="com.projectplayground.huckssnipnclip"

stop_exact_executable() {
    local executable="$1"
    local pid
    local command
    local attempt

    while IFS= read -r pid; do
        [ -n "$pid" ] || continue
        command="$(ps -p "$pid" -o command= 2>/dev/null || true)"
        if [ "$command" = "$executable" ]; then
            kill "$pid" 2>/dev/null || true
        fi
    done < <(pgrep -f "$executable" 2>/dev/null || true)

    for attempt in {1..25}; do
        if ! exact_executable_is_running "$executable"; then
            return 0
        fi
        sleep 0.2
    done

    echo "The installed app did not quit: $executable" >&2
    return 1
}

exact_executable_is_running() {
    local executable="$1"
    local pid
    local command

    while IFS= read -r pid; do
        [ -n "$pid" ] || continue
        command="$(ps -p "$pid" -o command= 2>/dev/null || true)"
        if [ "$command" = "$executable" ]; then
            return 0
        fi
    done < <(pgrep -f "$executable" 2>/dev/null || true)

    return 1
}

if [ ! -d "$SOURCE_APP" ]; then
    echo "No Huck's build found. Run: bash \"$SCRIPT_DIR/build.sh\"" >&2
    exit 1
fi

mkdir -p "$TARGET_DIR"

# Prepare and verify the complete replacement before touching the working installation.
rm -rf "$STAGED_APP" "$BACKUP_APP"
cp -R "$SOURCE_APP" "$STAGED_APP"
chmod +x "$STAGED_APP/Contents/MacOS/HucksSnipNClip"
find "$STAGED_APP" -name '._*' -delete 2>/dev/null || true
xattr -cr "$STAGED_APP" 2>/dev/null || true

SIGN_IDENTITY="${HUCKS_SNIP_N_CLIP_SIGN_IDENTITY:-Huck's Snip 'n' Clip Local Signing}"
SIGNATURE_DETAILS="$(codesign -d --verbose=4 "$STAGED_APP" 2>&1 || true)"
if [[ "$SIGNATURE_DETAILS" == *"Authority=$SIGN_IDENTITY"* ]]; then
    SIGNED_STABLY=yes
else
    SIGNED_STABLY=no
fi
codesign --verify --deep --strict "$STAGED_APP"

stop_exact_executable "$NEW_EXECUTABLE"
stop_exact_executable "$LEGACY_PUBLIC_APP/Contents/MacOS/QSnipAndClip"
stop_exact_executable "$LEGACY_INTERNAL_APP/Contents/MacOS/QSnipAndClip"

if [ -d "$TARGET_APP" ]; then
    mv "$TARGET_APP" "$BACKUP_APP"
fi

if ! mv "$STAGED_APP" "$TARGET_APP"; then
    if [ -d "$BACKUP_APP" ]; then
        mv "$BACKUP_APP" "$TARGET_APP"
    fi
    echo "Installation failed; the previous app was restored." >&2
    exit 1
fi

if ! codesign --verify --deep --strict "$TARGET_APP"; then
    rm -rf "$TARGET_APP"
    if [ -d "$BACKUP_APP" ]; then
        mv "$BACKUP_APP" "$TARGET_APP"
    fi
    echo "The installed copy failed signature verification; the previous app was restored." >&2
    exit 1
fi

rm -rf "$BACKUP_APP"

# Retire only the exact former app bundles. Legacy settings and recovered captures are left for
# the user under ~/Library/Application Support/QSNC; this identity migration never deletes them.
if [ -d "$LEGACY_PUBLIC_APP" ]; then
    rm -rf "$LEGACY_PUBLIC_APP"
fi
if [ -d "$LEGACY_INTERNAL_APP" ]; then
    rm -rf "$LEGACY_INTERNAL_APP"
fi

if [ "$SIGNED_STABLY" = "no" ] && command -v tccutil >/dev/null 2>&1; then
    tccutil reset ScreenCapture "$NEW_BUNDLE_ID" >/dev/null 2>&1 || true
fi

echo "Installed $TARGET_APP"
echo "Settings: $HOME/Library/Application Support/HucksSnipNClip/settings.ini"
echo "The retired QSNC settings folder was left untouched."

if [ "$SIGNED_STABLY" = "yes" ]; then
    echo "Signed as \"$SIGN_IDENTITY\". This new Huck's bundle identity needs Screen Recording"
    echo "permission once; later rebuilds will keep that grant."
else
    echo "Unsigned build: macOS will need Screen Recording permission after rebuilds."
    echo "Run 'bash setup-signing.sh' once to keep future grants stable."
fi

echo "Opening Huck's Snip 'n' Clip..."
open "$TARGET_APP"
