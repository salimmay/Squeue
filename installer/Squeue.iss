; Inno Setup script for Squeue. Build with: ./build.ps1 -Installer
; Installs per user (no administrator prompt) from the published exe in dist\Squeue.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

[Setup]
; AppId identifies the app across versions for upgrades and uninstall. Never change it.
AppId={{6E2B6C1D-6B0A-4F0C-9B43-5C0D2A7C3F51}
AppName=Squeue
AppVersion={#AppVersion}
AppPublisher=Salim May
AppPublisherURL=https://github.com/salimmay/Squeue
AppSupportURL=https://github.com/salimmay/Squeue/issues
DefaultDirName={autopf}\Squeue
DefaultGroupName=Squeue
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\dist
OutputBaseFilename=SqueueSetup
SetupIconFile=..\src\Squeue.App\Assets\squeue.ico
UninstallDisplayIcon={app}\Squeue.exe
UninstallDisplayName=Squeue
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "explorermenu"; Description: "Add ""Copy with Squeue"" and ""Paste with Squeue"" to the right-click menu in Explorer"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\dist\Squeue\Squeue.exe"; DestDir: "{app}"; Flags: ignoreversion

[Registry]
; Explorer right-click menu. Per user (HKCU), so no administrator prompt. On Windows 11 these appear
; under "Show more options"; the top-level menu needs a signed app package.
; Files and folders: "Copy with Squeue..." asks where to copy them.
Root: HKCU; Subkey: "Software\Classes\*\shell\Squeue.Copy"; ValueType: string; ValueName: ""; ValueData: "Copy with Squeue..."; Flags: uninsdeletekey; Tasks: explorermenu
Root: HKCU; Subkey: "Software\Classes\*\shell\Squeue.Copy"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\Squeue.exe"",0"; Tasks: explorermenu
Root: HKCU; Subkey: "Software\Classes\*\shell\Squeue.Copy"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Player"; Tasks: explorermenu
Root: HKCU; Subkey: "Software\Classes\*\shell\Squeue.Copy\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Squeue.exe"" ""%1"""; Tasks: explorermenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Squeue.Copy"; ValueType: string; ValueName: ""; ValueData: "Copy with Squeue..."; Flags: uninsdeletekey; Tasks: explorermenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Squeue.Copy"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\Squeue.exe"",0"; Tasks: explorermenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Squeue.Copy"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Player"; Tasks: explorermenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Squeue.Copy\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Squeue.exe"" ""%1"""; Tasks: explorermenu
; A whole drive, such as a memory card.
Root: HKCU; Subkey: "Software\Classes\Drive\shell\Squeue.Copy"; ValueType: string; ValueName: ""; ValueData: "Copy with Squeue..."; Flags: uninsdeletekey; Tasks: explorermenu
Root: HKCU; Subkey: "Software\Classes\Drive\shell\Squeue.Copy"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\Squeue.exe"",0"; Tasks: explorermenu
Root: HKCU; Subkey: "Software\Classes\Drive\shell\Squeue.Copy\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Squeue.exe"" ""%1"""; Tasks: explorermenu
; Folders, drives and the empty space inside a folder: "Paste with Squeue" copies what was copied with Ctrl+C.
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Squeue.Paste"; ValueType: string; ValueName: ""; ValueData: "Paste with Squeue"; Flags: uninsdeletekey; Tasks: explorermenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Squeue.Paste"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\Squeue.exe"",0"; Tasks: explorermenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Squeue.Paste\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Squeue.exe"" --paste ""%1"""; Tasks: explorermenu
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\Squeue.Paste"; ValueType: string; ValueName: ""; ValueData: "Paste with Squeue"; Flags: uninsdeletekey; Tasks: explorermenu
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\Squeue.Paste"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\Squeue.exe"",0"; Tasks: explorermenu
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\Squeue.Paste\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Squeue.exe"" --paste ""%V"""; Tasks: explorermenu
Root: HKCU; Subkey: "Software\Classes\Drive\shell\Squeue.Paste"; ValueType: string; ValueName: ""; ValueData: "Paste with Squeue"; Flags: uninsdeletekey; Tasks: explorermenu
Root: HKCU; Subkey: "Software\Classes\Drive\shell\Squeue.Paste"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\Squeue.exe"",0"; Tasks: explorermenu
Root: HKCU; Subkey: "Software\Classes\Drive\shell\Squeue.Paste\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Squeue.exe"" --paste ""%1"""; Tasks: explorermenu

[Icons]
Name: "{autoprograms}\Squeue"; Filename: "{app}\Squeue.exe"
Name: "{autodesktop}\Squeue"; Filename: "{app}\Squeue.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Squeue.exe"; Description: "{cm:LaunchProgram,Squeue}"; Flags: nowait postinstall skipifsilent

