#define AppName "Untamed Manager"
#define AppVersion "1.2.0"
#define AppExeName "UntamedManager.exe"
[Setup]
AppId={{F32A414B-35F7-4253-97B1-430DCA9A361E}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} v{#AppVersion}
AppPublisher=Untamed Kingdom
DefaultDirName={localappdata}\Programs\Untamed Manager
DefaultGroupName=Untamed Manager
UninstallDisplayIcon={app}\{#AppExeName}
OutputDir=..\..\dist
OutputBaseFilename=UntamedManager-1.2.0-SETUP
SetupIconFile=..\Assets\UntamedManager.ico
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=lowest
WizardStyle=modern
DisableDirPage=yes
DisableProgramGroupPage=yes
CloseApplications=yes
RestartApplications=no
ChangesAssociations=no
SetupLogging=yes
[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
[Files]
Source: "..\..\dist\UntamedManager.exe"; DestDir: "{app}"; Flags: ignoreversion
[Icons]
Name: "{userprograms}\Untamed Kingdom Manager"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Untamed Manager indítása"; Flags: nowait postinstall skipifsilent
