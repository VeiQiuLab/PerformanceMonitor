#define MyAppName "Performance Monitor"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Performance Monitor contributors"
#define MyAppExeName "PerformanceMonitor.exe"
#define PawnIoVersion "2.1.0.0"
#define PawnIoInstallerSha256 "A3A46226C5E2824F4CDD42BE0EECBABFC672C86F7889710F5AB1E6AD385B47A0"

[Setup]
AppId={{59561B17-DAC5-4192-8B7C-3F69E0AA4B00}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppCopyright=Copyright (c) 2026 Performance Monitor contributors
AppReadmeFile={app}\README.md
DefaultDirName={autopf}\Performance Monitor
DefaultGroupName=Performance Monitor
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
OutputDir=..\release
OutputBaseFilename=PerformanceMonitor-Setup-v1.0.0
SetupIconFile=..\assets\PerformanceMonitor.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
VersionInfoVersion=1.0.0.0
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=Self-contained Windows installer for {#MyAppName}
VersionInfoCopyright=Copyright (c) 2026 Performance Monitor contributors
LicenseFile=..\LICENSE

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "dependencies\PawnIO_setup.exe"; Flags: dontcopy

[Icons]
Name: "{autoprograms}\Performance Monitor"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\Performance Monitor"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Performance Monitor"; Flags: nowait postinstall skipifsilent; Check: CanLaunchAfterInstall

[UninstallRun]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--request-running-exit"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "RequestRunningExit"
Filename: "{app}\{#MyAppExeName}"; Parameters: "--remove-startup-task"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "RemoveStartupTask"

[Code]
var
  PawnIoRestartRequired: Boolean;

function TryGetPawnIoVersion(var Version: String): Boolean;
var
  Key: String;
begin
  Key := 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO';
  Result :=
    RegQueryStringValue(HKLM64, Key, 'DisplayVersion', Version) or
    RegQueryStringValue(HKLM32, Key, 'DisplayVersion', Version);
end;

function IsCompatiblePawnIoInstalled: Boolean;
var
  Version: String;
begin
  Result := TryGetPawnIoVersion(Version) and (Pos('2.', Version) = 1);
  if Result then
    Log('Compatible PawnIO already installed: ' + Version)
  else if Version <> '' then
    Log('PawnIO version is not compatible with this package: ' + Version)
  else
    Log('PawnIO is not installed.');
end;

procedure InstallPawnIoIfNeeded;
var
  InstallerPath: String;
  ActualHash: String;
  InstalledVersion: String;
  ResultCode: Integer;
begin
  if IsCompatiblePawnIoInstalled then
    Exit;

  ExtractTemporaryFile('PawnIO_setup.exe');
  InstallerPath := ExpandConstant('{tmp}\PawnIO_setup.exe');
  ActualHash := Uppercase(GetSHA256OfFile(InstallerPath));
  if ActualHash <> '{#PawnIoInstallerSha256}' then
    RaiseException('The bundled official PawnIO installer failed its SHA-256 integrity check.');

  Log('Installing official PawnIO {#PawnIoVersion}.');
  if not Exec(InstallerPath, '-install -silent', '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode) then
    RaiseException('The official PawnIO installer could not be started.');

  if (ResultCode <> 0) and (ResultCode <> 3010) then
    RaiseException(Format('The official PawnIO installer failed with exit code %d.', [ResultCode]));

  PawnIoRestartRequired := ResultCode = 3010;
  if not TryGetPawnIoVersion(InstalledVersion) or (Pos('2.', InstalledVersion) <> 1) then
    RaiseException('PawnIO installation completed without a compatible registered version.');

  Log('PawnIO installation completed: ' + InstalledVersion);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    InstallPawnIoIfNeeded;
end;

function NeedRestart: Boolean;
begin
  Result := PawnIoRestartRequired;
end;

function CanLaunchAfterInstall: Boolean;
begin
  Result := not PawnIoRestartRequired;
end;
