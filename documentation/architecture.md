# 架构与边界

verbit 是当前 Windows 用户的桌面工具：.NET Framework 4.8 / WinForms / RichEdit 处理托盘、快捷键、速记和 WASAPI 采集；Python worker 处理音频、转录与成品发布。没有远端账户、数据库或服务端授权系统。

| 模块 | 入口与职责 |
| --- | --- |
| 桌面与首次设置 | `ObsidianMeeting.cs`、`MeetingSettings.cs`；首次没有配置才弹窗，保存通过后进入托盘。 |
| 速记 | `QuickNoteWindow.cs`、`NativeNoteDocument.cs`、`QuickNoteStore.cs`；原生输入，后台自动保存 Markdown 与图片。 |
| 会话与回听 | `SessionCatalog.cs`、`MeetingActions.cs`；按会话路径、状态与进程身份恢复记录，回听由用户点击播放。 |
| 会议处理 | `meeting_worker.py`、`merge_audio.py`、`prepare_audio.py`；无损轨转录，最终发布 MP3 与 Markdown。 |
| 转写提供者 | `asr_engine.py` 为本地模型；`api_asr.py` 为用户配置的兼容语音 API。 |
| 分发 | `packaging/Build-Installer.ps1` 使用明确清单，不打包个人配置、模型或 Python 环境。 |

信任边界是 Windows 文件系统、用户主动粘贴的剪贴板内容、用户选择的模型/依赖下载源，以及 API 模式下的服务商。源码 MIT 不覆盖第三方依赖的许可。

已知风险：未签名安装器；转依赖尚无完整哈希锁；HTTP API 仍可配置；中文和跨轨身份质量缺少足量人工标注基线。详见 [开源检查](open-source-readiness.md)。没有邮件或公开网页，不生成邮件/SEO 运维文档。

## 相关文档

[关键流程](flows.md) · [权限](permissions.md) · [配置与密钥](variables.md) · [测试映射](tests.md) · [后台任务](cron.md) · [AI 处理边界](automation.md) · [安装](install-windows.md) · [发布验收](release-readiness.md)
