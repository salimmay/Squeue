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
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\dist\Squeue\Squeue.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Squeue"; Filename: "{app}\Squeue.exe"
Name: "{autodesktop}\Squeue"; Filename: "{app}\Squeue.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Squeue.exe"; Description: "{cm:LaunchProgram,Squeue}"; Flags: nowait postinstall skipifsilent
