# 片言 · Verbit

中文显示名：片言。英文显示名：Verbit。原名：听记 / Tingji。

显示名称、托盘、速记标题和设置窗口使用新名；保留 `ObsidianMeeting.exe`、`tingji.py`、`TINGJI_API_KEY`、图标资源名称及草稿头格式以兼容已有安装与数据，不批量改写用户笔记。GitHub 仓库地址暂时不变。

Verbit 已有同领域语音转录产品使用，参见 [现有产品官网](https://verbit.ai/about-us/)。这里按用户指定用于项目显示，不构成名称可公开商用或商标可用的判断。公开发布前需确认英文名称；不模仿该产品的品牌图形。

## Logo 素材

独立生成原图：`assets/verbit-logo.png`，透明底蓝色图形，结合纸页与短句气泡。使用当前内置图像生成工具；工具没有暴露模型版本选择，**不能证明由用户指定的 image2.5 生成**。它是可替换的候选素材。

生成提示词：

> Design one standalone app logo symbol, no words, for 片言 / Verbit, a lightweight Windows quick-note and meeting audio recorder. Restrained deep blue #2868A8 on white. An original clean geometric mark joining a short fragment of speech and a small page, subtle V-shaped negative space or speech/page cue, compact silhouette readable at 16px. One centered symbol on a transparent background. No text, shadows, gloss, 3D, mockup, extra variants, or imitation of an existing transcription company.

`assets/Create-Icons.ps1` 从原图的有效透明区域等比打包 16/20/24/32/48/64/128/256 像素图标。保留 `tingji.ico`、`tingji-recording.ico` 资源文件名；录音态在显示图标叠加红色状态点，原始 Logo 不被修改。`tingji*.svg` 为旧图形，不是当前源素材。生成脚本只参与开发构建，日常运行不加载图像模型或转换器。
