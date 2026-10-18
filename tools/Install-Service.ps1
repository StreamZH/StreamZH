<#
.SYNOPSIS
    Installs StreamZH as a Windows service (or updates the existing one) and starts it. Needs administrator rights.

.DESCRIPTION
    Creates the service "StreamZH" for <InstallDir>\StreamZH.exe: starts with Windows (delayed automatic start), keeps
    running when you log off, and Windows restarts it after a crash (after 5 s, 10 s, then every 60 s). Running the
    script again updates the service; your data in C:\ProgramData\StreamZH stays untouched.

    It also lets your Windows user start and stop the service without administrator rights, so
    C:\Repos\StreamZH\tools\Update-Local.ps1 can update it from a normal PowerShell.

    Tray icon: a service has no window, so StreamZH.Tray.exe (next to StreamZH.exe) shows StreamZH in the notification
    area with a menu (open the dashboard, pause downloads, restart, stop, start). The script registers it to start with
    Windows for your user (HKCU ... Run, "StreamZH Tray") and starts it; -NoTray leaves it out.

    The service runs as LocalSystem by default (no password needed). Two things work differently for a service:
    - Controlled folder access (Windows Security -> Ransomware protection) blocks unknown programs in protected folders
      such as Videos and Documents, also for a service. -AllowControlledFolderAccess allows StreamZH.exe.
    - Mapped network drives (Z:) do not exist for services. Use UNC paths (\\nas\movies) for root folders and download
      folders. LocalSystem logs on to other computers as the computer account; for a NAS with its own users run the
      service as your user with -Account.

    Build StreamZH.exe first: C:\Repos\StreamZH\tools\Update-Local.ps1 -NoStart

.PARAMETER InstallDir
    Folder with StreamZH.exe (the same as for Update-Local.ps1).

.PARAMETER DataDir
    Data folder. Empty: C:\ProgramData\StreamZH, the same as without a service.

.PARAMETER Account
    Run the service as this user (for example .\johndoe or DOMAIN\user) instead of LocalSystem; asks for the password.
    With a Microsoft account use the password of the Microsoft account, not the PIN. "LocalSystem" switches back.
    Without -Account an existing service keeps its account.

.PARAMETER Firewall
    Allows incoming connections to -Port from the private and domain network (not from public networks).

.PARAMETER Port
    Port for -Firewall. Must match Settings -> General -> Port.

.PARAMETER AllowControlledFolderAccess
    Adds StreamZH.exe to the allowed apps of Controlled folder access.

.PARAMETER NoStart
    Creates or updates the service but does not start it (nor the tray icon).

.PARAMETER NoTray
    No tray icon: removes its "Start with Windows" entry and closes it.

.EXAMPLE
    C:\Repos\StreamZH\tools\Install-Service.ps1

.EXAMPLE
    C:\Repos\StreamZH\tools\Install-Service.ps1 -Firewall -AllowControlledFolderAccess

.EXAMPLE
    C:\Repos\StreamZH\tools\Install-Service.ps1 -Account .\johndoe
#>
[CmdletBinding()]
param(
    [string]$InstallDir = 'C:\Tools\StreamZH',
    [string]$DataDir = '',
    [string]$Account = '',
    [switch]$Firewall,
    [ValidateRange(1, 65535)]
    [int]$Port = 9797,
    [switch]$AllowControlledFolderAccess,
    [switch]$NoStart,
    [switch]$NoTray
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ServiceHelpers.ps1')

function Step([string]$text) { Write-Host "`n==> $text" -ForegroundColor Cyan }
function Hint([string]$text) { Write-Host $text -ForegroundColor Yellow }

function Invoke-Sc([string[]]$arguments) {
    $output = & sc.exe @arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "sc.exe $($arguments -join ' ') failed ($LASTEXITCODE): $output" }
}

