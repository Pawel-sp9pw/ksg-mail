#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif
[Setup]
AppId={{5A118B1F-E4D9-428A-A8AB-4E35414665FC}
AppName=KSG Mail
AppVersion={#AppVersion}
AppPublisher=KSG
AppPublisherURL=https://github.com/Pawel-sp9pw/ksg-mail
DefaultDirName={localappdata}\Programs\KSG Mail
DefaultGroupName=KSG Mail
OutputDir=..\artifacts\installer
OutputBaseFilename=KsgMail-Setup-{#AppVersion}-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
CloseApplications=yes
UninstallDisplayIcon={app}\KsgMail.exe
[Languages]
Name: "polish"; MessagesFile: "compiler:Languages\Polish.isl"
[Tasks]
Name: "desktopicon"; Description: "Utwórz skrót na pulpicie"; Flags: unchecked
[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\KSG Mail"; Filename: "{app}\KsgMail.exe"
Name: "{autodesktop}\KSG Mail"; Filename: "{app}\KsgMail.exe"; Tasks: desktopicon
[Run]
Filename: "{app}\KsgMail.exe"; Description: "Uruchom KSG Mail"; Flags: nowait postinstall skipifsilent
[Code]
function GetCurrentUserSid: String;
var
  Locator, Services, Account, Accounts: Variant;
begin
  Result := '';
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Services := Locator.ConnectServer('', 'root\CIMV2');
    Accounts := Services.ExecQuery('SELECT SID FROM Win32_UserAccount WHERE Name = ''' +
      GetUserNameString + ''' AND Domain = ''' + GetEnv('USERDOMAIN') + '''');
    if Accounts.Count > 0 then begin
      Account := Accounts.ItemIndex(0);
      Result := Account.SID;
    end;
  except
    Result := '';
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  Sid: String;
begin
  if CurUninstallStep = usUninstall then begin
    Sid := GetCurrentUserSid;
    if Sid <> '' then
      Exec(ExpandConstant('{sys}\schtasks.exe'), '/Delete /TN "KSG-Mail-' + Sid + '" /F',
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;
