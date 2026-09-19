[CmdletBinding()]
param(
    [switch]$IncludeInteractiveInput
)

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
$testOutputDirectory = Join-Path $projectRoot 'artifacts\windows\tests'
if (-not (Test-Path -LiteralPath $testOutputDirectory)) {
    New-Item -ItemType Directory -Path $testOutputDirectory | Out-Null
}

$testOutputPath = Join-Path $testOutputDirectory 'HucksSnipNClip.CoreTests.exe'
$testSources = @(
    (Join-Path $PSScriptRoot 'src\AssemblyInfo.cs'),
    (Join-Path $PSScriptRoot 'src\CopyableDialog.cs'),
    (Join-Path $PSScriptRoot 'src\CaptureActionMenuRow.cs'),
    (Join-Path $PSScriptRoot 'src\PersistentTrayMenu.cs'),
    (Join-Path $PSScriptRoot 'src\TrayIconFactory.cs'),
    (Join-Path $PSScriptRoot 'src\StartupManager.cs'),
    (Join-Path $PSScriptRoot 'src\ShortcutBinding.cs'),
    (Join-Path $PSScriptRoot 'src\KeyboardShortcutCaptureSession.cs'),
    (Join-Path $PSScriptRoot 'src\ShortcutSettingsForm.cs'),
    (Join-Path $PSScriptRoot 'src\RecordingControlShortcuts.cs'),
    (Join-Path $PSScriptRoot 'src\RecordingControllerLayout.cs'),
    (Join-Path $PSScriptRoot 'src\FloatingRecordingController.cs'),
    (Join-Path $PSScriptRoot 'src\AppSettings.cs'),
    (Join-Path $PSScriptRoot 'src\SettingsStore.cs'),
    (Join-Path $PSScriptRoot 'src\CaptureSaveService.cs'),
    (Join-Path $PSScriptRoot 'src\CaptureClipboard.cs'),
    (Join-Path $PSScriptRoot 'src\CaptureFilename.cs'),
    (Join-Path $PSScriptRoot 'src\MediaFoundationVideoWriter.cs'),
    (Join-Path $PSScriptRoot 'src\PcmAudioMixer.cs'),
    (Join-Path $PSScriptRoot 'src\WasapiLoopbackCapture.cs'),
    (Join-Path $PSScriptRoot 'src\MicrophoneDevices.cs'),
    (Join-Path $PSScriptRoot 'src\ClipCursor.cs'),
    (Join-Path $PSScriptRoot 'src\RecordingResolution.cs'),
    (Join-Path $PSScriptRoot 'src\AppPaths.cs'),
    (Join-Path $PSScriptRoot 'src\RegionRecordingShade.cs'),
    (Join-Path $PSScriptRoot 'src\RegionSelectionForm.cs'),
    (Join-Path $PSScriptRoot 'src\UpdateChecker.cs'),
    (Join-Path $PSScriptRoot 'src\NativeMethods.cs'),
    (Join-Path $PSScriptRoot 'src\ScreenCaptureService.cs'),
    (Join-Path $PSScriptRoot 'src\ScreenClipRecorder.cs'),
    (Join-Path $PSScriptRoot 'tests\CoreTests.cs')
)

$compilerArguments = @(
    '/nologo',
    '/target:exe',
    '/optimize+',
    '/reference:System.dll',
    '/reference:System.Core.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Runtime.Serialization.dll',
    '/reference:System.Windows.Forms.dll',
    ('/out:' + $testOutputPath)
)

& $compiler @compilerArguments @testSources
if ($LASTEXITCODE -ne 0) {
    throw "Test build failed with exit code $LASTEXITCODE."
}

$testArguments = @()
if ($IncludeInteractiveInput) {
    $testArguments += '--include-interactive-input'
}

& $testOutputPath @testArguments
if ($LASTEXITCODE -ne 0) {
    throw "Tests failed with exit code $LASTEXITCODE."
}
