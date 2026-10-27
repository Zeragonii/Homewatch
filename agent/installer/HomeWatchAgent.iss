#ifndef MyAppVersion
  #define MyAppVersion "0.5.6"
#endif
#define MyAppName "HomeWatch Launcher"
#define MyAppPublisher "HomeWatch"
#define MyAppExeName "HomeWatchAgent.exe"

[Setup]
AppId={{D2A3AD9D-E63B-4AA3-8A05-1B51DE721C77}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\HomeWatch
DefaultGroupName=HomeWatch
DisableProgramGroupPage=yes
PrivilegesRequired=admin
OutputDir=..\..\out\installer
OutputBaseFilename=HomeWatch-Agent-Setup-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
; Required: ArchitecturesAllowed alone does NOT select native 64-bit install mode.
; Inno Setup 6 defaults to 32-bit mode otherwise, putting an x64 build in PF(x86).
ArchitecturesInstallIn64BitMode=x64compatible
; Never reuse a pre-64-bit installation directory from older Inno registry values.
UsePreviousAppDir=no
; HomeWatch uses the native Program Files directory for its protected updater.
DisableDirPage=yes
SetupIconFile=HomeWatch.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
RestartApplications=no

[Dirs]
; Shared device credentials are used by per-user UI agents. Access is necessary
; for the per-user app but this is NOT tamper-resistant against local users.
Name: "{commonappdata}\HomeWatch"; Permissions: users-modify
; Update staging is kept under Program Files to inherit protected system ACLs.
Name: "{app}\.updates"

[Files]
; Explicit files ensure the installer build fails if any required binary is missing.
Source: "..\..\out\agent\HomeWatchAgent.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\out\agent\HomeWatchService.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\out\agent\HomeWatchUpdater.exe"; DestDir: "{app}"; Flags: ignoreversion

[Run]
Filename: "{sys}\sc.exe"; Parameters: "create HomeWatchUpdaterService binPath= ""{app}\HomeWatchService.exe"" start= auto"; Flags: runhidden waituntilterminated; Check: not ServiceInstalled
; Repair older registrations even when the service already exists. The service
; may have been left configured for a previous installation directory.
Filename: "{sys}\sc.exe"; Parameters: "config HomeWatchUpdaterService binPath= ""{app}\HomeWatchService.exe"" start= auto"; Flags: runhidden waituntilterminated; Check: ServiceInstalled
Filename: "{sys}\sc.exe"; Parameters: "start HomeWatchUpdaterService"; Flags: runhidden waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Description: "Launch HomeWatch Launcher for this Windows account"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Registry]
; Every interactive Windows user launches their own agent on sign-in.
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "HomeWatchAgent"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue
; Remove older per-user autoruns for the account performing the upgrade.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "HomeWatchAgent"; Flags: deletevalue

[Tasks]
Name: "myappsdesktop"; Description: "Create a HomeWatch Launcher shortcut on the desktop"

[Icons]
Name: "{autoprograms}\HomeWatch Agent"; Filename: "{app}\{#MyAppExeName}"
Name: "{autoprograms}\HomeWatch Launcher"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--apps"
Name: "{commondesktop}\HomeWatch Launcher"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--apps"; Tasks: myappsdesktop

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop HomeWatchUpdaterService"; Flags: runhidden waituntilterminated; RunOnceId: "StopHomeWatchService"
Filename: "{sys}\sc.exe"; Parameters: "delete HomeWatchUpdaterService"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteHomeWatchService"
Filename: "{cmd}"; Parameters: "/C taskkill /IM HomeWatchAgent.exe /F >NUL 2>&1"; Flags: runhidden; RunOnceId: "StopHomeWatchAgent"

[Code]
// Upgrades from the old 32-bit-mode installer must remove the legacy install
// first. Installing over it would leave two uninstall entries and may break the
// registered service. The device identity lives in ProgramData and is retained.
function InitializeSetup(): Boolean;
var
  LegacyDir: String;
begin
  LegacyDir := ExpandConstant('{commonpf32}\HomeWatch');
  Result := not (FileExists(AddBackslash(LegacyDir) + 'HomeWatchAgent.exe') or
                FileExists(AddBackslash(LegacyDir) + 'HomeWatchService.exe'));
  if not Result then
    MsgBox('HomeWatch is still installed in Program Files (x86).' + #13#10 + #13#10 +
      'Uninstall the older HomeWatch Agent from Windows Installed Apps first, ' +
      'then run this installer again. Your device enrolment is kept in ' +
      'C:\ProgramData\HomeWatch\config.json (back this file up first).',
      mbCriticalError, MB_OK);
end;

function ServiceInstalled(): Boolean;
var ResultCode: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\sc.exe'), 'query HomeWatchUpdaterService', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;
procedure CurStepChanged(CurStep: TSetupStep);
var
  OldConfig, NewConfig: String;
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
  begin
    // Avoid attempting to overwrite a running Windows service binary.
    Exec(ExpandConstant('{sys}\sc.exe'), 'stop HomeWatchUpdaterService', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(2000);
  end;
  if CurStep = ssPostInstall then
  begin
    NewConfig := ExpandConstant('{commonappdata}\HomeWatch\config.json');
    OldConfig := ExpandConstant('{localappdata}\HomeWatch\config.json');
    if (not FileExists(NewConfig)) and FileExists(OldConfig) then
      FileCopy(OldConfig, NewConfig, False);
  end;
end;
