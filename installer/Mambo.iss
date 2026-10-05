#ifndef MyAppVersion
  #define MyAppVersion "0.1.4"
#endif
#ifndef PublishDir
  #error "请使用 scripts/publish.ps1 -Installer，或定义 PublishDir 指向已验收的发布目录。"
#endif
#ifndef InstallerOutputDir
  #define InstallerOutputDir "."
#endif

[Setup]
AppId={{A36B6A2B-B280-4C39-9517-C22230A61E8C}
AppName=Mambo
AppVersion={#MyAppVersion}
AppPublisher=Mambo contributors
DefaultDirName={localappdata}\Programs\Mambo
DefaultGroupName=Mambo
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19045
OutputDir={#InstallerOutputDir}
OutputBaseFilename=Mambo-{#MyAppVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter=Mambo.exe
RestartApplications=no
UninstallDisplayIcon={app}\Mambo.exe
LicenseFile={#PublishDir}\LICENSE
SetupLogging=yes

[Languages]
Name: "chinesesimp"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Mambo"; Filename: "{app}\Mambo.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Mambo"; Filename: "{app}\Mambo.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Mambo.exe"; Description: "启动 Mambo"; Flags: nowait postinstall skipifsilent

; 用户数据位于 {localappdata}\Mambo，不添加 UninstallDelete 或扫描其他路径。
; 升级通过 Restart Manager 请求关闭本安装目录下的 Mambo.exe，不强制终止外部 mpv。
