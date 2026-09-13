; Inno Setup script for The Chocolate Rabbit (Windows). Built by .github/workflows/chocolate-rabbit.yml with
;   ISCC.exe /DAppVersion=<version> /DSourceDir=<publish folder> /DOutputDir=<folder> TheChocolateRabbit.iss
; The result is The-Chocolate-Rabbit-Setup-<version>.exe: a normal Windows installer with Start menu and
; desktop shortcuts, an entry in Apps & features, and an uninstaller. Settings, the local sign-up
; record and logs live under %LocalAppData%\TheChocolateRabbit and are left alone by the uninstaller.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\out\TheChocolateRabbit"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\out"
#endif

#define AppName "The Chocolate Rabbit"
#define AppExe "TheChocolateRabbit.exe"
#define AppPublisher "The Chocolate Rabbit"

[Setup]
; Fixed id so every version upgrades the same installation.
AppId={{7C2E5A90-4B1D-4F6E-8A3C-D19B7E0F2C55}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://thechocolaterabbit.ca
AppSupportURL=https://github.com/ComputerGarage1837/StreamArcTV/issues
AppUpdatesURL=https://github.com/ComputerGarage1837/StreamArcTV/releases
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
OutputBaseFilename=The-Chocolate-Rabbit-Setup-{#AppVersion}
SetupIconFile=..\TheChocolateRabbit\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
; Closes a running copy before files are replaced and starts it again afterwards.
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
