<#
.SYNOPSIS
    Builds StreamZH from this repository and (re)installs it locally on Windows.

.DESCRIPTION
    git pull -> build web UI -> publish single-file StreamZH.exe -> stop running StreamZH
    -> copy to the install folder -> start it again. Your data in C:\ProgramData\StreamZH stays untouched.

    Windows service: when the service StreamZH exists (C:\Repos\StreamZH\tools\Install-Service.ps1), the script stops
    the service, installs the new StreamZH.exe, starts the service again and waits until it answers. Without the service
    StreamZH starts in a console window as before and stops when you log off. To switch to the service, once, in a
    PowerShell as administrator:
        C:\Repos\StreamZH\tools\Install-Service.ps1
    Back to the console window: C:\Repos\StreamZH\tools\Uninstall-Service.ps1

    Needs once: Git, .NET 10 SDK and Node.js (LTS). The script tells you how to install what is missing.

    par2 and 7-Zip are not handled here: StreamZH installs them itself (par2cmdline-turbo and 7-Zip, pinned
    official releases, SHA256 checked) into C:\ProgramData\StreamZH\tools, a few seconds after it starts,
    or with "Install" in Settings -> Tools. An existing C:\Program Files\7-Zip is used as it is.

.EXAMPLE
    C:\Repos\StreamZH\tools\Update-Local.ps1

.EXAMPLE
    C:\Repos\StreamZH\tools\Update-Local.ps1 -NoPull -NoStart
#>
[CmdletBinding()]
param(
    [string]$InstallDir = 'C:\Tools\StreamZH',
    [switch]$NoPull,
    [switch]$NoStart
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
        Write-Host "'$command' fehlt. Installieren mit:" -ForegroundColor Yellow
        Write-Host "    $install" -ForegroundColor Yellow
        Write-Host "Danach PowerShell neu öffnen und das Skript nochmals starten." -ForegroundColor Yellow
        exit 1
    }
}

Require 'git' 'winget install --id Git.Git -e'
Require 'dotnet' 'winget install --id Microsoft.DotNet.SDK.10 -e'
Require 'npm' 'winget install --id OpenJS.NodeJS.LTS -e'

if (-not ((& dotnet --list-sdks) -match '^10\.')) {
    Write-Host '.NET 10 SDK fehlt. Installieren mit:' -ForegroundColor Yellow
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

    Step 'Stop running StreamZH'
    $service = Get-StreamZhService
    if ($service) {
        try {
            if (Stop-StreamZhService) { Write-Host 'service stopped' } else { Write-Host 'service not running' }
        }
        catch {
            Write-Host "Der Dienst StreamZH lässt sich nicht stoppen: $($_.Exception.Message)" -ForegroundColor Yellow
            Write-Host 'Einmal als Administrator ausführen, danach geht es ohne Administratorrechte:' -ForegroundColor Yellow
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
    # /MIR also removes the old <InstallDir>\tools\par2.exe of earlier versions of this script; StreamZH then
    # installs par2cmdline-turbo into C:\ProgramData\StreamZH\tools by itself.
    robocopy $out $InstallDir /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }

    if ($service -and $NoStart) {
        Write-Host "`nDer Dienst StreamZH bleibt gestoppt. Starten mit: Start-Service StreamZH" -ForegroundColor Yellow
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
        Write-Host "`nStreamZH läuft im Konsolenfenster und stoppt bei der Abmeldung. Als Windows-Dienst läuft es"
        Write-Host 'auch ohne Anmeldung weiter. Einmalig in einer PowerShell als Administrator:'
        Write-Host "    & '$(Join-Path $PSScriptRoot 'Install-Service.ps1')'"
    }

    $version = (Get-Item (Join-Path $InstallDir 'StreamZH.exe')).VersionInfo.ProductVersion
    Write-Host ("`nFertig: StreamZH {0} in {1:n0} s" -f $version, $stopwatch.Elapsed.TotalSeconds) -ForegroundColor Green
}
finally {
    Pop-Location
}
