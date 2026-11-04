; Instalador da Sirius. Gere com installer\build.ps1, que passa AppVersion, PublishDir e OutputDir.
; Instala só para o usuário atual, sem pedir administrador.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

; Tipos que a Sirius abre como texto. "Abrir com Sirius" aparece só neles e em pastas.
#define public Extensions "txt;md;log;csv;json;jsonc;xml;yml;yaml;toml;ini;cfg;conf;env;sql;php;html;htm;css;scss;sass;less;js;jsx;mjs;ts;tsx;vue;svelte;cs;csproj;sln;slnx;axaml;xaml;py;rb;go;rs;java;kt;swift;c;h;cpp;hpp;lua;sh;bat;cmd;ps1;gitignore;editorconfig;"
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
CloseApplications=yes
RestartApplications=no

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
; A primeira versão aparecia em todos os arquivos, até em fotos. Atualizar limpa isso.
Root: HKCU; Subkey: "Software\Classes\*\shell\Sirius"; ValueType: none; Flags: deletekey
#for {0; Len(Extensions) > 0; 0} AddExtension

[Run]
Filename: "{app}\Sirius.exe"; Description: "{cm:LaunchProgram,Sirius}"; Flags: nowait postinstall skipifsilent
