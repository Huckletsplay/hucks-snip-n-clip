# macOS development

The macOS implementation is a Swift package using AppKit, ScreenCaptureKit, and AVFoundation.

```bash
bash test.sh
bash build.sh
bash install.sh
```

`build.sh` assembles `Huck's Snip 'n' Clip.app` under `../artifacts/macos/`. SwiftPM scratch data
stays in `~/Library/Caches/HucksSnipNClip` so the repository can also live on filesystems that do not
support symlinks.

Run `bash setup-signing.sh` once for a stable local signing identity. For public distribution, use
`bash release.sh --unsigned-beta` or configure a Developer ID identity and notary profile before
running `bash release.sh`.

The resulting DMG and checksum are written under `../artifacts/macos/release/`.
