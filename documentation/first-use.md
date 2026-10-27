# verbit：首次真实验收

> beta.11 首期调整：新用户默认仅录音，无推荐本地模型下载；速记、原生 MP3 导出与可选第三方 API 均无需 Python 或模型。新 API 提交完整 MP3，身份信息仅来自供应方；已有 local 模式及下文历史 Python 流程保留兼容，不属于新用户推荐路径。新会议按 `verbit年月日时分/同名.mp3` 保存，API 成功后生成同名 Markdown。实际配置见[安装说明](install-windows.md)。


`doctor --full` 只检查依赖、配置、模型文件、编码器及设备枚举。它不会录音、播放、加载转写模型或调用 API，不能用它的成功代替“这台电脑能开会”。

`verify` 是单独、明确授权的实际验收。它复用生产 `RecordingSession`、无损分段、混音、MP3 编码、说话人准备和转写代码。采集器运行在复制后的私有目录，既不注册全局快捷键，也不读取或写入正式笔记正文、系统剪贴板；不改音量、默认设备或开机启动。

先看方案，不产生文件或声音：

```powershell
.\.venv\Scripts\python.exe -B tingji.py verify --plan --transcribe --json
```

在用户确认可以短暂录制后，让用户使用正常开会的麦克风说约两句中文，并自行播放一段已知内容。运行：

```powershell
.\.venv\Scripts\python.exe -B tingji.py verify --seconds 20 --consent-record --json
```

如果需要 verbit 播放测试音，先准备自己有权使用的、已知逐字稿的 PCM WAV。测试音必须小于 32 MiB、单/双声道、长度至少比录制时长短 1 秒。只有获得播放同意才加以下两个参数。播放使用 Windows 默认输出，保持原音量；命令不会替用户切换扬声器或耳机。

```powershell
.\.venv\Scripts\python.exe -B tingji.py verify --seconds 30 --consent-record `
  --play-test 'D:\AudioTests\known-chinese.wav' --consent-playback --transcribe --json
```

本地模式的 `--transcribe` 会加载已安装的本地模型，速度受硬件影响。默认每个准备/转写子阶段限时 600 秒，可用 `--timeout-seconds 1200` 调整，范围为 30–3600 秒。超时不代表识别模型不支持该音频。原始资料保留在隔离目录，不重复采集来掩盖失败。

API 模式还必须得到“允许将本轮测试音频上传到配置的服务”这一单独同意，再加入 `--consent-upload`：

```powershell
.\.venv\Scripts\python.exe -B tingji.py verify --seconds 20 --consent-record `
  --transcribe --consent-upload --json
```

密钥仍只从 `config.json` 的 `api_key_env` 指定的进程环境变量读取，不写入命令行或报告。API 普通聊天模型不等于 `/audio/transcriptions` 转写模型。缺少任一必要同意或密钥时，命令在录音前退出。仅录音检查不会调用 API，即使配置选择了 API 模式。

如果最近会议仍在录音、暂停、排队或转写，命令拒绝启动，避免测试音混入会议。保存状态仍为 recording 的旧异常场次也会保守阻止验收；先按既有恢复流程确认，不能擅自修改原始状态。如果用户在验收中又主动开始会议，应中止本轮测试。

## 看结果

每轮独立保存在 `validation/first-use-<随机编号>/`，不会写入配置的会议保存目录：

| 文件 | 用途 |
| --- | --- |
| `report.json` | 所做阶段、是否通过、各来源信号能量、实际文件位置和未验收项；不含正文或密钥。 |
| `output/first-use.mp3` | 经过完整解码和无损混音长度核对的测试成品。 |
| `output/first-use.md` | 只有请求实际转写后才生成的正文；空结果不能判为转写通过。 |
| `runtime/work/<场次>/` | 原始无损分段、采集清单、混音及转写阶段结果，供本轮核对。 |

成功退出码是 `0`，未通过是 `2`。`scope: recording_only` 只表示录音技术检查，不包含模型或服务验证。报告的 `api_called` 仅表示已尝试启动 API 转写阶段，不代表服务成功接收音频。失败的 `stage` 指明停在录音、信号、混音/编码还是转写。

报告里的 `signal_detected` 只检查是否存在足够能量，底噪也可能满足阈值；不能用它证明说话内容正确。`playback_requested` 也不能证明人实际听见声音。`timeline_quality_verified`、`speaker_quality_verified` 始终为 false，`listening_review` 为 required，直到另有真实人工验收记录；命令不会自动把它们涂成绿色。

AI 应把 MP3 和 Markdown 的实际路径交给用户，请用户核对：自己的声音和电脑声音是否都在、开头和结尾是否完整、同一时刻是否重复/错位、姓名数字是否正确、同人是否拆开及不同人是否误合并。必要时用扬声器让已知测试音同时进入麦克风，再对原始双轨逐段比较时间线；耳机模式的麦克风没有测试音并不必然是漏录。

技术检查完成后保留这份简短交接：模式、`scope`、退出码、报告/MP3/Markdown 路径、用户实际回听结论、尚未通过项目。不要自动上传整个 `runtime/` 或 Markdown；其中含测试录音、正文、设备信息和本机路径。`report.json` 也可能带个人目录名称，分享前应脱敏。验收文件不自动删除，由用户确认后清理。

## 本轮实现的验证边界

自动测试仅用生成的 WAV、多段清单和模拟服务结果，实际运行生产混音与 MP3 完整解码，并覆盖分离授权、静音、损坏清单、空转写、异常报告保密和 API 调用阶段标记。没有运行麦克风、播放设备或真实云服务；编译采集桥也不是硬件录音已通过。当前用户的快捷键、录音和笔记无需为这些测试暂停。

2026-10-16 经授权完成了一次本机实际采集、播放及本地转写：电脑声音和分段合并获得有效证据，采集报告仍有麦克风驱动间断，首次验收未通过。用户随后确认听到了测试音、自己没有说话，额外讲话来自系统媒体应用；本轮麦克风近静音不能作为漏录证据，媒体讲话也不能作为 ASR 幻觉。有效麦克风人声、双路对齐及后续 MP3 人工回听仍未验证；详细数值、限定文字错误和说话人待确认结果见 [实际质量记录](quality.md#2026-10-16-首次验收命令的本机实际测试)。保留原失败报告后继续处理已有录音，不把后续 MP3 可播放或有转录改写为整轮通过。
