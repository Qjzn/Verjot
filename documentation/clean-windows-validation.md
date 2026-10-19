# 干净 Windows 验证

目标是 Windows 10/11 x64、.NET Framework 4.8 的新系统：起初没有 Python、模型、Git、Node、Visual Studio 或 .NET SDK。系统自带的 .NET Framework 编译器不属于额外开发环境。速记应在装 Python 前即可使用；本地会议转写需要按说明安装 Python、VC++ 运行库、依赖和模型，不承诺几 MB 安装包独立完成转写。

## 隔离环境与证据

维护者可用已启用的 Windows Sandbox。准备脚本只生成验证包，不启动沙箱、不启用系统功能、不重启。沙箱功能启用可能需要管理员和重启，按 [Microsoft 官方指南](https://learn.microsoft.com/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-install) 由机器所有者安排；没有沙箱时用全新 Windows 虚拟机，不以同一电脑的新 venv 代替。

准备两份**人工合成**的 16 kHz 单声道 PCM WAV，分别朗读以下固定句子；不要使用真实会议录音。生成语音不需要播放。

- A：今天讨论产品发布。录音文件和会议转录保存在本地。请在周五下午三点前完成验证。
- B：我来补充两点。首次打开请先选择保存目录，快捷键可以在设置中修改。

```powershell
# 在源码仓库根目录运行；替换为实际候选安装包和合成样本路径
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\packaging\Prepare-CleanWindows.ps1 `
  -InstallerPath 'C:\Validation\verbit-setup-x64.exe' `
  -SpeechA 'C:\Validation\known-a.wav' -SpeechB 'C:\Validation\known-b.wav'
```

打开输出目录的 `verbit-clean.wsb`。输入目录只读，仅含安装包、校验清单、合成样本与测试工具；另一个专用空目录接收报告。**不映射源码目录、个人笔记、宿主模型或缓存**，关闭剪贴板、麦克风、摄像头和打印机重定向。默认给沙箱 16 GiB 内存；宿主需有足够内存、磁盘和网络。禁止为了通过测试映射现有模型或借用宿主 Python。

客体脚本依次执行：

1. 记录系统和 Python/开发命令基线；发现已有解释器或开发命令即停止。
2. 安装真实 EXE，在没有 Python/模型时运行实际进程的首次设置、取消重开、目录错误修正、速记中文/英文自动保存与重启检查。测试窗口处于独立桌面，不读取系统剪贴板或注入全局按键。
3. 从 Python 官方下载 3.12.10 x64、从 Microsoft 下载 VC++ x64，验证签名并记录 SHA256。仅在一次性客体安装，不修改宿主；需要重启或安装失败则本轮不能通过。
4. 在实际安装目录运行 `Install.ps1 -Mode local -Backend cpu -PythonExe <客体解释器> -SkipDeviceCheck`，从官方源新下载依赖和模型，禁用 pip 缓存。`-SkipDeviceCheck` 只针对无声卡的测试客体；报告必须保留 `devices_checked: false`，不代表能真实采集。
5. 两路合成音分成 8 个无损段，调用真实本地模型与完整 worker；验证唯一 MP3 和 Markdown、192 kbps、完整帧数、两路位置误差 ≤1 ms、固定中文样本字错率 ≤8%，以及成功后临时音轨清理。

主机专用 `reports/` 中需要同时有 `result.json` 的 `passed: true`、28 项实际窗口断言、doctor 和转写报告。只生成 .wsb、静态检查、空 venv、缺失报告或中途失败都不是干净 Windows 通过。保留客体直至检查证据；关掉沙箱会删除其 Python、模型和音频。

仍需在 Windows 10 和 Windows 11 的实际目标机器分别完成真实中文输入法、图片粘贴、快捷键、麦克风/电脑声、设备切换和回听。沙箱中的目标窗口消息和合成音，不能证明这些设备体验。

## 当前状态

### 没有沙箱时使用干净虚拟机

准备脚本仍用于生成显式输入包。在全新 Windows 虚拟机中，将生成的 `input` 文件夹复制为 `C:\verbit-input`，新建空目录 `C:\verbit-reports`，在客体终端执行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\verbit-input\Test-CleanWindowsGuest.ps1 -Environment VM
```

只在可丢弃的客体执行。脚本检查虚拟机型号及输入校验清单，再检查 Python/开发命令的空白基线；不支持或无法读取型号时拒绝，不用物理主机开启了 Hyper-V 作为客体证明。支持 Hyper-V、VMware、VirtualBox、KVM/QEMU、Parallels 常见型号。客体不共享宿主模型、缓存、个人目录或剪贴板。按原检查清单读取报告，失败退出码为2；失败后保留报告，再新建干净客体重测，不在已装过依赖的客体重写“首次安装通过”。

2026-10-19 复查：本机未找到 Windows Sandbox、VirtualBox、VMware 启动入口；DISM 只读查询返回740，当前进程没有管理员令牌。因此未启动干净客体，不能由此前“已启用、待重启”的记录推断现在可运行。本轮新增显式 VM 入口与宿主拒绝回归，但这不是 VM 安装实测通过。当前候选状态见 [私有测试版验收](private-beta-acceptance.md)。

2026-10-16：经机器所有者授权，管理员操作已将 `Containers-DisposableClientVM` 从 Disabled 启用为 Enabled，返回 `RestartNeeded: true`。全程使用 `-NoRestart`，宿主尚未重启；重启由机器所有者安排。启用结束时 `WindowsSandbox.exe` 仍未出现，因此没有尝试启动客体。重启后重新核实功能和可执行文件，再打开准备好的 `.wsb`，不要以功能启用成功代替客体验收。**干净 Windows 客体尚未执行，不能标为通过**。当前机器已有环境的实际安装与本地模型证据见 [新用户验证](new-user-validation.md)。
