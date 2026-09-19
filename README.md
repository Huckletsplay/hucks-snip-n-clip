# Huck's Snip 'n' Clip

Capture what matters, put it where it belongs, and keep moving.

Huck's Snip 'n' Clip is a lightweight capture utility for Windows and macOS. It lives in the system
tray or menu bar instead of taking over your desktop. Use a shortcut to grab a region, window, or
screen; the result goes straight to the output folder you chose.

No account. No cloud upload. No permanent main window.

## Download

Open the [latest release](https://github.com/Huckletsplay/hucks-snip-n-clip/releases/latest) and
choose the file for your computer:

| Platform | Download | Requirement |
|---|---|---|
| Windows | `HucksSnipNClip-0.1.5-windows-x64-setup.exe` | Windows 10 version 2004 or newer, 64-bit |
| macOS | `HucksSnipNClip-0.1.5-macOS-universal-unsigned-beta.dmg` | macOS 14 or newer |

This is an unsigned public beta. Windows SmartScreen or macOS Gatekeeper will identify the publisher
as unknown. Installation instructions are below, and every download includes a SHA-256 checksum.

## Why use it?

- **Capture without breaking focus.** Global shortcuts handle Region, Window, and Screen Snips or
  Clips without opening a conventional app.
- **Skip the file-moving chore.** Named Outputs send captures directly to a project folder, shared
  folder, or any other location you choose.
- **Paste immediately.** Every Snip is copied as an image and every finished Clip is copied as a
  file, so `Ctrl+V` on Windows or `Command-V` on macOS can put it directly into apps that support
  that content.
- **Keep recording controls close.** Use recording-only shortcuts or the optional floating H to
  pause, resume, and stop even when the Windows taskbar or macOS menu bar is hidden. Move, resize, fade, or disable
  the controller to fit your workspace.
- **Record usable audio.** Choose computer/system audio, microphone, or both as separate editable
  tracks in one video.
- **Choose practical quality.** Control frame rate, resolution ceiling, quality, cursor visibility,
  microphone, audio gains, shortcuts, and filenames.
- **Keep captures private.** Captures stay on your computer unless you choose to share them.

## Install on Windows

1. Download `HucksSnipNClip-0.1.5-windows-x64-setup.exe` from the latest release.
2. Open it. If SmartScreen appears, choose **More info**, then **Run anyway**.
3. Follow the installer. A desktop shortcut is optional.
4. Look for the **H** near the clock. Right-click it for capture actions and settings.

The app installs for your Windows account and does not require administrator access. Remove it from
**Settings > Apps > Installed apps** or its Start-menu folder. Uninstalling leaves your captures and
personal settings intact.

To update, open the tray menu and choose **Settings > Check for Updates**. The app downloads the
installer from this GitHub release page and verifies its published SHA-256 checksum before asking to
run it.

## Install on macOS

1. Download and open the DMG.
2. Drag **Huck's Snip 'n' Clip** into Applications.
3. Try to open it once. macOS will block this unsigned beta.
4. Open **System Settings > Privacy & Security**, scroll to Security, and choose **Open Anyway**.
5. Open the app again and enable it under **Screen & System Audio Recording**.
6. Allow microphone access only if you want to record a microphone.

A fresh install saves to **Pictures > Huck's Snip 'n' Clip** until you choose another folder from
**Output** in the menu bar.

**Updates.** Choose **Settings > Check for Updates…** in the menu-bar H. The app asks GitHub
only when you choose it, downloads a newer Mac DMG with its published SHA-256 checksum, verifies
them, and asks before opening the DMG. Install it the usual way: quit the app, drag the new copy onto
Applications and replace the old one, then reopen it. It is still an unsigned beta, so macOS may ask
for **Open Anyway** again, and Screen & System Audio Recording may need switching on again for the
new copy. Your settings, Outputs, shortcuts and captures are kept.

If your Mac copy has no **Check for Updates…** item under **Settings**, download this DMG once by
hand - it is the first Mac build with it. From then on the app can find new releases itself.

## A simple workflow

1. Add an Output for the folder where the work belongs.
2. Assign that Output a shortcut if you switch destinations often.
3. Trigger Region, Window, or Screen from its shortcut.
4. Keep working—the capture is already named, saved, and routed.

On Windows, you can also paste the latest capture immediately. Snips paste as images; Clips paste as
video files in applications that accept pasted files.

## Capture options

- PNG Snips: Region, active Window, or full Screen
- H.264 Clips: Region, Window, or Screen after a `3–2–1` countdown
- Pause, resume, and fixed stop controls
- No audio, computer/system audio, microphone, or both as separate tracks
- Named Outputs and Output shortcuts
- Editable one- or two-step capture shortcuts
- Custom filename labels and templates with collision-safe saving
- Recovery storage when no configured Output is reachable
- Recording frame rate, quality, resolution, cursor, microphone, and gain controls

Windows Clips use MP4. macOS Clips with both audio sources use MOV. Windows stores Computer as audio
track 1 and Microphone as track 2; its MP4 writer does not embed track titles.

## Privacy

Huck's Snip 'n' Clip does not require an account, upload captures, or collect analytics. On both
Windows and macOS it contacts GitHub only when you explicitly choose **Check for Updates**.

## Verify a download

Each installer or DMG has a checksum file beside it on the release page.

Windows:

```powershell
Get-FileHash .\HucksSnipNClip-0.1.5-windows-x64-setup.exe -Algorithm SHA256
```

Expected Windows SHA-256:

```text
e1af1c222f34c1385d8a8ba0b54e8185036e446c83154e288b333de7d18d9b42
```

macOS:

```bash
shasum -a 256 HucksSnipNClip-0.1.5-macOS-universal-unsigned-beta.dmg
```

Expected macOS SHA-256:

```text
87f5f6550612ea7914ea57dd5e6bc52b4e6b805d4a874e12627f91760ef4fbdf
```

## Support

Found a bug or have an idea? Open a
[GitHub issue](https://github.com/Huckletsplay/hucks-snip-n-clip/issues) with your operating system,
what you expected, and what happened.
