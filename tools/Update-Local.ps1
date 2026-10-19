<#
.SYNOPSIS
    Builds StreamZH from this repository and (re)installs it locally on Windows (for development and testing; to just
    use StreamZH, install it with StreamZH-<version>-win-x64-setup.exe instead).

.DESCRIPTION
    git pull -> build web UI -> publish single-file StreamZH.exe -> stop running StreamZH
    -> copy to the install folder -> start it again. Your data in C:\ProgramData\StreamZH stays untouched.

    Windows service: when the service StreamZH exists (tools\Install-Service.ps1), the script stops the service,
    installs the new StreamZH.exe, starts the service again and waits until it answers. Without the service StreamZH
    starts in a console window and stops when you sign out. To switch to the service, once, in a PowerShell as
    administrator:
        tools\Install-Service.ps1
    Back to the console window: tools\Uninstall-Service.ps1

    Do not point -InstallDir at the folder of the Windows installer (C:\Program Files\StreamZH): only administrators
    may change it, and the next setup would overwrite the build.

    Program folder: only SYSTEM, administrators and your user may change <InstallDir> (inheritance from C:\ is cut, so
    other local accounts cannot replace StreamZH.exe); everyone else may read and run it.

    Tray icon: also builds StreamZH.Tray.exe (StreamZH in the notification area, bottom right, with a menu to open the
    dashboard, pause downloads, restart, stop and start StreamZH), registers it to start with Windows for your user
    (HKCU ... Run, "StreamZH Tray") and starts it. -NoTray leaves it out (and removes the entry). It needs the .NET 10
    Windows Desktop Runtime, which the .NET SDK on Windows includes.

    Needs once: Git, .NET 10 SDK and Node.js (LTS). The script tells you how to install what is missing.

    par2 and 7-Zip are not handled here: StreamZH installs them itself (par2cmdline-turbo and 7-Zip, pinned
    official releases, SHA256 checked) into C:\ProgramData\StreamZH\tools, a few seconds after it starts,
    or with "Install" in Settings -> Tools. An existing C:\Program Files\7-Zip is used as it is.

.PARAMETER InstallDir
    Folder to install to (default C:\Tools\StreamZH).

.PARAMETER NoPull
    Builds the checkout as it is, without git pull.

.PARAMETER NoStart
    Installs but does not start StreamZH (nor the tray icon); a service stays stopped.

.PARAMETER NoTray
    No tray icon: not built, its "Start with Windows" entry is removed.

.EXAMPLE
    .\tools\Update-Local.ps1

.EXAMPLE
    .\tools\Update-Local.ps1 -NoPull -NoStart

.EXAMPLE
    .\tools\Update-Local.ps1 -NoTray
