# 由 AI 代安装听记

这份说明供 AI 在目标 Windows 机器上执行安装和排障。先取得必要输入、查看安装计划，再安装；环境自检与真实录音验收分别记录。安装过程不启动听记、不录音、不调用转写 API。

## 先确认输入与已有环境

请使用用户已经选定的安装位置和保存位置。未提供时，只补问会影响安装的选择：

| 输入 | 如何使用 |
| --- | --- |
| 保存目录 | 完整 Windows 路径，例如 `D:\Meetings`。可以是普通文件夹；使用 Obsidian 时应选择已经登记的笔记库。 |
| 转写模式 | `local` 在本机转写；`api` 将讲话片段提交给用户选择的服务。不要替用户决定是否上传会议音频。 |
| 本地后端 | 本地模式默认 `cpu`；只有用户选择 NVIDIA CUDA 环境时使用 `cuda128`。安装脚本不自动猜 GPU。 |
| API 服务 | API 模式需要服务前缀、实际支持的音频转写模型名，以及密钥环境变量名。密钥值由用户在机器上提供，不写入配置或安装记录。 |

源码仓库是 [Qjzn/tingji](https://github.com/Qjzn/tingji)，当前为私有仓库。使用已有授权的 Git 访问方式取得源码；已有目录优先复用。无法访问时先处理账号授权，不索取用户的明文 token，也不修改仓库公开权限。

安装或更新应取得同一版本的完整源码。不要只替换 `ObsidianMeeting.cs` 或 EXE：原生程序还依赖 `SessionCatalog.cs`，字段校验依赖 `config.schema.json`，Python 转写脚本与配置示例也需要保持同版。构建入口为 `Build.ps1`；回归入口为 `Test.ps1`。`app.manifest` 嵌入 Windows 10 兼容声明，构建自动复制同名 `.exe.config`，为 .NET Framework 4.8 启用 `PerMonitorV2`；正式运行必须保留 `ObsidianMeeting.exe.config`，不能仅部署 EXE。更新前保留现有 `config.json`、`work/` 与成品，不把本机资料复制进 Git。

目标机器需要 Windows 10/11 x64、Python 3.12 x64、.NET Framework 4.8，以及可用的 Framework C# 编译器。检查已经安装的 Python：

```powershell
py -3.12 -c "import sys, struct; print(sys.executable); print(sys.version); print(struct.calcsize('P') * 8)"
```

位数应为 `64`。没有 `py` 时，可向安装命令追加 `-PythonExe "已安装的 Python 3.12\python.exe 完整路径"`。脚本不会安装系统 Python、切换全局 Python 或删除已有虚拟环境；缺少 Python 时由用户安装，或在用户明确授权后另行安装。

已有 `config.json` 时先只读核对它。安装脚本保留原文件；模式参数省略时继承已有 `transcription_mode`，旧配置缺省为 `local`。显式模式与已有配置冲突、已有 Torch 版本或后端与本地安装参数不匹配，都会停止并说明原因。不要用重建虚拟环境或覆盖配置来绕过提示。

听记正在运行时不能安装或重建。等用户结束录音和转写，再让用户从托盘正常退出；不要结束录音进程来腾出安装位置。

## 先运行安装计划

以下命令假设源码已放在 `E:\Tools\tingji`，保存目录为 `D:\Meetings`。替换为实际路径。`-ExecutionPolicy Bypass` 只作用于这次 PowerShell 进程，不修改机器或用户的执行策略。

API 模式计划：

```powershell
Set-Location 'E:\Tools\tingji'
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1 -NotesPath 'D:\Meetings' -Mode api -Plan
```

本地 CPU 模式计划：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1 -NotesPath 'D:\Meetings' -Mode local -Backend cpu -Plan
```

本地 CUDA 模式把 `-Backend cpu` 改为 `-Backend cuda128`。首次使用 Obsidian 时追加 `-UseObsidian`；已有配置不受该开关修改。

`-Plan` 只读取信息和输出步骤，不下载、不创建保存目录或配置、不建立启动项。计划中应核对模式、保存路径、已有配置是否保留，以及是否仍有本目录录音进程。API 模式计划应列出 `requirements-api.txt` 和 `download_models.py --speaker-only`。

## 执行所选模式

确认计划与用户输入一致后，去掉 `-Plan` 执行安装。例如首次 API 模式：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1 -NotesPath 'D:\Meetings' -Mode api
```

首次本地 CPU 模式：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1 -NotesPath 'D:\Meetings' -Mode local -Backend cpu
```

首次保存目录允许新建。默认使用普通文件夹，转写完成后不自动打开 Obsidian。只有用户选择 Obsidian 时追加 `-UseObsidian`；只有用户要求登录自动启动时追加 `-StartWithWindows`。不要直接调用 `Install-Shortcuts.ps1` 作为默认步骤，它会建立 Startup 快捷方式。

| 模式 | 安装内容与体积边界 |
| --- | --- |
| `local / cpu` | PyTorch 2.11.0 CPU、`requirements.txt`、Qwen3-ASR 中文识别模型和本地说话人模型。依赖与权重是数 GB 级安装，CPU 处理速度需在目标机器上实测。 |
| `local / cuda128` | PyTorch 2.11.0 + CUDA 12.8 对应 Windows/Python 3.12 wheel，其余同本地 CPU。下载使用分段恢复和官方 SHA256 校验；安装 CUDA wheel 不证明 GPU 已可推理。 |
| `api` | `requirements-api.txt` 和本地说话人模型，跳过 Torch、Transformers 与本地 Qwen 权重。说话人模型下载约 46 MB，不含 Python 依赖和解压后的占用。 |

安装器从官方 NuGet 包提取指定的 NAudio 1.10.0 DLL，并核对固定 SHA256；随后安装依赖、下载模型、构建程序、准备首次配置并执行完整自检。模型下载保留恢复资料，失败后先读取原因再重跑，不重复创建配置、不随意清理缓存。

模型只安装到程序目录的 `models/`；虚拟环境在 `.venv/`，下载缓存在 `runtime-wheels/`，安装日志、状态与自检证据在 `installation/`。这些是本机运行文件，不应提交到源码仓库。仓库不收录模型、运行依赖、个人配置、录音、密钥或本机验证截图。第三方条款见 [THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md)。

## 补齐 API 配置

API 服务必须支持 OpenAI 兼容的 `POST /audio/transcriptions` 音频转写端点，包括 WAV 文件、`model`、`language` 和 JSON 文本返回。只有聊天接口或只有聊天模型的供应方不能据此判断可用。

首次 API 安装会创建模板，但模板的服务地址和模型名为空。此时 doctor 失败、安装退出码为 `1` 是未完成结果；不能报告“API 已就绪”。AI 应按用户提供的信息，仅补齐 `config.json` 的必要字段，保留其他已确认设置。例如下面是占位示例，不能原样当作真实服务：

```json
{
  "transcription_mode": "api",
  "api_base_url": "https://provider.example/v1",
  "api_model": "YOUR_AUDIO_TRANSCRIPTION_MODEL",
  "api_key_env": "TINGJI_API_KEY",
  "api_timeout_seconds": 120
}
```

这只是字段片段，不要用它覆盖完整配置。程序把 `/audio/transcriptions` 追加到 `api_base_url`，示例实际对应 `https://provider.example/v1/audio/transcriptions`。前缀应包含供应方要求的版本路径；不要重复填写端点后缀。

`api_key_env` 保存环境变量名称，不保存密钥值。由用户在目标机器提供该变量；AI 不索取、打印或保存密钥。已设置变量时直接复用。新设置用户环境变量后重新打开 PowerShell，并重新启动听记，让进程继承它；另一个窗口中的临时变量不会改变已运行的进程。

必要字段与环境变量就绪后，重跑 API 安装即可复用已完成步骤：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1 -Mode api
```

## 自检与验收记录

在程序目录运行：

```powershell
.\.venv\Scripts\python.exe -B .\tingji.py doctor --json
.\.venv\Scripts\python.exe -B .\tingji.py doctor --full --json
.\.venv\Scripts\python.exe -B .\tingji.py status --json
```

doctor 退出码 `0` 表示所列环境检查健康，`2` 表示检查失败。`--full` 会导入依赖并核对默认音频设备，不录音、不加载识别模型、不调用 API；保存目录写入检查只使用自己的临时文件。`status` 只读取最近一场会议状态，没有最近会议时可返回 `idle`。

AI 的交付记录至少应说明所选模式、实际保存位置、已有配置是否保留、doctor 退出码及警告、安装失败时的日志路径。即使 doctor 通过，也应写明“尚未验证真实录音、本地模型推理或 API 服务转写”，按本次实际执行范围保留未验证项。

三项反馈配置默认开启，省略时由共享 schema 补齐：`show_processing_window` 控制结束后的轻量处理状态自动展开，`notify_device_changes` 提醒录音途中默认设备变化，`auto_open_completed_note` 控制空闲时自动打开 Obsidian 成品。`show_processing_window: false` 在结束时收起已有窗，仍允许从托盘手动展开，阶段刷新不会关闭手动展开的窗。安装器保留旧配置，无需为了增加字段覆盖它；若用户要求关闭某项，只修改对应布尔值。普通文件夹默认 `open_in_obsidian: false`，因此即使自动打开字段为 `true` 也不会自动打开应用。

动效配置 `animation_mode` 允许 `"auto"`、`"on"`、`"off"`，旧配置省略时采用 `"auto"`。`auto` 跟随 Windows 客户区动画及高对比设置；`on` 只为听记开启动效，适合系统关闭动画但希望听记仍有动效；`off` 停用非必要动画。只按用户选择修改这一字段，保留其他已确认设置；三种模式都不改变 Windows 设置。

快捷键由三个字段明确配置：`hotkey` 默认 `Ctrl+Alt+L`，`pause_hotkey` 默认 `Alt+Space`，`bookmark_hotkey` 默认 `Ctrl+Alt+K`。格式和支持的键见配置说明；三者的实际组合不能相同。暂停与标记仅在录音时注册。AI 修改时保留用户已有组合，检查格式、配置内冲突和真实注册结果；注册失败不能当作已生效。`silence_warning_seconds` 默认为整数 `60`，允许 `0`–`3600`，`0` 关闭；不要新增未被 schema 支持的音量或字体字段。

`asr_denoise` 允许 `"light"`、`"off"`，省略时默认 `"light"`。轻量模式只对送入识别的片段副本做零相位 70 Hz 高通，不改动保存 WAV，不代表全频降噪或已提升中文准确率。用户已有选择应保留；需要比较时用相同输入、人工真值分别测量，不能凭滤波完成就报告识别质量提高。

降噪配置用于新会议；已开始的会议和已有缓存的重试沿用本场保存的模式，旧缓存缺失模式时按 `off` 保留原处理。不要为使旧缓存采用新设置而删除缓存、修改会话快照或强制重新上传音频。

需要真实验收时，先取得用户对测试录音的同意；API 模式还需取得向所选服务上传这段测试音频的同意。已有明确授权可继续，不要重复索取。使用无敏感内容的短测试，人工启动听记后分别核对麦克风、电脑声音、暂停/继续和结束操作，再核对 WAV 回放、中文转录、输出文件及选择的打开方式。不要为了安装验收录下用户正在进行的会议。

对体验改动再分别核对：正常录音不在麦克风下方显示整场状态；左侧波形随任一路实际输入跳动，麦克风图标只反映麦克风；关闭麦克风后电脑声音继续录制，整场暂停才停止波形并在左侧显示「已暂停」；悬停设备名和两路状态正确；结束后仍是 186×68 可收起的小窗且隐藏录音按钮；收起后托盘继续显示处理阶段；后台前一场完成时不打断正在录制的新一场；「最近会议」打开和重试都指向所选时间。默认设备变更提醒只用于 ID 留空的那一路，不自动迁移设备；安静和手动暂停不应被当作故障。硬件切换未测试时明确保留该项未验证。

动效与成品入口验收：开启动效时波形随真实输入平滑变化、安静时回落，按钮有约 120–180 毫秒过渡；核对 `auto` 跟随 Windows 设置、`on` 仅为听记启用、`off` 停用非必要过渡与循环等待动画，均保留静态状态及真实输入、进度。转写等待动画只表示后台处理中，不能当作识别百分比或录音输入。完成后两行应为实际「录音：<相对路径>」「转录：<相对路径>」，相对于配置保存根目录；长路径优先省略目录，文件名仍过长时省略其中间部分并保留扩展名，悬停为完整绝对路径。分别点击定位、右键复制，并核对托盘最近会议的 WAV/Markdown 定位始终对应所选场次。保留 `auto_open_completed_note` 的 Obsidian 自动打开条件。

本轮功能还需在用户可操作入口逐项核对：

- 录音时从全局快捷键、托盘、浮窗空白处或录音按钮右键标记重点，核对 Markdown 的「重点时刻」与 WAV 时间轴一致；整场暂停不压缩时间轴。
- 浮窗右键能使用托盘操作，结果路径右键仍复制对应完整路径；未录音时暂停与标记快捷键已释放。用独立配置测试三个快捷键自定义及配置冲突报错，不覆盖用户配置。
- 在隔离录音中验证两路持续无声明确满阈值只提醒一次；有声恢复后重计时，关闭麦克风但电脑有声不提醒，整场暂停不提醒。可在测试配置缩短秒数，验收后恢复用户值。
- 空闲时给已完成会议命名，检查 MD 文件名、历史标题、打开与重试入口一致；同名文件不会覆盖，WAV 哈希与路径不变。正在录音或转写的场次不可命名。
- 在使用中的缩放比例核对浮窗和托盘/浮窗菜单文字清晰且没有裁切；有多屏时分别验证切换比例。尺寸为 186×68 逻辑像素，不要求截图的物理像素固定为该数值。没有验证的缩放比例要单独写明。
- 只对独立夹具删除两份正常成品，核对最近会议不再误报缺失；只删一份、未完成或异常任务、保存根目录不可用仍可见。不删除用户会议或恢复资料。
- 新转录没有录制信息模板行、空「我的笔记」和 HTML 标记；旧标记安全迁移，用户正文改动受到保护。说话人分组限制提示仍保留，不能用几次输出一致代替质量验收。
- 对跨轨说话人，明确验收同一人统一、不同人分开；新策略按讲话片段联合分组，短段、重叠和身份区分不足可待确认，须同时报告误并、漏并与待确认时长。不能把需求已实现为算法入口等同于准确率已达标。

异常恢复测试使用独立测试目录和无敏感的复制资料。检查活跃转写进程接回且不重复启动、退出期间完成后出现结果入口、有原始录音的失败场次对应重试、中断录音仅提供状态和资料检查而不提供普通重试。核对同一可读状态的提示去重，以及重试或状态变化后的新提示；异常 JSON 或路径无法安全记录提示时允许每次启动提醒。不要为了测试而结束用户会议或删除其恢复文件。

维护者在完整源码目录运行回归：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Test.ps1
```

`Test.ps1` 先编译执行原生会议目录、浮窗、动效与控制器回归，再运行 Python 定向回归。原生测试验证实际控制器、浮窗与会议目录类，不录制音频，不调用转写 API。记录实际退出码，失败先处理原因再重跑；不能把未运行的项目报告为通过。

只有这条用户可操作路径实际完成后，才能报告相应录音与转写链路通过。自检通过、依赖安装成功和模拟接口测试不能代替新机器或真实供应方的这一层证据。

完整字段约束与设备选择方式见 [配置说明](configuration.md)。录音稳定性、中文转录与说话人质量应采用人工真值和多场景指标，见 [质量标准与证据](quality.md)；当前证据不足以宣称高质量已达标，AI 不得据此删除限制说明。
