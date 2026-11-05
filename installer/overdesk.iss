; Windows installer for OverDesk (Inno Setup 6). One Setup.exe carries both builds and installs the one that fits the PC.
; Built by .github/workflows/release.yml after scripts/package-windows.ps1 has produced dist\OverDesk-win-x64 and dist\OverDesk-win-arm64.
; Local use: iscc /DAppVersion=0.2.0 installer\overdesk.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{6B1E4C1A-8F0D-4F7B-9B8C-0D5E2A7C4F11}
AppName=OverDesk
AppVersion={#AppVersion}
AppVerName=OverDesk {#AppVersion}
AppPublisher=OverDesk contributors
AppPublisherURL=https://github.com/ersanulker08/OverDesk
AppSupportURL=https://github.com/ersanulker08/OverDesk/issues
DefaultDirName={autopf}\OverDesk
DefaultGroupName=OverDesk
DisableProgramGroupPage=yes
; Per-user install by default (no admin prompt); the user can still choose "all users".
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible or arm64
ArchitecturesInstallIn64BitMode=x64compatible or arm64
MinVersion=10.0.19041
SetupIconFile=..\assets\overdesk.ico
UninstallDisplayIcon={app}\Overdesk.App.exe
OutputDir=..\dist
OutputBaseFilename=OverDesk-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\dist\OverDesk-win-arm64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: IsArm64
Source: "..\dist\OverDesk-win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: not IsArm64

[Icons]
Name: "{group}\OverDesk"; Filename: "{app}\Overdesk.App.exe"
Name: "{autodesktop}\OverDesk"; Filename: "{app}\Overdesk.App.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Overdesk.App.exe"; Description: "{cm:LaunchProgram,OverDesk}"; Flags: nowait postinstall skipifsilent

[Code]
// OverDesk's web tabs need the Evergreen WebView2 runtime (Windows 11 has it; Windows 10 may not).
var
  DownloadPage: TDownloadWizardPage;

function WebView2Installed: Boolean;
var
  Version: String;
begin
  Result :=
    (RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) or
     RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) or
     RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version))
    and (Version <> '') and (Version <> '0.0.0.0');
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpReady) and not WebView2Installed then
  begin
    DownloadPage.Clear;
    DownloadPage.Add('https://go.microsoft.com/fwlink/p/?LinkId=2124703', 'MicrosoftEdgeWebview2Setup.exe', '');
    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
      except
        // Offline: install OverDesk anyway; web tabs show a hint until the runtime is installed.
        if DownloadPage.AbortedByUser then Result := False;
      end;
    finally
      DownloadPage.Hide;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  Installer: String;
begin
  Installer := ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe');
  if (CurStep = ssPostInstall) and FileExists(Installer) and not WebView2Installed then
    Exec(Installer, '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;
