# 听记 · Tingji

一个 Windows 会议录音与中文转写小工具。按快捷键开始，会议结束后得到 **一条合并 WAV + 一份 Markdown 转录**。支持普通文件夹和 Obsidian；无需设置窗口，安装与配置可以交给 AI。

日常只有托盘和 **186×68 的置顶浮窗**：左侧计时与音量变化，中间麦克风开关，上方收起，右侧纯红方块结束。收起后继续录音，双击托盘恢复。蓝色托盘图标表示就绪，红色表示录音。

| 操作 | 默认方式 | 效果 |
| --- | --- | --- |
| 开始 / 结束会议 | `Ctrl+Alt+L` | 同时收录麦克风与电脑声音；结束后自动转写。 |
| 暂停 / 继续整场 | `Alt+Space`，按一次切换 | 同时暂停两路声音；恢复时保留之前的麦克风开关状态。只在录音期间占用此快捷键。 |
| 关闭 / 开启麦克风 | 点击浮窗麦克风 | 只影响听记的收录，电脑声音继续录制；不改变会议软件的通话。 |
| 收起 / 展开浮窗 | 上方箭头 / 双击托盘 | 录音持续进行。 |
| 重试失败的转写 | 托盘菜单 | 复用已完成片段，保留手写区内容。 |

默认每场重新选择 Windows 默认麦克风和播放设备，也可在配置中固定设备。暂停区间在成品录音中保留静音，时间戳与会议时间一致，转录中会标记暂停区间。

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

首次安装默认写普通 Markdown，不要求 Obsidian。开启 `open_in_obsidian` 后采用 Obsidian 音频引用并自动打开笔记；旧配置省略这个字段时保持原来的 Obsidian 行为。更改配置请先结束录音、等待转写并退出托盘，然后重启。

```text
保存目录/
└─ 会议记录/
   ├─ 2026-10-14 14-30-00.md
   └─ 附件/20261014-143000/meeting.wav
```

“附件”是唯一录音的保存目录，MD 引用同一 WAV，不会再复制音频。临时分轨、片段和日志在程序的 `work/`；成品验证成功后清理，笔记目录只留上述两份文件。失败时保留本机恢复资料，便于重试。手写内容放在自动转写标记之外，重试保留其原始字节。

## 自检、验证与更新

```powershell
.\.venv\Scripts\python.exe -B tingji.py doctor --full --json
.\.venv\Scripts\python.exe -B tingji.py status --json
.\.venv\Scripts\python.exe -B -m unittest test_tingji_config test_api_asr test_meeting_worker test_merge_audio test_prepare_audio -v
```

`doctor` 检查配置、依赖、模型文件、写入权限与空间；`--full` 进一步导入依赖并检查音频设备，始终不录音、不加载识别模型、不调用 API。健康退出码为 `0`，失败为 `2`。`status` 读取最近一次会议的处理状态。自检通过后，仍需用一段明确授权的音频验证目标机器的真实输入、输出和转写服务。

自动化回归覆盖配置、API 请求与断点恢复、混音时间轴、原子发布、成品校验和手写区保护。维护机另实测了全局快捷键、两路暂停、麦克风开关、收起、结束和两份成品。真实 API 供应方与全新机器安装需另外验证；模拟接口成功不能证明供应方可用或其中文准确率。

运行 `ObsidianMeeting.exe` 启动听记，此文件名为兼容现有快捷方式保留。开发更新可用 `Build.ps1 -OutputPath ObsidianMeeting.next.exe` 生成候选；更新前完成会议及转写并正常退出托盘。`assets/Create-Icons.ps1` 可重新生成图标。

旧版附件整理工具默认只规划：`Compact-Completed-Meetings.py`；未合并但完整的旧记录可先用 `Prepare-Legacy-Merged-Meetings.py`。实际变更需明确传入 `--apply`，后者还要求 `--only-session` 名单。只清理已验证的生成文件，未知文件与缺少成品的记录保留。

产品边界与后续小功能候选见 [产品范围](documentation/product-scope.md)。第三方条款见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
