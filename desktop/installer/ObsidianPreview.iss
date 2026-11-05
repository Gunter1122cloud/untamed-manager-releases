#define MyAppName "Untamed Kingdom — Obsidian Command PREVIEW"
#define MyAppVersion "0.1.0"
#ifndef BuildDir
  #define BuildDir "..\publish"
#endif

[Setup]
AppId={{84C6EE39-7FE4-4E40-BC4C-DBA371CE5438}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=Untamed Kingdom
DefaultDirName={localappdata}\Programs\UntamedObsidianPreview
DefaultGroupName=Untamed Kingdom - Obsidian Preview
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
OutputDir=..\..\desktop-artifacts
OutputBaseFilename=Untamed_Obsidian_Desktop_Preview_Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\UntamedDesktop.exe
SetupIconFile=..\..\wpf\Assets\UntamedManager.ico

[Files]
Source: "{#BuildDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Untamed Obsidian PREVIEW"; Filename: "{app}\UntamedDesktop.exe"

[Run]
Filename: "{app}\UntamedDesktop.exe"; Description: "Untamed Obsidian Desktop megnyitása"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}\ui"

; Test-only separate directory and AppId. No service installation,
; no production server changes, no Store/INI migrations.
