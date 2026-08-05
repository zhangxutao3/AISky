#ifndef MyAppVersion
  #error MyAppVersion must be provided by the release script.
#endif
#ifndef SourceDir
  #error SourceDir must be provided by the release script.
#endif
#ifndef OutputDir
  #error OutputDir must be provided by the release script.
#endif

#define MyAppName "AISky"
#define MyAppDisplayName "AISky 桌面气象平台"
#define MyAppExeName "AISky.Desktop.exe"
#define MyAppPublisher "AISky"
#define MyAppUrl "https://github.com/zhangxutao3/AISky"

[Setup]
AppId={{7A48745C-A2E0-4B41-AB0B-9B9B838AF42C}
AppName={#MyAppName}
AppVerName={#MyAppName} {#MyAppVersion}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}/issues
AppUpdatesURL={#MyAppUrl}/releases/latest
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppDisplayName} 安装程序
VersionInfoProductName={#MyAppDisplayName}
VersionInfoProductVersion={#MyAppVersion}
DefaultDirName={localappdata}\Programs\AISky
DefaultGroupName=AISky
DisableProgramGroupPage=yes
AllowNoIcons=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=AISky-Setup-win-x64
SetupIconFile={#SourceDir}\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppDisplayName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
AppMutex=Local\AISky.Desktop.SingleInstance
UsePreviousAppDir=yes
UsePreviousTasks=yes
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
SetupAppTitle=安装
SetupWindowTitle=安装 - %1
UninstallAppTitle=卸载
UninstallAppFullTitle=%1 卸载
InformationTitle=提示
ConfirmTitle=确认
ErrorTitle=出现问题
ButtonBack=< 上一步(&B)
ButtonNext=下一步(&N) >
ButtonInstall=安装(&I)
ButtonOK=确定
ButtonCancel=取消
ButtonYes=是(&Y)
ButtonYesToAll=全部是(&A)
ButtonNo=否(&N)
ButtonNoToAll=全部否(&O)
ButtonFinish=完成(&F)
WelcomeLabel1=欢迎使用 [name] 安装向导
WelcomeLabel2=即将在这台电脑上安装 [name/ver]。%n%n建议先关闭正在运行的 AISky，再继续安装。
WizardSelectDir=选择安装位置
SelectDirDesc=[name] 要安装到哪里？
SelectDirLabel3=安装程序会将 [name] 安装到以下文件夹。
WizardSelectTasks=选择附加任务
SelectTasksDesc=还需要完成哪些设置？
SelectTasksLabel2=选择安装 [name] 时需要创建的附加项目，然后点击“下一步”。
WizardReady=准备安装
ReadyLabel1=已经准备好在这台电脑上安装 [name]。
ReadyLabel2a=点击“安装”开始；如需修改设置，请点击“上一步”。
ReadyLabel2b=点击“安装”开始。
ReadyMemoDir=安装位置：
ReadyMemoType=安装类型：
ReadyMemoComponents=已选组件：
ReadyMemoGroup=开始菜单文件夹：
ReadyMemoTasks=附加任务：
WizardPreparing=正在准备安装
PreparingDesc=正在准备将 [name] 安装到这台电脑。
PreviousInstallNotCompleted=上一次安装或卸载尚未完成。请重新启动电脑，然后再次运行 [name] 安装程序。
WizardInstalling=正在安装
InstallingLabel=正在安装 [name]，请稍候。
FinishedHeadingLabel=[name] 安装完成
FinishedLabelNoIcons=[name] 已成功安装到这台电脑。
FinishedLabel=[name] 已成功安装，可以通过创建的快捷方式启动。
ClickFinish=点击“完成”退出安装程序。
ConfirmUninstall=确定要完整移除 %1 及其程序组件吗？
UninstallStatusLabel=正在从这台电脑移除 %1，请稍候。
UninstalledAll=%1 已成功移除。
UninstalledMost=%1 已完成卸载。%n%n少量文件无法自动移除，可以稍后手动删除。
ShutdownBlockReasonInstallingApp=正在安装 %1。
ShutdownBlockReasonUninstallingApp=正在卸载 %1。

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: checkedonce

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[INI]
Filename: "{app}\.aisky-install.ini"; Section: "Install"; Key: "Mode"; String: "InnoSetup"
Filename: "{app}\.aisky-install.ini"; Section: "Install"; Key: "Version"; String: "{#MyAppVersion}"

[Icons]
Name: "{userprograms}\AISky\AISky"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Comment: "{#MyAppDisplayName}"; AppUserModelID: "AISky.Desktop"; Flags: runmaximized
Name: "{userprograms}\AISky\卸载 AISky"; Filename: "{uninstallexe}"
Name: "{userdesktop}\AISky"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Comment: "{#MyAppDisplayName}"; AppUserModelID: "AISky.Desktop"; Tasks: desktopicon; Flags: runmaximized

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 AISky"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent
