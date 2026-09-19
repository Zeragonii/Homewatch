#ifndef MyAppVersion
  #define MyAppVersion "0.1.5"
#endif

#define MyAppName "HomeWatch Agent"
#define MyAppPublisher "HomeWatch"
#define MyAppExeName "HomeWatchAgent.exe"

[Setup]
AppId={{D2A3AD9D-E63B-4AA3-8A05-1B51DE721C77}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\HomeWatch
DefaultGroupName=HomeWatch
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\..\out\installer
OutputBaseFilename=HomeWatch-Agent-Setup-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
RestartApplications=no

[Files]
Source: "..\..\out\agent\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\HomeWatch Agent"; Filename: "{app}\{#MyAppExeName}"

[Run]
Filename: "{cmd}"; Parameters: "/C taskkill /IM HomeWatchAgent.exe /F >NUL 2>&1"; Flags: runhidden; StatusMsg: "Stopping existing HomeWatch Agent..."
Filename: "{app}\{#MyAppExeName}"; Description: "Start HomeWatch Agent"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C taskkill /IM HomeWatchAgent.exe /F >NUL 2>&1"; Flags: runhidden; RunOnceId: "StopHomeWatchAgent"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  RunKey: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    RunKey := 'Software\Microsoft\Windows\CurrentVersion\Run';
    RegDeleteValue(HKEY_CURRENT_USER, RunKey, 'HomeWatchAgent');
  end;
end;
