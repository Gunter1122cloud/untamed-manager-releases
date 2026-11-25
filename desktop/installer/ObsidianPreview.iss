#define MyAppName "Untamed Kingdom — Obsidian Command PREVIEW"
// The version is supplied from desktop/Version.props by CI. No local duplicate.
#ifndef ObsidianVersion
  #error "ObsidianVersion required: use /DObsidianVersion from desktop/Version.props"
#endif
#define MyAppVersion ObsidianVersion
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
; Operators testing on an actual ASA host can use real manual bridges
; without triggering previously persisted automatic schedules on launch.
Name: "{group}\Obsidian MANUÁLIS TESZT"; Filename: "{app}\UntamedDesktop.exe"; Parameters: "--manual-test"

[Run]
Filename: "{app}\UntamedDesktop.exe"; Parameters: "--manual-test"; Description: "Obsidian indítása MANUÁLIS TESZT módban"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}\ui"

; Test-only separate directory and AppId. No service installation,
; no production server changes, no Store/INI migrations.

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  CurrentCommand, OwnCommand: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    OwnCommand := '"' + ExpandConstant('{app}\UntamedDesktop.exe') + '"';
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run',
      'ObsidianCommandLogin', CurrentCommand) then
    begin
      { Only remove this installation's exact opt-in. Never clear a different portable/installed copy. }
      if CompareText(CurrentCommand, OwnCommand) = 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run',
          'ObsidianCommandLogin');
    end;
  end;
end;
