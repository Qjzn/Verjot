# 听记 · Tingji

一个 Windows 会议录音与中文转写小工具。按快捷键开始，会议结束后得到 **一条合并 WAV + 一份 Markdown 转录**。支持普通文件夹和 Obsidian；无需设置窗口，安装与配置可以交给 AI。

日常只有托盘和 **186×68 的置顶浮窗**：左侧计时与录音输入波形，中间麦克风开关与输入反馈，上方收起，右侧纯红方块结束。正常录音时不显示「录音中」文字；任一路有实际声音时左侧波形跳动，安静时回落。关闭麦克风用划线图标表示，电脑声音继续录制；整场暂停时波形停止，并在左侧显示「已暂停」。收起后继续录音，双击托盘恢复。蓝色托盘图标表示就绪，红色表示录音。

输入波形平滑变化，按钮状态使用约 120–180 毫秒的短过渡。`animation_mode` 默认 `"auto"`，跟随 Windows 客户区动画及高对比设置；`"on"` 只为听记开启动效，适合系统关闭动画但希望听记保留动效的情况；`"off"` 关闭非必要动画。三种模式都不修改 Windows 设置，波形只反映真实输入，安静时不制造音量跳动。

| 操作 | 默认方式 | 效果 |
| --- | --- | --- |
| 开始 / 结束会议 | `Ctrl+Alt+L` | 同时收录麦克风与电脑声音；结束后自动转写。 |
| 暂停 / 继续整场 | `Alt+Space`，按一次切换 | 同时暂停两路声音；恢复时保留之前的麦克风开关状态。只在录音期间占用此快捷键。 |
| 关闭 / 开启麦克风 | 点击浮窗麦克风 | 只影响听记的收录，电脑声音继续录制；不改变会议软件的通话。 |
| 收起 / 展开浮窗 | 上方箭头 / 双击托盘 | 录音持续进行。 |
| 查看处理结果 / 重试 | 托盘「最近会议」 | 按会议时间选择对应笔记、录音资料或失败重试；复用已完成片段，保留手写区内容。 |

默认每场重新选择 Windows 默认麦克风和播放设备，也可在配置中固定设备。悬停可查看本场设备名称和两路输入状态；安静、麦克风关闭、整场暂停与设备错误分别显示。录音途中默认设备改变会提醒，但当前录音继续使用本场原设备，不自动迁移；固定设备 ID 不随默认设备变化提醒。暂停区间在成品录音中保留静音，时间戳与会议时间一致，转录中会标记暂停区间。

## 结束后与异常恢复

结束录音后，原有浮窗改为两行处理状态，隐藏麦克风和结束等录音按钮，仍可收起。等待动画表示后台正在处理；阶段名称和片段数只采用实际状态，动画不代表音频输入或整场百分比。托盘持续显示正在执行的阶段；已经取得片段总数时显示真实完成段数。开始下一场时浮窗与托盘优先显示当前录音，悬停可查看后台任务数量。

完成后小窗两行分别显示「录音：<相对路径>」「转录：<相对路径>」，直接展示实际保存位置。路径相对于配置的保存根目录，长路径优先省略目录，文件名仍过长时省略其中间部分并保留扩展名；悬停查看完整绝对路径，点击在资源管理器定位该文件，右键复制完整路径。托盘「最近会议」也可分别定位所选场次的 WAV 和 Markdown。

托盘「最近会议」用精确到秒的会议时间标识各场状态，打开笔记、查看录音资料和重试都对应所选那一场。菜单最多显示 20 场，优先包含需要关注的记录；更早的本机资料可从 `work/` 查看。上一场失败后即使已经开始下一场，重试也不会改指向新录音。转写完成时，只有启用 Obsidian、允许自动打开且当前没有录音，才自动打开刚完成的笔记；录音中完成的会议从托盘手动打开，不切换当前前台窗口。

启动时检查本机 `work/` 中的会议状态。仍在运行的原转写进程会被接回，不重复启动；工具退出期间已完成的会议提供结果入口，失败的会议在存在原始录音时提供对应重试。需要关注的同一可读状态只提示一次；重试或状态改变后可以再提示。JSON 或路径异常无法安全记录提示状态时，每次启动仍会提醒检查。开启处理浮窗时用原小窗展示提示。中断录音只提供状态与资料检查入口，不能直接按普通失败任务重试；不自动继续录音、修复文件头、删除原资料或上传旧录音。文件是否可恢复需要先检查，不能保证任何中断都能完整找回。

三项反馈可通过 `show_processing_window`、`notify_device_changes`、`auto_open_completed_note` 控制，省略时均为 `true`。旧配置省略 `animation_mode` 时采用 `"auto"`，其他已确认字段保留原值。`show_processing_window: false` 不自动显示会后浮窗，结束时收起已有窗；仍可从托盘手动展开状态和结果，后续阶段刷新不会把手动展开的窗关闭。

## 交给 AI 安装

把下面这段需求连同源码目录交给你的 AI：

> 请按 `documentation/ai-install.md` 安装听记。先核对保存目录、local/api 模式与已有环境，运行 `Install.ps1 -Plan`，再执行安装。保留已有配置和会议；密钥仅使用环境变量。完成后运行 doctor，说明实际验证与未验证的部分；不要默认修改开机启动。

完整步骤见 [AI 安装说明](documentation/ai-install.md)。配置字段、默认值、示例与排障见 [配置说明](documentation/configuration.md)；机器可读约束在 [config.schema.json](config.schema.json)，首次配置模板为 [config.example.json](config.example.json)。

需要 **Windows 10/11 x64、Python 3.12 x64、.NET Framework 4.x**。以下命令在源码目录执行，保存位置请换成自己的路径：

```powershell
# 先看计划；不下载、不创建配置、不修改启动项
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1 -NotesPath 'D:\Meetings' -Mode api -Plan

# 确认模式后安装；API 服务参数还需按配置说明补齐
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1 -NotesPath 'D:\Meetings' -Mode api
```

本地 CPU 模式改为 `-Mode local -Backend cpu`；NVIDIA CUDA 环境可选 `-Mode local -Backend cuda128`。首次使用 Obsidian 加 `-UseObsidian`，需要登录自启才加 `-StartWithWindows`。安装器保留现有 `config.json`；已有 Torch 后端与参数不一致时会停止，需按实际环境选择参数。安装过程不启动录音，也不调用 API。

首次 API 安装会留下模板，服务地址和模型名未填写时自检失败并返回非零退出码。补齐配置与密钥环境变量后重跑安装，不能把模板生成当作安装就绪。

## 选择转写方式

| 模式 | 中文转写 | 本机安装与数据流 |
| --- | --- | --- |
| `local` | Qwen3-ASR-1.7B | 下载识别与说话人模型及 PyTorch 等依赖，体积为数 GB；会议处理离线完成。GPU 更适合长会议，CPU 速度需要在目标机器实测。 |
| `api` | 用户配置的语音转写服务 | 不装本地 Qwen、Torch、Transformers；仍安装小型说话人模型及音频处理依赖。讲话片段上传所选服务。 |

API 兼容 **`POST /audio/transcriptions`**，使用 WAV、`model`、`language=zh` 和 JSON 文本返回。普通聊天模型能否用作语音转写，必须以供应方实际支持的这个端点为准。服务地址填写含版本路径的前缀，如 `https://provider.example/v1`；密钥通过 `api_key_env` 指定的环境变量读取，配置里不放密钥值。

两种模式都在本机用 sherpa-onnx / 3D-Speaker 做说话人分组，麦克风与电脑声音分别识别、分组，再合并为一条回放录音。两路上的同一说话人不会自动归并；噪声、串音和多人同时讲话可能影响分组与识别。转录提供说话人编号与时间戳，不推断真实姓名，不自动生成会议结论。

模型在会议结束处理时加载。模型下载、运行库和个人配置保存在安装机器上，**不放进 Git 仓库**。API 模式也并非完全没有本地模型。

## 文件与配置

首次安装默认写普通 Markdown，不要求 Obsidian。开启 `open_in_obsidian` 后采用 Obsidian 音频引用；是否自动打开由 `auto_open_completed_note` 和当前录音状态共同控制。旧配置省略这些字段时保留 Obsidian 输出和空闲时自动打开。更改配置请先结束录音、等待转写并退出托盘，然后重启。

```text
保存目录/
└─ 会议记录/
   ├─ 2026-10-14 14-30-00.md
   └─ 附件/20261014-143000/meeting.wav
```

“附件”是唯一录音的保存目录，MD 引用同一 WAV，不会再复制音频。临时分轨、片段和日志在程序的 `work/`；成品验证成功后清理，笔记目录只留上述两份文件。失败时保留本机恢复资料，便于重试。`work/` 内的 `controller.json` 仅记录本机进程身份与提示状态，用于接回任务和避免重复提示，不是额外会议附件。手写内容放在自动转写标记之外，重试保留其原始字节。

## 自检、验证与更新

```powershell
.\.venv\Scripts\python.exe -B tingji.py doctor --full --json
.\.venv\Scripts\python.exe -B tingji.py status --json
.\.venv\Scripts\python.exe -B -m unittest test_tingji_config test_api_asr test_meeting_worker test_merge_audio test_prepare_audio -v
# 完整源码回归：Python + 编译执行原生控制器、浮窗和会议目录测试
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Test.ps1
```

`doctor` 检查配置、依赖、模型文件、写入权限与空间；`--full` 进一步导入依赖并检查音频设备，始终不录音、不加载识别模型、不调用 API。健康退出码为 `0`，失败为 `2`。`status` 读取最近一次会议的处理状态。自检通过后，仍需用一段明确授权的音频验证目标机器的真实输入、输出和转写服务。

自动化回归覆盖配置、API 请求与断点恢复、混音时间轴、原子发布、成品校验和手写区保护。原生回归覆盖按会议重试、进程接回、启动恢复提示、两路输入反馈和轻量处理浮窗。真实硬件切换、API 供应方与全新机器安装需分别验证；模拟接口和界面测试不能证明供应方可用、中文准确率或目标机器的设备行为。

运行 `ObsidianMeeting.exe` 启动听记，此文件名为兼容现有快捷方式保留。开发更新可用 `Build.ps1 -OutputPath ObsidianMeeting.next.exe` 生成候选；构建需要同版 `ObsidianMeeting.cs`、`SessionCatalog.cs` 与图标资源。更新前完成会议及转写并正常退出托盘，保留本机 `config.json` 与 `work/`。`assets/Create-Icons.ps1` 可重新生成图标。

旧版附件整理工具默认只规划：`Compact-Completed-Meetings.py`；未合并但完整的旧记录可先用 `Prepare-Legacy-Merged-Meetings.py`。实际变更需明确传入 `--apply`，后者还要求 `--only-session` 名单。只清理已验证的生成文件，未知文件与缺少成品的记录保留。

产品边界与后续小功能候选见 [产品范围](documentation/product-scope.md)。第三方条款见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
