#ifndef PayloadDir
  #error PayloadDir is required. Run Build-Installer.ps1.
#endif
#ifndef OutputDir
  #error OutputDir is required. Run Build-Installer.ps1.
#endif
#ifndef ReleaseVersion
  #define ReleaseVersion "0.1.0-beta.16"
#endif
#ifndef NumericVersion
  #define NumericVersion "0.1.0.0"
#endif

[Setup]
AppId={{DE124FA5-8E3D-4B80-A62E-AF8FA54B1E43}
AppName=verbit
AppVersion={#ReleaseVersion}
UninstallDisplayName=verbit
VersionInfoVersion={#NumericVersion}
AppPublisher=Qjzn
AppPublisherURL=https://github.com/Qjzn/verbit
AppSupportURL=https://github.com/Qjzn/verbit/issues
DefaultDirName={localappdata}\Programs\verbit
DefaultGroupName=verbit
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
MinVersion=10.0.17763
WizardStyle=modern
DisableProgramGroupPage=no
DisableWelcomePage=no
LicenseFile={#PayloadDir}\LICENSE
InfoBeforeFile=INSTALL.txt
SetupIconFile={#PayloadDir}\assets\tingji.ico
UninstallDisplayIcon={app}\ObsidianMeeting.exe
OutputDir={#OutputDir}
OutputBaseFilename=verbit-{#ReleaseVersion}-setup-x64
Compression=lzma2
SolidCompression=yes
CloseApplications=no
RestartApplications=no
AllowNoIcons=no
ChangesAssociations=no
Uninstallable=yes
UsePreviousTasks=yes

[Languages]
Name: "default"; MessagesFile: "compiler:Default.isl"

[Messages]
SetupWindowTitle=安装 - %1
UninstallAppFullTitle=卸载 %1
InformationTitle=信息
ConfirmTitle=确认
WelcomeLabel1=欢迎安装 verbit
WelcomeLabel2=verbit 提供全局快捷键速记和会议记录。%n%n速记可以直接使用。录音和速记无需 Python 或模型。转写可在设置中选择自定义第三方 API，提交前会确认音频上传。%n%n请先从托盘正常退出正在运行的 verbit，再升级或卸载。
ButtonBack=< 返回(&B)
ButtonNext=下一步(&N) >
ButtonInstall=安装(&I)
ButtonCancel=取消
ButtonFinish=完成(&F)
ButtonBrowse=浏览(&B)…
ButtonWizardBrowse=浏览(&R)…
WizardLicense=许可协议
LicenseLabel=继续安装前，请阅读许可协议。
LicenseLabel3=请阅读以下许可协议。接受协议后可继续安装。
LicenseAccepted=我接受协议(&A)
LicenseNotAccepted=我不接受协议(&D)
WizardInfoBefore=安装前须知
InfoBeforeLabel=继续安装前，请阅读以下使用条件。
InfoBeforeClickLabel=准备好后，点击“下一步”。
WizardSelectDir=选择安装位置
SelectDirDesc=将 verbit 安装到哪里？
SelectDirLabel3=安装到当前用户可写的文件夹。个人配置和会议环境保存在安装目录，录音与笔记保存到首次设置选择的位置。
SelectDirBrowseLabel=点击“下一步”继续；点击“浏览”选择其他文件夹。
DiskSpaceMBLabel=至少需要 [mb] MB 可用空间。
DiskSpaceGBLabel=至少需要 [gb] GB 可用空间。
WizardSelectTasks=可选功能
SelectTasksDesc=选择便捷入口与本地转写
SelectTasksLabel2=均可不选。推荐模型和环境需数 GB 下载与磁盘空间；稍后也可从设置下载，不影响速记。
WizardReady=准备安装
ReadyLabel1=即将安装 verbit。
ReadyLabel2a=点击“安装”继续；点击“返回”检查设置。
ReadyLabel2b=点击“安装”继续。
ReadyMemoDir=安装位置：
ReadyMemoGroup=开始菜单：
ReadyMemoTasks=可选操作：
InstallingLabel=正在安装 verbit，请稍候。
StatusCreateDirs=正在创建目录…
StatusExtractFiles=正在解压程序文件…
StatusCreateIcons=正在创建快捷方式…
StatusCreateRegistryEntries=正在注册卸载信息…
StatusSavingUninstall=正在保存卸载信息…
StatusRunProgram=正在完成安装…
FinishedHeadingLabel=verbit 安装完成
FinishedLabelNoIcons=首次启动后，请设置保存目录和快捷键。默认仅录音；可在设置中选配第三方音频转写 API。已有模型和配置保留。
FinishedLabel=首次启动后，请设置保存目录和快捷键。默认仅录音；可在设置中选配第三方音频转写 API。已有模型和配置保留。
ConfirmUninstall=是否卸载 %1？%n%n卸载仅移除安装包中的程序文件及快捷方式。个人配置、会议环境、工作资料，以及录音和笔记会保留。
WizardUninstalling=卸载状态
UninstallStatusLabel=正在移除程序文件，请稍候。个人配置与录音笔记会保留。
StatusUninstalling=正在卸载 %1…
UninstalledAll=%1 已卸载。个人配置、环境与已经保存的记录会保留。

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: unchecked
Name: "startup"; Description: "登录 Windows 后启动 verbit"; Flags: unchecked

[Files]
; Payload is assembled from the reviewed explicit list in Build-Installer.ps1.
; Never use a wildcard against the developer's working checkout.
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PayloadDir}\packaging\Check-InstallSafety.ps1"; Flags: dontcopy

[Icons]
Name: "{group}\verbit"; Filename: "{app}\ObsidianMeeting.exe"; Parameters: "--show"; WorkingDir: "{app}"; IconFilename: "{app}\ObsidianMeeting.exe"
Name: "{group}\安装与使用说明"; Filename: "{app}\packaging\quick-start.html"; WorkingDir: "{app}"
Name: "{autodesktop}\verbit"; Filename: "{app}\ObsidianMeeting.exe"; Parameters: "--show"; WorkingDir: "{app}"; Tasks: desktopicon
; A distinct startup filename never overwrites an earlier source installation.
Name: "{userstartup}\verbit installed"; Filename: "{app}\ObsidianMeeting.exe"; WorkingDir: "{app}"; Tasks: startup

[Run]
Filename: "{app}\ObsidianMeeting.exe"; Description: "启动 verbit 并设置保存目录"; Flags: nowait postinstall skipifsilent

[Code]
function CreateFile(FileName: string; DesiredAccess, ShareMode, SecurityAttributes,
  CreationDisposition, FlagsAndAttributes, TemplateFile: LongWord): THandle;
  external 'CreateFileW@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

function RegisteredDirectoryMatches(): Boolean;
var
  RegisteredPath: string;
begin
  Result := True;
  if RegKeyExists(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{DE124FA5-8E3D-4B80-A62E-AF8FA54B1E43}_is1') then begin
    Result := RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{DE124FA5-8E3D-4B80-A62E-AF8FA54B1E43}_is1', 'InstallLocation', RegisteredPath);
    if Result then
      Result := (Copy(RegisteredPath, 1, 2) = '\\') or ((Length(RegisteredPath) >= 3) and (Copy(RegisteredPath, 2, 2) = ':\'));
    if Result then
      Result := CompareText(RemoveBackslashUnlessRoot(ExpandFileName(RegisteredPath)), RemoveBackslashUnlessRoot(ExpandFileName(ExpandConstant('{app}')))) = 0;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Shell, Link: Variant;
  Entry, Target: string;
  Index: Integer;
begin
  if CurUninstallStep <> usUninstall then exit;
  { Settings-created startup links may not be in Inno's install log. }
  try
    Shell := CreateOleObject('WScript.Shell');
    for Index := 0 to 1 do begin
      if Index = 0 then Entry := ExpandConstant('{userstartup}\verbit installed.lnk')
      else Entry := ExpandConstant('{userstartup}\verbit.lnk');
      if FileExists(Entry) then begin
        Link := Shell.CreateShortcut(Entry);
        Target := Link.TargetPath;
        if CompareText(Target, ExpandConstant('{app}\ObsidianMeeting.exe')) = 0 then
          DeleteFile(Entry);
      end;
    end;
  except
    Log('Could not inspect settings-created startup links; unrelated entries were left untouched.');
  end;
end;

function SafetyCheck(ScriptPath: string): Boolean;
var
  ExitCode: Integer;
begin
  Result := False;
  if not FileExists(ScriptPath) then exit;
  if Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ScriptPath + '" -Root "' + RemoveBackslashUnlessRoot(ExpandConstant('{app}')) + '"',
      '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
    Result := ExitCode = 0;
end;

function ProgramFilesAvailable(): Boolean;
var
  Handle: THandle;
  Target: string;
begin
  Target := ExpandConstant('{app}\ObsidianMeeting.exe');
  Result := True;
  if not FileExists(Target) then exit;
  { Open without writing a byte. A running executable or inaccessible target
    must be resolved by the user; Setup never terminates a recording process. }
  Handle := CreateFile(Target, $C0000000, 0, 0, 3, 0, 0);
  Result := Handle <> THandle(-1);
  if Result then CloseHandle(Handle);
end;

function InitializeSetup(): Boolean;
begin
  Result := IsDotNetInstalled(net48, 0);
  if not Result then
    SuppressibleMsgBox('verbit 需要 .NET Framework 4.8。请先通过 Windows 更新或 Microsoft 官方安装程序准备运行环境，然后重试。本安装程序不会自动修改系统运行库。', mbError, MB_OK, IDOK);
end;

function PrepareToInstall(var NeedsRestart: Boolean): string;
begin
  Result := '';
  if not RegisteredDirectoryMatches() then begin
    Result := '此用户已在另一目录安装 verbit。请在原目录升级，或先正常卸载原安装，再更换目录。未覆盖原安装或注册信息。';
    exit;
  end;
  if not ProgramFilesAvailable() then begin
    Result := '无法更新目标程序。请先从托盘正常退出此目录的 verbit，并确认安装目录可写，然后重试。不会强制结束录音。';
    exit;
  end;
  ExtractTemporaryFile('Check-InstallSafety.ps1');
  if not SafetyCheck(ExpandConstant('{tmp}\Check-InstallSafety.ps1')) then
    Result := '无法确认会议任务已结束。请等待后台转写完成，检查 work 中的会议状态，并确认目录可写后重试。未结束任何进程，也未修改记录。';
end;

function InitializeUninstall(): Boolean;
begin
  Result := RegisteredDirectoryMatches();
  if not Result then begin
    SuppressibleMsgBox('此卸载程序不再拥有当前注册的 verbit 安装位置。为保护另一目录的安装和菜单，本次卸载已停止。', mbError, MB_OK, IDOK);
    exit;
  end;
  Result := ProgramFilesAvailable() and SafetyCheck(ExpandConstant('{app}\packaging\Check-InstallSafety.ps1'));
  if not Result then
    SuppressibleMsgBox('无法卸载正在使用或不可写的程序。请先从托盘正常退出此目录的 verbit，然后重试。录音与笔记不会被删除。', mbError, MB_OK, IDOK);
end;
