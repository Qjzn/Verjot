# 听记配置说明

听记从程序目录中的 `config.json` 读取配置。配置与 `ObsidianMeeting.exe`、`meeting_worker.py` 放在同一目录；它不从当前 PowerShell 目录、环境变量或其他位置寻找配置。

首次安装使用 `config.example.json` 创建配置；安装程序不会覆盖已有 `config.json`。可以由 AI 按本文填写和检查，日常使用无需设置界面。修改配置前先完成录音、等待转写结束并退出托盘程序，修改后重新启动，让录音程序与后台转写使用同一份配置。

## 普通文件夹与 Obsidian

`vault_root` 是输出根目录，也可以是普通文件夹，并不要求安装 Obsidian。该目录必须已经存在；首次安装时由安装程序创建。笔记保存在 `vault_root/meeting_folder/`，完整会议录音保存在该目录内的 `附件/会议编号/meeting.wav`。

- `open_in_obsidian: false`：生成普通 Markdown，录音使用相对于笔记的标准 Markdown 链接。转写完成后不自动打开 Obsidian 或其他应用，可以手动从托盘的「打开会议记录」或资源管理器查看文件。
- `open_in_obsidian: true`：生成适合 Obsidian 的笔记与录音 Wiki 链接，并在转写完成后通过 `obsidian://` 打开笔记。需要已安装 Obsidian，且输出根目录是已登记的笔记库；`vault_id` 可填写其库 ID 或库名。

为兼容旧配置，省略 `open_in_obsidian` 时默认是 `true`。新版 `config.example.json` 显式填写 `false`，便于先用普通文件夹完成安装与验证。

## 字段、默认值与约束

配置必须是一个 JSON 对象。所有字符串字段都必须使用字符串，不能用数字、布尔值、数组或 `null` 代替；可留空的字段应填写 `""`。未知字段会被拒绝，拼错字段名也会报错。

| 字段 | 类型 | 省略时的默认值 | 必填 | 约束与用途 |
| --- | --- | --- | --- | --- |
| `vault_root` | 字符串 | 无 | 是 | 已存在的绝对普通目录，不能使用相对路径或链接目录。普通文件夹模式与 Obsidian 模式都使用此字段。 |
| `vault_id` | 字符串 | `""` | 否 | Obsidian 库 ID 或库名。普通文件夹模式不使用。Obsidian 模式留空时先尝试 `vault_name`，再使用根目录的文件夹名称。 |
| `vault_name` | 字符串 | `""` | 否 | 兼容旧配置的库名字段。新配置优先填写 `vault_id`；普通文件夹模式可以留空。 |
| `meeting_folder` | 字符串 | `"会议记录"` | 否 | 根目录内的非空相对目录，可以使用安全的多级目录。禁止绝对或带根的路径、`.`、`..`、空路径段、Windows 非法字符和保留设备名。 |
| `mic_device_id` | 字符串 | `""` | 否 | 麦克风输入设备的完整 ID。留空选择 Windows 默认录音设备，具体为 `Capture / Console` 角色。 |
| `system_device_id` | 字符串 | `""` | 否 | 电脑声音输出设备的完整 ID。留空选择 Windows 默认播放设备，具体为 `Render / Multimedia` 角色；通过该设备录制电脑声音。 |
| `asr_context` | 字符串 | `""` | 否 | 可选的识别术语提示，最多 2000 个字符。适合项目名、产品名、专有词；不是会议内容、历史对话或摘要指令。 |
| `hotkey` | 字符串 | `"Ctrl+Alt+L"` | 否 | 全局开始/结束录音快捷键。至少包含一个 `Ctrl`、`Alt`、`Shift`、`Win` 修饰键，最后一个键为 `A`–`Z`、`F1`–`F24` 或 `Space`。同一修饰键不能重复。 |
| `pause_hotkey` | 字符串 | `"Alt+Space"` | 否 | 整场录音暂停/继续快捷键，格式同 `hotkey`，且不能与其相同。仅在录音时注册；再次按下恢复麦克风和电脑声音。 |
| `open_in_obsidian` | 布尔值 | `true` | 否 | 填写 `false` 使用普通 Markdown 并关闭完成后的自动打开；填写 `true` 使用 Obsidian。不能写成字符串 `"false"` 或 `"true"`。 |
| `transcription_mode` | 字符串 | `"local"` | 否 | 仅允许 `"local"` 或 `"api"`。新版模板使用 `"local"`。 |
| `api_base_url` | 字符串 | `""` | API 模式是 | OpenAI 兼容服务的完整 HTTP(S) 前缀，包含服务所需的版本路径。程序追加 `/audio/transcriptions`，不会猜测或补齐 `/v1`。 |
| `api_model` | 字符串 | `""` | API 模式是 | 服务实际支持的语音转写模型名，必须非空。不能用聊天模型名代替语音转写模型名。 |
| `api_key_env` | 字符串 | `"TINGJI_API_KEY"` | 否 | 存放 API 密钥的环境变量名称，不是密钥本身。API 模式要求启动进程能读取该变量中的非空密钥。 |
| `api_timeout_seconds` | 整数 | `120` | 否 | API 请求超时秒数，允许 `10`–`600`；不能使用浮点数、字符串或布尔值。 |

