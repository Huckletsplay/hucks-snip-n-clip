<#
    Runs the complete Windows suite, including the real SendInput/WH_KEYBOARD_LL integration test.
    This must be launched deliberately from a normal PowerShell window on the active Windows
    desktop. The deterministic suite remains available through test.ps1 for automation.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
public static class HucksInteractiveTestDesktop {
    [DllImport("kernel32.dll")]
    public static extern uint WTSGetActiveConsoleSessionId();
}
'@

$currentSession = [Diagnostics.Process]::GetCurrentProcess().SessionId
$consoleSession = [HucksInteractiveTestDesktop]::WTSGetActiveConsoleSessionId()
if (-not [Environment]::UserInteractive -or $currentSession -ne $consoleSession) {
    Write-Host 'SKIPPED: TestShortcutCaptureChordWindow requires the active interactive Windows desktop.'
    Write-Host ('Current session: {0}; active console session: {1}; UserInteractive: {2}' -f `
        $currentSession, $consoleSession, [Environment]::UserInteractive)
    Write-Host 'Run windows\test-interactive.ps1 from a normal desktop PowerShell window before release.'
    exit 2
}

$runningApps = @(Get-Process HucksSnipNClip,QSnipAndClip -ErrorAction SilentlyContinue)
$restartPaths = @($runningApps | Where-Object Path | Select-Object -ExpandProperty Path -Unique)
if ($runningApps.Count -gt 0) {
    Write-Host ('Temporarily stopping installed app process(es): ' + `
        (($runningApps | ForEach-Object { $_.ProcessName + ':' + $_.Id }) -join ', '))
    $runningApps | Stop-Process -Force
    $runningApps | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}

$exitCode = 1
try {
    & (Join-Path $PSScriptRoot 'test.ps1') -IncludeInteractiveInput
    $exitCode = $LASTEXITCODE
    if ($null -eq $exitCode) { $exitCode = 0 }
}
catch {
    Write-Error $_
    $exitCode = 1
}
finally {
    foreach ($path in $restartPaths) {
        if (Test-Path -LiteralPath $path) {
            $startInfo = New-Object Diagnostics.ProcessStartInfo
            $startInfo.FileName = $path
            $startInfo.UseShellExecute = $true
            [void][Diagnostics.Process]::Start($startInfo)
            Write-Host ('Restarted installed app: ' + $path)
        }
    }
}

exit $exitCode
