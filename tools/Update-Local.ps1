<#
.SYNOPSIS
    Builds StreamZH from this repository and (re)installs it locally on Windows.

.DESCRIPTION
    git pull -> build web UI -> publish single-file StreamZH.exe -> stop running StreamZH
    -> copy to the install folder -> start it again. Your data in C:\ProgramData\StreamZH stays untouched.

    Needs once: Git, .NET 10 SDK and Node.js (LTS). The script tells you how to install what is missing.

    par2 (for repairing damaged downloads) is downloaded once into <InstallDir>\tools from the official
    par2cmdline release on GitHub (pinned version, SHA256 checked). Skipped when it is already there or with -NoTools.
    7-Zip is not downloaded: install it from https://www.7-zip.org (StreamZH finds C:\Program Files\7-Zip\7z.exe).

.EXAMPLE
    C:\Repos\StreamZH\tools\Update-Local.ps1

.EXAMPLE
    C:\Repos\StreamZH\tools\Update-Local.ps1 -NoPull -NoStart

.EXAMPLE
    C:\Repos\StreamZH\tools\Update-Local.ps1 -NoTools
#>
[CmdletBinding()]
param(
    [string]$InstallDir = 'C:\Tools\StreamZH',
    [switch]$NoPull,
    [switch]$NoStart,
    [switch]$NoTools
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:NPM_CONFIG_UPDATE_NOTIFIER = 'false'
$repo = Split-Path -Parent $PSScriptRoot
$stopwatch = [Diagnostics.Stopwatch]::StartNew()

function Step([string]$text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

# par2cmdline (GPL-2.0-or-later) is an external program; StreamZH only calls it. Pinned release, checked by SHA256.
$par2Version = '1.4.0'
$par2Url = "https://github.com/Parchive/par2cmdline/releases/download/v$par2Version/par2cmdline-$par2Version-win-x64.zip"
$par2Sha256 = '200130143dba1e12f9d1be567d5a446e785d2093e5d735225d8ad0539125b71c'

function Install-Par2([string]$toolsDir) {
    $exe = Join-Path $toolsDir 'par2.exe'
    if (Test-Path $exe) { return }

    Step "Download par2cmdline $par2Version (repairs damaged downloads)"
    Write-Host "Quelle: $par2Url"
    Write-Host "Ziel:   $exe  (überspringen mit -NoTools)"
    $zip = Join-Path $env:TEMP "par2cmdline-$par2Version-win-x64.zip"
    $extract = Join-Path $env:TEMP "par2cmdline-$par2Version"
    try {
        Invoke-WebRequest -Uri $par2Url -OutFile $zip -UseBasicParsing
        $hash = (Get-FileHash -Path $zip -Algorithm SHA256).Hash
        if ($hash -ne $par2Sha256) {
            throw "SHA256 stimmt nicht: erwartet $par2Sha256, erhalten $hash"
        }
        if (Test-Path $extract) { Remove-Item $extract -Recurse -Force }
        Expand-Archive -Path $zip -DestinationPath $extract
        $found = Get-ChildItem -Path $extract -Filter 'par2.exe' -Recurse | Select-Object -First 1
        if (-not $found) { throw 'par2.exe fehlt im Archiv' }
        New-Item -ItemType Directory -Path $toolsDir -Force | Out-Null
        Copy-Item $found.FullName $exe
        Write-Host "par2 installiert: $exe" -ForegroundColor Green
    }
    catch {
        # Not fatal: StreamZH runs without par2, only repairs are not possible (Settings -> Downloads -> Tools).
        Write-Host "par2 konnte nicht installiert werden: $($_.Exception.Message)" -ForegroundColor Yellow
        Write-Host "Manuell: par2.exe von https://github.com/Parchive/par2cmdline/releases nach $toolsDir kopieren." -ForegroundColor Yellow
    }
    finally {
        Remove-Item $zip, $extract -Recurse -Force -ErrorAction SilentlyContinue
    }
}

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
    $running = Get-Process -Name 'StreamZH' -ErrorAction SilentlyContinue
    if ($running) {
        $running | Stop-Process -Force
        $running | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue
        Write-Host 'stopped'
    } else {
        Write-Host 'not running'
    }

    Step "Install to $InstallDir"
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    # /XD keeps <InstallDir>\tools (par2.exe) although /MIR removes everything that is not in the build.
    $toolsDir = Join-Path $InstallDir 'tools'
    robocopy $out $InstallDir /MIR /XD $toolsDir /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }

    if (-not $NoTools) {
        Install-Par2 $toolsDir
    }

    if (-not $NoStart) {
        Step 'Start StreamZH'
        Start-Process -FilePath (Join-Path $InstallDir 'StreamZH.exe') -WorkingDirectory $InstallDir
        Write-Host 'http://localhost:9797'
    }

    $version = (Get-Item (Join-Path $InstallDir 'StreamZH.exe')).VersionInfo.ProductVersion
    Write-Host ("`nFertig: StreamZH {0} in {1:n0} s" -f $version, $stopwatch.Elapsed.TotalSeconds) -ForegroundColor Green
}
finally {
    Pop-Location
}
