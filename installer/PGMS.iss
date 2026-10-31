#ifndef PackageDir
  #error Build with scripts/Build-GraphicalInstaller.ps1
#endif
#define ProductId "PGMS.GraphicalSetup.5A16E882-3D1B-4756-8A3E-A8DA775FC8C1"

[Setup]
AppId={#ProductId}
AppName=Postgres Management Studio
AppVersion={#ReleaseVersion}
AppVerName=Postgres Management Studio {#ReleaseVersion}
AppPublisher=Leonardo Santos
AppPublisherURL=https://github.com/leoars/PostgresManagementStudio
AppSupportURL=https://github.com/leoars/PostgresManagementStudio/issues
VersionInfoVersion={#NumericVersion}
DefaultDirName={autopf}\PostgresManagementStudio
DefaultGroupName=Postgres Management Studio
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
SetupArchitecture=x64
MinVersion=10.0.19045
WizardStyle=modern dynamic
WizardSizePercent=110
DisableWelcomePage=no
SetupIconFile=..\src\PGMS.Desktop\Resources\PGMS.ico
UninstallDisplayIcon={app}\versions\{#Milestone}\PGMS.exe
UninstallDisplayName=Postgres Management Studio {#ReleaseVersion}
OutputDir={#OutputDir}
OutputBaseFilename=PGMS-{#ReleaseVersion}-Setup-x64
Compression=lzma2
SolidCompression=yes
CloseApplications=no
RestartApplications=no
RestartIfNeededByRun=no
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Messages]
english.WelcomeLabel2=Install PGMS for your account or for all users of this computer.%n%nThe .NET desktop runtime is included. PostgreSQL server and tools are not installed.%n%nClose PGMS before continuing. Your connections and credentials remain separate for each Windows user.
brazilianportuguese.WelcomeLabel2=Instale o PGMS para sua conta ou para todos os usuários deste computador.%n%nO runtime .NET está incluído. O servidor e as ferramentas PostgreSQL não são instalados.%n%nFeche o PGMS antes de continuar. Conexões e credenciais permanecem separadas por usuário do Windows.

[CustomMessages]
english.InvalidTarget=Choose an empty, dedicated folder, or the existing PGMS graphical installation. System folders, profile data and linked folders cannot be used.
brazilianportuguese.InvalidTarget=Escolha uma pasta dedicada vazia ou a instalação gráfica existente do PGMS. Pastas do sistema, dados de perfil e pastas vinculadas não podem ser usadas.
english.AppBusy=Close PGMS and check folder permissions before continuing.
brazilianportuguese.AppBusy=Feche o PGMS e verifique as permissões da pasta antes de continuar.
english.LaunchPGMS=Open Postgres Management Studio
brazilianportuguese.LaunchPGMS=Abrir Postgres Management Studio

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PackageDir}\*"; DestDir: "{app}\versions\{#Milestone}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#MarkerFile}"; DestDir: "{app}"; DestName: ".pgms-gui-installation.ini"; Flags: ignoreversion
Source: "{#CompilerLicense}"; DestDir: "{app}"; DestName: "INNO-SETUP-LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\Postgres Management Studio"; Filename: "{app}\versions\{#Milestone}\PGMS.exe"; WorkingDir: "{app}\versions\{#Milestone}"
Name: "{autodesktop}\Postgres Management Studio"; Filename: "{app}\versions\{#Milestone}\PGMS.exe"; WorkingDir: "{app}\versions\{#Milestone}"; Tasks: desktopicon

[Run]
Filename: "{app}\versions\{#Milestone}\PGMS.exe"; Description: "{cm:LaunchPGMS}"; Flags: nowait postinstall skipifsilent unchecked runasoriginaluser

[Code]
function GetFileAttributesW(Name: string): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function HasLinkedAncestor(Path: string): Boolean;
var Parent: string; Attr: LongWord;
begin
  Result := False;
  while Length(Path) > 3 do begin
    Attr := GetFileAttributesW(Path);
    if (Attr <> $FFFFFFFF) and ((Attr and $400) <> 0) then begin Result := True; exit; end;
    Parent := ExtractFileDir(Path);
    if Parent = Path then exit;
    Path := Parent;
  end;
end;

function HasLinkedDescendant(Path: string): Boolean;
var Item: TFindRec;
begin
  Result := False;
  if FindFirst(AddBackslash(Path) + '*', Item) then begin
    try
      repeat
        if (Item.Name <> '.') and (Item.Name <> '..') then begin
          if (Item.Attributes and $400) <> 0 then begin Result := True; exit; end;
          if (Item.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
            if HasLinkedDescendant(AddBackslash(Path) + Item.Name) then begin Result := True; exit; end;
        end;
      until not FindNext(Item);
    finally FindClose(Item); end;
  end;
end;

function IsOwned(Path: string): Boolean;
begin
  Result := GetIniString('Installation', 'Product', '', AddBackslash(Path) + '.pgms-gui-installation.ini') = '{#ProductId}';
end;

function IsDedicated(Path: string): Boolean;
var Item: TFindRec; ProtectedPath, ProfilePath: string;
begin
  Path := RemoveBackslashUnlessRoot(ExpandFileName(Path));
  Result := False;
  if Length(Path) <= 3 then exit;
  if Pos('\\', Path) = 1 then exit;
  ProfilePath := RemoveBackslashUnlessRoot(GetEnv('USERPROFILE'));
  ProtectedPath := ExpandConstant('{localappdata}\PGMS');
  if (CompareText(Path, ProfilePath) = 0) or
     (CompareText(Path, ExpandConstant('{localappdata}')) = 0) or
     (CompareText(Path, ExpandConstant('{autopf}')) = 0) or
     (CompareText(Path, ExpandConstant('{win}')) = 0) or
     (Pos(Lowercase(AddBackslash(ExpandConstant('{win}'))), Lowercase(AddBackslash(Path))) = 1) or
     (Pos(Lowercase(AddBackslash(ProtectedPath)), Lowercase(AddBackslash(Path))) = 1) then exit;
  if HasLinkedAncestor(Path) or HasLinkedDescendant(Path) then exit;
  if IsOwned(Path) then begin Result := True; exit; end;
  if FindFirst(AddBackslash(Path) + '*', Item) then begin
    try
      repeat
        if (Item.Name <> '.') and (Item.Name <> '..') then exit;
      until not FindNext(Item);
    finally FindClose(Item); end;
  end;
  Result := True;
end;

function CanReplaceExecutable(Path: string): Boolean;
var Stream: TFileStream;
begin
  Result := True;
  if not FileExists(Path) then exit;
  try
    Stream := TFileStream.Create(Path, $0012);
    Stream.Free;
  except Result := False; end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): string;
var RootKey: Integer; PreviousPath, OldMilestone: string;
begin
  Result := '';
  if not IsDedicated(ExpandConstant('{app}')) then begin Result := CustomMessage('InvalidTarget'); exit; end;
  if IsAdminInstallMode then RootKey := HKLM64 else RootKey := HKCU64;
  if RegQueryStringValue(RootKey, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#ProductId}_is1', 'InstallLocation', PreviousPath) then
    if CompareText(RemoveBackslashUnlessRoot(PreviousPath), RemoveBackslashUnlessRoot(ExpandConstant('{app}'))) <> 0 then begin
      Result := CustomMessage('InvalidTarget'); exit;
    end;
  OldMilestone := GetIniString('Installation', 'Milestone', '{#Milestone}', ExpandConstant('{app}\.pgms-gui-installation.ini'));
  if (Pos('\', OldMilestone) <> 0) or (Pos('/', OldMilestone) <> 0) or (Pos('..', OldMilestone) <> 0) then begin Result := CustomMessage('InvalidTarget'); exit; end;
  if not CanReplaceExecutable(ExpandConstant('{app}\versions\{#Milestone}\PGMS.exe')) or
     not CanReplaceExecutable(ExpandConstant('{app}\versions\') + OldMilestone + '\PGMS.exe') then Result := CustomMessage('AppBusy');
end;

function InitializeUninstall(): Boolean;
begin
  Result := IsOwned(ExpandConstant('{app}')) and not HasLinkedAncestor(ExpandConstant('{app}')) and not HasLinkedDescendant(ExpandConstant('{app}'));
  if not Result then SuppressibleMsgBox(CustomMessage('InvalidTarget'), mbError, MB_OK, IDOK);
end;
