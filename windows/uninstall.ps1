[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$productName = "Huck's Snip 'n' Clip"
$displayName = 'Huck' + [char]0x2019 + 's Snip ' + [char]0x2019 + 'n' + [char]0x2019 + ' Clip'
$installRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\HucksSnipNClip'))
$legacyInstallRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\QSNC'))
$installedExe = [IO.Path]::GetFullPath((Join-Path $installRoot 'HucksSnipNClip.exe'))
$allowedInstallParent = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs'))
if (-not $installRoot.StartsWith($allowedInstallParent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The installation directory escaped the current-user Programs folder.'
}

$runningCopies = @(Get-Process -Name HucksSnipNClip -ErrorAction SilentlyContinue | Where-Object {
    $_.Path -and ([IO.Path]::GetFullPath($_.Path) -ieq $installedExe)
})
foreach ($process in $runningCopies) {
    Stop-Process -Id $process.Id -Force
}

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
foreach ($runValue in @('HucksSnipNClip', 'QSnipAndClip')) {
    Remove-ItemProperty -LiteralPath $runKey -Name $runValue -ErrorAction SilentlyContinue
}
foreach ($key in @('HucksSnipNClip', 'QSNC')) {
    Remove-Item -LiteralPath ('HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $key) `
        -Recurse -Force -ErrorAction SilentlyContinue
}

$startMenuRoot = Join-Path ([Environment]::GetFolderPath('Programs')) $displayName
$shortcutPaths = @(
    (Join-Path $startMenuRoot ($displayName + '.lnk')),
    (Join-Path $startMenuRoot ('Uninstall ' + $displayName + '.lnk')),
    (Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) ($displayName + '.lnk')),
    (Join-Path $startMenuRoot ($productName + '.lnk')),
    (Join-Path $startMenuRoot ('Uninstall ' + $productName + '.lnk')),
    (Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) ($productName + '.lnk')),
    (Join-Path $startMenuRoot "Q's Snip 'n' Clip.lnk"),
    (Join-Path $startMenuRoot "Uninstall Q's Snip 'n' Clip.lnk"),
    (Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) "Q's Snip 'n' Clip.lnk")
)
foreach ($shortcutPath in $shortcutPaths) {
    Remove-Item -LiteralPath $shortcutPath -Force -ErrorAction SilentlyContinue
}

Set-Location -LiteralPath $env:TEMP
foreach ($root in @($installRoot, $legacyInstallRoot)) {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}

Write-Host ($displayName + ' was removed. Personal settings and captures were preserved.')
