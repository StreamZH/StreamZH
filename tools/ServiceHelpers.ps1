<#
.SYNOPSIS
    Shared helpers for Install-Service.ps1, Install-Tray.ps1, Uninstall-Service.ps1 and Update-Local.ps1 (dot-sourced,
    not run directly). The Windows installer runs the same scripts, so there is one implementation of the service work.
#>

$StreamZhServiceName = 'StreamZH'
$StreamZhFirewallRule = 'StreamZH-HTTP'
$StreamZhLocalSystem = 'S-1-5-18'
$StreamZhAdministrators = 'S-1-5-32-544'

# Owners the data folder may have besides the accounts a script names: SYSTEM, Administrators, TrustedInstaller.
$StreamZhTrustedOwners = @($StreamZhLocalSystem, $StreamZhAdministrators, 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')

# Folders below the data folder with secrets (DataFolderPermissions.SecretFolders, plus the installed tools).
$StreamZhSecretFolders = @('keys', 'Backups', 'restore', 'tools')

function Test-Administrator {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# A value as a PowerShell single-quoted literal: 'it''s'. PowerShell also ends a single-quoted string at the
# typographic quotes U+2018 to U+201B (a folder "John's Movies" typed with autocorrect), so those are doubled too.
function ConvertTo-StreamZhLiteral([string]$value) {
    $quotes = @([char]0x27, [char]0x2018, [char]0x2019, [char]0x201A, [char]0x201B)
    $text = New-Object System.Text.StringBuilder
    foreach ($c in $value.ToCharArray()) {
        [void]$text.Append($c)
        if ($quotes -contains $c) { [void]$text.Append($c) }
    }
    return "'" + $text.ToString() + "'"
}

# The bound parameters of a script as command line text (values in double quotes), to print a command to run again.
function Format-StreamZhArguments($bound) {
    $parts = @()
    foreach ($entry in $bound.GetEnumerator()) {
        if ($entry.Value -is [System.Management.Automation.SwitchParameter]) {
            if ($entry.Value.IsPresent) { $parts += "-$($entry.Key)" }
        }
        elseif ($entry.Value -is [array]) {
            $parts += "-$($entry.Key) " + (($entry.Value | ForEach-Object { ConvertTo-StreamZhLiteral $_.ToString() }) -join ', ')
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
    Write-Host 'This script needs administrator rights. Open PowerShell as administrator and run:' -ForegroundColor Yellow
    Write-Host "    $direct" -ForegroundColor Yellow
    Write-Host 'Or open a window as administrator from this PowerShell that runs it:' -ForegroundColor Yellow
    Write-Host "    Start-Process powershell -Verb RunAs -ArgumentList '$($elevated.Replace("'", "''"))'" -ForegroundColor Yellow
    exit 1
}

# The folder with StreamZH.exe: -InstallDir when given; else the folder above this tools folder when StreamZH.exe is
# there (an installed or unpacked package); else C:\Tools\StreamZH (where Update-Local.ps1 installs a build from source).
function Resolve-StreamZhInstallDir([string]$installDir) {
    if ($installDir) { return $installDir.Trim().Trim('"').TrimEnd('\') }
    $package = Split-Path -Parent $PSScriptRoot
    if ($package -and (Test-Path -LiteralPath (Join-Path $package 'StreamZH.exe'))) { return $package }
    return 'C:\Tools\StreamZH'
}

# True when the folder is inside C:\Program Files (or C:\Program Files (x86)): only administrators change it there.
function Test-StreamZhProgramFiles([string]$folder) {
    $full = [IO.Path]::GetFullPath($folder).TrimEnd('\') + '\'
    foreach ($root in @($env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:ProgramW6432)) {
        if (-not $root) { continue }
        $prefix = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
        if ($full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

function Get-StreamZhService {
    return Get-Service -Name $StreamZhServiceName -ErrorAction SilentlyContinue
}

# True when StreamZH was set up on this computer before: the service exists, or this user's tray icon starts it.
function Test-StreamZhInstalled {
    if (Get-StreamZhService) { return $true }
    return [bool](Get-ItemProperty -Path $StreamZhRunKey -ErrorAction SilentlyContinue).$StreamZhTrayRunValue
}

# Stops a script that runs as SYSTEM where StreamZH would then run as SYSTEM (SV4 X-05).
function Assert-NotLocalSystem([string]$what) {
    if ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -ne $StreamZhLocalSystem) { return }
    Write-Host "$what runs as SYSTEM (for example from a deployment tool such as Intune or SCCM). StreamZH would then run as SYSTEM," -ForegroundColor Red
    Write-Host 'with full control of Windows, and the tray icon would belong to nobody. Install the Windows service instead' -ForegroundColor Red
    Write-Host "(Install-Service.ps1, or the setup with /TASKS=service), or run this as administrator from the user's own session." -ForegroundColor Red
    exit 1
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
            Write-Host 'The StreamZH service stopped again.' -ForegroundColor Red
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
        Write-Host "StreamZH runs and listens on port $ports, but does not answer on http://localhost:<port>/health (HTTPS only?)." -ForegroundColor Yellow
        return "https://localhost:$($listening[0].LocalPort)"
    }

    Write-Host "StreamZH has not opened a port after $timeoutSeconds s." -ForegroundColor Yellow
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

# The program the service runs (without quotes and arguments), or ''.
function Get-StreamZhServiceExe {
    $info = Get-CimInstance -ClassName Win32_Service -Filter "Name='$StreamZhServiceName'" -ErrorAction SilentlyContinue
    if (-not $info) { return '' }
    if ($info.PathName -match '^\s*"([^"]+)"') { return $Matches[1] }
    return ($info.PathName -split '\s+')[0]
}

# Tray icon (StreamZH.Tray.exe next to StreamZH.exe): "Start with Windows" for the current user, like the menu item.
$StreamZhRunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$StreamZhTrayRunValue = 'StreamZH Tray'

function Stop-StreamZhTray {
    $tray = @(Get-Process -Name 'StreamZH.Tray' -ErrorAction SilentlyContinue)
    if ($tray.Count -eq 0) { return $false }
    # Without administrator rights only the own ones; that is enough to replace the file for this user.
    $tray | Stop-Process -Force -ErrorAction SilentlyContinue
    $tray | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
    return $true
}

# The Run entry; -StartHost adds --start: with no service, the tray starts StreamZH.exe at sign-in (Install-Tray.ps1).
function Register-StreamZhTray([string]$installDir, [string]$dataDir, [switch]$StartHost) {
    $exe = Join-Path $installDir 'StreamZH.Tray.exe'
    if (-not (Test-Path $exe)) { return $false }
    $command = "`"$exe`""
    # Without a trailing backslash, which would escape the closing quote.
    if ($dataDir) { $command = "$command --data `"$($dataDir.TrimEnd('\'))`"" }
    if ($StartHost) { $command = "$command --start" }
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

# ---- The data folder (security review SV4 X-02) ----
# Any local user may create folders in C:\ProgramData. Whoever creates C:\ProgramData\StreamZH before StreamZH is
# installed owns it, and an owner may always change the access list again (read the database and the sign-in keys,
# plant a database with a known password); a junction there would make the elevated scripts change another folder.
# So before the folder is made private the scripts look at what is there and who owns it, and Administrators become
# the owner.

# What to do with the data folder (no side effects, so it is tested on any system: tests/scripts):
#   create          it does not exist yet
#   protect         its owner is SYSTEM, Administrators, TrustedInstaller or one of $trustedSids (you, the service)
#   recreate        another account owns it, but it is empty: delete it and create it anew
#   take-ownership  another account owns it and it is not empty, but StreamZH was installed already ($existingInstall:
#                   the folder holds its data, e.g. a console window of another user created it)
#   refuse-link     it is a junction or symbolic link
#   refuse-owner    another account owns it and it is not empty, on a first installation
# An owner that cannot be read ('') counts as another account.
function Get-StreamZhDataFolderAction([bool]$exists, [bool]$isLink, [string]$ownerSid, [bool]$isEmpty, [string[]]$trustedSids, [bool]$existingInstall) {
    if (-not $exists) { return 'create' }
    if ($isLink) { return 'refuse-link' }
    $trusted = @($StreamZhTrustedOwners) + @($trustedSids | Where-Object { $_ })
    if ($ownerSid -and ($trusted -contains $ownerSid)) { return 'protect' }
    if ($isEmpty) { return 'recreate' }
    if ($existingInstall) { return 'take-ownership' }
    return 'refuse-owner'
}

# The data folder's exact access list as SDDL: protected (nothing inherited from C:\ProgramData), SYSTEM and
# Administrators full control, $fullControl (the service account) full control, $modify (your user: tray icon, console
# window) change, each for the folder, its subfolders and files. Nobody else.
function Get-StreamZhDataFolderSddl([string[]]$fullControl, [string[]]$modify) {
    $aces = @('(A;OICI;FA;;;SY)', '(A;OICI;FA;;;BA)')
    $seen = @{ $StreamZhLocalSystem = $true; $StreamZhAdministrators = $true }
    $wanted = @()
    foreach ($sid in @($fullControl | Where-Object { $_ })) { $wanted += , @($sid, 'FA') }
    foreach ($sid in @($modify | Where-Object { $_ })) { $wanted += , @($sid, '0x1301bf') }
    foreach ($entry in $wanted) {
        $sid = $entry[0]
        if ($seen.ContainsKey($sid)) { continue }
        if ($sid -notmatch '^S-1-\d+(-\d+)+$') { throw "not a SID: $sid" }
        $aces += "(A;OICI;$($entry[1]);;;$sid)"
        $seen[$sid] = $true
    }
    return 'D:PAI' + ($aces -join '')
}

# The owner of a file or folder as a SID, or '' when it cannot be read.
function Get-StreamZhOwnerSid([string]$path) {
    try { return (Get-Acl -LiteralPath $path -ErrorAction Stop).GetOwner([Security.Principal.SecurityIdentifier]).Value }
    catch { return '' }
}

function Get-StreamZhAccountName([string]$sid) {
    if (-not $sid) { return 'an account whose name cannot be read' }
    try { return (New-Object Security.Principal.SecurityIdentifier($sid)).Translate([Security.Principal.NTAccount]).Value }
    catch { return $sid }
}

# Looks at the data folder and stops (throws) when it must not be used: a link, or a folder of another account on a
# first installation. Changes nothing; returns the action of Get-StreamZhDataFolderAction. The scripts call it before
# they change anything, and Protect-StreamZhDataFolder calls it again.
function Assert-StreamZhDataFolder([string]$folder, [string[]]$trustedSids, [bool]$existingInstall) {
    $item = Get-Item -LiteralPath $folder -Force -ErrorAction SilentlyContinue
    $exists = $null -ne $item
    if ($exists -and -not $item.PSIsContainer) { throw "$folder is a file. StreamZH's data folder must be a folder: move the file away and run this again." }
    $isLink = $exists -and (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
    $owner = ''
    $isEmpty = $false
    if ($exists -and -not $isLink) {
        $owner = Get-StreamZhOwnerSid $folder
        try { $isEmpty = @(Get-ChildItem -LiteralPath $folder -Force -ErrorAction Stop | Select-Object -First 1).Count -eq 0 }
        catch { $isEmpty = $false }
    }

    $action = Get-StreamZhDataFolderAction $exists $isLink $owner $isEmpty $trustedSids $existingInstall
    if ($action -eq 'refuse-link') {
        throw "$folder is a junction or symbolic link. StreamZH's data folder must be a real folder; remove the link (Remove-Item -LiteralPath $(ConvertTo-StreamZhLiteral $folder)) and run this again."
    }
    if ($action -eq 'refuse-owner') {
        throw ("$folder already exists, belongs to $(Get-StreamZhAccountName $owner) and is not empty. Another account may have " +
            "prepared it to read StreamZH's data later, so it is not used. Check what is in it. If it is StreamZH data you want " +
            "to keep, make Administrators its owner and drop the access others set on it (takeown /F `"$folder`" /A /R /D Y, then " +
            "icacls `"$folder`" /reset /T /C /L) and run this again; otherwise delete the folder.")
    }
    return $action
}

# Makes the data folder private (it holds the database with the API key and password hashes, the sign-in keys and the
# backups): checks it first (Assert-StreamZhDataFolder), makes Administrators its owner, then writes the exact access
# list of Get-StreamZhDataFolderSddl in one step. Items inside that another account owns get Administrators as owner
# and only the inherited access (Repair-StreamZhDataFolderItems). StreamZH checks the folder at every start
# (DataFolderAcl.cs) and shows a notice when another person's account owns it.
function Protect-StreamZhDataFolder([string]$folder, [string[]]$fullControl, [string[]]$modify, [bool]$existingInstall) {
    $trusted = @($fullControl) + @($modify)
    $action = Assert-StreamZhDataFolder $folder $trusted $existingInstall
    if ($action -eq 'create') {
        New-StreamZhPrivateFolder $folder (Get-StreamZhDataFolderSddl $fullControl $modify)
    }
    elseif ($action -eq 'recreate') {
        Write-Host "$folder was created by $(Get-StreamZhAccountName (Get-StreamZhOwnerSid $folder)) and is empty: created anew." -ForegroundColor Yellow
        [IO.Directory]::Delete($folder, $false)
        New-StreamZhPrivateFolder $folder (Get-StreamZhDataFolderSddl $fullControl $modify)
    }
    elseif ($action -eq 'take-ownership') {
        Write-Host "$folder belongs to $(Get-StreamZhAccountName (Get-StreamZhOwnerSid $folder)); Administrators become its owner." -ForegroundColor Yellow
    }

    # The folder itself only, no /R: nothing below it is followed.
    if ((Get-StreamZhOwnerSid $folder) -ne $StreamZhAdministrators) {
        $output = & takeown.exe /F $folder /A 2>&1
        if ($LASTEXITCODE -ne 0) { throw "takeown /F $folder /A failed ($LASTEXITCODE): $output" }
    }
    # One write with exactly these entries (not grant first, then remove).
    $acl = Get-Acl -LiteralPath $folder
    $acl.SetSecurityDescriptorSddlForm((Get-StreamZhDataFolderSddl $fullControl $modify), [Security.AccessControl.AccessControlSections]::Access)
    Set-Acl -LiteralPath $folder -AclObject $acl

    Repair-StreamZhDataFolderItems $folder $trusted ($action -eq 'take-ownership')
}

# SV4 Y-01: creates $folder in one step with Administrators as owner and the final access list ($sddl), so it is never
# open to other users (a new folder in C:\ProgramData would inherit "Users may create files and folders" until the
# access list is written). Fails when the folder appeared in the meantime and is not ours.
function New-StreamZhPrivateFolder([string]$folder, [string]$sddl) {
    if (Test-Path -LiteralPath $folder) { throw "$folder appeared while the setup was running; run it again." }
    $security = New-Object System.Security.AccessControl.DirectorySecurity
    $security.SetSecurityDescriptorSddlForm('O:BA' + $sddl)
    $create = [IO.Directory].GetMethod('CreateDirectory', [type[]]@([string], [System.Security.AccessControl.DirectorySecurity]))
    if ($create) {
        # Windows PowerShell 5.1 (.NET Framework)
        $create.Invoke($null, @($folder, $security)) | Out-Null
    }
    else {
        # PowerShell 7 (.NET): the same through the extension method
        [System.IO.FileSystemAclExtensions]::Create((New-Object System.IO.DirectoryInfo $folder), $security)
    }
    if ((Get-StreamZhOwnerSid $folder) -ne $StreamZhAdministrators) {
        throw "$folder was not created by this setup (its owner is $(Get-StreamZhAccountName (Get-StreamZhOwnerSid $folder))); check it and run this again."
    }
}

# SIDs with explicit (not inherited) access entries on $path that are not in $trusted.
function Get-StreamZhForeignAceSids([string]$path, [string[]]$trusted) {
    $acl = Get-Acl -LiteralPath $path
    $sids = @()
    foreach ($rule in $acl.GetAccessRules($true, $false, [System.Security.Principal.SecurityIdentifier])) {
        $sid = $rule.IdentityReference.Value
        if (-not ($trusted -contains $sid)) { $sids += $sid }
    }
    return $sids
}

# Items in the data folder that another account owns (created while the folder was open to every user) get
# Administrators as owner and only the access they inherit from the data folder (icacls /reset). Links are never
# followed nor changed, only reported. Without $all only the top level and the secret folders (keys, Backups, restore,
# tools) are checked, not downloads and logs (many files, no secrets).
function Repair-StreamZhDataFolderItems([string]$folder, [string[]]$trustedSids, [bool]$all) {
    $trusted = @($StreamZhTrustedOwners) + @($trustedSids | Where-Object { $_ })
    $pending = New-Object System.Collections.Generic.Stack[string]
    $pending.Push($folder)
    $fixed = 0
    while ($pending.Count -gt 0) {
        $dir = $pending.Pop()
        foreach ($entry in @(Get-ChildItem -LiteralPath $dir -Force -ErrorAction SilentlyContinue)) {
            $path = $entry.FullName
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                Write-Host "$path is a link (not followed, not changed). StreamZH's data folder should hold no links; check it." -ForegroundColor Yellow
                continue
            }
            $owner = Get-StreamZhOwnerSid $path
            # SV4 Y-01: also items that carry access entries of accounts outside the trusted set (left after a takeown).
            $foreign = @(Get-StreamZhForeignAceSids $path ($trusted + @($StreamZhLocalSystem, $StreamZhAdministrators)))
            if (-not ($owner -and ($trusted -contains $owner)) -or $foreign.Count -gt 0) {
                $output = & takeown.exe /F $path /A 2>&1
                if ($LASTEXITCODE -ne 0) { throw "takeown /F $path /A failed ($LASTEXITCODE): $output" }
                $output = & icacls.exe $path /reset /Q 2>&1
                if ($LASTEXITCODE -ne 0) { throw "icacls $path /reset failed ($LASTEXITCODE): $output" }
                $fixed++
            }
            if ($entry.PSIsContainer -and ($all -or $dir -ne $folder -or $StreamZhSecretFolders -contains $entry.Name)) {
                $pending.Push($path)
            }
        }
    }
    if ($fixed -gt 0) { Write-Host "${folder}: $fixed item(s) of other accounts now belong to Administrators and inherit its private access list" }
}

# Makes the program folder (StreamZH.exe, StreamZH.Tray.exe and their files) changeable only by SYSTEM, administrators
# and $modify (your user, so Update-Local.ps1 can install updates without administrator rights; empty for a folder in
# C:\Program Files, which only administrators change); every other account, also the service account, may only read
# and run it ($readers, plus Users). Without this, C:\Tools\StreamZH inherits "Authenticated Users: Modify" from C:\
# and any local account could replace StreamZH.exe, which the service and your tray icon then run. StreamZH checks this
# at every start (AppFolderAcl.cs) and shows a notice when it is not so.
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

# Inbound TCP $port from private and domain networks (not from public networks) for the web UI and API.
function Set-StreamZhFirewallRule([int]$port) {
    Get-NetFirewallRule -Name $StreamZhFirewallRule -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -Name $StreamZhFirewallRule -DisplayName "StreamZH (TCP $port)" -Description 'StreamZH web UI and API' `
        -Direction Inbound -Protocol TCP -LocalPort $port -Action Allow -Profile Domain, Private | Out-Null
}

function Remove-StreamZhFirewallRule {
    $rule = Get-NetFirewallRule -Name $StreamZhFirewallRule -ErrorAction SilentlyContinue
    if (-not $rule) { return $false }
    $rule | Remove-NetFirewallRule
    return $true
}

# Controlled folder access (Windows Security -> Ransomware protection) blocks unknown programs in protected folders
# such as Videos and Documents. With $allow StreamZH.exe is added to the allowed apps; without it a hint is printed
# when the feature is on.
function Set-StreamZhControlledFolderAccess([string]$exe, [bool]$allow) {
    $defender = $null
    try { $defender = Get-MpPreference -ErrorAction Stop } catch { $defender = $null }
    if (-not $defender) {
        Write-Host 'Microsoft Defender is not available, nothing to do'
    }
    elseif ($defender.EnableControlledFolderAccess -eq 0) {
        Write-Host 'off, nothing to do'
    }
    elseif (@($defender.ControlledFolderAccessAllowedApplications) -contains $exe) {
        Write-Host "$exe is allowed"
    }
    elseif ($allow) {
        Add-MpPreference -ControlledFolderAccessAllowedApplications $exe
        Write-Host "$exe allowed"
    }
    else {
        Write-Host 'Controlled folder access is on. Without an exception StreamZH may not import into or rename in' -ForegroundColor Yellow
        Write-Host 'protected folders (Videos, Documents, Pictures ...). Allow it with:' -ForegroundColor Yellow
        Write-Host "    Add-MpPreference -ControlledFolderAccessAllowedApplications '$exe'" -ForegroundColor Yellow
        Write-Host 'or run this script again with -AllowControlledFolderAccess.' -ForegroundColor Yellow
    }
}

function Write-StreamZhTroubleshooting {
    $logs = Join-Path $env:ProgramData 'StreamZH\logs'
    Write-Host "Log files: $logs" -ForegroundColor Yellow
    Write-Host "Start errors: Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'StreamZH' } -MaxEvents 5 | Format-List" -ForegroundColor Yellow
    Write-Host 'Service events: Get-WinEvent -FilterHashtable @{ LogName = ''System''; ProviderName = ''Service Control Manager'' } -MaxEvents 10 | Where-Object Message -like ''*StreamZH*''' -ForegroundColor Yellow
}
