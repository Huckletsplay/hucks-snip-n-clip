#!/usr/bin/env bash
# Builds and runs the headless core tests: settings, shortcuts, and the save/recovery path.
#
# Xcode is not required to build this app, and XCTest is not available without it, so the tests
# are compiled the same way the Windows build compiles its own - a named list of core sources
# plus the test file, built into one executable that reports and exits.
set -euo pipefail

export COPYFILE_DISABLE=1

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
OUTPUT_DIR="$PROJECT_ROOT/artifacts/macos/tests"
OUTPUT="$OUTPUT_DIR/HucksSnipNClipCoreTests"
LEGACY_OUTPUT="$OUTPUT_DIR/QSnipAndClipCoreTests"

SOURCES=(
    "$SCRIPT_DIR/Sources/HucksSnipNClip/AppSettings.swift"
    "$SCRIPT_DIR/Sources/HucksSnipNClip/CaptureFilenameTemplate.swift"
    "$SCRIPT_DIR/Sources/HucksSnipNClip/ShortcutBinding.swift"
    "$SCRIPT_DIR/Sources/HucksSnipNClip/SettingsStore.swift"
    "$SCRIPT_DIR/Sources/HucksSnipNClip/CaptureSaveService.swift"
    "$SCRIPT_DIR/Sources/HucksSnipNClip/ScreenCaptureService.swift"
    "$SCRIPT_DIR/Sources/HucksSnipNClip/RegionSelectionOverlay.swift"
    "$SCRIPT_DIR/Sources/HucksSnipNClip/ScreenClipRecorder.swift"
    "$SCRIPT_DIR/Sources/HucksSnipNClip/RegionRecordingShade.swift"
    "$SCRIPT_DIR/Sources/HucksSnipNClip/TrayIconFactory.swift"
    "$SCRIPT_DIR/Sources/HucksSnipNClip/MeterSupport.swift"
    "$SCRIPT_DIR/Sources/HucksSnipNClip/ScreenFrameAdmission.swift"
    "$SCRIPT_DIR/Sources/HucksSnipNClip/FrameDelivery.swift"
    "$SCRIPT_DIR/Sources/HucksSnipNClip/LaunchAtLoginService.swift"
    "$SCRIPT_DIR/Sources/HucksSnipNClip/MicrophoneDeviceService.swift"
    "$SCRIPT_DIR/Sources/HucksSnipNClip/PersistentMenuChoiceView.swift"
    "$SCRIPT_DIR/Sources/HucksSnipNClip/ShortcutCaptureOverlay.swift"
    "$SCRIPT_DIR/Sources/HucksSnipNClip/OutputShortcutCaptureOverlay.swift"
    "$SCRIPT_DIR/Sources/HucksSnipNClip/RecordingControls.swift"
    "$SCRIPT_DIR/Sources/HucksSnipNClip/FloatingRecordingController.swift"
    "$SCRIPT_DIR/Sources/HucksSnipNClip/CapturePasteboard.swift"
    "$SCRIPT_DIR/Sources/HucksSnipNClip/UpdateChecker.swift"
    "$SCRIPT_DIR/Sources/HucksSnipNClip/CopyableAlert.swift"
    "$SCRIPT_DIR/Tests/CoreTests.swift"
)

mkdir -p "$OUTPUT_DIR"

echo "Building tests..."
swiftc -O -parse-as-library -o "$OUTPUT" "${SOURCES[@]}"

echo "Running tests..."
"$OUTPUT"

if [ -f "$LEGACY_OUTPUT" ]; then
    rm -f "$LEGACY_OUTPUT"
fi
