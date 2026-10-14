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

目标机器需要 64 位 Windows、Python 3.12 x64，以及可用的 .NET Framework 4.x 编译器。检查已经安装的 Python：

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

需要真实验收时，先取得用户对测试录音的同意；API 模式还需取得向所选服务上传这段测试音频的同意。已有明确授权可继续，不要重复索取。使用无敏感内容的短测试，人工启动听记后分别核对麦克风、电脑声音、暂停/继续和结束操作，再核对 WAV 回放、中文转录、输出文件及选择的打开方式。不要为了安装验收录下用户正在进行的会议。

只有这条用户可操作路径实际完成后，才能报告相应录音与转写链路通过。自检通过、依赖安装成功和模拟接口测试不能代替新机器或真实供应方的这一层证据。

完整字段约束与设备选择方式见 [配置说明](configuration.md)。
