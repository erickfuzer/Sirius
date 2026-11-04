; Instalador da Sirius. Gere com installer\build.ps1, que passa AppVersion, PublishDir e OutputDir.
; Instala só para o usuário atual, sem pedir administrador.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

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
Root: HKCU; Subkey: "Software\Classes\*\shell\Sirius"; ValueType: string; ValueData: "{cm:OpenWith}"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\*\shell\Sirius"; ValueName: "Icon"; ValueType: string; ValueData: "{app}\Sirius.exe"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\*\shell\Sirius\command"; ValueType: string; ValueData: """{app}\Sirius.exe"" ""%1"""; Tasks: contextmenu

[Run]
Filename: "{app}\Sirius.exe"; Description: "{cm:LaunchProgram,Sirius}"; Flags: nowait postinstall skipifsilent
