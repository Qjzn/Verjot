# 由 AI 代安装 verbit

项目显示名统一为小写「verbit」，此前为片言、听记。Logo 只含图形，名称单独排版。为兼容已有安装，EXE、Python 入口、密钥环境变量默认名称、`pianyan://` 回听协议和状态格式继续沿用；不要因此重建用户的配置或笔记。日常可从托盘/浮窗右键「设置…」修改保存目录及快捷键，结束会议与后台任务后保存即可生效。

这份说明供 AI 在目标 Windows 机器上执行安装和排障。先取得必要输入、查看安装计划，再安装；环境自检与真实录音验收分别记录。安装过程不启动 verbit、不录音、不调用转写 API。

用户也可先安装 [Windows EXE 版](install-windows.md)：首次启动确认保存地址和快捷键后即可速记，不要求 Python。EXE 安装包不含模型或 Python 转写环境；后续启用会议时，在已安装目录按本说明继续配置 `local` / `api` 并执行 `Install.ps1`。保留首次设置生成的配置，不把「桌面已安装」当作「会议引擎已验收」。

## 先确认输入与已有环境

请使用用户已经选定的安装位置和保存位置。未提供时，只补问会影响安装的选择：

| 输入 | 如何使用 |
| --- | --- |
| 保存目录 | 完整 Windows 路径，例如 `D:\Meetings`。可以是普通文件夹；使用 Obsidian 时应选择已经登记的笔记库。 |
| 转写模式 | `local` 在本机转写；`api` 将讲话片段提交给用户选择的服务。不要替用户决定是否上传会议音频。 |
| 本地后端 | 本地模式默认 `cpu`；只有用户选择 NVIDIA CUDA 环境时使用 `cuda128`。安装脚本不自动猜 GPU。 |
| API 服务 | API 模式需要服务前缀、实际支持的音频转写模型名，以及密钥环境变量名。密钥值由用户在机器上提供，不写入配置或安装记录。 |

