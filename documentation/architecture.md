# 架构与边界

> beta.11 首期调整：新用户默认仅录音，无推荐本地模型下载；速记、原生 MP3 导出与可选第三方 API 均无需 Python 或模型。新 API 提交完整 MP3，身份信息仅来自供应方；已有 local 模式及下文历史 Python 流程保留兼容，不属于新用户推荐路径。新会议按 `verbit年月日时分/同名.mp3` 保存，API 成功后生成同名 Markdown。实际配置见[安装说明](install-windows.md)。


verbit 是当前 Windows 用户的桌面工具：.NET Framework 4.8 / WinForms / RichEdit 处理托盘、快捷键、速记和 WASAPI 采集；原生后台进程负责 MP3 导出、可选 API 转写及成品发布，不依赖 Python。Python worker 仅保留历史会议和研究流程兼容；桌面入口不再自动连接本地模型。没有远端账户、数据库或服务端授权系统。

以下桌面、转写与配置文件在源码仓库的 `src/`，测试在 `tests/`。源码版进入 `src/` 安装和运行；EXE 安装版保留平铺的程序目录，安装步骤不追加 `src`。仓库根目录只展示产品说明与政策文件。

| 模块 | 入口与职责 |
| --- | --- |
| 桌面与首次设置 | `ObsidianMeeting.cs`、`MeetingSettings.cs`；首次没有配置才弹窗，保存通过后进入托盘。 |
| 速记 | `QuickNoteWindow.cs`、`NativeNoteDocument.cs`、`QuickNoteStore.cs`；原生输入，后台自动保存 Markdown 与图片。 |
| 会话与回听 | `SessionCatalog.cs`、`MeetingActions.cs`；按会话路径、状态与进程身份恢复记录，回听由用户点击播放。 |
| 会议处理 | `NativeAudioExport.cs`；无损分段按时间线合并，192 kbps MP3 完整解码校验后发布；成功清理临时资料，失败保留恢复资料。 |
| 转写提供者 | `NativeAudioExport.cs` 中原生 API 流程将完整 MP3 提交到用户确认的音频接口，校验结果并发布 Markdown；`asr_engine.py`、`api_asr.py` 仅保留历史兼容。 |
| 分发 | `packaging/Build-Installer.ps1` 使用明确清单，不打包个人配置、模型或 Python 环境。 |

信任边界是 Windows 文件系统、用户主动粘贴的剪贴板内容、用户选择的模型/依赖下载源，以及 API 模式下的服务商。源码 MIT 不覆盖第三方依赖的许可。

已知风险：未签名安装器；转依赖尚无完整哈希锁；HTTP API 仍可配置；中文和跨轨身份质量缺少足量人工标注基线。详见 [开源检查](open-source-readiness.md)。没有邮件或公开网页，不生成邮件/SEO 运维文档。

## 相关文档

[关键流程](flows.md) · [权限](permissions.md) · [配置与密钥](variables.md) · [测试映射](tests.md) · [后台任务](cron.md) · [AI 处理边界](automation.md) · [安装](install-windows.md) · [发布验收](release-readiness.md)
