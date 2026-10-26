#ifndef MyAppVersion
  #define MyAppVersion "0.4.2"
#endif
#define MyAppName "HomeWatch Agent"
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
Source: "..\..\out\agent\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Run]
Filename: "{sys}\sc.exe"; Parameters: "create HomeWatchUpdaterService binPath= ""{app}\HomeWatchService.exe"" start= auto"; Flags: runhidden waituntilterminated; Check: not ServiceInstalled
Filename: "{sys}\sc.exe"; Parameters: "start HomeWatchUpdaterService"; Flags: runhidden waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Description: "Launch HomeWatch Agent for this Windows account"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Registry]
; Every interactive Windows user launches their own agent on sign-in.
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "HomeWatchAgent"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue
; Remove older per-user autoruns for the account performing the upgrade.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "HomeWatchAgent"; Flags: deletevalue

[Tasks]
Name: "myappsdesktop"; Description: "Create a My Apps shortcut on the desktop"

[Icons]
Name: "{autoprograms}\HomeWatch Agent"; Filename: "{app}\{#MyAppExeName}"
Name: "{autoprograms}\HomeWatch My Apps"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--apps"
Name: "{commondesktop}\HomeWatch My Apps"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--apps"; Tasks: myappsdesktop

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop HomeWatchUpdaterService"; Flags: runhidden waituntilterminated; RunOnceId: "StopHomeWatchService"
Filename: "{sys}\sc.exe"; Parameters: "delete HomeWatchUpdaterService"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteHomeWatchService"
Filename: "{cmd}"; Parameters: "/C taskkill /IM HomeWatchAgent.exe /F >NUL 2>&1"; Flags: runhidden; RunOnceId: "StopHomeWatchAgent"

[Code]
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
