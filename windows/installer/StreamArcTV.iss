; Inno Setup script for Stream Arc TV (Windows). Built by .github/workflows/windows.yml with
;   ISCC.exe /DAppVersion=<version> /DSourceDir=<publish folder> /DOutputDir=<folder> StreamArcTV.iss
; The result is Stream-Arc-TV-Setup-<version>.exe: a normal Windows installer with Start menu and
; desktop shortcuts, an entry in Apps & features, and an uninstaller. Settings, downloads and logs
; live under %LocalAppData%\StreamArcTV and are left alone by the uninstaller.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\out\StreamArcTV"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\out"
#endif

#define AppName "Stream Arc TV"
#define AppExe "StreamArcTV.exe"
#define AppPublisher "Computer Garage"

[Setup]
; Fixed id so every version upgrades the same installation.
AppId={{6F1E1B2C-3D4A-4C5B-9E6F-0A1B2C3D4E5F}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Installs for the current user (no administrator prompt) by default, so the app can update
; itself without elevation; the user may still choose "all users" from the prompt.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=Stream-Arc-TV-Setup-{#AppVersion}
SetupIconFile=..\StreamArcTV\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
; Closes a running Stream Arc TV before files are replaced and starts it again afterwards.
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=no
ChangesAssociations=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; Offered at the end of an interactive install and done automatically by a silent in-app update.
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall

[UninstallDelete]
; Files the in-app updater may have left behind.
Type: files; Name: "{app}\*.old"

[Code]
// Removes the scheduled recording tasks the app registers ("StreamArcTV Recording <id>"), so
// nothing tries to start a program that is gone. Downloads and settings are kept.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    Exec('powershell.exe',
      '-NoProfile -NonInteractive -Command "Get-ScheduledTask -TaskName ''StreamArcTV Recording *'' -ErrorAction SilentlyContinue | Unregister-ScheduledTask -Confirm:$false"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;
