# 听记 · Tingji

Windows 上的本地中文会议录音与转写工具，配合 Obsidian 使用。

按 **Ctrl+Alt+L** 开始／结束录制系统默认麦克风与电脑声音。186×68 的置顶浮窗提供计时、实时音量反馈、麦克风开关、收起和纯红方块结束按钮。蓝色声波托盘图标代表就绪，红色代表录音中；收起浮窗后仍继续录制，可双击托盘恢复。

麦克风开关只影响本场录音，电脑声音持续收录，不改变会议软件的通话。每次开始都重新读取系统默认设备。

转写成功后，笔记库只保留 **一条合并 WAV + 一份 Markdown 转录**。MD 内播放器直接引用同一 WAV；临时分轨、片段和处理日志放在程序的 `work/`，成品验证成功后自动清理。失败保留恢复资料，可从托盘重试。手写内容放在自动转写标记之外，重试保留原始字节。

中文识别使用 Qwen3-ASR-1.7B，说话人分组使用 sherpa-onnx / 3D-Speaker。麦克风和电脑声分别识别与分组，再合成日常回放录音；两轨上同一说话人的编号不会自动归并。模型在处理时加载，录音期间不运行识别模型。噪声、串音及多人同时讲话仍需结合录音核对。

## 构建与配置

需要 Windows、.NET Framework 4.x、Python 3.12。已验证的 GPU 环境为 PyTorch 2.11.0 + CUDA 12.8；GPU 不可用时识别可回退 CPU，速度会降低。

1. 从 [NAudio 1.10.0 NuGet](https://www.nuget.org/packages/NAudio/1.10.0) 下载包，作为 ZIP 解压，将 `lib/net35/NAudio.dll` 放入本项目的 `lib/NAudio.dll`。已验证 DLL SHA256：`BC4BACC3B8B28D898F1671B79F216CCA439F95EB60CD32D3E3ECAFBECAC42780`。
2. 在项目目录执行：

   ```powershell
   py -3.12 -m venv .venv
   .\.venv\Scripts\python.exe -m pip install "torch==2.11.0+cu128" --index-url https://download.pytorch.org/whl/cu128
   .\.venv\Scripts\python.exe -m pip install -r requirements.txt
   .\.venv\Scripts\python.exe download_models.py
   .\Build.ps1
   Copy-Item config.example.json config.json
   ```

3. 编辑 `config.json`：`vault_root` 是笔记库完整路径；`vault_id` 可填 Obsidian 库 ID 或库名。留空设备 ID 表示系统默认设备。默认保存到库内的 `会议记录/`。
4. 运行 `ObsidianMeeting.exe`。保留此兼容文件名，界面、托盘及启动项显示「听记」。如需登录自动启动，执行 `Install-Shortcuts.ps1`；它会建立「听记」快捷方式并移除指向本工具的旧名称。

首次安装下载公开模型和依赖；处理会议时模型离线加载，音频不会上传。本仓库仅包含源码与原创图标，不包含模型、运行库、个人配置、会议音频或本机验证截图。

`assets/Create-Icons.ps1` 可重新生成 16–256 像素的两套图标；SVG 为对应的矢量源。`Build.ps1 -OutputPath ObsidianMeeting.next.exe` 可构建更新候选，更新前先正常退出托盘程序；不要中断正在进行的录音。

## 验证与维护

```powershell
.\.venv\Scripts\python.exe -m unittest test_meeting_worker test_merge_audio test_prepare_audio -v
```

核心回归覆盖混音时间轴、静音增益、分轨准备、原子发布、成品校验、失败恢复、清理中断重试及手写区字节保留。桌面快捷键、麦克风开关、收起、结束及仅两份成品另通过本机实测；新机器仍需验证自己的输入／输出设备。

旧版附件整理工具默认只规划：`Compact-Completed-Meetings.py`；未合并但完整的旧记录可先用 `Prepare-Legacy-Merged-Meetings.py`。实际变更需明确传入 `--apply`，后者还要求 `--only-session` 名单。只清理经过验证的生成文件，未知文件与缺少成品的记录保留。

第三方依赖与模型条款见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