源码仓库是 [Qjzn/verbit](https://github.com/Qjzn/verbit)，当前为私有仓库。使用已有授权的 Git 访问方式取得源码；已有目录优先复用。无法访问时先处理账号授权，不索取用户的明文 token，也不修改仓库公开权限。

安装或更新应取得同一版本的完整源码。不要只替换 `ObsidianMeeting.cs` 或 EXE：原生程序还依赖 `SessionCatalog.cs`、`MeetingActions.cs`、`QuickNoteStore.cs`、`NativeNoteDocument.cs` 与 `QuickNoteWindow.cs`，字段校验依赖 `config.schema.json`，Python 转写脚本与配置示例也需要保持同版。构建入口为 `Build.ps1`；回归入口为 `Test.ps1`。`app.manifest` 嵌入 Windows 10 兼容声明，构建自动复制同名 `.exe.config`，为 .NET Framework 4.8 启用 `PerMonitorV2`；正式运行必须保留 `ObsidianMeeting.exe.config`，不能仅部署 EXE。更新前保留现有 `config.json`、`work/` 与成品，不把本机资料复制进 Git。

目标机器需要 Windows 10/11 x64、Python 3.12 x64、.NET Framework 4.8，以及可用的 Framework C# 编译器。检查已经安装的 Python：

```powershell
py -3.12 -c "import sys, struct; print(sys.executable); print(sys.version); print(struct.calcsize('P') * 8)"
```

位数应为 `64`。没有 `py` 时，可向安装命令追加 `-PythonExe "已安装的 Python 3.12\python.exe 完整路径"`。脚本不会安装系统 Python、切换全局 Python 或删除已有虚拟环境；缺少 Python 时由用户安装，或在用户明确授权后另行安装。

本轮测试使用 Python 3.12.10，普通 x64 安装器见 [Python 官方发布页](https://www.python.org/downloads/release/python-31210/)。需要 pip 与 venv，不使用 embeddable package；已有 Python 3.12 x64 可通过明确路径复用，不要求切换机器全局版本。

新电脑启用本地转写前还需官方 [Microsoft Visual C++ v14 Redistributable x64](https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist)，无需 Visual Studio 或 .NET SDK。安装脚本会在大模型下载前检查其 DLL；若安装器要求重启，先由用户安排，不强制重启。不应把 DLL 加载失败误判为要重装模型。干净 Windows 的无宿主缓存步骤见 [独立验收](clean-windows-validation.md)。无声卡沙箱才显式加 `-SkipDeviceCheck`，报告会保留 `devices_checked: false`；此开关不写入配置，也不代表设备可用。

已有 `config.json` 时先只读核对它。安装脚本保留原文件；模式参数省略时继承已有 `transcription_mode`，旧配置缺省为 `local`。显式模式与已有配置冲突、已有 Torch 版本或后端与本地安装参数不匹配，都会停止并说明原因。不要用重建虚拟环境或覆盖配置来绕过提示。

verbit 正在运行时不能安装或重建。等用户结束录音和转写，再让用户从托盘正常退出；不要结束录音进程来腾出安装位置。

内置速记沿用 `vault_root`；旧配置省略 `quick_note_folder` 和 `quick_note_hotkey` 时分别默认 `"速记"`、`"Ctrl+Alt+J"`，无需覆盖整份配置。更新时保留 `work/quick-note/` 的恢复草稿和本机窗口设置；退出先保存便签，保存失败或输入法组合尚未提交时取消退出并保留编辑器，录音不会先被停止。点击底部提示可复制当前内容、查看草稿，保存冲突时可保留草稿后载入磁盘版本；草稿目录也不可写时，先复制窗口内容另存，不能承诺强制退出后可恢复。先处理提示，不能强杀 verbit 来完成更新。

## 先运行安装计划

EXE 安装版在程序安装目录执行以下命令，不追加 src。Git 源码版先进入 `E:\Tools\verbit\src`；运行环境安装在 src 下，测试统一在 tests 下。保存目录示例为 `D:\Meetings`，替换为实际路径。`-ExecutionPolicy Bypass` 只作用于这次 PowerShell 进程，不修改机器或用户的执行策略。

API 模式计划：

```powershell
Set-Location 'E:\Tools\verbit\src'
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1 -NotesPath 'D:\Meetings' -Mode api -Plan
```

本地 CPU 模式计划：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1 -NotesPath 'D:\Meetings' -Mode local -Backend cpu -Plan
```

本地 CUDA 模式把 `-Backend cpu` 改为 `-Backend cuda128`。首次使用 Obsidian 时追加 `-UseObsidian`；已有配置不受该开关修改。

用户需要点击新转录时间戳回听时，在计划及安装命令中显式加 `-EnablePlaybackLinks`。它只为当前用户注册本目录的 `pianyan://` 协议，不覆盖已归属其他安装的协议；不改变默认播放器。独立执行/撤销见 `Register-Playback.ps1 -Plan` / `-Unregister`。快捷方式显示名改为「verbit」，旧的本程序「片言」「听记」快捷方式仅在显式更新快捷方式时替换；不为改名额外开启开机启动。

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

模型续传会检查 HTTP 范围起点、终点和总量，拒绝错位或越界的响应；失败时保留有效断点。模型完整性、长会议资源和故障恢复的已验范围见 [隐患审查与验证计划](risk-review.md)。不要把正常下载、自检通过或短录音成功当作长会议与中文质量均已验收。

模型只安装到程序目录的 `models/`；虚拟环境在 `.venv/`，下载缓存在 `runtime-wheels/`，安装日志、状态与自检证据在 `installation/`。这些是本机运行文件，不应提交到源码仓库。仓库不收录模型、运行依赖、个人配置、录音、密钥或本机验证截图。第三方条款见 [THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md)。

两种模式都安装 `imageio-ffmpeg==0.6.0`，Windows wheel 包含本地 FFmpeg 编码器（本次下载约 31 MB），不需要常驻服务或设置全局 PATH。编码仅在会后运行，不上传音频。更新旧环境时也要安装新增依赖，`doctor --full` 会实际检查 `libmp3lame` 编码器。默认配置为 `recording_segment_seconds: 300`、`mp3_bitrate_kbps: 192`，缺省旧配置采用同样的新会议默认值；已有 WAV 成品保持原样。

`recording_protection_seconds` 缺省为整数 `5`，允许 `0`–`300`，`0` 关闭。结束时扣除整场暂停后的有效录音不足阈值，清理本场临时录音，不生成 MP3/Markdown、不调用本地或 API 转写；恰好达到阈值正常保留。单独关闭麦克风时电脑声仍录制，该时长仍计入保护时间。更新保留已有配置；新值只用于之后开始的会议。验收应分别检查不足/等于阈值、长时间暂停后仍不足、关闭麦克风但持续录电脑声、`0` 关闭，不能只看总墙钟时长。

分段验收可在独立测试配置临时设 `recording_segment_seconds: 10`，完成超过 20 秒、含麦克风开关和整场暂停的双路测试，再恢复为 300。检查无损段帧数连续、交界附近标记和讲话无遗漏/重复、电脑声在麦克风关闭期间持续、MP3 完整解码与原无损时长一致、Markdown 引用唯一 MP3，正常成功后笔记目录只有两份成品。不要用更短分段测试冒充数小时硬件稳定性验收。

编码回归须运行 `test_mp3_audio`：固定尾块余数 1、46、47、0 及真实复现长度，核对独立的 FFmpeg 和 libsndfile 解码帧数、最后有效信号及损坏拒绝。当前对 FFmpeg 7.1 已复现的极短尾块 Info/LAME 填充错误有受限修正，仅改本次编码产物的尾填充和标签 CRC，不改音频负载、不放宽 1 采样长度校验；未知标签或损坏文件仍保留原轨并报错，不能为了完成安装而跳过成品验证。

## 补齐 API 配置

API 服务必须支持 OpenAI 兼容的 `POST /audio/transcriptions` 音频转写端点，包括 WAV 文件、`model`、`language` 和 JSON 文本返回。只有聊天接口或只有聊天模型的供应方不能据此判断可用。

首次 API 安装会创建模板，但模板的服务地址和模型名为空。此时 doctor 失败、安装退出码为 `1` 是未完成结果；不能报告“API 已就绪”。AI 应按用户提供的信息，仅补齐 `config.json` 的必要字段，保留其他已确认设置。例如下面是占位示例，不能原样当作真实服务：

```json
{
  "transcription_mode": "api",
  "recording_protection_seconds": 5,
  "recording_segment_seconds": 300,
  "mp3_bitrate_kbps": 192,
  "api_base_url": "https://provider.example/v1",
  "api_model": "YOUR_AUDIO_TRANSCRIPTION_MODEL",
  "api_key_env": "TINGJI_API_KEY",
  "api_timeout_seconds": 120
}
```

这只是字段片段，不要用它覆盖完整配置。程序把 `/audio/transcriptions` 追加到 `api_base_url`，示例实际对应 `https://provider.example/v1/audio/transcriptions`。前缀应包含供应方要求的版本路径；不要重复填写端点后缀。

`api_key_env` 保存环境变量名称，不保存密钥值。由用户在目标机器提供该变量；AI 不索取、打印或保存密钥。已设置变量时直接复用。新设置用户环境变量后重新打开 PowerShell，并重新启动 verbit，让进程继承它；另一个窗口中的临时变量不会改变已运行的进程。

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

再参照 [首次实际验收](first-use.md) 执行 `verify --plan`，说明会采集、是否播放和是否上传；取得相应授权后再执行。交付 `report.json` 的技术结果、MP3/Markdown 路径及人工回听/文字/说话人待确认项。没有同意时停在计划，不自动录音、播放或云端上传。发布前检查 [发布验收清单](release-readiness.md)，不把本机短验收写成干净机器或数小时会议通过。

三项反馈配置默认开启，省略时由共享 schema 补齐：`show_processing_window` 控制结束后的轻量处理状态自动展开，`notify_device_changes` 提醒录音途中默认设备变化，`auto_open_completed_note` 控制空闲时自动打开 Obsidian 成品。`show_processing_window: false` 在结束时收起已有窗，仍允许从托盘手动展开，阶段刷新不会关闭手动展开的窗。安装器保留旧配置，无需为了增加字段覆盖它；若用户要求关闭某项，只修改对应布尔值。普通文件夹默认 `open_in_obsidian: false`，因此即使自动打开字段为 `true` 也不会自动打开应用。

动效配置 `animation_mode` 允许 `"auto"`、`"on"`、`"off"`，旧配置省略时采用 `"auto"`。`auto` 跟随 Windows 客户区动画及高对比设置；`on` 只为 verbit 开启动效，适合系统关闭动画但希望 verbit 仍有动效；`off` 停用非必要动画。只按用户选择修改这一字段，保留其他已确认设置；三种模式都不改变 Windows 设置。

快捷键由四个字段明确配置：`hotkey` 默认 `Ctrl+Alt+L`，`pause_hotkey` 默认 `Alt+Space`，`bookmark_hotkey` 默认 `Ctrl+Alt+K`，`quick_note_hotkey` 默认 `Ctrl+Alt+J`。格式和支持的键见配置说明；四者的实际组合不能相同。暂停与标记仅在录音时注册，速记可独立使用。AI 修改时保留用户已有组合，检查格式、配置内冲突和真实注册结果；注册失败不能当作已生效。`silence_warning_seconds` 默认为整数 `60`，允许 `0`–`3600`，`0` 关闭；不要新增未被 schema 支持的音量或字体字段。

从旧独立 Obsidian 速记迁移时，先核对并停用 `ObsidianQuickNoteHotkey.exe` 和 Startup 中指向它的「Obsidian 速记快捷键.lnk」，释放 `Ctrl+Alt+J`，保留可回滚备份及原笔记。不要结束 Obsidian、删除速记内容，或用旧 QuickAdd 配置备份覆盖现有配置；QuickAdd 宏与无快捷键的手动入口不需要为释放全局键而删除。停用是实际机器变更，应在用户已授权的迁移范围内执行并记录结果。

`asr_denoise` 允许 `"light"`、`"off"`，省略时默认 `"light"`。轻量模式只对送入识别的片段副本做零相位 70 Hz 高通，不改动保存的会议录音，不代表全频降噪或已提升中文准确率。用户已有选择应保留；需要比较时用相同输入、人工真值分别测量，不能凭滤波完成就报告识别质量提高。

`system_capture_mode` 默认 `"all_active"`，覆盖所有活跃播放端点，包含应用单独选定的输出；`"default"` 只录 Windows 默认播放设备。非空 `system_device_id` 始终优先，锁定指定设备。留空的 `mic_device_id` 跟随系统默认麦克风；新输出接入、旧设备断开和默认麦克风切换会更新采集来源。`notify_device_changes` 仅控制提示，不控制是否跟随。设备初始化期间仍可能短暂缺音，诊断不能包装成无缝切换。不要擅自修改 Windows 默认设备或音量。

`asr_echo_filter` 默认 `true`，只为满足多窗口声学门槛且 ASR 长文本完全一致的延迟回声副本去重，不改变保存的会议录音，也不代替实时回声消除。降噪和回声配置用于新会议；已开始的会议和已有缓存的重试沿用本场保存的模式，旧缓存缺失模式时按 `off`/`false` 保留原处理。不要为使旧缓存采用新设置而删除缓存、修改会话快照或强制重新上传音频。

需要真实验收时，先取得用户对测试录音的同意；API 模式还需取得向所选服务上传这段测试音频的同意。已有明确授权可继续，不要重复索取。使用无敏感内容的短测试，人工启动 verbit 后分别核对麦克风、电脑声音、暂停/继续和结束操作，再核对 MP3 回放、中文转录、输出文件及选择的打开方式。不要为了安装验收录下用户正在进行的会议。

对体验改动再分别核对：正常录音不在麦克风下方显示整场状态；左侧波形随任一路实际输入跳动，麦克风图标只反映麦克风；关闭麦克风后电脑声音继续录制，整场暂停才停止波形并在左侧显示「已暂停」；悬停设备名和两路状态正确；结束后仍是 186×68 可收起的小窗且隐藏录音按钮；收起后托盘继续显示处理阶段；后台前一场完成时不打断正在录制的新一场；「最近会议」打开和重试都指向所选时间。分别验证 `all_active` 的非默认输出、`default` 的跟随切换、固定 ID 的锁定以及默认麦克风切换；检查新增来源的初始化缺口和诊断。安静和手动暂停不应被当作故障，驱动数据间断则保留诊断。没有实际验证的设备切换明确列为未验证。

动效与成品入口验收：开启动效时波形随真实输入平滑变化、安静时回落，按钮有约 120–180 毫秒过渡；核对 `auto` 跟随 Windows 设置、`on` 仅为 verbit 启用、`off` 停用非必要过渡与循环等待动画，均保留静态状态及真实输入、进度。转写等待动画只表示后台处理中，不能当作识别百分比或录音输入。完成后两行应为实际「录音：<相对路径>」「转录：<相对路径>」，相对于配置保存根目录；长路径优先省略目录，文件名仍过长时省略其中间部分并保留扩展名，悬停为完整绝对路径。分别点击定位、右键复制，并核对托盘最近会议的 MP3/Markdown 定位始终对应所选场次，旧 WAV 历史也可正常打开。保留 `auto_open_completed_note` 的 Obsidian 自动打开条件。

速记自动回归与桌面探针必须使用隔离目录和自建数据，禁止读取、写入或清空用户的系统剪贴板，也禁止「先备份、测试后恢复」。不要调用 `Clipboard.GetDataObject`、`SetImage`、`SetDataObject`、`SetText` 或 `Clear`；不能为测试点击实际「复制当前内容」动作。禁止使用 `SendInput`、`SendKeys`、`keybd_event` 等全局输入注入，不能向当前前台窗口发送真实粘贴、复制或速记快捷键。需要观察真实安装入口时，保留未验证项，由用户主动操作；不能用桌面注入补齐证据。

图片与文字粘贴按 `TestQuickNoteEditor.cs` 的方法验证：在内存创建 `System.Windows.Forms.DataObject`，放入自建图片或 Unicode 文本，调用生产 `PasteData` 处理函数；拖入则将指向隔离图片的局部数据交给 `DropData`。局部 `DataObject.SetData` 不写入系统剪贴板。检查真实编辑器内容、相对图片引用、撤销重做、自动保存及错误状态；快捷键消息分发和 Esc 使用目标控件的本地事件或消息。性能测试使用同类隔离数据，分别记录实际处理耗时与界面响应。这些证据覆盖生产处理函数，不代表真实系统剪贴板、全局按键或用户设备入口已经验收。

逐字符输入使用 `TestQuickNoteInput.cs`：真实 `Application.Run` 消息泵向原生编辑器自己的 HWND 定向投递 `WM_CHAR`；混合组的空格用 `WM_KEYDOWN/WM_KEYUP`，由消息泵自然生成字符。在收到消息与 `TextChanged` 事件内计时，核对中英文正文、撤销重做、取消和只读输入以及关闭落盘；不要用 `AppendText` 或消息泵返回后的计时替代。性能测试串行执行，记录 p50/p95/最大值、实际字符数、投递排队和实际发送间隔，不能同时跑多个界面压力测试。该测试使用生产 manifest 和 `.exe.config`；Unicode 字符输入不等于真实拼音候选组合验证，也不能换算为屏幕帧率。原生编辑器不增加配置字段，保留现有用户配置即可。

`TestMeetingHotkeys` 真实调用 `RegisterHotKey` 验证注册、占用与释放，再向测试自身窗口投递 `WM_HOTKEY` 检查事件分发，不注入全局按键。Windows 收到真实按键后能否将其投递给已安装的 verbit，仍需用户主动按键验证并单独记录；本地 `WM_HOTKEY` 通过不能报告为真实全局按键已经通过。

旧的 `validation/QuickNoteInstalledProbe` 已停用：它曾写入测试图片到系统剪贴板并全局发送粘贴按键，结束后尝试恢复也会干扰用户当时及期间新复制的内容。不要恢复或运行它，后续探针须遵守上述隔离方式。

本轮功能还需在用户可操作入口逐项核对：

- 录音时从全局快捷键、托盘、浮窗空白处或录音按钮右键标记重点，核对 Markdown 的「重点时刻」与最终录音时间轴一致；整场暂停不压缩时间轴。
- 浮窗右键能使用托盘操作，结果路径右键仍复制对应完整路径；未录音时暂停与标记快捷键已释放。用独立配置测试快捷键自定义及配置冲突报错，不覆盖用户配置。
- 从 `Ctrl+Alt+J` 和托盘「速记便签」打开内置便签，核对默认置顶、调整尺寸、500 毫秒自动保存、Esc/关闭收起及再次打开；录音和转写期间便签不改变会议状态，`Ctrl+Alt+L` 仍控制录音。检查每日 Markdown、截图粘贴、本地图片文件粘贴/拖入及相对引用：源文件和转换后 PNG 均不超过 12 MiB、3600 万像素，多帧只取首帧。外部修改或删除笔记时不覆盖，核对库外草稿、底部恢复菜单和当天版本一致时的草稿恢复；另测草稿也不可写时保留编辑器并可复制内容。速记与录音等四个快捷键的自定义、配置冲突及速记键占用后托盘仍可使用，用独立配置核对。未实际操作的场景保留为未验证。
- 在隔离录音中验证两路持续无声明确满阈值只提醒一次；有声恢复后重计时，关闭麦克风但电脑有声不提醒，整场暂停不提醒。可在测试配置缩短秒数，验收后恢复用户值。
- 空闲时给已完成会议命名，检查 MD 文件名、历史标题、打开与重试入口一致；同名文件不会覆盖，录音哈希与路径不变。正在录音或转写的场次不可命名。
- 在使用中的缩放比例核对浮窗和托盘/浮窗菜单文字清晰且没有裁切；有多屏时分别验证切换比例。尺寸为 186×68 逻辑像素，不要求截图的物理像素固定为该数值。没有验证的缩放比例要单独写明。
- 只对独立夹具删除两份正常成品，核对最近会议不再误报缺失；只删一份、未完成或异常任务、保存根目录不可用仍可见。不删除用户会议或恢复资料。
- 新转录没有录制信息模板行、空「我的笔记」和 HTML 标记；旧标记安全迁移，用户正文改动受到保护。说话人分组限制提示仍保留，不能用几次输出一致代替质量验收。
- 对跨轨说话人，明确验收同一人统一、不同人分开；新策略按讲话片段联合分组，短段、重叠和身份区分不足可待确认，须同时报告误并、漏并与待确认时长。不能把需求已实现为算法入口等同于准确率已达标。

异常恢复测试使用独立测试目录和无敏感的复制资料。检查活跃转写进程接回且不重复启动、退出期间完成后出现结果入口、有原始录音的失败场次对应重试、中断录音仅提供状态和资料检查而不提供普通重试。核对同一可读状态的提示去重，以及重试或状态变化后的新提示；异常 JSON 或路径无法安全记录提示时允许每次启动提醒。不要为了测试而结束用户会议或删除其恢复文件。

维护者在完整源码的 src 目录运行回归；EXE 安装包不包含测试代码：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Test.ps1
```

`Test.ps1` 启动测试前扫描 tests 目录中的 `Test*.cs`，发现共享剪贴板访问、全局输入注入或 `SetForegroundWindow` 调用就停止，要求改用局部 `DataObject` 或目标窗口消息。随后编译执行原生会议目录、浮窗、动效、控制器与速记回归，再运行 Python 定向回归。

所有原生测试都由 `TestDesktopRunner.cs` 通过 `CreateDesktop` 和 `CreateProcess` 放到各自的独立测试桌面，始终不调用 `SwitchDesktop`。测试窗口可在自己的桌面显示和激活，不会激活用户正在使用的窗口，也不切换用户屏幕；桌面创建或子进程启动失败就报错，不回退到用户桌面。独立桌面用于隔离窗口与焦点，剪贴板访问仍受前述源码禁令约束。子进程标准输出与退出码用于记录结果；原生测试不录制音频、不调用转写 API。记录实际退出码，失败先处理原因再重跑；不能把未运行的项目报告为通过。

只有这条用户可操作路径实际完成后，才能报告相应录音与转写链路通过。自检通过、依赖安装成功和模拟接口测试不能代替新机器或真实供应方的这一层证据。

完整字段约束与设备选择方式见 [配置说明](configuration.md)。录音稳定性、中文转录与说话人质量应采用人工真值和多场景指标，见 [质量标准与证据](quality.md)；当前证据不足以宣称高质量已达标，AI 不得据此删除限制说明。

识别优化候选通过 `speaker_anchor_mode: "clean"` 与 `asr_segmentation: "speech"` 单独启用，当前默认保持旧路径。`asr_terms` 为明确的已知术语清单，可与 `asr_context` 合用；新会议会冻结提示和模式。AI 不得替用户猜测人名、将人数直接固定为6、为了编号好看放宽阈值，或以新模式覆盖旧会议。多人转写段没有字级归属，需保留时间范围和准确限制。启用和回退示例见 [识别候选配置](configuration.md#识别候选与术语提示)。
