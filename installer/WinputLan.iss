#define AppName "Winput LAN"
#ifndef AppVersion
#define AppVersion "0.1.0"
#endif
#define AppPublisher "luingry"
#define AppExeName "WinputLan.exe"

[Setup]
AppId={{B5A6F11B-0D0F-4A09-9A2C-3AE5F13C3D23}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\Winput LAN
DefaultGroupName={#AppName}
OutputDir=..\artifacts\installer
OutputBaseFilename=WinputLan-{#AppVersion}-setup
SetupIconFile=..\assets\brand\winput-lan.ico
UninstallDisplayIcon={app}\winput-lan.ico
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
WizardStyle=modern
SignedUninstaller=no

[Files]
Source: "..\src\WinputLan\bin\Release\net48\WinputLan.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\src\WinputLan\bin\Release\net48\WinputLan.Core.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\src\WinputLan\bin\Release\net48\WinputLan.exe.config"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\assets\brand\winput-lan.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\assets\brand\icon-normalized.png"; DestDir: "{app}\assets\brand"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Winput LAN"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\winput-lan.ico"

[Run]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""Winput LAN (Private TCP)"" description=""Winput LAN secure peer input transport; Private profile only."" dir=in action=allow enable=yes profile=Private protocol=TCP localport=45900 program=""{app}\WinputLan.exe"""; Flags: runhidden waituntilterminated; StatusMsg: "Creating the explicit Private-profile firewall rule..."
Filename: "{app}\{#AppExeName}"; Description: "Abrir Winput LAN"; Flags: nowait postinstall skipifsilent runasoriginaluser
; OTA updates run silently; reopen the app for the signed-in user (not elevated) when they finish.
Filename: "{app}\{#AppExeName}"; Flags: nowait runasoriginaluser; Check: WizardSilent

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Winput LAN (Private TCP)"" program=""{app}\WinputLan.exe"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoveWinputLanFirewallRule"
