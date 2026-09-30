; Montogo — Windows installer (Inno Setup 6)
; Build with build.ps1 (publishes the app self-contained, then compiles this).

#define MyAppName "Montogo"
#define MyAppVersion "1.4.0"
#define MyAppPublisher "Ilke"
#define MyAppURL "https://github.com/iilke/Montogo"
#define MyAppExeName "Montogo.App.exe"
#define DriverMsi "virtual-display-driver-0.3.1-x86_64.msi"

[Setup]
; A unique, stable AppId so upgrades/uninstall work across versions.
AppId={{A7E3F2C1-9B4D-4E6A-8C2F-1D5B6E7A9C0F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
DefaultDirName={autopf}\Montogo
DefaultGroupName=Montogo
DisableProgramGroupPage=yes
OutputDir=dist
OutputBaseFilename=MontogoSetup
SetupIconFile=..\..\windows\Montogo.App\montogo.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Needs admin: it installs a driver and trusts its certificate.
PrivilegesRequired=admin
; Offer to close Montogo if it is running while we replace its files.
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; The self-contained app + elevated helper (produced by build.ps1).
Source: "build\app\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
; The pinned v0.3.1 driver payload, staged to a temp folder and removed after install.
Source: "driver\extracted\*"; DestDir: "{tmp}\driver"; Flags: recursesubdirs deleteafterinstall

[Icons]
Name: "{group}\Montogo"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall Montogo"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Montogo"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; The driver steps run ONLY when NO virtual display driver is present (ShouldInstallDriver =
; the driver is absent). If one is already installed we never touch it: re-running the MSI over
; an existing install can remove the device node without cleanly re-adding it. If one is present
; but broken, InitializeWizard warns the user instead of reinstalling over it.
; 1) Trust the driver's self-signed certificate (Root + TrustedPublisher), mirroring the
;    upstream install-cert.bat, so Windows will accept the driver package silently.
Filename: "certutil.exe"; Parameters: "-addstore -f root ""{tmp}\driver\DriverCertificate.cer"""; StatusMsg: "Trusting the display-driver certificate..."; Flags: runhidden waituntilterminated; Check: ShouldInstallDriver
Filename: "certutil.exe"; Parameters: "-addstore -f TrustedPublisher ""{tmp}\driver\DriverCertificate.cer"""; StatusMsg: "Trusting the display-driver certificate..."; Flags: runhidden waituntilterminated; Check: ShouldInstallDriver
; 2) Install the pinned v0.3.1 driver silently.
Filename: "msiexec.exe"; Parameters: "/i ""{tmp}\driver\{#DriverMsi}"" /qn /norestart"; StatusMsg: "Installing the virtual display driver (v0.3.1)..."; Flags: runhidden waituntilterminated; Check: ShouldInstallDriver
; 3) Optionally launch Montogo when finished.
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,Montogo}"; Flags: nowait postinstall skipifsilent

; NOTE: uninstalling Montogo intentionally leaves the virtual display driver in place. The
; driver is a separate, reusable component with its own "Virtual Display Driver" entry in
; Settings > Apps; removing it here (via msiexec during our uninstall) risks the same device-
; node churn that can leave it in a broken state. Users who want it gone remove it there.

[Code]
var
  InitialDriverState: Integer;   { 0 = absent, 1 = healthy, 2 = present but broken }

{ Probe the virtual display driver via PowerShell, reading the exit code:
    1 = a healthy "Virtual Display" device exists,
    2 = a driver/product is present but no healthy device (broken),
    0 = no driver at all. }
function ProbeDriverState(): Integer;
var
  PSScript, Params: String;
  RC: Integer;
begin
  PSScript :=
    '$ErrorActionPreference=''SilentlyContinue'';' +
    '$d=Get-PnpDevice -FriendlyName ''Virtual Display'';' +
    '$reg=@(Get-ItemProperty ''HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'',''HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*''|?{$_.DisplayName -match ''Virtual Display Driver''});' +
    'if(@($d|?{$_.Status -eq ''OK''}).Count -ge 1){exit 1}elseif($d -or $reg.Count -gt 0){exit 2}else{exit 0}';
  Params := '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -Command "' + PSScript + '"';
  if Exec('powershell.exe', Params, '', SW_HIDE, ewWaitUntilTerminated, RC) then
    Result := RC
  else
    Result := 0;   { if the probe can't run, treat as absent and install }
end;

function InitializeSetup(): Boolean;
begin
  InitialDriverState := ProbeDriverState();
  Result := True;
end;

{ Used by [Run]: install the bundled driver only when none is present. }
function ShouldInstallDriver(): Boolean;
begin
  Result := (InitialDriverState = 0);
end;

procedure InitializeWizard;
begin
  if InitialDriverState = 0 then
    { No driver yet: explain we'll install it and trust its certificate. }
    CreateOutputMsgPage(wpWelcome,
      'Virtual display driver',
      'Montogo installs a small open-source display driver',
      'To mirror a screen to your Mac, Montogo needs a virtual monitor. This installer will:'
        + #13#10#13#10
        + '    -  install the open-source virtual-display-rs driver (v0.3.1, AGPLv3-licensed), and'
        + #13#10
        + '    -  trust that driver''s certificate on this PC so Windows accepts the driver.'
        + #13#10#13#10
        + 'The driver only creates a virtual monitor. Montogo itself runs without administrator '
        + 'rights; only a tiny helper is briefly elevated to talk to the driver.')
  else if InitialDriverState = 2 then
    { Present but broken: do NOT reinstall over it (that can corrupt it further). }
    CreateOutputMsgPage(wpWelcome,
      'Existing display driver detected',
      'A virtual display driver is already installed',
      'A virtual display driver is present but does not appear to be working. To avoid damaging '
        + 'it, this installer will install the Montogo app only and will NOT change the driver.'
        + #13#10#13#10
        + 'If Montogo later reports "virtual display did not appear", please:'
        + #13#10
        + '    1.  open Settings > Apps and uninstall "Virtual Display Driver",'
        + #13#10
        + '    2.  restart your PC, then'
        + #13#10
        + '    3.  run this installer again (it will install a fresh driver).');
  { InitialDriverState = 1 (healthy): no driver page — just install the app. }
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  { After a fresh driver install, confirm the device started; if not, advise a reboot. }
  if (CurStep = ssPostInstall) and (InitialDriverState = 0) then
    if ProbeDriverState() <> 1 then
      MsgBox('The virtual display driver was installed, but its device has not started yet.'
        + #13#10#13#10
        + 'This usually completes after a restart. If Montogo reports "virtual display did not '
        + 'appear" the first time, please reboot your PC and try again.',
        mbInformation, MB_OK);
end;