#>
[CmdletBinding()]
param(
    [string]$InstallDir = 'C:\Tools\StreamZH',
    [switch]$NoPull,
    [switch]$NoStart,
    [switch]$NoTray
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ServiceHelpers.ps1')
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:NPM_CONFIG_UPDATE_NOTIFIER = 'false'
$repo = Split-Path -Parent $PSScriptRoot
$stopwatch = [Diagnostics.Stopwatch]::StartNew()

function Step([string]$text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

function Require([string]$command, [string]$install) {
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
        Write-Host "'$command' is missing. Install it with:" -ForegroundColor Yellow
        Write-Host "    $install" -ForegroundColor Yellow
        Write-Host "Then open a new PowerShell and run the script again." -ForegroundColor Yellow
        exit 1
    }
}

Require 'git' 'winget install --id Git.Git -e'
Require 'dotnet' 'winget install --id Microsoft.DotNet.SDK.10 -e'
Require 'npm' 'winget install --id OpenJS.NodeJS.LTS -e'

if (-not ((& dotnet --list-sdks) -match '^10\.')) {
    Write-Host 'The .NET 10 SDK is missing. Install it with:' -ForegroundColor Yellow
    Write-Host '    winget install --id Microsoft.DotNet.SDK.10 -e' -ForegroundColor Yellow
    exit 1
}

Push-Location $repo
try {
    if (-not $NoPull) {
        Step 'git pull'
        git pull --ff-only
        if ($LASTEXITCODE -ne 0) { throw 'git pull failed (local changes?)' }
    }

    Step 'Build web UI'
    Push-Location (Join-Path $repo 'frontend')
    try {
        npm ci --no-audit --no-fund --loglevel=error
        if ($LASTEXITCODE -ne 0) { throw 'npm ci failed' }
        npm run build --silent
        if ($LASTEXITCODE -ne 0) { throw 'web UI build failed' }
    }
    finally { Pop-Location }

    Step 'Build StreamZH.exe'
    $out = Join-Path $env:TEMP 'streamzh-publish'
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    dotnet publish (Join-Path $repo 'src\StreamZH.Host') -c Release -r win-x64 --self-contained `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -p:DebugType=none -o $out --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

    $tray = -not $NoTray
    if ($tray -and -not ((& dotnet --list-runtimes) -match '^Microsoft\.WindowsDesktop\.App 10\.')) {
        Write-Host 'The .NET 10 Windows Desktop Runtime is missing, the tray icon is not built. Install it with:' -ForegroundColor Yellow
        Write-Host '    winget install --id Microsoft.DotNet.DesktopRuntime.10 -e' -ForegroundColor Yellow
        $tray = $false
    }
    if ($tray) {
        Step 'Build StreamZH.Tray.exe'
        # Framework-dependent: one small file instead of a second copy of .NET.
        dotnet publish (Join-Path $repo 'src\StreamZH.Tray') -c Release -r win-x64 --self-contained false `
            -p:PublishSingleFile=true -p:DebugType=none -o $out --nologo -v q
        if ($LASTEXITCODE -ne 0) { throw 'dotnet publish StreamZH.Tray failed' }
    }

    Step 'Stop running StreamZH'
    # The tray icon holds StreamZH.Tray.exe; it starts again below.
    if (Stop-StreamZhTray) { Write-Host 'tray icon closed' }
    $service = Get-StreamZhService
    if ($service) {
        try {
            if (Stop-StreamZhService) { Write-Host 'service stopped' } else { Write-Host 'service not running' }
        }
        catch {
            Write-Host "The StreamZH service cannot be stopped: $($_.Exception.Message)" -ForegroundColor Yellow
            Write-Host 'Run once as administrator; afterwards this works without administrator rights:' -ForegroundColor Yellow
            Write-Host "    & '$(Join-Path $PSScriptRoot 'Install-Service.ps1')'" -ForegroundColor Yellow
            throw 'stopping the StreamZH service failed'
        }
    }
    # A StreamZH in a console window (also next to the service) would lock StreamZH.exe.
    $running = Get-Process -Name 'StreamZH' -ErrorAction SilentlyContinue
    if ($running) {
        $running | Stop-Process -Force
        $running | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue
        Write-Host 'stopped'
    } elseif (-not $service) {
        Write-Host 'not running'
    }

    Step "Install to $InstallDir"
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    # Only SYSTEM, administrators and you may change the program folder; everyone else (also the service) reads and
    # runs it. Before the copy, so the new files get these rules too.
    $me = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    if (Protect-StreamZhInstallFolder $InstallDir $me @()) { Write-Host 'program folder: only SYSTEM, administrators and you may change it' }
    # /MIR also removes the old <InstallDir>\tools\par2.exe of earlier versions of this script; StreamZH then
    # installs par2cmdline-turbo into C:\ProgramData\StreamZH\tools by itself.
    robocopy $out $InstallDir /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }

    if ($service -and $NoStart) {
        Write-Host "`nThe StreamZH service stays stopped. Start it with: Start-Service StreamZH" -ForegroundColor Yellow
    } elseif ($service) {
        Step 'Start StreamZH service'
        Start-Service -Name $StreamZhServiceName
        $url = Wait-StreamZhReady
        if (-not $url) { throw 'the StreamZH service did not start' }
        Write-Host $url
    } elseif (-not $NoStart) {
        Step 'Start StreamZH'
        Start-Process -FilePath (Join-Path $InstallDir 'StreamZH.exe') -WorkingDirectory $InstallDir
        Write-Host 'http://localhost:9797'
        Write-Host "`nStreamZH runs in a console window and stops when you sign out. As a Windows service it keeps"
        Write-Host 'running without sign-in. Once, in a PowerShell as administrator:'
        Write-Host "    & '$(Join-Path $PSScriptRoot 'Install-Service.ps1')'"
    }

    $dataDir = ''
    if ($service) { $dataDir = Get-StreamZhServiceDataDir }
    if ($tray) {
        if (Register-StreamZhTray $InstallDir $dataDir) { Write-Host "`ntray icon: starts with Windows (HKCU Run '$StreamZhTrayRunValue')" }
        if (-not $NoStart -and (Start-StreamZhTray $InstallDir $dataDir)) { Write-Host 'tray icon started (notification area, bottom right)' }
    }
    elseif ($NoTray -and (Unregister-StreamZhTray)) {
        Write-Host "`ntray icon: start with Windows removed"
    }

    $version = (Get-Item (Join-Path $InstallDir 'StreamZH.exe')).VersionInfo.ProductVersion
    Write-Host ("`nDone: StreamZH {0} in {1:n0} s" -f $version, $stopwatch.Elapsed.TotalSeconds) -ForegroundColor Green
}
finally {
    Pop-Location
}