默认值指代码在字段省略时采用的值，不等同于示例文件中显式填写的值。

`meeting_folder` 可填写 `"会议记录"` 或 `"项目A/会议记录"`；不要填写 `"../会议记录"`、`"./会议记录"`、`"D:\\会议记录"`、`"项目A//会议记录"` 或 `"CON"`。

快捷键示例：`"Ctrl+Alt+L"`、`"Ctrl+Shift+F8"`、`"Win+F10"`、`"Alt+Space"`。须按示例大小写填写；`"L"` 缺少修饰键，`"Ctrl+Ctrl+L"` 重复修饰键，`"Ctrl+Alt+1"` 使用了不支持的普通数字键。合法组合仍可能被系统或其他程序占用；注册失败时需退出程序、修改配置并重新启动。

默认 `Ctrl+Alt+L` 开始或结束整场录音；录音过程中按一次 `Alt+Space` 暂停整场，再按一次继续，麦克风和电脑声音同时暂停。浮窗中的「关闭麦克风」只控制麦克风，与整场暂停是不同操作。`Alt+Space` 在未录音时不由听记注册，系统和其他程序可以使用它。

## 普通文件夹配置示例

以下示例目录须先存在，再启动听记。请替换成实际的输出目录；不要把示例中的路径当作已安装目录。

```json
{
  "vault_root": "D:\\Meetings",
  "vault_id": "",
  "vault_name": "",
  "meeting_folder": "会议记录",
  "mic_device_id": "",
  "system_device_id": "",
  "asr_context": "",
  "hotkey": "Ctrl+Alt+L",
  "pause_hotkey": "Alt+Space",
  "open_in_obsidian": false,
  "transcription_mode": "local",
  "api_base_url": "",
  "api_model": "",
  "api_key_env": "TINGJI_API_KEY",
  "api_timeout_seconds": 120
}
```

如需 Obsidian，在确认笔记库已登记后，把 `vault_root` 改为该库的完整路径，填写 `vault_id`，并将 `open_in_obsidian` 改为 `true`。

## JSON 文件格式

- 使用 UTF-8 保存。JSON 不支持注释，不允许末尾多余逗号。
- Windows 路径中的反斜线必须写成 `\\`，例如实际路径 `D:\Meetings` 在 JSON 中是 `"D:\\Meetings"`。
- 字符串使用英文双引号；布尔值使用小写、不带引号的 `true` 或 `false`。
- 不要在配置中添加本文未列出的字段。

## 默认设备与固定设备

默认设备和固定设备是两种选择方式：

- ID 留空时，每次开始一场录音都会按上述 Windows 角色取得当时的默认设备。这里不是“默认通信设备”角色，也不是自动寻找当前有声音的任意设备。录音途中更改 Windows 默认设备，不会把当前场次自动迁移到新设备。
- 填写设备 ID 时，听记固定使用该设备，设备名称不能代替 ID。设备被拔出、禁用或失效时，不能假定会自动切换到另一设备，应重新检查 ID。

程序的 `--list-devices` 入口可列出当前活动的输入与输出设备，并写入程序目录中的 `audio-devices.json`。选择麦克风应使用 `Capture` 设备 ID，选择电脑声音应使用 `Render` 设备 ID。安装和排障时可以让 AI 根据设备清单与默认设备诊断报告核对，配置文件中只保存所选 ID。

## 本地转写与 API 转写

`transcription_mode: "local"` 使用本地模型完成说话人分组与中文转写，会议音频不上传。首次安装需要取得模型和依赖，实际处理会议时使用已下载的本地文件。

`transcription_mode: "api"` 仍使用本地的小模型分析音轨、分组说话人，但将分出的讲话音频片段上传到配置的 API 服务进行转写。选择 API 模式就意味着这些片段会离开本机；其保存、保留期限和使用政策由所选服务决定。此模式仍需要说话人分组模型，不能把它当作完全不需要本地模型的模式。