# "Log on as a service" for -Account (Windows refuses to start the service without it; sc.exe and New-Service do not grant it).
function Grant-ServiceLogonRight([string]$name) {
    if (-not ('StreamZhLsa' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

public static class StreamZhLsa
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LsaUnicodeString { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaObjectAttributes
    {
        public int Length; public IntPtr RootDirectory; public IntPtr ObjectName; public uint Attributes;
        public IntPtr SecurityDescriptor; public IntPtr SecurityQualityOfService;
    }

    [DllImport("advapi32.dll")]
    private static extern uint LsaOpenPolicy(IntPtr systemName, ref LsaObjectAttributes attributes, uint access, out IntPtr handle);

    [DllImport("advapi32.dll")]
    private static extern uint LsaAddAccountRights(IntPtr handle, byte[] sid, LsaUnicodeString[] rights, uint count);

    [DllImport("advapi32.dll")]
    private static extern uint LsaClose(IntPtr handle);

    [DllImport("advapi32.dll")]
    private static extern int LsaNtStatusToWinError(uint status);

    public static void Grant(string sidValue, string right)
    {
        SecurityIdentifier sid = new SecurityIdentifier(sidValue);
        byte[] binary = new byte[sid.BinaryLength];
        sid.GetBinaryForm(binary, 0);

        LsaObjectAttributes attributes = new LsaObjectAttributes();
        IntPtr policy;
        // POLICY_CREATE_ACCOUNT | POLICY_LOOKUP_NAMES
        uint status = LsaOpenPolicy(IntPtr.Zero, ref attributes, 0x00000010 | 0x00000800, out policy);
        if (status != 0) { throw new Win32Exception(LsaNtStatusToWinError(status)); }
        try
        {
            LsaUnicodeString name = new LsaUnicodeString();
            name.Buffer = Marshal.StringToHGlobalUni(right);
            name.Length = (ushort)(right.Length * 2);
            name.MaximumLength = (ushort)((right.Length + 1) * 2);
            try
            {
                status = LsaAddAccountRights(policy, binary, new LsaUnicodeString[] { name }, 1);
                if (status != 0) { throw new Win32Exception(LsaNtStatusToWinError(status)); }
            }
            finally { Marshal.FreeHGlobal(name.Buffer); }
        }
        finally { LsaClose(policy); }
    }
}
'@
    }

    $sid = (New-Object Security.Principal.NTAccount($name)).Translate([Security.Principal.SecurityIdentifier]).Value
    [StreamZhLsa]::Grant($sid, 'SeServiceLogonRight')
    return $sid
}

# Lets the user start and stop the service (RP start, WP stop, LO interrogate; CC LC SW RC read), keeps all other entries.
function Grant-ServiceControl([string]$sid) {
    $sddl = ((& sc.exe sdshow $StreamZhServiceName) | Where-Object { $_ -match '^D:' } | Select-Object -First 1)
    if (-not $sddl) { throw 'sc.exe sdshow returned no security descriptor' }
    if ($sddl -match [regex]::Escape(";;;$sid)")) { return $false }
    $ace = "(A;;CCLCSWRPWPLORC;;;$sid)"
    $saclStart = $sddl.IndexOf('S:')
    if ($saclStart -ge 0) { $sddl = $sddl.Insert($saclStart, $ace) } else { $sddl = $sddl + $ace }
    Invoke-Sc @('sdset', $StreamZhServiceName, $sddl)
    return $true
}

# Modify for the user (OI)(CI), so StreamZH started in a console can still write files the service created.
function Grant-DataFolder([string]$folder, [string]$sid) {
    $output = & icacls.exe $folder /grant "*${sid}:(OI)(CI)M" /Q 2>&1
    if ($LASTEXITCODE -ne 0) { throw "icacls $folder failed ($LASTEXITCODE): $output" }
}

Assert-Administrator $PSCommandPath (Format-StreamZhArguments $PSBoundParameters)

$exe = Join-Path $InstallDir 'StreamZH.exe'
if (-not (Test-Path $exe)) {
    Hint "StreamZH.exe fehlt in $InstallDir. Zuerst bauen und installieren:"
    Hint "    & '$(Join-Path $PSScriptRoot 'Update-Local.ps1')' -NoStart"
    exit 1
}

# A trailing backslash would escape the closing quote on the service command line.
$DataDir = $DataDir.TrimEnd('\')
$data = $DataDir
if (-not $data) { $data = Join-Path $env:ProgramData 'StreamZH' }
$binaryPath = "`"$exe`""
if ($DataDir) { $binaryPath = "$binaryPath --data `"$DataDir`"" }
$description = 'StreamZH - one app for your whole media pipeline (library, indexers, downloads). Web UI on http://localhost:9797 unless changed in Settings -> General.'
$me = [Security.Principal.WindowsIdentity]::GetCurrent()

$credential = $null
$useLocalSystem = $Account -eq 'LocalSystem' -or $Account -eq 'NT AUTHORITY\SYSTEM'
if ($Account -and -not $useLocalSystem) {
    if ($Account -notmatch '[\\@]') { $Account = ".\$Account" }
    $credential = Get-Credential -UserName $Account -Message "Passwort für das Dienstkonto von StreamZH ($Account)"
    if (-not $credential) { exit 1 }
}

Step 'Stop StreamZH'
$service = Get-StreamZhService
if ($service -and (Stop-StreamZhService)) { Write-Host 'service stopped' }
# A StreamZH in a console window holds the port and the database.
$console = Get-Process -Name 'StreamZH' -ErrorAction SilentlyContinue
if ($console) {
    $console | Stop-Process -Force
    $console | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue
    Write-Host 'console instance stopped'
}

if ($service) {
    Step 'Update service StreamZH'
    $changes = @{ PathName = $binaryPath; DisplayName = 'StreamZH' }
    if ($credential) {
        $changes.StartName = $credential.UserName
        $changes.StartPassword = $credential.GetNetworkCredential().Password
    }
    elseif ($useLocalSystem) {
        $changes.StartName = 'LocalSystem'
        $changes.StartPassword = ''
    }
    $info = Get-CimInstance -ClassName Win32_Service -Filter "Name='$StreamZhServiceName'"
    $result = Invoke-CimMethod -InputObject $info -MethodName Change -Arguments $changes
    if ($result.ReturnValue -ne 0) { throw "Win32_Service.Change failed (return value $($result.ReturnValue))" }
}
else {
    Step 'Create service StreamZH'
    $parameters = @{
        Name           = $StreamZhServiceName
        BinaryPathName = $binaryPath
        DisplayName    = 'StreamZH'
        Description    = $description
        StartupType    = 'Automatic'
    }
    if ($credential) { $parameters.Credential = $credential }
    New-Service @parameters | Out-Null
}

Set-Service -Name $StreamZhServiceName -Description $description
Invoke-Sc @('config', $StreamZhServiceName, 'start=', 'delayed-auto')
# Restart after a crash: 5 s, 10 s, then every 60 s; the count starts again after a day without a crash.
Invoke-Sc @('failure', $StreamZhServiceName, 'reset=', '86400', 'actions=', 'restart/5000/restart/10000/restart/60000')
# Also when StreamZH ends with an error code instead of crashing.
Invoke-Sc @('failureflag', $StreamZhServiceName, '1')

$runsAs = (Get-CimInstance -ClassName Win32_Service -Filter "Name='$StreamZhServiceName'").StartName
Write-Host "binary:  $binaryPath"
Write-Host "account: $runsAs"
Write-Host "data:    $data"

Step 'Permissions'
if ($credential) {
    $serviceSid = Grant-ServiceLogonRight $credential.UserName
    Write-Host "$($credential.UserName): log on as a service"
}
if (Grant-ServiceControl $me.User.Value) { Write-Host "$($me.Name): may start and stop the service" }
else { Write-Host "$($me.Name): may start and stop the service (already set)" }

New-Item -ItemType Directory -Path $data -Force | Out-Null
# The service writes a new tray token for its account when it starts.
Remove-StreamZhTrayFiles $data
Grant-DataFolder $data $me.User.Value
Write-Host "$($me.Name): may change $data"
if ($credential) {
    Grant-DataFolder $data $serviceSid
    Write-Host "$($credential.UserName): may change $data"
}

if (-not [Diagnostics.EventLog]::SourceExists($StreamZhServiceName)) {
    [Diagnostics.EventLog]::CreateEventSource($StreamZhServiceName, 'Application')
    Write-Host 'event log source StreamZH (start errors) registered'
}

if ($Firewall) {
    Step "Firewall: TCP $Port"
    Get-NetFirewallRule -Name 'StreamZH-HTTP' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -Name 'StreamZH-HTTP' -DisplayName "StreamZH (TCP $Port)" -Description 'StreamZH web UI and API' `
        -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow -Profile Domain, Private | Out-Null
    Write-Host "inbound TCP $Port allowed (domain and private networks)"
}

Step 'Controlled folder access'
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
elseif ($AllowControlledFolderAccess) {
    Add-MpPreference -ControlledFolderAccessAllowedApplications $exe
    Write-Host "$exe allowed"
}
else {
    Hint 'Überwachter Ordnerzugriff ist eingeschaltet. Ohne Freigabe darf StreamZH nicht in geschützte Ordner'
    Hint '(Videos, Dokumente, Bilder ...) importieren oder umbenennen. Freigeben mit:'
    Hint "    Add-MpPreference -ControlledFolderAccessAllowedApplications '$exe'"
    Hint 'oder dieses Skript mit -AllowControlledFolderAccess nochmals ausführen.'
}

# Mapped drives of this user: the saved ones (HKCU:\Network; an elevated PowerShell does not see the drives of the
# normal session) and the ones of this session.
$mapped = @{}
foreach ($key in @(Get-ChildItem -Path 'HKCU:\Network' -ErrorAction SilentlyContinue)) {
    $remote = (Get-ItemProperty -Path $key.PSPath -ErrorAction SilentlyContinue).RemotePath
    if ($remote) { $mapped[$key.PSChildName.ToUpperInvariant()] = $remote }
}
foreach ($drive in @(Get-PSDrive -PSProvider FileSystem | Where-Object { $_.DisplayRoot -like '\\*' })) {
    $mapped[$drive.Name.ToUpperInvariant()] = $drive.DisplayRoot
}
if ($mapped.Count -gt 0) {
    Hint "`nNetzlaufwerke sieht der Dienst nicht. In StreamZH (Stammordner, Download-Ordner) diese Pfade verwenden:"
    foreach ($letter in ($mapped.Keys | Sort-Object)) { Hint "    ${letter}: -> $($mapped[$letter])" }
    if (-not $credential -and $runsAs -eq 'LocalSystem') {
        Hint 'Braucht das NAS eine Anmeldung, den Dienst unter deinem Benutzer laufen lassen:'
        Hint "    & '$PSCommandPath' -Account .\$env:USERNAME"
    }
}

Step 'Tray icon'
if ($NoTray) {
    if (Unregister-StreamZhTray) { Write-Host 'start with Windows: removed' }
    if (Stop-StreamZhTray) { Write-Host 'tray icon closed' }
    Write-Host 'not used (-NoTray)'
}
elseif (Register-StreamZhTray $InstallDir $DataDir) {
    Write-Host "start with Windows: on (for $($me.Name))"
}
else {
    Hint "StreamZH.Tray.exe fehlt in $InstallDir. Mit Update-Local.ps1 bauen, dann dieses Skript nochmals ausführen:"
    Hint "    & '$(Join-Path $PSScriptRoot 'Update-Local.ps1')'"
}

if ($NoStart) {
    Write-Host "`nDienst eingerichtet, nicht gestartet. Starten mit: Start-Service StreamZH" -ForegroundColor Green
    exit 0
}

Step 'Start service StreamZH'
try {
    Start-Service -Name $StreamZhServiceName
}
catch {
    Write-Host "Der Dienst startet nicht: $($_.Exception.Message)" -ForegroundColor Red
    if ($credential) { Hint 'Bei -Account: Benutzername und Passwort prüfen (Microsoft-Konto: Passwort, nicht die PIN).' }
    Write-StreamZhTroubleshooting
    exit 1
}

$url = Wait-StreamZhReady
if (-not $url) { exit 1 }
Write-Host $url
if (-not $NoTray -and (Start-StreamZhTray $InstallDir $DataDir)) { Write-Host 'tray icon started (notification area, bottom right)' }
Write-Host "`nFertig: StreamZH läuft als Windows-Dienst, auch ohne Anmeldung." -ForegroundColor Green
Write-Host "Updates wie bisher mit: & '$(Join-Path $PSScriptRoot 'Update-Local.ps1')'"
Write-Host "Zurück zum Start im Konsolenfenster: & '$(Join-Path $PSScriptRoot 'Uninstall-Service.ps1')'"
