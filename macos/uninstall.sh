#!/usr/bin/env bash
# Removes Huck's Snip 'n' Clip. New settings are retained unless --purge is passed. The former
# QSNC support folder is deliberately never deleted here.
set -euo pipefail

TARGET_APP="$HOME/Applications/Huck's Snip 'n' Clip.app"
LEGACY_PUBLIC_APP="$HOME/Applications/Q's Snip 'n' Clip.app"
LEGACY_INTERNAL_APP="$HOME/Applications/QSnipAndClip.app"
SUPPORT_DIR="$HOME/Library/Application Support/HucksSnipNClip"

stop_exact_executable() {
    local executable="$1"
    local pid
    local command

    while IFS= read -r pid; do
        [ -n "$pid" ] || continue
        command="$(ps -p "$pid" -o command= 2>/dev/null || true)"
        if [ "$command" = "$executable" ]; then
            kill "$pid" 2>/dev/null || true
        fi
    done < <(pgrep -f "$executable" 2>/dev/null || true)
}

stop_exact_executable "$TARGET_APP/Contents/MacOS/HucksSnipNClip"
stop_exact_executable "$LEGACY_PUBLIC_APP/Contents/MacOS/QSnipAndClip"
stop_exact_executable "$LEGACY_INTERNAL_APP/Contents/MacOS/QSnipAndClip"

for app in "$TARGET_APP" "$LEGACY_PUBLIC_APP" "$LEGACY_INTERNAL_APP"; do
    if [ -d "$app" ]; then
        rm -rf "$app"
        echo "Removed $app"
    fi
done

if [ "${1:-}" = "--purge" ] && [ -d "$SUPPORT_DIR" ]; then
    rm -rf "$SUPPORT_DIR"
    echo "Removed $SUPPORT_DIR"
fi

echo "Former QSNC settings/recovery files were left untouched."
