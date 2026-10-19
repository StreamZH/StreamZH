<#
.SYNOPSIS
    Packs a published win-x64 folder into the Windows release files (see docs/PACKAGING.md).

.DESCRIPTION
    StreamZH-<version>-win-x64.zip        portable: the program, tools\*.ps1 (service scripts), LICENSE, NOTICE.md
    StreamZH-<version>-win-x64-setup.exe  the installer (Inno Setup 6, packaging\windows\StreamZH.iss)

    The published folder must hold StreamZH.exe, StreamZH.Tray.exe and wwwroot (the release workflow publishes both
    programs self-contained). The version comes from Directory.Build.props unless -Version is given.

.PARAMETER SourceDir
    The published win-x64 folder.

.PARAMETER OutputDir
    Where the files go (created when missing).

.PARAMETER Version
    Version for the file names and the installer; empty: <Version> from Directory.Build.props.

.PARAMETER Iscc
    Path of ISCC.exe; empty: ISCC.exe on PATH, else C:\Program Files (x86)\Inno Setup 6\ISCC.exe.

.PARAMETER NoInstaller
    Only the .zip.

.EXAMPLE
    .\packaging\windows\build.ps1 -SourceDir out\win-x64 -OutputDir dist
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SourceDir,
    [Parameter(Mandatory = $true)][string]$OutputDir,
    [string]$Version = '',
    [string]$Iscc = '',
    [switch]$NoInstaller
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$SourceDir = (Resolve-Path $SourceDir).Path
foreach ($file in @('StreamZH.exe', 'StreamZH.Tray.exe', 'wwwroot')) {
    if (-not (Test-Path (Join-Path $SourceDir $file))) { throw "$file is missing in $SourceDir" }
}
if (-not $Version) {
    $Version = ([xml](Get-Content -Raw (Join-Path $repo 'Directory.Build.props'))).Project.PropertyGroup.Version | Select-Object -First 1
}
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "version must look like 1.2.3 or 1.2.3-rc.1: $Version" }
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
$OutputDir = (Resolve-Path $OutputDir).Path

# ---- Portable .zip: one folder "StreamZH" ----
$work = Join-Path ([IO.Path]::GetTempPath()) ("streamzh-zip-" + [Guid]::NewGuid().ToString('N'))
$stage = Join-Path $work 'StreamZH'
New-Item -ItemType Directory -Path (Join-Path $stage 'tools') -Force | Out-Null
try {
    Copy-Item -Path (Join-Path $SourceDir '*') -Destination $stage -Recurse -Exclude 'install-linux.sh', '*.pdb'
    foreach ($script in @('ServiceHelpers.ps1', 'Install-Service.ps1', 'Install-Tray.ps1', 'Uninstall-Service.ps1')) {
        Copy-Item -Path (Join-Path $repo "tools\$script") -Destination (Join-Path $stage 'tools')
    }
    Copy-Item -Path (Join-Path $repo 'LICENSE'), (Join-Path $repo 'NOTICE.md') -Destination $stage
    $zip = Join-Path $OutputDir "StreamZH-$Version-win-x64.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal
    Write-Host $zip
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($NoInstaller) { return }

# ---- Installer ----
if (-not $Iscc) {
    $onPath = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($onPath) { $Iscc = $onPath.Source }
    else { $Iscc = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe' }
}
if (-not (Test-Path $Iscc)) { throw "ISCC.exe not found ($Iscc). Install Inno Setup 6 (choco install innosetup) or pass -Iscc." }
& $Iscc /Q "/DSourceDir=$SourceDir" "/DOutputDir=$OutputDir" "/DAppVersion=$Version" (Join-Path $PSScriptRoot 'StreamZH.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }
Write-Host (Join-Path $OutputDir "StreamZH-$Version-win-x64-setup.exe")
