#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{CCBE76C0-65D1-45CB-9AD5-6739497BDE26}
AppName=IP 国旗监视器
AppVersion={#AppVersion}
AppVerName=IP 国旗监视器 {#AppVersion}
AppPublisher=Anna-SAP
AppPublisherURL=https://github.com/Anna-SAP/IPCountryWatcher
AppSupportURL=https://github.com/Anna-SAP/IPCountryWatcher/issues
AppUpdatesURL=https://github.com/Anna-SAP/IPCountryWatcher/releases/latest
DefaultDirName={localappdata}\Programs\IPCountryWatcher
DefaultGroupName=IP 国旗监视器
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\dist
OutputBaseFilename=IPCountryWatcher-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\IPCountryWatcher.exe
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#AppVersion}.0
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "..\bin\IPCountryWatcher.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\bin\IPCountryWatcher.exe.config"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\bin\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\bin\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

Source: "..\bin\IPCountryWatcher.Probe.*.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\bin\IPCountryWatcher.ProbeHost.*.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\IP 国旗监视器"; Filename: "{app}\IPCountryWatcher.exe"
Name: "{autodesktop}\IP 国旗监视器"; Filename: "{app}\IPCountryWatcher.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\IPCountryWatcher.exe"; Description: "Launch IP Country Watcher"; Flags: nowait postinstall skipifsilent unchecked

[Code]
function InitializeSetup(): Boolean;
var
  Release: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release);
  if Result then
    Result := Release >= 528040;
  if not Result then
    MsgBox('Microsoft .NET Framework 4.8 or later is required. Windows 11 includes it.', mbError, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  StartupValue: String;
begin
  if CurUninstallStep = usPostUninstall then
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'IPCountryWatcher', StartupValue) then
      if CompareText(StartupValue, '"' + ExpandConstant('{app}\IPCountryWatcher.exe') + '"') = 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'IPCountryWatcher');
end;
