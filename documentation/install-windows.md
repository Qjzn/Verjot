# Windows 安装版

## 下载与首次启动

`0.1.0-beta.4` 为本轮本机候选；下方 `beta.2` 是既有发布版，不代表候选已发布。候选增加原生模型设置、自启和版本入口，参见[版本说明](changelog.md)。安装后可在开始菜单的 **verbit → verbit** 打开；已在托盘运行时会唤出浮窗。右键「设置…」修改选项，「关于 verbit」查看版本、GitHub 和更新。开机自启与自动检查均可关闭；程序不自动安装更新。

从 [GitHub Releases](https://github.com/Qjzn/verbit/releases) 下载 `verbit-0.1.0-beta.2-setup-x64.exe` 和 `SHA256SUMS.txt`。仓库目前私有，需有仓库访问权限才能下载。安装包没有数字签名，Windows 可能提示未知发布者；先核对来源及 SHA256，不关闭系统安全防护。

```powershell
Get-FileHash .\verbit-0.1.0-beta.2-setup-x64.exe -Algorithm SHA256
```

需要 Windows 10 1809 或更新版本 / Windows 11 x64，以及 .NET Framework 4.8。安装到当前用户目录，默认 `%LOCALAPPDATA%\Programs\verbit`，不要求管理员权限。安装程序只包含桌面程序、NAudio、配置模板、安装脚本与说明，**不包含个人配置、会议数据、Python、模型或 FFmpeg**，不自动安装转写依赖或上传音频。

首次打开会显示设置窗口：点击「选择…」指定已有保存目录（可在目录选择器里新建文件夹），确认四组快捷键及默认 5 秒误触保护，点击「保存并启动」。直接填写尚不存在的目录时会提示先创建，设置窗口保持可编辑；不会静默改用其他位置。保存成功后才创建 `config.json`；取消会退出，下次打开仍显示首次设置。已有配置的安装不会反复弹出首次窗口，也不会覆盖原配置。保存前会检测快捷键占用，有冲突可直接修改。

完成设置后程序驻留托盘，按 `Ctrl+Alt+J` 即可使用置顶速记，支持文字、图片和自动保存。速记无需 Python、模型或 Obsidian。托盘或浮窗右键 →「设置…」可再次修改目录与快捷键。

安装器默认建立开始菜单入口，可选桌面快捷方式和登录启动，两项默认关闭。不自动注册回放协议；需要时间戳回听时，按使用说明运行 `Register-Playback.ps1`。

## 启用会议转写

安装桌面程序不等于会议引擎已就绪。首次设置页会说明这一点；缺少 Python 环境或 worker 时，开始录音会在采集前提示安装，不会让用户录完才发现这两项缺失。文件存在检查不能替代下面的完整自检。

先从托盘退出 verbit，安装 **Python 3.12 x64**，再在程序安装目录打开 PowerShell。可将 [AI 安装说明](ai-install.md) 交给 AI，并明确选择以下一种方式：

本轮验证使用 Python 3.12.10；其[官方发布页](https://www.python.org/downloads/release/python-31210/)提供 **Windows installer (64-bit)**。选择普通安装器，不选 embeddable package 或 ARM64。安装时保留 pip/venv 和 Python Launcher；如果已有其他 Python，不需要替换全局版本。先用 `py -3.12 -c "import sys; print(sys.executable)"` 确认，缺少 `py` 时给 `Install.ps1` 追加 `-PythonExe "Python 3.12 的 python.exe 完整路径"`。安装脚本不会代装系统 Python；可先运行 `-Plan` 检查路径和所选模式。

| 方式 | 安装内容 | 音频去向 |
| --- | --- | --- |
| 本地 | Qwen3-ASR-1.7B、说话人模型及 Python / PyTorch 等依赖，下载与磁盘占用为数 GB | 在本机处理 |
| API | 音频处理依赖和小型本地说话人模型；配置 OpenAI 兼容 `/audio/transcriptions` 服务 | 讲话片段上传用户选择的服务 |

首次设置默认选择本地模式。切换 API 时，先在安装目录的 `config.json` 中设置 `transcription_mode: "api"`、`api_base_url`、`api_model`，并按 [API 配置说明](configuration.md) 设置 `api_key_env` 指定的用户环境变量；不要把密钥写进 JSON 或提交到 Git。已有配置的模式与 `-Mode` 参数不一致时，安装脚本会停止，避免静默改装环境。

```powershell
Set-Location "$env:LOCALAPPDATA\Programs\verbit"

# 已有首次设置的 config.json：先预览安装计划
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1 -Plan

# 按现有配置安装环境；默认本地 CPU，可按说明选择 CUDA
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1

# 只检查，不录音、不播放、不调用 API
.\.venv\Scripts\python.exe -B tingji.py doctor --full --json
```

自检通过后重新打开程序，并按 [首次真实验收](first-use.md) 在得到同意后检查麦克风、电脑声音、回听和转录。API 必须实际支持语音转写端点；普通聊天模型不能直接替代。缺少设备测试或服务调用证据时，不应宣称会议已完全可用。

## 新电脑的额外条件与排障

本地转写还依赖 [Microsoft Visual C++ v14 Redistributable x64](https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist)。它是运行库，无需安装 Visual Studio、C++ 开发工具或 .NET SDK；官方安装器可能要求管理员权限或重启。先完成安装，再重跑 `Install.ps1`。脚本在下载大模型前检查所需 DLL；自检遇到 DLL 加载错误时会指出官方修复入口，不从第三方 DLL 网站复制文件。[PyTorch 官方 Windows FAQ](https://docs.pytorch.org/docs/main/notes/windows.html)说明了这类加载依赖。

依赖与模型需数 GB 下载和可用磁盘；本地 CPU 模式建议先在 16 GiB 及以上内存机器评估实际推理速度，这不是最低配置或性能保证。速记仍不依赖这些组件。网络需要能访问 Python、PyPI、PyTorch、Hugging Face 和说话人模型的 GitHub 下载源；无法访问时应保留下载日志和断点，不能用“已装桌面程序”代替转写已就绪。

安装中断后，保留安装目录和日志，修复网络、权限或磁盘问题，再运行相同的 `Install.ps1` 命令。若虚拟环境已创建但 pip 缺失，脚本使用 Python 自带的 `ensurepip` 离线补齐；已有 pip 损坏、Python 或 Torch 后端不匹配时仍停止，不删除环境或静默重装。模型下载复用已校验文件及有效断点。只看到 EXE 或 `.venv` 目录，不能判定安装成功，应以完整 doctor 结果为准。

无声卡的隔离客体可显式使用 `Install.ps1 -SkipDeviceCheck`，以及 `tingji.py doctor --full --skip-devices --json`；报告标明未检查设备，仅用于运行环境与离线样本验证。日常机器保留默认设备检查，正式录音仍须真实验收。干净系统的可重复步骤见 [干净 Windows 验证](clean-windows-validation.md)。

## 日常操作

| 操作 | 默认快捷键或入口 |
| --- | --- |
| 开始 / 结束会议 | `Ctrl+Alt+L` |
| 暂停 / 继续整场录音 | `Alt+Space`，仅录音期间占用 |
| 标记重点时间 | `Ctrl+Alt+K` |
| 展开 / 收起速记 | `Ctrl+Alt+J` |
| 只关闭麦克风，保留电脑声音 | 点击浮窗麦克风 |
| 结束整个记录 | 点击红色实心方块 |
| 打开成品 / 命名 / 重试 | 托盘或浮窗右键 → 最近会议 |

浮窗关闭按钮只隐藏窗口，录音继续。会议正常处理成功后保留一条 MP3 和一份 Markdown；速记按天保存 Markdown，粘贴图片另存到速记图片目录。有效录音不足保护时间时不保存、不转写。详见 [配置说明](configuration.md)。

## 更新、卸载与资料保留

更新前先结束会议、等待转写完成并从托盘退出。安装到原目录，保留已有配置和资料；安装器会检查运行中的程序、后台转写锁和会议状态，不会强制结束进程。若状态未确认或被占用，请先检查恢复资料并正常退出后重试，不要直接删除工作目录来绕过保护。同一用户已有安装时，更换目录需要先卸载原安装，避免旧卸载程序影响另一目录的版本。

卸载仅移除安装包管理的文件与快捷方式，不清除用户保存的会议、速记、配置或后装的运行环境。因此安装目录可能仍有 `config.json`、`work/`、`.venv/`、模型和恢复资料。确认不再需要恢复、已另存笔记后，再自行决定是否清理；不要把整个保存目录当缓存删除。开发版运行在其他目录时，其启动项、回放协议和资料也不会由本安装包接管。

## 预发布边界

`0.1.0-beta.2` 是预发布版。安装器在本机隔离目录完成安装、升级保留与卸载检查，不代表已经覆盖干净 Windows、SmartScreen、所有声卡、API 服务及数小时会议。测试结果与公开前阻断项见 [开源准备检查](open-source-readiness.md) 和 [发布验收清单](release-readiness.md)。
