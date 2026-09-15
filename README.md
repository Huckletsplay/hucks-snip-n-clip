# Huck's Snip 'n' Clip

A lightweight capture utility for **Windows** and **macOS**. Capture a region, a window, or a whole
screen — as a still image or a video clip — and send it straight to the folder you want it in.

It lives in the Windows system tray and the macOS menu bar. No main window. No sign-in. No uploads.

**[Download the latest release →](https://github.com/Huckletsplay/hucks-snip-n-clip/releases/latest)**

Both platforms are public betas.

---

## What it does

**Capture**

- Snip a region, a specific window, or the display under your pointer, saved as PNG.
- Clip a region, a window, or the screen to H.264 video after a 3–2–1 countdown.
- Pause and resume mid-recording — the paused time is left out of the finished file.
- Freeze the desktop while you drag out a region, so your target can't move underneath you.
- While a region records, the rest of the desktop dims so you can see exactly what's being captured.
  The dimming never appears in the finished video.

**Audio**

- Record with no audio, computer/system audio, a microphone, or **both as two separate tracks in one
  file**, so you can adjust or mute either one in an editor.
- Independent gain for each source, from muted to 300%.
- Choose which microphone to use.

**Quality**

- Frame rate: 15, 30, 60, or match your display's refresh rate.
- Resolution ceiling: Native, 1440p, 1080p, or 720p. It never enlarges a smaller source.
- Quality: Original, Balanced, or High.
- Cursor capture is off by default and can be turned on.

**Where files go**

- Save to named output folders you set up once and pick from the menu.
- Assign a shortcut to each output, so you choose the destination before you capture.
- Name your own files with editable labels and templates, a running counter, and live previews.
- If a destination folder goes missing, the capture lands in a local Recovery folder instead of
  being lost. Nothing is ever silently overwritten.

**Shortcuts**

- Every capture action has an editable global shortcut.
- Single-press shortcuts, or two-step sequences.
- Rebind anything from the menu without restarting the app.

**In the tray / menu bar**

- A grayscale **H**. While recording, audio level fills its left side and CPU usage fills its right,
  turning red when the machine is working too hard.
- Optionally start automatically when you sign in.

### Differences between the two versions

| | Windows | macOS |
|---|---|---|
| Video container | `.mp4` | `.mov` |
| Two-source audio | two separate tracks, in a fixed order: track 1 computer, track 2 microphone | two separate **named** tracks |
| Default shortcuts | `Ctrl+Alt+Shift+` S / W / F / C / V / R | `Control-Option-Shift-` S / W / F / C / V / R |
| Permissions | none needed | Screen Recording must be enabled by hand |

Windows cannot store a track *title* in an MP4, so the two audio tracks are identified by their
order rather than by name.

---

## Install — Windows

Requires **Windows 10 version 2004 or newer, 64-bit**. Nothing else; the .NET runtime it uses is
already part of Windows.

**Tested on Windows 10 22H2 only.** Windows 11 has not been tested yet.

1. Download the `.zip` from the
   [latest release](https://github.com/Huckletsplay/hucks-snip-n-clip/releases/latest).
2. Extract the whole ZIP to a folder.
3. Right-click **Install.ps1** and choose **Run with PowerShell**.
   If nothing happens, open PowerShell in that folder and run:
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1
   ```
4. The app starts immediately. Look for the **H** icon in your system tray, near the clock, and
   right-click it for every action and setting.

It installs to `%LOCALAPPDATA%\Programs\QSNC` under your own account. No administrator rights are
needed and nothing is written outside your user profile.

To remove it: **Start Menu → Project Playground → Uninstall**, or **Settings → Apps**. Your settings
and saved captures are never deleted by the uninstaller.

### Why Windows warns you

This build isn't code-signed, because a Windows signing certificate is a paid yearly product.
SmartScreen will say the publisher is unknown. To run it anyway, click **More info → Run anyway**.

If that bothers you, don't run it — or check the download against the published checksum first.

---

## Install — macOS

Requires **macOS 14 or newer**. The universal build contains native Apple Silicon and Intel code,
but this beta has only been tested on Apple Silicon so far. Intel compatibility is not yet
confirmed.

**Confirmed test environment:** macOS Tahoe 26.1 (build 25B78) on Apple Silicon. Earlier supported
macOS releases and Intel Macs have not yet received hands-on testing.

1. Download the `.dmg` from the
   [latest release](https://github.com/Huckletsplay/hucks-snip-n-clip/releases/latest).
2. Open it and drag **Huck's Snip 'n' Clip** into **Applications**.
3. Open the app once. macOS will refuse to open it — this is expected, see below.
4. Go to **System Settings → Privacy & Security**, scroll down to the Security section, and
   choose **Open Anyway**.
5. Open the app again. It now stays open.
6. Turn it on under **Privacy & Security → Screen & System Audio Recording**.
7. Allow microphone access only if you plan to record a microphone.

### Why macOS blocks it the first time

This beta isn't notarized through Apple's paid Developer Program, so macOS treats it as unidentified
software and asks you to approve it by hand once. That's the only consequence — the app itself is
unchanged by it. Notarized builds are planned for a later release.

---

## Verifying your download

Each download has a matching `.sha256` / `.sha256.txt` file in the release. To confirm yours:

```powershell
# Windows
Get-FileHash .\HucksSnipNClip-0.1.5-windows-x64.zip -Algorithm SHA256
```

```bash
# macOS
shasum -a 256 ~/Downloads/HucksSnipNClip-*.dmg
```

Compare the result against the published checksum.

---

## Privacy

**Everything stays on your own machine.**

Huck's Snip 'n' Clip does not upload your screenshots, recordings, audio, filenames, folder
locations, or any usage information — not to me, not to anyone. The app contains no networking code
and no analytics.

Captures are written only to the output folder you chose. Your preferences, shortcuts, filename
counters, and saved destinations are stored locally under your own account. Removing the app does
not delete your captures or your destination folders.

On macOS, Screen Recording and microphone access are controlled by the system. The app uses them
only to perform a Snip or Clip that you started, and microphone access only for recording modes that
include one. You can revoke either permission in System Settings at any time.

If a future release ever adds networking, automatic updates, crash reporting, or analytics, this
statement will be updated before that release ships.

---

## Support

Found a bug, or something behaves unexpectedly?
**[Open an issue](https://github.com/Huckletsplay/hucks-snip-n-clip/issues)** — please include your
operating system and version.

---

## Status

**Windows** — public beta, available above.
**macOS** — public beta, available above.

This repository is the download and support page for Huck's Snip 'n' Clip. The application source is
not published here.
