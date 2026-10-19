<#
.SYNOPSIS
    Shared helpers for Install-Service.ps1, Uninstall-Service.ps1 and Update-Local.ps1 (dot-sourced, not run directly).
#>

$StreamZhServiceName = 'StreamZH'

function Test-Administrator {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# The bound parameters of a script as command line text (values in double quotes), to print a command to run again.
function Format-StreamZhArguments($bound) {
    $parts = @()
    foreach ($entry in $bound.GetEnumerator()) {
        if ($entry.Value -is [System.Management.Automation.SwitchParameter]) {
            if ($entry.Value.IsPresent) { $parts += "-$($entry.Key)" }
        }
        else {
            $parts += "-$($entry.Key) `"$($entry.Value)`""
        }
    }
    return ($parts -join ' ')
}

# Stops the script with the exact command to start it again as administrator.
function Assert-Administrator([string]$scriptPath, [string]$arguments) {
    if (Test-Administrator) { return }
    $direct = "& '$scriptPath'"
    $elevated = "-NoExit -ExecutionPolicy Bypass -File `"$scriptPath`""
    if ($arguments) {
        $direct = "$direct $arguments"
        $elevated = "$elevated $arguments"
    }
    Write-Host 'Dieses Skript braucht Administratorrechte. PowerShell als Administrator öffnen und ausführen:' -ForegroundColor Yellow
    Write-Host "    $direct" -ForegroundColor Yellow
    Write-Host 'Oder aus dieser PowerShell ein Fenster als Administrator öffnen, das es ausführt:' -ForegroundColor Yellow
    Write-Host "    Start-Process powershell -Verb RunAs -ArgumentList '$($elevated.Replace("'", "''"))'" -ForegroundColor Yellow
    exit 1
}

function Get-StreamZhService {
    return Get-Service -Name $StreamZhServiceName -ErrorAction SilentlyContinue
}

function Get-StreamZhServiceProcessId {
    $info = Get-CimInstance -ClassName Win32_Service -Filter "Name='$StreamZhServiceName'" -ErrorAction SilentlyContinue
    if ($info -and $info.ProcessId -gt 0) { return [int]$info.ProcessId }
    return $null
}

# Stops the service and waits until its process has exited (the program files can be replaced then).
function Stop-StreamZhService {
    $service = Get-StreamZhService
    if (-not $service -or $service.Status -eq 'Stopped') { return $false }
    $processId = Get-StreamZhServiceProcessId
    Stop-Service -Name $StreamZhServiceName -Force -ErrorAction Stop
    if ($processId) { Wait-Process -Id $processId -Timeout 60 -ErrorAction SilentlyContinue }
    return $true
}

# Waits until the service answers on /health. Returns the address, or $null when it stopped or did not answer in time.
function Wait-StreamZhReady([int]$timeoutSeconds = 90) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    $listening = @()
    while ((Get-Date) -lt $deadline) {
        $service = Get-StreamZhService
        if (-not $service -or $service.Status -eq 'Stopped') {
            Write-Host 'Der Dienst StreamZH hat sich wieder beendet.' -ForegroundColor Red
            Write-StreamZhTroubleshooting
            return $null
        }

        $processId = Get-StreamZhServiceProcessId
        if ($processId) {
            # The port comes from Settings -> General, so ask Windows where the service listens.
            $listening = @(Get-NetTCPConnection -OwningProcess $processId -State Listen -ErrorAction SilentlyContinue |
                Sort-Object -Property LocalPort -Unique)
            foreach ($connection in $listening) {
                $address = $connection.LocalAddress
                if ($address -eq '0.0.0.0' -or $address -eq '::') { $address = 'localhost' }
                elseif ($address.Contains(':')) { $address = "[$address]" }
                $url = "http://${address}:$($connection.LocalPort)"
                try {
                    $response = Invoke-WebRequest -Uri "$url/health" -UseBasicParsing -TimeoutSec 3
                    if ($response.StatusCode -eq 200) { return $url }
                }
                catch {
                    # Not ready yet, or this port is the HTTPS port.
                }
            }
        }

        Start-Sleep -Seconds 1
    }

    if ($listening.Count -gt 0) {
        $ports = ($listening | ForEach-Object { $_.LocalPort }) -join ', '
        Write-Host "StreamZH läuft und hört auf Port $ports, antwortet aber nicht auf http://localhost:<port>/health (nur HTTPS?)." -ForegroundColor Yellow
        return "https://localhost:$($listening[0].LocalPort)"
    }

    Write-Host "StreamZH hat nach $timeoutSeconds s noch keinen Port geöffnet." -ForegroundColor Yellow
    Write-StreamZhTroubleshooting
    return $null
}

# The data folder from the service command line (--data "D:\StreamZH"), or '' for the default.
function Get-StreamZhServiceDataDir {
    $info = Get-CimInstance -ClassName Win32_Service -Filter "Name='$StreamZhServiceName'" -ErrorAction SilentlyContinue
    if ($info -and $info.PathName -match '--data\s+"([^"]+)"') { return $Matches[1] }
    if ($info -and $info.PathName -match '--data\s+(\S+)') { return $Matches[1] }
    return ''
}

# Tray icon (StreamZH.Tray.exe next to StreamZH.exe): "Start with Windows" for the current user, like the menu item.
$StreamZhRunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$StreamZhTrayRunValue = 'StreamZH Tray'

function Stop-StreamZhTray {
    $tray = @(Get-Process -Name 'StreamZH.Tray' -ErrorAction SilentlyContinue)
    if ($tray.Count -eq 0) { return $false }
    # Only the own ones without administrator rights; that is enough to replace the file for this user.
    $tray | Stop-Process -Force -ErrorAction SilentlyContinue
    $tray | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
    return $true
}

function Register-StreamZhTray([string]$installDir, [string]$dataDir) {
    $exe = Join-Path $installDir 'StreamZH.Tray.exe'
    if (-not (Test-Path $exe)) { return $false }
    $command = "`"$exe`""
    # Without a trailing backslash, which would escape the closing quote.
    if ($dataDir) { $command = "$command --data `"$($dataDir.TrimEnd('\'))`"" }
    if (-not (Test-Path $StreamZhRunKey)) { New-Item -Path $StreamZhRunKey | Out-Null }
    Set-ItemProperty -Path $StreamZhRunKey -Name $StreamZhTrayRunValue -Value $command
    return $true
}

function Unregister-StreamZhTray {
    if ((Get-ItemProperty -Path $StreamZhRunKey -ErrorAction SilentlyContinue).$StreamZhTrayRunValue) {
        Remove-ItemProperty -Path $StreamZhRunKey -Name $StreamZhTrayRunValue
        return $true
    }
    return $false
}

function Start-StreamZhTray([string]$installDir, [string]$dataDir) {
    $exe = Join-Path $installDir 'StreamZH.Tray.exe'
    if (-not (Test-Path $exe)) { return $false }
    if ((Test-Administrator) -and -not $dataDir) {
        # From an administrator PowerShell explorer.exe starts it as the signed-in user, without administrator rights.
        Start-Process -FilePath 'explorer.exe' -ArgumentList "`"$exe`""
    }
    else {
        $start = @{ FilePath = $exe; WorkingDirectory = $installDir }
        if ($dataDir) { $start.ArgumentList = @('--data', "`"$($dataDir.TrimEnd('\'))`"") }
        Start-Process @start
    }
    return $true
}

# Makes the data folder private (it holds the database with the API key and password hashes, the sign-in keys and the
# backups): SYSTEM and administrators full control, $fullControl (the service account) full control, $modify (your
# user: tray icon, console window) change; no inheritance from C:\ProgramData, Users, Authenticated Users, Everyone and
# Interactive removed. StreamZH checks the same at every start (DataFolderAcl.cs).
function Protect-StreamZhDataFolder([string]$folder, [string[]]$fullControl, [string[]]$modify) {
    $grants = @('*S-1-5-18:(OI)(CI)F', '*S-1-5-32-544:(OI)(CI)F')
    foreach ($sid in @($fullControl | Where-Object { $_ })) { $grants += "*${sid}:(OI)(CI)F" }
    foreach ($sid in @($modify | Where-Object { $_ })) { $grants += "*${sid}:(OI)(CI)M" }
    # Grant first, so nobody is locked out, then cut the inherited and the broad entries.
    $steps = @()
    $steps += , (@('/grant') + $grants)
    $steps += , @('/inheritance:r')
    $steps += , @('/remove:g', '*S-1-1-0', '*S-1-5-4', '*S-1-5-11', '*S-1-5-32-545')
    foreach ($step in $steps) {
        $output = & icacls.exe $folder @step /Q 2>&1
        if ($LASTEXITCODE -ne 0) { throw "icacls $folder $($step -join ' ') failed ($LASTEXITCODE): $output" }
    }
}

# Makes the program folder (StreamZH.exe, StreamZH.Tray.exe and their files) changeable only by SYSTEM, administrators
# and $modify (your user, so Update-Local.ps1 can install updates without administrator rights); every other account,
# also the service account, may only read and run it ($readers, plus Users). Without this, C:\Tools\StreamZH inherits
# "Authenticated Users: Modify" from C:\ and any local account could replace StreamZH.exe, which the service and your
# tray icon then run. StreamZH checks this at every start (AppFolderAcl.cs) and shows a notice when it is not so.
# Returns $true when the rules are set, $false (with a warning) when this account may not change them.
function Protect-StreamZhInstallFolder([string]$folder, [string]$modify, [string[]]$readers) {
    $grants = @('*S-1-5-18:(OI)(CI)F', '*S-1-5-32-544:(OI)(CI)F')
    if ($modify) { $grants += "*${modify}:(OI)(CI)M" }
    foreach ($sid in @($readers | Where-Object { $_ })) { $grants += "*${sid}:(OI)(CI)RX" }
    # Grant first, so nobody is locked out; Users only read and run (:r replaces what they had); then cut the
    # inherited entries (Authenticated Users: Modify from C:\) and any broad entry that is left.
    $steps = @()
    $steps += , (@('/grant') + $grants)
    $steps += , @('/grant:r', '*S-1-5-32-545:(OI)(CI)RX')
    $steps += , @('/inheritance:r')
    $steps += , @('/remove:g', '*S-1-1-0', '*S-1-5-4', '*S-1-5-11', '*S-1-3-0')
    foreach ($step in $steps) {
        $output = & icacls.exe $folder @step /Q 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Host "The program folder $folder could not be protected (icacls $($step -join ' '): $output)." -ForegroundColor Yellow
            Write-Host 'Run once in a PowerShell as administrator:' -ForegroundColor Yellow
            Write-Host "    & '$(Join-Path $PSScriptRoot 'Install-Service.ps1')'" -ForegroundColor Yellow
            return $false
        }
    }
    return $true
}

# The tray reads <data>\tray.token and tray.json; a new owner (service account or console user) writes them anew.
function Remove-StreamZhTrayFiles([string]$dataDir) {
    if (-not $dataDir) { $dataDir = Join-Path $env:ProgramData 'StreamZH' }
    foreach ($name in @('tray.token', 'tray.json')) {
        Remove-Item -LiteralPath (Join-Path $dataDir $name) -Force -ErrorAction SilentlyContinue
    }
}

function Write-StreamZhTroubleshooting {
    $logs = Join-Path $env:ProgramData 'StreamZH\logs'
    Write-Host "Log-Dateien: $logs" -ForegroundColor Yellow
    Write-Host "Fehler beim Start: Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'StreamZH' } -MaxEvents 5 | Format-List" -ForegroundColor Yellow
    Write-Host 'Dienst-Ereignisse: Get-WinEvent -FilterHashtable @{ LogName = ''System''; ProviderName = ''Service Control Manager'' } -MaxEvents 10 | Where-Object Message -like ''*StreamZH*''' -ForegroundColor Yellow
}
