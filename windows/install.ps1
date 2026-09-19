[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$productName = "Huck's Snip 'n' Clip"
$displayName = 'Huck' + [char]0x2019 + 's Snip ' + [char]0x2019 + 'n' + [char]0x2019 + ' Clip'
$projectRoot = Split-Path -Parent $PSScriptRoot
$sourceExe = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts\windows\HucksSnipNClip.exe'))
$installRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\HucksSnipNClip'))
$installedExe = [IO.Path]::GetFullPath((Join-Path $installRoot 'HucksSnipNClip.exe'))
$installedUninstaller = [IO.Path]::GetFullPath((Join-Path $installRoot 'uninstall.ps1'))
$programsRoot = [Environment]::GetFolderPath('Programs')
$startMenuRoot = [IO.Path]::GetFullPath((Join-Path $programsRoot $displayName))
$startMenuShortcut = Join-Path $startMenuRoot ($displayName + '.lnk')
$uninstallShortcut = Join-Path $startMenuRoot ('Uninstall ' + $displayName + '.lnk')
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) ($displayName + '.lnk')
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runValueName = 'HucksSnipNClip'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\HucksSnipNClip'
$legacyUninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\QSNC'
$legacyInstallRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\QSNC'))
# Get-ItemPropertyValue throws a terminating PSArgumentException when the value is simply absent,
# which -ErrorAction cannot suppress. Start with Windows being off is the normal case, not an error.
$existingStartup = $null
try { $existingStartup = Get-ItemPropertyValue -LiteralPath $runKey -Name $runValueName -ErrorAction Stop }
catch { $existingStartup = $null }

$allowedInstallParent = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs'))
if (-not $installRoot.StartsWith($allowedInstallParent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The installation directory escaped the current-user Programs folder.'
}

$runningCopies = @(Get-Process -Name HucksSnipNClip -ErrorAction SilentlyContinue | Where-Object {
    $_.Path -and (
        ([IO.Path]::GetFullPath($_.Path) -ieq $sourceExe) -or
        ([IO.Path]::GetFullPath($_.Path) -ieq $installedExe))
})
foreach ($process in $runningCopies) {
    Stop-Process -Id $process.Id -Force
}

& (Join-Path $PSScriptRoot 'build.ps1')
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $sourceExe)) {
    throw 'The production executable could not be built.'
}

New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
Copy-Item -LiteralPath $sourceExe -Destination $installedExe -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'uninstall.ps1') -Destination $installedUninstaller -Force
New-Item -ItemType Directory -Path $startMenuRoot -Force | Out-Null

$shell = New-Object -ComObject WScript.Shell
function Set-Shortcut([string] $path, [string] $target, [string] $arguments, [string] $workingDirectory) {
    $shortcut = $shell.CreateShortcut($path)
    $shortcut.TargetPath = $target
    $shortcut.Arguments = $arguments
    $shortcut.WorkingDirectory = $workingDirectory
    $shortcut.IconLocation = $installedExe + ',0'
    $shortcut.Description = $displayName
    $shortcut.Save()
}

$legacyShortcutPaths = @(
    (Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Q Snip & Clip.lnk'),
    (Join-Path $startMenuRoot 'Q Snip & Clip.lnk'),
    (Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) "Q's Snip 'n' Clip.lnk"),
    (Join-Path $startMenuRoot "Q's Snip 'n' Clip.lnk"),
    (Join-Path $startMenuRoot "Uninstall Q's Snip 'n' Clip.lnk"),
    (Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) ($productName + '.lnk')),
    (Join-Path $startMenuRoot ($productName + '.lnk')),
    (Join-Path $startMenuRoot ('Uninstall ' + $productName + '.lnk'))
)
foreach ($legacyShortcutPath in $legacyShortcutPaths) {
    if (Test-Path -LiteralPath $legacyShortcutPath) {
        $legacyShortcut = $shell.CreateShortcut($legacyShortcutPath)
        $legacyArtifactSuffix = [IO.Path]::Combine('Projects', 'Q-clip', 'artifacts', 'QSnipAndClip.exe')
        $targetsInstalledCopy = -not [String]::IsNullOrWhiteSpace($legacyShortcut.TargetPath) -and
            ([IO.Path]::GetFullPath($legacyShortcut.TargetPath) -ieq $installedExe)
        $targetsInstalledUninstaller = -not [String]::IsNullOrWhiteSpace($legacyShortcut.Arguments) -and
            $legacyShortcut.Arguments.Contains($installedUninstaller)
        if (-not [String]::IsNullOrWhiteSpace($legacyShortcut.TargetPath)) {
            $legacyTarget = [IO.Path]::GetFullPath($legacyShortcut.TargetPath)
            if ($targetsInstalledCopy -or $targetsInstalledUninstaller -or
                $legacyTarget.EndsWith($legacyArtifactSuffix, [StringComparison]::OrdinalIgnoreCase)) {
                Remove-Item -LiteralPath $legacyShortcutPath -Force
            }
        }
    }
}

Set-Shortcut $startMenuShortcut $installedExe '' $installRoot
Set-Shortcut $desktopShortcut $installedExe '' $installRoot
$powerShellExe = Join-Path $PSHOME 'powershell.exe'
Set-Shortcut $uninstallShortcut $powerShellExe ('-NoProfile -ExecutionPolicy Bypass -File "' + $installedUninstaller + '"') $installRoot

if (-not [String]::IsNullOrWhiteSpace($existingStartup)) {
    Set-ItemProperty -LiteralPath $runKey -Name $runValueName -Value ('"' + $installedExe + '"')
}

New-Item -Path $uninstallKey -Force | Out-Null
Set-ItemProperty -LiteralPath $uninstallKey -Name 'DisplayName' -Value $displayName
Set-ItemProperty -LiteralPath $uninstallKey -Name 'DisplayIcon' -Value $installedExe
Set-ItemProperty -LiteralPath $uninstallKey -Name 'DisplayVersion' -Value '0.1.5'
Set-ItemProperty -LiteralPath $uninstallKey -Name 'Publisher' -Value 'Quintin Huckaby'
Set-ItemProperty -LiteralPath $uninstallKey -Name 'InstallLocation' -Value $installRoot
Set-ItemProperty -LiteralPath $uninstallKey -Name 'UninstallString' -Value ('"' + $powerShellExe + '" -NoProfile -ExecutionPolicy Bypass -File "' + $installedUninstaller + '"')
Set-ItemProperty -LiteralPath $uninstallKey -Name 'NoModify' -Value 1 -Type DWord
Set-ItemProperty -LiteralPath $uninstallKey -Name 'NoRepair' -Value 1 -Type DWord

# Clear out the retired QSNC install so a dev machine never ends up with two tray copies.
Remove-Item -LiteralPath $legacyUninstallKey -Recurse -Force -ErrorAction SilentlyContinue
Remove-ItemProperty -LiteralPath $runKey -Name 'QSnipAndClip' -ErrorAction SilentlyContinue
if (Test-Path -LiteralPath $legacyInstallRoot) {
    Remove-Item -LiteralPath $legacyInstallRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Start-Process -FilePath $installedExe -WorkingDirectory $installRoot
Write-Host ('Installed ' + $displayName + ' at:')
Write-Host $installedExe
Write-Host 'The E: project is no longer required to run the installed app.'
