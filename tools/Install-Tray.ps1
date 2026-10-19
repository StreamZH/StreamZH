<#
.SYNOPSIS
    Runs StreamZH without a Windows service: the tray icon starts it when you sign in. Needs administrator rights.

.DESCRIPTION
    The simple alternative to Install-Service.ps1 (the Windows installer runs this script for its "tray icon only"
    choice). StreamZH then runs as your user while you are signed in, in a minimized console window, and stops when you
    sign out. Your user's mapped drives and folders work as they are; nothing needs extra permissions.

    - Removes the service StreamZH when there is one (Uninstall-Service.ps1); your data stays.
    - Registers StreamZH.Tray.exe to start with Windows for your user (HKCU ... Run, "StreamZH Tray") with --start: the
      tray starts StreamZH.exe when it is not running.
    - Protects the program folder (only SYSTEM and administrators, and outside C:\Program Files also your user, may
      change it) and the data folder (SYSTEM, administrators and your user).
    - -Firewall and -AllowControlledFolderAccess as in Install-Service.ps1.
    - Starts the tray icon (and with it StreamZH) unless -NoStart.

.PARAMETER InstallDir
    Folder with StreamZH.exe. Empty: the folder above this tools folder when StreamZH.exe is there, else
    C:\Tools\StreamZH.

.PARAMETER DataDir
    Data folder. Empty: C:\ProgramData\StreamZH.

.PARAMETER Firewall
    Allows incoming connections to -Port from private and domain networks (not from public networks).

.PARAMETER Port
    Port for -Firewall. Must match Settings -> General -> Port.

.PARAMETER AllowControlledFolderAccess
    Adds StreamZH.exe to the allowed apps of Controlled folder access.

.PARAMETER NoStart
    Sets everything up but starts neither the tray icon nor StreamZH (the installer starts the tray as your normal
    user itself).

.PARAMETER NonInteractive
    Never asks (for the installer and scripts).

.EXAMPLE
    .\Install-Tray.ps1

.EXAMPLE
    .\Install-Tray.ps1 -Firewall
#>
[CmdletBinding()]
param(
    [string]$InstallDir = '',
    [string]$DataDir = '',
    [switch]$Firewall,
    [ValidateRange(1, 65535)]
    [int]$Port = 9797,
    [switch]$AllowControlledFolderAccess,
    [switch]$NoStart,
    [switch]$NonInteractive
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ServiceHelpers.ps1')

function Step([string]$text) { Write-Host "`n==> $text" -ForegroundColor Cyan }
function Hint([string]$text) { Write-Host $text -ForegroundColor Yellow }

Assert-Administrator $PSCommandPath (Format-StreamZhArguments $PSBoundParameters)

$InstallDir = Resolve-StreamZhInstallDir $InstallDir
$exe = Join-Path $InstallDir 'StreamZH.exe'
$trayExe = Join-Path $InstallDir 'StreamZH.Tray.exe'
foreach ($file in @($exe, $trayExe)) {
    if (-not (Test-Path $file)) {
        Hint "$file is missing. Install StreamZH first (setup or unpacked package), or build it from source:"
        Hint "    & '$(Join-Path $PSScriptRoot 'Update-Local.ps1')' -NoStart"
        exit 1
    }
}
$DataDir = $DataDir.TrimEnd('\')
$me = [Security.Principal.WindowsIdentity]::GetCurrent()
# StreamZH runs as the user who runs this script: never as SYSTEM (SV4 X-05).
Assert-NotLocalSystem 'Install-Tray.ps1'

$existingInstall = Test-StreamZhInstalled
$planned = $DataDir
if (-not $planned) { $planned = (Get-StreamZhServiceDataDir).TrimEnd('\') }
if (-not $planned) { $planned = Join-Path $env:ProgramData 'StreamZH' }
# Before anything changes: the data folder must not be a link or another account's folder (SV4 X-02).
try { [void](Assert-StreamZhDataFolder $planned @($me.User.Value) $existingInstall) }
catch { Write-Host $_.Exception.Message -ForegroundColor Red; exit 1 }

if (Get-StreamZhService) {
    Step 'Remove the StreamZH service'
    # Keeps the data; also resets the data folder to "your user may change it".
    $serviceData = Get-StreamZhServiceDataDir
    if (-not $DataDir -and $serviceData) { $DataDir = $serviceData.TrimEnd('\') }
    & (Join-Path $PSScriptRoot 'Uninstall-Service.ps1') -InstallDir $InstallDir -NonInteractive
}

$data = $DataDir
if (-not $data) { $data = Join-Path $env:ProgramData 'StreamZH' }

Step 'Permissions'
$programWriter = $me.User.Value
$writers = "SYSTEM, administrators and $($me.Name)"
if (Test-StreamZhProgramFiles $InstallDir) {
    $programWriter = ''
    $writers = 'SYSTEM and administrators'
}
if (Protect-StreamZhInstallFolder $InstallDir $programWriter @()) {
    Write-Host "${InstallDir}: only $writers may change it; everyone else reads and runs it"
}
# StreamZH writes a new tray token for your user when it starts.
Remove-StreamZhTrayFiles $data
Protect-StreamZhDataFolder $data @() @($me.User.Value) $existingInstall
Write-Host "$($me.Name): may change $data (nobody else but SYSTEM and administrators)"

if ($Firewall) {
    Step "Firewall: TCP $Port"
    Set-StreamZhFirewallRule $Port
    Write-Host "inbound TCP $Port allowed (domain and private networks)"
}

Step 'Controlled folder access'
Set-StreamZhControlledFolderAccess $exe ([bool]$AllowControlledFolderAccess)

Step 'Tray icon'
[void](Register-StreamZhTray $InstallDir $DataDir -StartHost)
Write-Host "start with Windows: on (for $($me.Name)), starts StreamZH when it is not running"

if ($NoStart) {
    Write-Host "`nSet up, not started. StreamZH starts with the tray icon at your next sign-in." -ForegroundColor Green
    exit 0
}

# Not as administrator: explorer.exe starts the tray as the signed-in user; the tray then starts StreamZH.exe.
Stop-StreamZhTray | Out-Null
if ($DataDir) {
    Hint 'Start the tray icon from a normal PowerShell (not as administrator):'
    Hint "    Start-Process '$trayExe' -ArgumentList '--data', '`"$DataDir`"', '--start'"
}
else {
    # explorer.exe passes no arguments to a program, so it runs a one-line .cmd with --start. The file lives in a new
    # folder with a random name (no fixed name another account could prepare in %TEMP%, SV4 X-07) and is removed
    # once the tray icon runs.
    $runnerDir = Join-Path ([IO.Path]::GetTempPath()) ('streamzh-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $runnerDir -ErrorAction Stop | Out-Null
    try {
        $runner = Join-Path $runnerDir 'start-tray.cmd'
        Set-Content -LiteralPath $runner -Value "@start `"`" `"$trayExe`" --start" -Encoding ASCII
        Start-Process -FilePath 'explorer.exe' -ArgumentList "`"$runner`""
        for ($i = 0; $i -lt 30 -and -not (Get-Process -Name 'StreamZH.Tray' -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 }
    }
    finally {
        Remove-Item -LiteralPath $runnerDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    Write-Host 'tray icon started; it starts StreamZH (notification area, bottom right)'
}
Write-Host "`nDone: StreamZH starts with the tray icon when you sign in. http://localhost:$Port" -ForegroundColor Green
Write-Host "As a Windows service instead (runs without sign-in): & '$(Join-Path $PSScriptRoot 'Install-Service.ps1')'"
