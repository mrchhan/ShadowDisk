; Inno Setup script for ShadowDisk — produces a normal Windows installer (ShadowDisk-Setup-x.y.z.exe).
; Build with:  iscc /DAppVersion=0.1.0 installer\ShadowDisk.iss   (after running dotnet publish, see build\build-installer.ps1)

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#define AppName "ShadowDisk"
#define AppExe "ShadowDisk.exe"
#define AppPublisher "AIShadow"
#define AppUrl "https://github.com/mrchhan/ShadowDisk"

[Setup]
AppId={{69A72C95-C12B-455D-9419-1F82055DD39B}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Lets people install just for themselves without an admin prompt, or for everyone.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 version 1809 or later
MinVersion=10.0.17763
LicenseFile=..\LICENSE
InfoBeforeFile=before-you-install.txt
OutputDir=..\artifacts\installer
OutputBaseFilename=ShadowDisk-Setup-{#AppVersion}
SetupIconFile=..\src\ShadowDisk.App\Assets\shadowdisk.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autoprograms}\{#AppName} (demo disks)"; Filename: "{app}\{#AppExe}"; Parameters: "--demo"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
