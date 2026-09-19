<#
    Builds the public Windows download: a standalone ZIP plus its SHA-256.

    This is NOT build.ps1 with a different output path. A release build must carry nothing about
    the machine or drive it was built on, so it compiles with debug information switched off -
    /debug:pdbonly embeds the absolute .pdb path, which leaks the private project layout - and
    then verifies that no such string survived before anything is packaged.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$compilerCandidates = @(
    'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe',
    'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
)
$compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compiler) { throw 'The Windows .NET Framework C# compiler was not found.' }

# Nothing ships without the suite passing. Among other things it pins that a fresh install saves
# into the user's own Pictures folder and never hunts the filesystem for a development folder.
Write-Host 'Running the Windows suite before packaging...'
& (Join-Path $PSScriptRoot 'test.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Tests failed; the release was NOT packaged.' }

$version = '0.1.5'
$projectRoot = Split-Path -Parent $PSScriptRoot
$artifactDirectory = Join-Path $projectRoot 'artifacts\windows'
$releaseRoot = Join-Path $artifactDirectory 'release'
$stage = Join-Path $releaseRoot ('HucksSnipNClip-' + $version + '-windows-x64')

if (Test-Path -LiteralPath $releaseRoot) { Remove-Item -LiteralPath $releaseRoot -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

# --- icon -------------------------------------------------------------------------------------
$iconTool = Join-Path $releaseRoot 'BuildIcon.exe'
$iconPath = Join-Path $releaseRoot 'HucksSnipNClip.ico'
& $compiler /nologo /target:exe /reference:System.Drawing.dll ('/out:' + $iconTool) `
    (Join-Path $PSScriptRoot 'tools\BuildIcon.cs') (Join-Path $PSScriptRoot 'src\TrayIconFactory.cs')
if ($LASTEXITCODE -ne 0) { throw 'Icon tool build failed.' }
& $iconTool $iconPath
if ($LASTEXITCODE -ne 0) { throw 'Icon generation failed.' }

# --- application ------------------------------------------------------------------------------
$releasedExe = Join-Path $stage 'HucksSnipNClip.exe'
$sourceFiles = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' |
    Sort-Object Name | ForEach-Object { $_.FullName }

$compilerArguments = @(
    '/nologo', '/target:winexe', '/optimize+', '/debug-', '/platform:anycpu',
    '/reference:System.dll', '/reference:System.Core.dll',
    '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll',
    '/reference:System.Runtime.Serialization.dll',
    ('/win32manifest:' + (Join-Path $PSScriptRoot 'src\app.manifest')),
    ('/win32icon:' + $iconPath),
    ('/out:' + $releasedExe)
)
& $compiler @compilerArguments @sourceFiles
if ($LASTEXITCODE -ne 0) { throw "Release build failed with exit code $LASTEXITCODE." }

# --- privacy audit ----------------------------------------------------------------------------
# Nothing about this machine, this drive, or its owner may reach a public download.
$text = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($releasedExe))
$forbidden = @{
    'a build path'       = '[A-Za-z]:\\[^\x00]{0,160}\.(pdb|cs)\b'
    'a source path'      = [regex]::Escape($projectRoot)
    'a user profile'     = '(?i)[A-Za-z]:\\Users\\'
    'an email address'   = '[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}'
}
$leaks = @()
foreach ($name in $forbidden.Keys) {
    $match = [regex]::Match($text, $forbidden[$name])
    if ($match.Success) { $leaks += ("  {0}: {1}" -f $name, $match.Value) }
}
if ($leaks.Count -gt 0) {
    throw ("The release build leaks private information and was NOT packaged:`n" + ($leaks -join "`n"))
}
Write-Host 'Privacy audit passed: no build path, drive, profile or email in the binary.'

# --- installer --------------------------------------------------------------------------------
$innoCandidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 6\ISCC.exe'
)
$innoCompiler = $innoCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $innoCompiler) {
    throw 'Inno Setup 6 was not found. Install JRSoftware.InnoSetup before building a release.'
}

$installerScript = Join-Path $PSScriptRoot 'installer\HucksSnipNClip.iss'
& $innoCompiler ('/DAppVersion=' + $version) ('/DSourceExe=' + $releasedExe) `
    ('/DOutputDir=' + $releaseRoot) ('/DSetupIcon=' + $iconPath) $installerScript
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }

$installerPath = Join-Path $releaseRoot ('HucksSnipNClip-' + $version + '-windows-x64-setup.exe')
if (-not (Test-Path -LiteralPath $installerPath)) { throw 'The installer compiler produced no setup executable.' }
$hash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLower()
$checksumPath = $installerPath + '.sha256'
[IO.File]::WriteAllText($checksumPath, $hash + '  ' + (Split-Path -Leaf $installerPath) + "`n")

Remove-Item -LiteralPath $iconTool -Force -ErrorAction SilentlyContinue

Write-Host ''
Write-Host ('Installer: ' + $installerPath)
Write-Host ('Size:      {0:N0} bytes' -f (Get-Item -LiteralPath $installerPath).Length)
Write-Host ('SHA-256:  ' + $hash)
Write-Host ('Checksum:  ' + $checksumPath)
