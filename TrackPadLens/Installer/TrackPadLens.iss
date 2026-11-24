; TrackPadLens Windows x64 Setup Installer (Inno Setup 6)
; Builds TrackPadLens-Setup-x64.exe — standard per-user install (no admin required)
; with optional "Install for all users", Desktop shortcut, Windows auto-start,
; clean upgrade handling, and full uninstaller in Windows Apps & Features.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#ifndef PayloadDir
  #define PayloadDir "..\publish-installer"
#endif

#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

#define AppName "TrackPadLens"
#define AppPublisher "TrackPadLens"
#define AppExeName "TrackPadLens.exe"

[Setup]
AppId={{7B6F1E2A-4D3C-4B9E-8F12-3A5C9D7E1024}}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
AllowNoIcons=yes
DisableProgramGroupPage=auto
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=TrackPadLens-Setup-x64
SetupIconFile=..\Assets\TrackPadLens.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
AppMutex=TrackPadLens_SingleInstance_Mutex
CloseApplications=yes
CloseApplicationsFilter=*.exe
RestartApplications=no
MinVersion=10.0.19041
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription=TrackPadLens Setup Installer (Windows x64)

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "autostart"; Description: "Start TrackPadLens automatically when Windows starts (minimized to system tray)"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Comment: "Precision Touchpad Writing Tablet & Floating Lens"
Name: "{autoprograms}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Comment: "Precision Touchpad Writing Tablet & Floating Lens"; Tasks: desktopicon

[Registry]
; Matches TrackPadLens.Services.StartupService (HKCU\Software\Microsoft\Windows\CurrentVersion\Run\TrackPadLens)
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "TrackPadLens"; ValueData: """{app}\{#AppExeName}"" --tray"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  SettingsDir: string;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    { Always remove per-user startup entry even if enabled later inside TrackPadLens Settings/Tray }
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'TrackPadLens');

    SettingsDir := ExpandConstant('{userappdata}\TrackPadLens');
    if DirExists(SettingsDir) and (not UninstallSilent()) then
    begin
      if MsgBox('Do you also want to remove your saved TrackPadLens settings and calibration profile?',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      begin
        DelTree(SettingsDir, True, True, True);
      end;
    end;
  end;
end;
