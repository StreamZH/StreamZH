<#
.SYNOPSIS
    Builds StreamZH from this repository and (re)installs it locally on Windows.

.DESCRIPTION
    git pull -> build web UI -> publish single-file StreamZH.exe -> stop running StreamZH
    -> copy to the install folder -> start it again. Your data in C:\ProgramData\StreamZH stays untouched.

    Needs once: Git, .NET 10 SDK and Node.js (LTS). The script tells you how to install what is missing.

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
    robocopy $out $InstallDir /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }

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
