; Installer für re admin (wird auf GitHub automatisch gebaut)
#ifndef AppVer
  #define AppVer "1.0"
#endif

[Setup]
AppId={{6F1B7C2E-3A5D-4E8B-9C1F-2D7A0E5B4C31}
AppName=re admin
AppVersion={#AppVer}
AppPublisher=Reinbacher solution
DefaultDirName={localappdata}\Programs\re admin
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=re-admin-Setup
SetupIconFile=ReAdmin\Admin.ico
UninstallDisplayIcon={app}\ReAdmin.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Languages]
Name: "de"; MessagesFile: "compiler:Languages\German.isl"

[Tasks]
Name: "desktopicon"; Description: "Verknüpfung auf dem Desktop anlegen"; GroupDescription: "Zusätzlich:"

[Files]
Source: "out\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\re admin"; Filename: "{app}\ReAdmin.exe"
Name: "{autodesktop}\re admin"; Filename: "{app}\ReAdmin.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\ReAdmin.exe"; Description: "re admin jetzt starten"; Flags: nowait postinstall skipifsilent
