#ifndef PackageDirectory
  #error PackageDirectory must point to the published Windows package.
#endif
#ifndef InstallerOutputDirectory
  #error InstallerOutputDirectory must be explicit.
#endif
#ifndef RuntimePath
  #error RuntimePath must point to the verified Microsoft runtime installer.
#endif
#define AppVersion GetStringFileInfo(AddBackslash(PackageDirectory) + "ScreenLingo.exe", "ProductVersion")

[Setup]
AppId=ScreenLingo
AppName=ScreenLingo
AppVersion={#AppVersion}
AppPublisher=itourboy-OG
AppPublisherURL=https://github.com/itourboy-OG/ScreenLingo
AppSupportURL=https://github.com/itourboy-OG/ScreenLingo/issues
AppUpdatesURL=https://github.com/itourboy-OG/ScreenLingo/releases/latest
DefaultDirName={localappdata}\Programs\ScreenLingo
DefaultGroupName=ScreenLingo
PrivilegesRequired=lowest
SetupArchitecture=x64
MinVersion=10.0.19041
DisableWelcomePage=no
DisableDirPage=no
DisableProgramGroupPage=yes
AllowRootDirectory=no
AllowNetworkDrive=no
AllowUNCPath=no
UsePreviousAppDir=yes
CloseApplications=yes
CloseApplicationsFilter=ScreenLingo.exe
RestartApplications=no
SetupMutex=ScreenLingoSetup
SetupIconFile=..\app.ico
UninstallDisplayIcon={app}\ScreenLingo.exe
UninstallDisplayName=ScreenLingo
WizardStyle=modern dark windows11 includetitlebar hidebevels
WizardBackColor=#211e1a
WizardImageFile=..\assets\installer-welcome.png
WizardSmallImageFile=..\assets\logo.png
WizardImageBackColor=#211e1a
WizardSmallImageBackColor=#211e1a
WizardSizePercent=110
OutputDir={#InstallerOutputDirectory}
OutputBaseFilename=ScreenLingo-{#AppVersion}-Setup-x64
Compression=lzma2
SolidCompression=yes
SetupLogging=yes
VersionInfoDescription=ScreenLingo Setup
VersionInfoProductName=ScreenLingo
VersionInfoProductVersion={#AppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#RuntimePath}"; DestName: "vc_redist.x64.exe"; Flags: dontcopy
Source: "{#PackageDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "Install-Update.ps1"

[Icons]
Name: "{autoprograms}\ScreenLingo"; Filename: "{app}\ScreenLingo.exe"; WorkingDir: "{app}"; Check: not WizardNoIcons
Name: "{autodesktop}\ScreenLingo"; Filename: "{app}\ScreenLingo.exe"; WorkingDir: "{app}"; Tasks: desktopicon; Check: not WizardNoIcons

[Run]
Filename: "{app}\ScreenLingo.exe"; Description: "{cm:LaunchProgram,ScreenLingo}"; Flags: nowait postinstall skipifsilent

[Messages]
english.WelcomeLabel2=Translate game menus and everyday applications without reaching for your phone.%n%nSetup will install ScreenLingo and its supporting files in the folder you choose.
spanish.WelcomeLabel2=Traduce los menús de juegos y otras aplicaciones sin usar tu teléfono.%n%nEl instalador copiará ScreenLingo y sus archivos en la carpeta que elijas.
BeveledLabel=ScreenLingo {#AppVersion}

[Code]
function RuntimeNeeded: Boolean;
var
  VersionText: String;
  InstalledVersion: Int64;
begin
  Result := True;
  if not RegQueryStringValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Version', VersionText) then
    Exit;
  if Copy(VersionText, 1, 1) = 'v' then
    Delete(VersionText, 1, 1);
  if StrToVersion(VersionText, InstalledVersion) then
    Result := ComparePackedVersion(InstalledVersion, PackVersionComponents(14, 44, 35211, 0)) < 0;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  if not RuntimeNeeded then
    Exit;
  ExtractTemporaryFile('vc_redist.x64.exe');
  if not ShellExec('runas', ExpandConstant('{tmp}\vc_redist.x64.exe'), '/install /passive /norestart', '', SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode) then
  begin
    Result := 'The Microsoft Visual C++ runtime could not start. Allow its Windows permission prompt, then try Setup again. Windows error: ' + IntToStr(ResultCode);
    Exit;
  end;
  if ResultCode = 3010 then
  begin
    NeedsRestart := True;
    Result := 'The Microsoft Visual C++ runtime requires a Windows restart. Restart Windows, then run ScreenLingo Setup again.';
  end
  else if ResultCode <> 0 then
    Result := 'The Microsoft Visual C++ runtime installation failed. Exit code: ' + IntToStr(ResultCode)
  else if RuntimeNeeded then
    Result := 'The Microsoft Visual C++ runtime installation finished, but the required x64 runtime version is still missing. Repair the runtime, then try Setup again.';
end;
