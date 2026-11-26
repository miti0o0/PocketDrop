; PocketDrop installer (Inno Setup 6).
; Build: tools\build-release.ps1  (or: iscc /DAppVersion=1.0.0 desktop\installer\PocketDrop.iss)
; Expects the published app in ..\publish\PocketDrop.exe

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppName "PocketDrop"
#define AppExe "PocketDrop.exe"

[Setup]
AppId={{6E0C7B7A-3B2F-4C55-9E7B-2E8A9D1C4F10}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=PocketDrop contributors
AppPublisherURL=https://github.com/
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\..\dist
OutputBaseFilename=PocketDrop-Setup-{#AppVersion}
SetupIconFile=app.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; The running app is detected in InitializeSetup (it waits for the app to exit during in-app updates).
CloseApplications=yes

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "bg"; MessagesFile: "compiler:Languages\Bulgarian.isl"

[CustomMessages]
en.BtTitle=Bluetooth
en.BtSubtitle=Can this PC receive files over Bluetooth?
en.BtPrompt=PocketDrop always works over Wi-Fi (phone and PC on the same network). It can also receive over Bluetooth, which works without a network but is slower. The phone has to be paired with the PC once.
en.BtYes=Yes, this PC has Bluetooth — use Bluetooth and Wi-Fi
en.BtNo=No Bluetooth — Wi-Fi only
en.BtDetected=(A Bluetooth adapter was detected on this PC.)
en.FolderTitle=Receive folder
en.FolderSubtitle=Where should files from your phone be saved?
en.FolderPrompt=Every drop gets its own sub-folder inside this folder. The name is chosen on the phone, or the files go into an existing sub-folder. You can change this later in PocketDrop.
en.AppRunning=PocketDrop is running. Close it (right-click the tray icon → Quit) and press Retry.
bg.AppRunning=PocketDrop работи в момента. Затвори го (десен бутон върху иконката до часовника → Изход) и натисни „Опитай пак“.
en.AutostartTask=Start PocketDrop automatically when the PC starts (in the background, ready to receive)
bg.AutostartTask=Стартирай PocketDrop автоматично при пускане на компютъра (на заден план, готов да получава)
en.FirewallTask=Allow PocketDrop through Windows Firewall on public networks too (needed when your home Wi-Fi is set to "Public")
bg.BtTitle=Bluetooth
bg.BtSubtitle=Може ли този компютър да получава файлове през Bluetooth?
bg.BtPrompt=PocketDrop винаги работи през Wi-Fi (телефонът и компютърът в една мрежа). Може да получава и през Bluetooth — работи без мрежа, но е по-бавно. Телефонът трябва да се сдвои с компютъра веднъж.
bg.BtYes=Да, компютърът има Bluetooth — използвай Bluetooth и Wi-Fi
bg.BtNo=Няма Bluetooth — само Wi-Fi
bg.BtDetected=(На този компютър е открит Bluetooth адаптер.)
bg.FolderTitle=Папка за получаване
bg.FolderSubtitle=Къде да се записват файловете от телефона?
bg.FolderPrompt=Всяко изпращане получава собствена подпапка в тази папка. Името се избира от телефона, или файловете отиват в съществуваща подпапка. Може да се промени по-късно от PocketDrop.
bg.FirewallTask=Разреши PocketDrop в защитната стена на Windows и за публични мрежи (нужно, ако домашният Wi-Fi е зададен като „Публична“)

[Tasks]
Name: "autostart"; Description: "{cm:AutostartTask}"
Name: "firewallpublic"; Description: "{cm:FirewallTask}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\publish\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; The app writes this value itself (Start with Windows); remove it on uninstall.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "{#AppName}"; Flags: uninsdeletevalue

[Run]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#AppName}"""; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#AppName}"" dir=in action=allow program=""{app}\{#AppExe}"" enable=yes profile=private,domain"; Flags: runhidden; Tasks: not firewallpublic
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#AppName}"" dir=in action=allow program=""{app}\{#AppExe}"" enable=yes profile=any"; Flags: runhidden; Tasks: firewallpublic
; Passes the answers from the wizard to the app, which stores them in the user's settings.
Filename: "{app}\{#AppExe}"; Parameters: "{code:FirstRunArgs}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall runasoriginaluser

[UninstallDelete]
; cloudflared.exe, downloaded by "From anywhere".
Type: filesandordirs; Name: "{localappdata}\{#AppName}\cloudflared"

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#AppName}"""; Flags: runhidden; RunOnceId: "RemoveFirewallRule"

[Code]
const
  AppMutexName = 'PocketDrop.SingleInstance.v1';

var
  BtPage: TInputOptionWizardPage;
  FolderPage: TInputDirWizardPage;

{ Started by the app's Update button: /SILENT ... /UPDATE=1 }
function IsUpdate(): Boolean;
begin
  Result := ExpandConstant('{param:UPDATE|0}') = '1';
end;

function InitializeSetup(): Boolean;
var
  Waited: Integer;
begin
  Result := True;
  if IsUpdate() then
  begin
    { The app quits right after starting us; give it a moment to release its mutex. }
    Waited := 0;
    while CheckForMutexes(AppMutexName) and (Waited < 30000) do
    begin
      Sleep(250);
      Waited := Waited + 250;
    end;
  end
  else
  begin
    while CheckForMutexes(AppMutexName) do
    begin
      if SuppressibleMsgBox(CustomMessage('AppRunning'), mbError, MB_RETRYCANCEL, IDCANCEL) = IDCANCEL then
      begin
        Result := False;
        Exit;
      end;
    end;
  end;
end;

function HasBluetoothAdapter(): Boolean;
var
  ResultCode: Integer;
begin
  { Any present device in the Bluetooth class means there is an adapter. }
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -Command "if (Get-PnpDevice -Class Bluetooth -PresentOnly -ErrorAction SilentlyContinue) { exit 0 } else { exit 1 }"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

procedure InitializeWizard();
var
  Prompt, PrevBt: String;
  Detected: Boolean;
begin
  PrevBt := GetPreviousData('Bluetooth', '');
  if IsUpdate() or (PrevBt <> '') then
    Detected := PrevBt = 'yes'
  else
    Detected := HasBluetoothAdapter();

  Prompt := CustomMessage('BtPrompt');
  if Detected and (PrevBt = '') then
    Prompt := Prompt + #13#10#13#10 + CustomMessage('BtDetected');

  BtPage := CreateInputOptionPage(wpSelectTasks, CustomMessage('BtTitle'), CustomMessage('BtSubtitle'),
    Prompt, True, False);
  BtPage.Add(CustomMessage('BtYes'));
  BtPage.Add(CustomMessage('BtNo'));
  if Detected then BtPage.SelectedValueIndex := 0 else BtPage.SelectedValueIndex := 1;

  FolderPage := CreateInputDirPage(BtPage.ID, CustomMessage('FolderTitle'), CustomMessage('FolderSubtitle'),
    CustomMessage('FolderPrompt'), False, '');
  FolderPage.Add('');
  FolderPage.Values[0] := GetPreviousData('ReceiveFolder', ExpandConstant('{%USERPROFILE}\PocketDrop'));
end;

{ Remember the answers so a reinstall suggests the same ones. }
procedure RegisterPreviousData(PreviousDataKey: Integer);
var
  Bt: String;
begin
  if BtPage.SelectedValueIndex = 0 then Bt := 'yes' else Bt := 'no';
  SetPreviousData(PreviousDataKey, 'Bluetooth', Bt);
  SetPreviousData(PreviousDataKey, 'ReceiveFolder', FolderPage.Values[0]);
end;

function FirstRunArgs(Param: String): String;
var
  Bt: String;
begin
  { An in-app update must not touch the user's settings. }
  if IsUpdate() then
  begin
    Result := '--updated';
    Exit;
  end;
  if BtPage.SelectedValueIndex = 0 then Bt := 'yes' else Bt := 'no';
  Result := '--bluetooth=' + Bt + ' "--folder=' + FolderPage.Values[0] + '"';
  if WizardIsTaskSelected('autostart') then
    Result := Result + ' --autostart=yes'
  else
    Result := Result + ' --autostart=no';
end;
