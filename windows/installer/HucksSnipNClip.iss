#ifndef AppVersion
  #error AppVersion must be supplied by release.ps1
#endif
#ifndef SourceExe
  #error SourceExe must be supplied by release.ps1
#endif
#ifndef OutputDir
  #error OutputDir must be supplied by release.ps1
#endif
#ifndef SetupIcon
  #error SetupIcon must be supplied by release.ps1
#endif

#define AppName "Huck's Snip 'n' Clip"
#define AppExeName "HucksSnipNClip.exe"

[Setup]
AppId={{6D77CC8F-E224-49A2-A239-85985B059947}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Quintin Huckaby
AppPublisherURL=https://github.com/Huckletsplay/hucks-snip-n-clip
AppSupportURL=https://github.com/Huckletsplay/hucks-snip-n-clip/issues
AppUpdatesURL=https://github.com/Huckletsplay/hucks-snip-n-clip/releases
DefaultDirName={localappdata}\Programs\HucksSnipNClip
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=HucksSnipNClip-{#AppVersion}-windows-x64-setup
SetupIconFile={#SetupIcon}
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
AppMutex=Local\QSnipAndClip.SingleInstance
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany=Quintin Huckaby
VersionInfoDescription={#AppName} Installer
VersionInfoProductName={#AppName}

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; DestName: "{#AppExeName}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[InstallDelete]
Type: filesandordirs; Name: "{localappdata}\Programs\QSNC"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\QSNC"; Flags: deletekey

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
