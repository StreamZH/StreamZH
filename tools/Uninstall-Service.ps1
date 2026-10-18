<#
.SYNOPSIS
    Removes the Windows service StreamZH. Needs administrator rights.

.DESCRIPTION
    Stops and deletes the service, its firewall rule (from Install-Service.ps1 -Firewall) and its event log source.
    Closes the tray icon and removes its "Start with Windows" entry for your user (Update-Local.ps1 sets it up again
    for StreamZH in a console window).
    The program files in C:\Tools\StreamZH and your data in C:\ProgramData\StreamZH stay, so StreamZH can be started
    in a console window again (C:\Repos\StreamZH\tools\Update-Local.ps1 does that) or installed as a service again
    (C:\Repos\StreamZH\tools\Install-Service.ps1).

.PARAMETER InstallDir
    Folder with StreamZH.exe (only for the hint how to start it again).

.EXAMPLE
    C:\Repos\StreamZH\tools\Uninstall-Service.ps1
#>
[CmdletBinding()]
param(
    [string]$InstallDir = 'C:\Tools\StreamZH'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ServiceHelpers.ps1')

function Step([string]$text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

Assert-Administrator $PSCommandPath (Format-StreamZhArguments $PSBoundParameters)

$service = Get-StreamZhService
$dataDir = Get-StreamZhServiceDataDir

Step 'Tray icon'
if (Stop-StreamZhTray) { Write-Host 'closed' } else { Write-Host 'not running' }
if (Unregister-StreamZhTray) { Write-Host 'start with Windows: removed' }
# Written by the service account; StreamZH in a console window writes its own.
Remove-StreamZhTrayFiles $dataDir

if (-not $service) {
    Write-Host 'Der Dienst StreamZH ist nicht installiert.' -ForegroundColor Yellow
}
else {
    Step 'Stop service StreamZH'
    if (Stop-StreamZhService) { Write-Host 'stopped' } else { Write-Host 'not running' }

    Step 'Remove service StreamZH'
    $output = & sc.exe delete $StreamZhServiceName 2>&1
    if ($LASTEXITCODE -ne 0) { throw "sc.exe delete failed ($LASTEXITCODE): $output" }
    Write-Host 'removed'
}

$rule = Get-NetFirewallRule -Name 'StreamZH-HTTP' -ErrorAction SilentlyContinue
if ($rule) {
    $rule | Remove-NetFirewallRule
    Write-Host 'firewall rule removed'
}

if ([Diagnostics.EventLog]::SourceExists($StreamZhServiceName)) {
    [Diagnostics.EventLog]::DeleteEventSource($StreamZhServiceName)
    Write-Host 'event log source removed'
}

Write-Host "`nFertig. Programm und Daten sind noch da." -ForegroundColor Green
# Not started from here: this PowerShell runs as administrator, and StreamZH should run as your normal user.
Write-Host 'In einer normalen PowerShell (nicht als Administrator) im Konsolenfenster starten:'
Write-Host "    Start-Process '$(Join-Path $InstallDir 'StreamZH.exe')'"
Write-Host 'Wieder als Dienst einrichten:'
Write-Host "    & '$(Join-Path $PSScriptRoot 'Install-Service.ps1')'"
