; Inno Setup script for Tapline for Windows. Built by .github/workflows/release-windows.yml:
;   iscc /DAppVersion=0.1.0 /DSourceDir=..\artifacts\publish /DOutputDir=..\artifacts\dist Tapline.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\dist"
#endif

[Setup]
AppId={{6E0C3F55-8C21-4D0B-9F35-7A54B7C5A3D1}
AppName=Tapline
AppVersion={#AppVersion}
AppVerName=Tapline {#AppVersion}
AppPublisher=Tapline contributors
AppPublisherURL=https://github.com/belugaming/tapline
AppSupportURL=https://github.com/belugaming/tapline/issues
LicenseFile=..\..\LICENSE
; Per-user install: no administrator rights needed.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\Tapline
DefaultGroupName=Tapline
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=Tapline-{#AppVersion}-win-x64-setup
SetupIconFile=..\src\Tapline\Assets\Tapline.ico
UninstallDisplayIcon={app}\Tapline.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\Tapline.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\Tapline"; Filename: "{app}\Tapline.exe"
Name: "{autodesktop}\Tapline"; Filename: "{app}\Tapline.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Tapline.exe"; Description: "{cm:LaunchProgram,Tapline}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Hands Equalizer APO back its previous configuration and removes the login item.
Filename: "{app}\Tapline.exe"; Parameters: "--uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "TaplineCleanup"
