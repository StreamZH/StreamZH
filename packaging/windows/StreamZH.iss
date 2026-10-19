; StreamZH - one app for your whole media pipeline.
; Copyright (c) 2026 The StreamZH Authors.
; SPDX-License-Identifier: GPL-3.0-only
;
; Windows installer (Inno Setup 6): StreamZH-<version>-win-x64-setup.exe. See docs/PACKAGING.md.
;
;   iscc /DSourceDir=<published win-x64 folder> /DOutputDir=<dist> packaging\windows\StreamZH.iss
;
; SourceDir holds StreamZH.exe, StreamZH.Tray.exe (self-contained, no .NET runtime needed) and wwwroot. The version
; comes from <Version> in Directory.Build.props unless /DAppVersion=... is given.
;
; What the setup does:
; - installs per machine to C:\Program Files\StreamZH (only administrators may change it, as StreamZH's program
;   folder check expects); data stays in C:\ProgramData\StreamZH and survives upgrades and uninstalling;
; - runs the bundled PowerShell scripts for the service work (one implementation with the manual install):
;   tools\Install-Service.ps1 (Windows service, the default) or tools\Install-Tray.ps1 (tray icon only), with
;   -Firewall, -AllowControlledFolderAccess and -Folder from the wizard pages;
; - upgrades in place: stops StreamZH, replaces the program, runs the script again with the same choices;
; - moves an older manual installation (service from C:\Tools\StreamZH) over to the new program folder;
; - uninstall: tools\Uninstall-Service.ps1, then removes the program; the data only when "Also delete data" is ticked.
;
; Silent installs: setup.exe /VERYSILENT /TASKS="service,firewall" /FOLDERS="D:\Media;D:\Downloads"
; Silent uninstall that also deletes the data: unins000.exe /VERYSILENT /DELETEDATA=1
;
; Security (review SV4):
; - the program folder must be inside Program Files, where only administrators may change files, because the setup runs
;   scripts from it as administrator and the service runs StreamZH.exe from it. Another folder needs /ALLOWCUSTOMDIR;
;   the setup then makes it administrators-only (owner and access list) BEFORE it copies anything, and refuses a link
;   or a folder that already holds files (X-06);
; - folder names never become PowerShell code: the folders go to Install-Service.ps1 in a file (-FoldersFile), and the
;   paths that are still quoted double every kind of single quote (X-04);
; - "tray only" is refused when the setup runs as SYSTEM (Intune, SCCM, RMM tools), because StreamZH would run as SYSTEM;
;   an upgrade of a tray-only installation then only replaces the files (X-05);
; - "Also delete data" uses DelTree, which deletes junctions and symbolic links inside the data folder without following
;   them (Inno Setup's DelTree skips the contents of reparse points; checked in the 6.7.0 source) (X-11).

#ifndef SourceDir
  #error Pass the published win-x64 folder: iscc /DSourceDir=...
#endif
#ifndef OutputDir
  #define OutputDir "..\..\dist"
#endif
#define RepoDir AddBackslash(SourcePath) + "..\.."

#ifndef AppVersion
  ; <Version>1.2.3</Version> from Directory.Build.props.
  #define PropsHandle FileOpen(RepoDir + "\Directory.Build.props")
  #if !PropsHandle
    #error Directory.Build.props not found
  #endif
  #define VersionLine ""
  #sub ReadPropsLine
    #define PropsLine Trim(FileRead(PropsHandle))
    #define public VersionLine Pos("<Version>", PropsLine) == 1 ? PropsLine : VersionLine
  #endsub
  #for {0; !FileEof(PropsHandle); 0} ReadPropsLine
  #expr FileClose(PropsHandle)
  #if VersionLine == ""
    #error No <Version> in Directory.Build.props
  #endif
  #define AppVersion Copy(VersionLine, 10, Pos("</Version>", VersionLine) - 10)
#endif

; 1.0.0-rc.1 -> 1.0.0 for the numeric file version.
#if Pos("-", AppVersion) > 0
  #define NumericVersion Copy(AppVersion, 1, Pos("-", AppVersion) - 1)
#else
  #define NumericVersion AppVersion
#endif

#define AppUrl "https://github.com/cloudzh/streamzh"
#define WebUi "http://localhost:9797"

[Setup]
; Never change the AppId: upgrades find the installed copy by it.
AppId={{80733154-B4A9-4387-94A6-31794276A4B5}
AppName=StreamZH
AppVersion={#AppVersion}
AppVerName=StreamZH {#AppVersion}
AppPublisher=StreamZH contributors
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
AppCopyright=Copyright (c) 2026 The StreamZH Authors
VersionInfoVersion={#NumericVersion}
VersionInfoProductVersion={#NumericVersion}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoDescription=StreamZH setup
DefaultDirName={autopf}\StreamZH
DisableProgramGroupPage=yes
DefaultGroupName=StreamZH
UsePreviousAppDir=yes
UsePreviousTasks=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
LicenseFile={#RepoDir}\LICENSE
SetupIconFile={#RepoDir}\src\StreamZH.Tray\streamzh.ico
UninstallDisplayIcon={app}\StreamZH.Tray.exe
UninstallDisplayName=StreamZH
OutputDir={#OutputDir}
OutputBaseFilename=StreamZH-{#AppVersion}-win-x64-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; StreamZH, its tray icon and the service are stopped in PrepareToInstall; no Restart Manager.
CloseApplications=no
RestartApplications=no
SetupLogging=yes
ChangesAssociations=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "service"; Description: "As a &Windows service (recommended): runs in the background, also when nobody is signed in"; GroupDescription: "How StreamZH runs:"; Flags: exclusive
Name: "trayonly"; Description: "With the &tray icon only: starts when you sign in and stops when you sign out"; GroupDescription: "How StreamZH runs:"; Flags: exclusive unchecked
Name: "firewall"; Description: "Allow other devices on &private networks to open StreamZH (Windows Firewall, TCP 9797)"; GroupDescription: "Options:"; Flags: unchecked
Name: "cfa"; Description: "Allow StreamZH in &Controlled folder access (only matters when Ransomware protection is on)"; GroupDescription: "Options:"; Flags: unchecked

[InstallDelete]
; The web UI's files have hashed names; remove the old ones on upgrade.
Type: filesandordirs; Name: "{app}\wwwroot"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "install-linux.sh,*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#RepoDir}\tools\ServiceHelpers.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "{#RepoDir}\tools\Install-Service.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "{#RepoDir}\tools\Install-Tray.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "{#RepoDir}\tools\Uninstall-Service.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "{#RepoDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoDir}\NOTICE.md"; DestDir: "{app}"; Flags: ignoreversion
; Tells StreamZH's update check that the setup installed it (it then offers the setup of a new version).
Source: "{#SourcePath}\package-type"; DestDir: "{app}"; Flags: ignoreversion

[INI]
Filename: "{group}\StreamZH.url"; Section: "InternetShortcut"; Key: "URL"; String: "{#WebUi}"

[Icons]
Name: "{group}\StreamZH tray icon"; Filename: "{app}\StreamZH.Tray.exe"; Comment: "StreamZH in the notification area"

[UninstallDelete]
Type: files; Name: "{group}\StreamZH.url"

[Run]
; Tray only: start the tray icon as the signed-in user (not elevated); it starts StreamZH. Never as SYSTEM.
Filename: "{app}\StreamZH.Tray.exe"; Parameters: "--start"; Flags: runasoriginaluser nowait; Check: StartTrayNow
; Wait until StreamZH answers, then open it in the browser.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command ""for ($i = 0; $i -lt 60; $i++) {{ try {{ if ((Invoke-WebRequest -Uri '{#WebUi}/health' -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200) {{ break }} }} catch {{ }}; Start-Sleep -Seconds 1 }}; Start-Process '{#WebUi}'"""; Description: "Open StreamZH in the browser"; Flags: postinstall nowait runhidden runasoriginaluser skipifsilent

[Code]
var
  FoldersPage: TInputQueryWizardPage;
  PreviousExe: string;
  ScriptFailed: Boolean;
  DeleteData: Boolean;
  { Tray only, upgraded by a setup that runs as SYSTEM: only the files are replaced (SV4 X-05). }
  FilesOnly: Boolean;
  SystemChecked, SystemAccount: Boolean;

const
  FolderCount = 3;
  ServiceKey = 'SYSTEM\CurrentControlSet\Services\StreamZH';
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{80733154-B4A9-4387-94A6-31794276A4B5}_is1';
  NoFileAttributes = $FFFFFFFF;

function GetFileAttributes(lpFileName: string): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function SetEnvironmentVariable(lpName: string; lpValue: string): BOOL;
  external 'SetEnvironmentVariableW@kernel32.dll stdcall';

{ A value as a PowerShell string literal: 'it''s'. PowerShell also ends a single-quoted string at the typographic
  quotes U+2018 to U+201B, so those are doubled too (SV4 X-04). }
function PsQuote(Value: string): string;
begin
  StringChangeEx(Value, '''', '''''', True);
  StringChangeEx(Value, #$2018, #$2018#$2018, True);
  StringChangeEx(Value, #$2019, #$2019#$2019, True);
  StringChangeEx(Value, #$201A, #$201A#$201A, True);
  StringChangeEx(Value, #$201B, #$201B#$201B, True);
  Result := '''' + Value + '''';
end;

function PowerShellExe(): string;
begin
  Result := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
end;

function IsTrayOnly(): Boolean;
begin
  Result := WizardIsTaskSelected('trayonly');
end;

{ True when the setup runs as LocalSystem (S-1-5-18), as deployment tools run it. Asked once. }
function RunsAsSystem(): Boolean;
var
  Code: Integer;
begin
  if not SystemChecked then
  begin
    SystemChecked := True;
    SystemAccount := Exec(PowerShellExe(), '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' +
      'if ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -eq ''S-1-5-18'') { exit 18 }; exit 0"',
      '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 18);
    Log('Setup runs as SYSTEM: ' + IntToStr(Ord(SystemAccount)));
  end;
  Result := SystemAccount;
end;

{ The [Run] entry that starts the tray icon (and with it StreamZH) right after a tray-only install. }
function StartTrayNow(): Boolean;
begin
  Result := IsTrayOnly() and not FilesOnly and not RunsAsSystem();
end;

{ The program folder of the installed copy ('' when StreamZH is not installed). }
function PreviousAppDir(): string;
begin
  if not RegQueryStringValue(HKLM, UninstallKey, 'Inno Setup: App Path', Result) then
    Result := '';
end;

{ True when Folder is inside C:\Program Files (or C:\Program Files (x86)), which only administrators may change. }
function InProgramFiles(Folder: string): Boolean;
var
  Full: string;
begin
  Full := Lowercase(AddBackslash(ExpandFileName(Folder)));
  Result := (Pos(Lowercase(AddBackslash(ExpandConstant('{commonpf64}'))), Full) = 1) or
    (Pos(Lowercase(AddBackslash(ExpandConstant('{commonpf32}'))), Full) = 1);
end;

function CustomDirAllowed(): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount() do
    if CompareText(ParamStr(I), '/ALLOWCUSTOMDIR') = 0 then
      Result := True;
end;

function IsLink(Path: string): Boolean;
var
  Attributes: Cardinal;
begin
  Attributes := GetFileAttributes(Path);
  Result := (Attributes <> NoFileAttributes) and ((Attributes and FILE_ATTRIBUTE_REPARSE_POINT) <> 0);
end;

function IsEmptyDir(Dir: string): Boolean;
var
  FindRec: TFindRec;
begin
  Result := True;
  if FindFirst(AddBackslash(Dir) + '*', FindRec) then
  try
    repeat
      if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
      begin
        Result := False;
        Exit;
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

function RunTool(Exe, Arguments: string): Boolean;
var
  Code: Integer;
begin
  Result := Exec(Exe, Arguments, '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
  Log(ExtractFileName(Exe) + ' ' + Arguments + ': exit code ' + IntToStr(Code));
end;

{ Why the program folder cannot be used, or '' (SV4 X-06). The setup runs scripts from it as administrator and the
  service runs StreamZH.exe from it, so nobody but administrators may change it. Inside Program Files that is so.
  Another folder needs /ALLOWCUSTOMDIR and is made administrators-only before any file is copied: never a link; the
  installed copy's own folder is kept (owner Administrators, inheritance cut); otherwise it must be new or empty and is
  created anew with its access list in the same step (owner Administrators; SYSTEM and Administrators full control,
  Users read and run; nothing inherited, so no "Authenticated Users: Modify" from the drive), and the owner is checked
  afterwards. Install-Service.ps1 adds the service account later. }
function CheckProgramFolder(): string;
var
  Dir: string;
  Code: Integer;
begin
  Result := '';
  Dir := RemoveBackslashUnlessRoot(ExpandConstant('{app}'));
  if InProgramFiles(Dir) then
    Exit;
  if not CustomDirAllowed() then
  begin
    Result := 'StreamZH must be installed inside ' + ExpandConstant('{commonpf64}') + ', where only administrators ' +
      'may change the program. ' + Dir + ' is not. To use another folder anyway, start the setup with /ALLOWCUSTOMDIR; ' +
      'the setup then makes that folder administrators-only.';
    Exit;
  end;
  if IsLink(Dir) then
  begin
    Result := Dir + ' is a junction or symbolic link. Choose a real folder.';
    Exit;
  end;

  if DirExists(Dir) and (CompareText(Dir, RemoveBackslashUnlessRoot(PreviousAppDir())) = 0) then
  begin
    { The installed copy's folder: keep its entries (Install-Service.ps1 added the service account). }
    if not RunTool(ExpandConstant('{sys}\takeown.exe'), '/F "' + Dir + '" /A') or
       not RunTool(ExpandConstant('{sys}\icacls.exe'), '"' + Dir + '" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F ' +
         '*S-1-5-32-544:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX /Q') then
      Result := 'The folder ' + Dir + ' could not be made administrators-only (see the setup log).';
    Exit;
  end;

  if DirExists(Dir) and not IsEmptyDir(Dir) then
  begin
    Result := Dir + ' already holds files. Choose a new or empty folder for StreamZH.';
    Exit;
  end;

  { The path reaches PowerShell as an environment variable, never as code. }
  if not SetEnvironmentVariable('STREAMZH_SETUP_DIR', Dir) then
  begin
    Result := 'The folder ' + Dir + ' could not be prepared.';
    Exit;
  end;
  if not Exec(PowerShellExe(), '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' +
    '$d = $env:STREAMZH_SETUP_DIR; ' +
    'if (Test-Path -LiteralPath $d) { [IO.Directory]::Delete($d, $false) }; ' +
    '$s = New-Object Security.AccessControl.DirectorySecurity; ' +
    '$s.SetSecurityDescriptorSddlForm(''O:BAD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)''); ' +
    '[void][IO.Directory]::CreateDirectory($d, $s); ' +
    '$i = Get-Item -LiteralPath $d -Force; if ($i.Attributes -band [IO.FileAttributes]::ReparsePoint) { exit 5 }; ' +
    'if ((Get-Acl -LiteralPath $d).GetOwner([Security.Principal.SecurityIdentifier]).Value -ne ''S-1-5-32-544'') { exit 4 }; ' +
    'if (@(Get-ChildItem -LiteralPath $d -Force).Count -ne 0) { exit 3 }; exit 0"',
    '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
    Result := 'The folder ' + Dir + ' could not be created as an administrators-only folder (exit code ' +
      IntToStr(Code) + '). Choose another folder, or install to ' + ExpandConstant('{commonpf64}') + '.';
  Log('Program folder ' + Dir + ' created administrators-only: exit code ' + IntToStr(Code));
end;

{ The program of a service command line: "C:\Tools\StreamZH\StreamZH.exe" --data "..." -> C:\Tools\StreamZH\StreamZH.exe }
function ExeOf(CommandLine: string): string;
var
  P: Integer;
begin
  CommandLine := Trim(CommandLine);
  if (Length(CommandLine) > 0) and (CommandLine[1] = '"') then
  begin
    Delete(CommandLine, 1, 1);
    P := Pos('"', CommandLine);
  end
  else
    P := Pos(' ', CommandLine);
  if P > 0 then
    Result := Copy(CommandLine, 1, P - 1)
  else
    Result := CommandLine;
end;

function ServiceExe(): string;
var
  ImagePath: string;
begin
  Result := '';
  if RegQueryStringValue(HKLM, ServiceKey, 'ImagePath', ImagePath) then
    Result := ExeOf(ImagePath);
end;

{ Folders from the wizard page plus /FOLDERS="D:\Media;D:\Downloads", without duplicates. }
function SelectedFolders(): TArrayOfString;
var
  All, Item: string;
  I, P: Integer;
begin
  All := '';
  for I := 0 to FolderCount - 1 do
    if Trim(FoldersPage.Values[I]) <> '' then
      All := All + Trim(FoldersPage.Values[I]) + ';';
  All := All + ExpandConstant('{param:FOLDERS|}') + ';';
  SetArrayLength(Result, 0);
  while All <> '' do
  begin
    P := Pos(';', All);
    Item := Trim(Copy(All, 1, P - 1));
    Delete(All, 1, P);
    StringChangeEx(Item, '"', '', True);
    if (Item <> '') and (Pos(';' + Lowercase(Item) + ';', ';' + Lowercase(All) + ';') = 0) then
    begin
      SetArrayLength(Result, GetArrayLength(Result) + 1);
      Result[GetArrayLength(Result) - 1] := Item;
    end;
  end;
end;

procedure BrowseClick(Sender: TObject);
var
  Index: Integer;
  Folder: string;
begin
  Index := TNewButton(Sender).Tag;
  Folder := FoldersPage.Values[Index];
  if BrowseForFolder('Select a folder the StreamZH service may change:', Folder, False) then
    FoldersPage.Values[Index] := Folder;
end;

procedure InitializeWizard();
var
  I: Integer;
  Button: TNewButton;
  Edit: TPasswordEdit;
begin
  FoldersPage := CreateInputQueryPage(wpSelectTasks,
    'Folders for the service',
    'Which folders may StreamZH change?',
    'The service runs as its own account (NT SERVICE\StreamZH), which may change only its data folder and the ' +
    'folders you name here: where your movies and series are, and where downloads go. Folders on other drives ' +
    'that every user may change work without this. Network shares: use \\server\share paths in StreamZH (the ' +
    'service does not see mapped drives). You can leave this empty and add folders later with ' +
    'tools\Install-Service.ps1 -Folder.');
  for I := 0 to FolderCount - 1 do
  begin
    FoldersPage.Add('Folder ' + IntToStr(I + 1) + ':', False);
    FoldersPage.Values[I] := GetPreviousData('Folder' + IntToStr(I), '');
    Edit := FoldersPage.Edits[I];
    Button := TNewButton.Create(FoldersPage);
    Button.Parent := FoldersPage.Surface;
    Button.Caption := 'Browse...';
    Button.Tag := I;
    Button.Width := ScaleX(80);
    Button.Height := Edit.Height + ScaleY(2);
    Button.Top := Edit.Top - ScaleY(1);
    Button.Left := FoldersPage.SurfaceWidth - Button.Width;
    Button.Anchors := [akTop, akRight];
    Button.OnClick := @BrowseClick;
    Edit.Width := Button.Left - Edit.Left - ScaleX(8);
  end;
end;

procedure RegisterPreviousData(PreviousDataKey: Integer);
var
  I: Integer;
begin
  for I := 0 to FolderCount - 1 do
    SetPreviousData(PreviousDataKey, 'Folder' + IntToStr(I), FoldersPage.Values[I]);
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = FoldersPage.ID) and not WizardIsTaskSelected('service');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  I: Integer;
  Folder: string;
begin
  Result := True;
  if (CurPageID = wpSelectDir) and not InProgramFiles(WizardDirValue()) and not CustomDirAllowed() then
  begin
    MsgBox('Install StreamZH inside ' + ExpandConstant('{commonpf64}') + ': only administrators may change the ' +
      'program there, and the service runs it. (Another folder needs the setup option /ALLOWCUSTOMDIR.)', mbError, MB_OK);
    Result := False;
    Exit;
  end;
  if CurPageID = FoldersPage.ID then
    for I := 0 to FolderCount - 1 do
    begin
      Folder := Trim(FoldersPage.Values[I]);
      if (Folder <> '') and (Copy(Folder, 1, 2) <> '\\') and not DirExists(Folder) then
      begin
        MsgBox('The folder ' + Folder + ' does not exist.', mbError, MB_OK);
        Result := False;
        Exit;
      end;
    end;
end;

{ Refuses what must not be installed (tray only as SYSTEM, a program folder others may change), then stops the
  service, the tray icons and a StreamZH in a console window, so the files can be replaced. }
function PrepareToInstall(var NeedsRestart: Boolean): string;
var
  Code: Integer;
begin
  Result := '';
  if IsTrayOnly() and RunsAsSystem() then
  begin
    { A new installation, or a switch from the service to tray only, needs the user's session. }
    if (PreviousAppDir() = '') or (ServiceExe() <> '') then
    begin
      Result := '"Tray only" cannot be installed by a setup that runs as SYSTEM (a deployment tool such as Intune or ' +
        'SCCM): StreamZH would run as SYSTEM, and the tray icon belongs to the user who signs in. Use /TASKS="service", ' +
        'or run the setup as administrator in the user''s session.';
      Exit;
    end;
    { Upgrade of a tray-only installation: replace the files; the user's tray icon starts StreamZH at the next sign-in. }
    FilesOnly := True;
    Log('Tray only, setup as SYSTEM: only the files are replaced');
  end;
  Result := CheckProgramFolder();
  if Result <> '' then
    Exit;

  PreviousExe := ServiceExe();
  Exec(PowerShellExe(), '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' +
    'Stop-Service -Name StreamZH -Force -ErrorAction SilentlyContinue; ' +
    '$p = Get-Process -Name StreamZH, StreamZH.Tray -ErrorAction SilentlyContinue; ' +
    'if ($p) { $p | Stop-Process -Force -ErrorAction SilentlyContinue; $p | Wait-Process -Timeout 30 -ErrorAction SilentlyContinue }"',
    '', SW_HIDE, ewWaitUntilTerminated, Code);
  Log('Stopped StreamZH (exit code ' + IntToStr(Code) + ')');
end;

{ Runs tools\<Script> with the arguments; its output goes into the setup log. False when it failed. }
function RunScript(Script, Arguments: string; var Output: string): Boolean;
var
  LogFile: string;
  Lines: TArrayOfString;
  Code, I: Integer;
begin
  LogFile := ExpandConstant('{tmp}\') + Script + '.log';
  Log('Running ' + Script + ' ' + Arguments);
  Result := Exec(PowerShellExe(), '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "& ' +
    PsQuote(ExpandConstant('{app}\tools\') + Script) + ' ' + Arguments + ' *>&1 | Out-File -FilePath ' +
    PsQuote(LogFile) + ' -Encoding utf8 -Width 400; exit $LASTEXITCODE"', '', SW_HIDE, ewWaitUntilTerminated, Code)
    and (Code = 0);
  Output := '';
  if LoadStringsFromFile(LogFile, Lines) then
    for I := 0 to GetArrayLength(Lines) - 1 do
    begin
      Log(Lines[I]);
      if I >= GetArrayLength(Lines) - 12 then
        Output := Output + Lines[I] + #13#10;
    end;
  Log(Script + ' exit code ' + IntToStr(Code));
end;

{ The folders for the service in a new file in a new folder with a unique name in the setup's own temporary folder
  (the tmp constant), one per line: Install-Service.ps1 -FoldersFile reads them, so no folder name is parsed as PowerShell code
  (SV4 X-04). '' when there are none. }
function WriteFoldersFile(Folders: TArrayOfString): string;
var
  Dir: string;
begin
  Result := '';
  if GetArrayLength(Folders) = 0 then
    Exit;
  Dir := GenerateUniqueName(ExpandConstant('{tmp}'), '.dir');
  if not CreateDir(Dir) then
    RaiseException('Could not create ' + Dir);
  Result := AddBackslash(Dir) + 'folders.txt';
  if not SaveStringsToUTF8File(Result, Folders, False) then
    RaiseException('Could not write ' + Result);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Arguments, Script, Output, FoldersFile: string;
begin
  if CurStep <> ssPostInstall then
    Exit;
  if FilesOnly then
  begin
    Log('Files replaced; Install-Tray.ps1 not run (setup as SYSTEM)');
    Exit;
  end;

  Arguments := '-InstallDir ' + PsQuote(ExpandConstant('{app}')) + ' -NonInteractive';
  if WizardIsTaskSelected('firewall') then
    Arguments := Arguments + ' -Firewall';
  if WizardIsTaskSelected('cfa') then
    Arguments := Arguments + ' -AllowControlledFolderAccess';

  if IsTrayOnly() then
  begin
    Script := 'Install-Tray.ps1';
    { The [Run] section starts the tray icon as the signed-in user. }
    Arguments := Arguments + ' -NoStart';
  end
  else
  begin
    Script := 'Install-Service.ps1';
    FoldersFile := WriteFoldersFile(SelectedFolders());
    if FoldersFile <> '' then
      Arguments := Arguments + ' -FoldersFile ' + PsQuote(FoldersFile);
  end;

  WizardForm.StatusLabel.Caption := 'Setting up StreamZH (' + Script + ')...';
  ScriptFailed := not RunScript(Script, Arguments, Output);
  if FoldersFile <> '' then
    DelTree(ExtractFileDir(FoldersFile), True, True, True);
  if ScriptFailed then
  begin
    if not WizardSilent() then
      MsgBox('StreamZH was installed, but ' + Script + ' did not finish:' + #13#10#13#10 + Output + #13#10 +
        'Run it again in a PowerShell as administrator:' + #13#10 + '& ''' + ExpandConstant('{app}\tools\') +
        Script + '''', mbError, MB_OK);
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
var
  Text: string;
begin
  if CurPageID <> wpFinished then
    Exit;
  if IsTrayOnly() then
    Text := 'StreamZH starts with the tray icon when you sign in.'
  else
    Text := 'StreamZH runs as a Windows service and starts with Windows.';
  Text := Text + ' Open it at {#WebUi} and create your account. Your data is in ' +
    ExpandConstant('{commonappdata}\StreamZH') + '.';
  if (PreviousExe <> '') and (CompareText(ExtractFileDir(PreviousExe), ExpandConstant('{app}')) <> 0) then
    Text := Text + #13#10#13#10 + 'The service now runs from ' + ExpandConstant('{app}') + '. The old program folder ' +
      ExtractFileDir(PreviousExe) + ' is no longer used; you can delete it (your data is not in it).';
  if ScriptFailed then
    Text := Text + #13#10#13#10 + 'Setting up the service or tray icon did not finish; see the message before.';
  WizardForm.FinishedLabel.Caption := Text;
  WizardForm.FinishedLabel.AdjustHeight();
end;

{ ---- Uninstall ---- }

{ /DELETEDATA=1 (or /DELETEDATA) on the uninstaller's command line. }
function DeleteDataRequested(): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount() do
    if (CompareText(ParamStr(I), '/DELETEDATA=1') = 0) or (CompareText(ParamStr(I), '/DELETEDATA') = 0) then
      Result := True;
end;

function InitializeUninstall(): Boolean;
var
  Form: TSetupForm;
  Info: TNewStaticText;
  Check: TNewCheckBox;
  OkButton, CancelButton: TNewButton;
begin
  Result := True;
  DeleteData := DeleteDataRequested();
  Log('Command line: ' + GetCmdTail() + '; delete data: ' + IntToStr(Ord(DeleteData)));
  if UninstallSilent() then
    Exit;

  Form := CreateCustomForm(ScaleX(420), ScaleY(150), False, False);
  try
    Form.Caption := 'Uninstall StreamZH';

    Info := TNewStaticText.Create(Form);
    Info.Parent := Form;
    Info.Left := ScaleX(12);
    Info.Top := ScaleY(12);
    Info.Width := Form.ClientWidth - ScaleX(24);
    Info.WordWrap := True;
    Info.Caption := 'StreamZH, its service and its tray icon will be removed. Your data (database, settings, logs, ' +
      'backups) in ' + ExpandConstant('{commonappdata}\StreamZH') + ' stays unless you tick the box.';

    Check := TNewCheckBox.Create(Form);
    Check.Parent := Form;
    Check.Left := ScaleX(12);
    Check.Top := ScaleY(70);
    Check.Width := Form.ClientWidth - ScaleX(24);
    Check.Caption := 'Also delete data';
    Check.Checked := DeleteData;

    OkButton := TNewButton.Create(Form);
    OkButton.Parent := Form;
    OkButton.Caption := 'Uninstall';
    OkButton.ModalResult := mrOk;
    OkButton.Default := True;
    OkButton.Width := ScaleX(90);
    OkButton.Height := ScaleY(25);
    OkButton.Top := Form.ClientHeight - OkButton.Height - ScaleY(12);
    OkButton.Left := Form.ClientWidth - ScaleX(2 * 90 + 12 + 8);

    CancelButton := TNewButton.Create(Form);
    CancelButton.Parent := Form;
    CancelButton.Caption := 'Cancel';
    CancelButton.ModalResult := mrCancel;
    CancelButton.Cancel := True;
    CancelButton.Width := ScaleX(90);
    CancelButton.Height := ScaleY(25);
    CancelButton.Top := OkButton.Top;
    CancelButton.Left := Form.ClientWidth - ScaleX(90 + 12);

    Form.ActiveControl := OkButton;
    Result := Form.ShowModal() = mrOk;
    DeleteData := Check.Checked;
  finally
    Form.Free();
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Code: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    { Removes the service, its firewall rule and the tray's "Start with Windows" entry; keeps the data. }
    Exec(PowerShellExe(), '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
      ExpandConstant('{app}\tools\Uninstall-Service.ps1') + '" -InstallDir "' + ExpandConstant('{app}') +
      '" -NonInteractive', '', SW_HIDE, ewWaitUntilTerminated, Code);
    Log('Uninstall-Service.ps1 exit code ' + IntToStr(Code));
    { A StreamZH started by the tray (no service) and every tray icon. }
    Exec(PowerShellExe(), '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' +
      '$p = Get-Process -Name StreamZH, StreamZH.Tray -ErrorAction SilentlyContinue; ' +
      'if ($p) { $p | Stop-Process -Force -ErrorAction SilentlyContinue; $p | Wait-Process -Timeout 30 -ErrorAction SilentlyContinue }"',
      '', SW_HIDE, ewWaitUntilTerminated, Code);
    { Nothing runs any more that holds the database; the data goes only when the user asked for it. DelTree does not
      follow junctions or symbolic links (the service account or the user could have made one in the data folder): it
      deletes the link itself and leaves its target alone (SV4 X-11; Inno Setup 6.7.0, IsDirectoryAndNotReparsePoint). }
    if DeleteData then
    begin
      if DelTree(ExpandConstant('{commonappdata}\StreamZH'), True, True, True) then
        Log('Deleted the data folder ' + ExpandConstant('{commonappdata}\StreamZH'))
      else
        Log('Could not delete the data folder ' + ExpandConstant('{commonappdata}\StreamZH'));
    end;
  end;
end;
