; Instalador da Sirius. Gere com installer\build.ps1, que passa AppVersion, PublishDir e OutputDir.
; Instala só para o usuário atual, sem pedir administrador.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

; Tipos que a Sirius abre, como texto ou imagem. "Abrir com Sirius" aparece só neles e em pastas.
#define public Extensions "txt;md;log;csv;json;jsonc;xml;yml;yaml;toml;ini;cfg;conf;env;sql;php;html;htm;css;scss;sass;less;js;jsx;mjs;ts;tsx;vue;svelte;cs;csproj;sln;slnx;axaml;xaml;py;rb;go;rs;java;kt;swift;c;h;cpp;hpp;lua;sh;bat;cmd;ps1;gitignore;editorconfig;svg;png;jpg;jpeg;jfif;gif;webp;bmp;ico;"
#sub AddExtension
  #define public Current Copy(Extensions, 1, Pos(";", Extensions) - 1)
  #define public Extensions Copy(Extensions, Pos(";", Extensions) + 1)
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.{#Current}\shell\Sirius"; ValueType: string; ValueData: "{cm:OpenWith}"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.{#Current}\shell\Sirius"; ValueName: "Icon"; ValueType: string; ValueData: "{app}\Sirius.exe"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.{#Current}\shell\Sirius\command"; ValueType: string; ValueData: """{app}\Sirius.exe"" ""%1"""; Tasks: contextmenu
#endsub

[Setup]
; Este identificador liga as atualizações à mesma instalação: nunca o mude.
AppId={{6F1E2C54-8A37-4D19-9B60-3C7E5D2A8F41}
AppName=Sirius
AppVersion={#AppVersion}
AppVerName=Sirius {#AppVersion}
AppPublisher=erickfuzer
AppPublisherURL=https://github.com/erickfuzer/Sirius
DefaultDirName={localappdata}\Programs\Sirius
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=Sirius-{#AppVersion}-Setup
SetupIconFile=..\src\Sirius\Assets\sirius.ico
UninstallDisplayIcon={app}\Sirius.exe
UninstallDisplayName=Sirius
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ChangesAssociations=no
; force: o Claude Code conectado deixa um Sirius.exe --mcp aberto, sem janela para fechar com educação.
; Com "yes", o instalador silencioso desistia sozinho e a atualização não terminava.
CloseApplications=force
RestartApplications=no
; Um "Setup Log" na pasta temporária, para entender uma atualização que não termina.
SetupLogging=yes

[Languages]
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
ptbr.ContextMenuTask=Mostrar "Abrir com Sirius" ao clicar com o botão direito em arquivos e pastas
en.ContextMenuTask=Show "Open with Sirius" when right-clicking files and folders
ptbr.OpenWith=Abrir com Sirius
en.OpenWith=Open with Sirius

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "contextmenu"; Description: "{cm:ContextMenuTask}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\Sirius"; Filename: "{app}\Sirius.exe"
Name: "{userdesktop}\Sirius"; Filename: "{app}\Sirius.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Sirius"; ValueType: string; ValueData: "{cm:OpenWith}"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Sirius"; ValueName: "Icon"; ValueType: string; ValueData: "{app}\Sirius.exe"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Sirius\command"; ValueType: string; ValueData: """{app}\Sirius.exe"" ""%V"""; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\Sirius"; ValueType: string; ValueData: "{cm:OpenWith}"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\Sirius"; ValueName: "Icon"; ValueType: string; ValueData: "{app}\Sirius.exe"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\Sirius\command"; ValueType: string; ValueData: """{app}\Sirius.exe"" ""%V"""; Tasks: contextmenu
; A primeira versão aparecia em todos os arquivos, mesmo os que a Sirius não abre. Atualizar limpa isso.
Root: HKCU; Subkey: "Software\Classes\*\shell\Sirius"; ValueType: none; Flags: deletekey
#for {0; Len(Extensions) > 0; 0} AddExtension

[Run]
Filename: "{app}\Sirius.exe"; Description: "{cm:LaunchProgram,Sirius}"; Flags: nowait postinstall skipifsilent
; A atualização feita pela própria Sirius instala em silêncio com /relaunch=1 e a abre de novo no fim.
Filename: "{app}\Sirius.exe"; Flags: nowait; Check: ShouldRelaunch

[Code]
var
  Finished: Boolean;

function ShouldRelaunch: Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:relaunch|0}') = '1');
end;

// As pontes do Claude Code (Sirius.exe --mcp) abertas pelo app do Claude: o Restart Manager atribui as dlls
// delas ao "System", conclui que precisaria reiniciar o Windows e não fecha nada, nem com force. Por isso o
// instalador as encerra antes, pelo caminho e pelo --mcp, sem tocar em nenhuma janela da Sirius.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Exe, Command: String;
  Code: Integer;
begin
  Result := '';
  try
    Exe := ExpandConstant('{app}\Sirius.exe');
  except
    exit;
  end;
  if not FileExists(Exe) then
    exit;
  StringChangeEx(Exe, '''', '''''', True);
  Command := '-NoProfile -NonInteractive -Command "Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -eq ''' + Exe +
    ''' -and $_.CommandLine -like ''*--mcp*'' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force; Write-Output $_.ProcessId }; Start-Sleep -Milliseconds 500"';
  if Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Command, '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Log(Format('Pontes do Claude Code encerradas (código %d)', [Code]))
  else
    Log(Format('Não deu para encerrar as pontes: %d', [Code]));
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssDone then
    Finished := True;
end;

// A atualização que não termina não pode deixar a pessoa sem a Sirius: abre de novo a que já estava instalada.
procedure DeinitializeSetup();
var
  App: String;
  Code: Integer;
begin
  Log(Format('Reabrir a Sirius? terminou=%d, reabrir=%d', [Ord(Finished), Ord(ShouldRelaunch)]));
  if Finished or not ShouldRelaunch then
    exit;
  try
    App := ExpandConstant('{app}\Sirius.exe');
  except
    Log('Sem a pasta da instalação: ' + GetExceptionMessage);
    exit;
  end;
  if FileExists(App) then
  begin
    if ShellExec('', App, '', '', SW_SHOWNORMAL, ewNoWait, Code) then
      Log('Reaberta: ' + App)
    else
      Log(Format('Não reabriu %s: %d', [App, Code]));
  end
  else
    Log('Não existe: ' + App);
end;
