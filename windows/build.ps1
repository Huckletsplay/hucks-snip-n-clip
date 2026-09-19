[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$compilerCandidates = @(
    'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe',
    'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
)

$compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compiler) {
    throw 'The Windows .NET Framework C# compiler was not found.'
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$artifactDirectory = Join-Path $projectRoot 'artifacts\windows'
if (-not (Test-Path -LiteralPath $artifactDirectory)) {
    New-Item -ItemType Directory -Path $artifactDirectory | Out-Null
}

$outputPath = Join-Path $artifactDirectory 'HucksSnipNClip.exe'
$manifestPath = Join-Path $PSScriptRoot 'src\app.manifest'
$iconTool = Join-Path $artifactDirectory 'BuildIcon.exe'
$iconPath = Join-Path $artifactDirectory 'HucksSnipNClip.ico'
& $compiler /nologo /target:exe /reference:System.Drawing.dll ('/out:' + $iconTool) (Join-Path $PSScriptRoot 'tools\BuildIcon.cs') (Join-Path $PSScriptRoot 'src\TrayIconFactory.cs')
if ($LASTEXITCODE -ne 0) { throw 'Icon tool build failed.' }
& $iconTool $iconPath
if ($LASTEXITCODE -ne 0) { throw 'Icon generation failed.' }
$sourceFiles = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' |
    Sort-Object Name |
    ForEach-Object { $_.FullName }

$compilerArguments = @(
    '/nologo',
    '/target:winexe',
    '/optimize+',
    '/debug:pdbonly',
    '/platform:anycpu',
    '/reference:System.dll',
    '/reference:System.Core.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Runtime.Serialization.dll',
    '/reference:System.Windows.Forms.dll',
    ('/win32manifest:' + $manifestPath),
    ('/win32icon:' + $iconPath),
    ('/out:' + $outputPath)
)

& $compiler @compilerArguments @sourceFiles
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE."
}

$builtFile = Get-Item -LiteralPath $outputPath
Write-Host ("Built {0} ({1:N0} bytes)" -f $builtFile.FullName, $builtFile.Length)
