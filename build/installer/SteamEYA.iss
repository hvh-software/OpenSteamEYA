#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#ifndef PublishDir
  #error "PublishDir is not defined."
#endif

#ifndef OutputDir
  #define OutputDir SourcePath
#endif

[Setup]
; 单个闭括号：{{ 转义成字面 {，末尾单 } 收尾，得到 {GUID}。写成 }} 会多出一个字面 }，
; 让卸载注册键变成 {GUID}}_is1；AppId 一旦随首个安装包发布就不可再改，故务必保持规范形式。
AppId={{A49BF24E-5D48-4CF6-9A6F-D205668123B5}
AppName=SteamEYA
AppVersion={#AppVersion}
AppPublisher=hvh-software
DefaultDirName={localappdata}\SteamEYA
DefaultGroupName=SteamEYA
DisableDirPage=no
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=SteamEYA-{#AppVersion}-win-x64-setup
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; 应用最低要求 Windows 10 1809（net10.0-windows / Windows App SDK）；否则会装到跑不起来的旧系统。
MinVersion=10.0.17763
PrivilegesRequired=lowest
WizardStyle=modern
ChangesAssociations=no
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\SteamEyaWinUI.exe
SetupIconFile={#PublishDir}\Assets\AppIcon.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\SteamEYA"; Filename: "{app}\SteamEyaWinUI.exe"
Name: "{autodesktop}\SteamEYA"; Filename: "{app}\SteamEyaWinUI.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\SteamEyaWinUI.exe"; Description: "{cm:LaunchProgram,SteamEYA}"; Flags: nowait postinstall skipifsilent