API 使用 OpenAI 兼容的 `/audio/transcriptions` 接口。`api_base_url` 填写完整服务前缀，例如 `https://api.example.com/v1`，实际请求地址为 `https://api.example.com/v1/audio/transcriptions`；不要在前缀中重复填写该接口路径，也不能包含空白、地址内的账号密码、查询参数或片段。模型名以服务供应方实际支持的语音转写模型为准。

API 模式必须同时满足：服务前缀有效、模型名非空、`api_key_env` 指向的环境变量含有密钥。诊断会检查这些条件。配置文件只保存环境变量的名称，不保存密钥；不要添加 `api_key` 字段，也不要把密钥写进 `api_base_url`、`asr_context`、文档或诊断报告。

让运行听记的进程继承所需环境变量后再启动。修改环境变量后要重新启动听记；仅在另一个 PowerShell 窗口设置变量，不会改变已经运行的听记进程。

模型下载由安装步骤完成，模型、虚拟环境与个人配置不应提交到源码仓库。无需为了填写配置把模型上传到 Git。

每场会议保存录制时的转写模式、模型与 API 服务前缀。尚未完成的场次重试时，若这些设置已改变，程序会保留录音与已有成品并提示恢复原配置，避免把原本选择本地处理的录音上传，或把录音发往另一服务。没有模式信息的旧录音按本地模式处理。更换模式或服务应在现有场次处理完成后进行；已经完成的成品不需要重新转写。

## 本地模式的 CPU、GPU 与识别设置

CPU 或 CUDA 后端由安装步骤选择并安装，不能通过新增 `backend`、`gpu` 等配置字段切换。模型与 Python 环境使用程序目录中的固定位置：`.venv/Scripts/python.exe` 与 `models/`，不支持用 `python_path`、`model_dir` 等配置键覆盖。

本地会议转写实际使用 ASR 的 `auto` 选择：CUDA 可用时优先使用 GPU，CUDA 不可用或遇到显存不足时可回退 CPU。CPU 转写通常更慢。安装 CUDA 后端本身不证明机器上的 CUDA 已可用，需以诊断和实际转写结果为准。API 模式的远端转写计算不受这项本地 ASR 选择控制。

`asr_engine.py` 独立命令行另有 `--device`、`--no-cpu-fallback` 和 `--max-new-tokens` 等参数；这些不是 `config.json` 字段。会议录音入口不会从配置传递这些参数，当前使用其默认值：`auto`、允许 CPU 回退、每段最多生成 512 个 token。识别语言为中文。

## 让 AI 运行配置与设备诊断

在程序目录运行以下命令，`--json` 输出供 AI 读取的 JSON 报告。不要使用 `ObsidianMeeting.exe --doctor`，诊断入口是 `tingji.py`。

```powershell
.\.venv\Scripts\python.exe tingji.py doctor --json
.\.venv\Scripts\python.exe tingji.py doctor --full --json
```

基础诊断检查严格配置与 schema、Python 3.12 x64、必要依赖的安装元数据、程序和 DLL、所需模型文件、目录写入能力及磁盘余量。目录写入检查只创建并删除自己的临时文件；磁盘余量不足会报告警告。API 模式检查服务前缀、模型名和密钥环境变量；只输出变量存在或缺失，不输出密钥值。

`--full` 进一步真正导入运行时依赖、检查本地 Torch/CUDA 可用性，并调用程序的设备诊断入口，从临时 JSON 报告核对默认麦克风、默认播放设备和配置设备是否活动可用。基础和完整诊断都不录音、不加载识别模型、不触发转写、不调用云 API、不联网，也不自动修改设置。

诊断通过代表报告中这些检查成功，不能替代一段已授权测试录音，也不能证明 API 服务接受了实际转写请求。AI 应保留未执行和警告项，不把它们当作已通过的证据。

## 配置出错时

启动时先检查配置结构、字段类型、输出目录和快捷键，按错误提示修正后再启动。路径不存在、字段拼错、类型错误或快捷键格式错误，应在开始真实录音前处理。

有效的配置不能证明设备已可录音、Obsidian 已登记或 GPU 已可使用；安装完成后仍需检查设备诊断，并用一段明确授权的测试音频验证录音与转写。修改输出目录、子目录或打开模式前，应先等待现有转写完成，避免同一场录音前后使用不同配置。
