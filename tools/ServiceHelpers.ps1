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

function Write-StreamZhTroubleshooting {
    $logs = Join-Path $env:ProgramData 'StreamZH\logs'
    Write-Host "Log-Dateien: $logs" -ForegroundColor Yellow
    Write-Host "Fehler beim Start: Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'StreamZH' } -MaxEvents 5 | Format-List" -ForegroundColor Yellow
    Write-Host 'Dienst-Ereignisse: Get-WinEvent -FilterHashtable @{ LogName = ''System''; ProviderName = ''Service Control Manager'' } -MaxEvents 10 | Where-Object Message -like ''*StreamZH*''' -ForegroundColor Yellow
}
