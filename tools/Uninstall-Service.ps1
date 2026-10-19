<#
.SYNOPSIS
    Removes the Windows service StreamZH. Needs administrator rights.

.DESCRIPTION
    Stops and deletes the service, its firewall rule (from Install-Service.ps1 -Firewall) and its event log source.
    Closes the tray icon and removes its "Start with Windows" entry for your user (Update-Local.ps1 and Install-Tray.ps1
    set it up again for StreamZH without a service).
    The program files and your data in C:\ProgramData\StreamZH stay, so StreamZH can be started without a service
    (Install-Tray.ps1, or StreamZH.exe in a console window) or installed as a service again (Install-Service.ps1).
    The Windows installer runs this script when StreamZH is uninstalled or switched to "tray icon only".

.PARAMETER InstallDir
    Folder with StreamZH.exe (only for the hint how to start it again). Empty: the folder above this tools folder when
    StreamZH.exe is there, else C:\Tools\StreamZH.

.PARAMETER NonInteractive
    Never asks (for the installer and scripts). The script asks nothing today; the switch keeps the command lines of
    the installer stable.

.EXAMPLE
    .\Uninstall-Service.ps1
#>
[CmdletBinding()]
param(
    [string]$InstallDir = '',
    [switch]$NonInteractive
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ServiceHelpers.ps1')

function Step([string]$text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

Assert-Administrator $PSCommandPath (Format-StreamZhArguments $PSBoundParameters)

$InstallDir = Resolve-StreamZhInstallDir $InstallDir
$service = Get-StreamZhService
$dataDir = Get-StreamZhServiceDataDir

Step 'Tray icon'
if (Stop-StreamZhTray) { Write-Host 'closed' } else { Write-Host 'not running' }
if (Unregister-StreamZhTray) { Write-Host 'start with Windows: removed' }
# Written by the service account; StreamZH without a service writes its own.
Remove-StreamZhTrayFiles $dataDir

if (-not $service) {
    Write-Host 'The StreamZH service is not installed.' -ForegroundColor Yellow
}
else {
    Step 'Stop service StreamZH'
    if (Stop-StreamZhService) { Write-Host 'stopped' } else { Write-Host 'not running' }

    Step 'Remove service StreamZH'
    $output = & sc.exe delete $StreamZhServiceName 2>&1
    if ($LASTEXITCODE -ne 0) { throw "sc.exe delete failed ($LASTEXITCODE): $output" }
    Write-Host 'removed'
}

if (Remove-StreamZhFirewallRule) { Write-Host 'firewall rule removed' }

if ([Diagnostics.EventLog]::SourceExists($StreamZhServiceName)) {
    [Diagnostics.EventLog]::DeleteEventSource($StreamZhServiceName)
    Write-Host 'event log source removed'
}

# StreamZH without a service runs as you: the data folder stays private (owned by Administrators) and you may change
# it. A link or a file there is left alone.
$data = $dataDir
if (-not $data) { $data = Join-Path $env:ProgramData 'StreamZH' }
$dataItem = Get-Item -LiteralPath $data -Force -ErrorAction SilentlyContinue
if ($dataItem -and $dataItem.PSIsContainer -and ($dataItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
    $me = [Security.Principal.WindowsIdentity]::GetCurrent()
    Protect-StreamZhDataFolder $data @() @($me.User.Value) $true
    Write-Host "$($me.Name): may change $data (nobody else but SYSTEM and administrators)"
}
elseif ($dataItem) {
    Write-Host "$data is not a folder (a link or a file); its permissions were not changed." -ForegroundColor Yellow
}

Write-Host "`nDone. Program and data are still there." -ForegroundColor Green
# Not started from here: this PowerShell runs as administrator, and StreamZH should run as your normal user.
Write-Host 'Start StreamZH with the tray icon at sign-in (as administrator):'
Write-Host "    & '$(Join-Path $PSScriptRoot 'Install-Tray.ps1')'"
Write-Host 'Or in a console window, from a normal PowerShell (not as administrator):'
Write-Host "    Start-Process '$(Join-Path $InstallDir 'StreamZH.exe')'"
Write-Host 'Back to the service:'
Write-Host "    & '$(Join-Path $PSScriptRoot 'Install-Service.ps1')'"
