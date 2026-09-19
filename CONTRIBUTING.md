# Contributing

Thanks for helping improve Huck's Snip 'n' Clip.

## Before opening a change

1. Search existing issues and pull requests.
2. Keep Windows work in `windows/` and macOS work in `macos/` unless the change is genuinely shared.
3. Avoid unrelated refactors in the same pull request.
4. Never include captures, settings, credentials, signing material, personal paths, or generated
   artifacts.

## Validate your change

Windows:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\windows\test.ps1
```

macOS:

```bash
bash macos/test.sh
```

For capture, audio, shortcut, installer, or updater changes, describe the hands-on checks you ran
and the operating-system version used. A passing unit suite does not replace runtime validation of
screen capture or recording behavior.

## Pull requests

Explain the user-visible problem, the approach taken, tests performed, and any platform-specific
limitations. Keep public discussion focused on the software; do not include private conversations,
development journals, or unrelated project material.
