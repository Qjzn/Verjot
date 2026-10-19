# 验证映射

2026-10-19 私有候选补充：新增 `ValidateLongTimeline.py` 的显式长音频离线压力检查，以及 `EvaluateSpeakers.py` / `test_speaker_evaluation.py` 的人工核验与开发集/留出集评分。前者不证明真实设备长录，后者不接受竞品输出替代人工标注。安装前置条件回归补齐缺失 pip 的离线恢复、已有损坏 pip 不覆盖，以及干净客体入口拒绝物理宿主。执行状态和未测门槛见 [私有测试版验收](private-beta-acceptance.md)。

## 已有覆盖

| 用例 → 规则 → 预期 | 测试与文档来源 | 状态 |
| --- | --- | --- |
| 首次设置 → 取消不落盘、已有配置不覆盖、冲突拒绝 | `TestMeetingSettings.cs`；[流程](flows.md) | 已有，Windows CI 执行。 |
| 双路采集 → 暂停/分段/时间线与短录音保护 | `TestTimestampedTimeline.cs`、`TestSegmentedTimeline.cs`、`TestRecordingProtection.cs`；[质量](quality.md) | 已有合成回归；原生测试由 CI 执行。 |
| 会后恢复 → 当前尝试可完成、旧状态不冒充成功、异常仍提醒 | `TestSessionCatalog.cs`、`TestMeetingExperience.cs`、`test_meeting_worker.py` | 已有；CI 执行前两个，worker 测试为本地完整回归。 |
| 速记 → 图片/自动保存、外部改写保护、原生输入与菜单关闭 | `TestQuickNote*.cs`、`TestNativeNoteDocument.cs` | 已有；CI 执行。程序化输入不能证明所有输入法手感。 |
| API → 危险 URL/重定向拒绝、响应限制与密钥不泄漏 | `test_api_asr.py`；[AI 边界](automation.md) | 已有模拟服务测试，CI 执行；不调用真实服务。 |
| 安装/升级/卸载 → 归属、占用拒绝与个人数据保留 | `packaging/Test-Installer.ps1`；[发布验收](release-readiness.md) | 已有本机隔离安装器回归，不在当前 CI 中。 |
| 异常 QPC → 拒绝未来时间、保留可靠帧位置间隔、音频不落到结束边界之外 | `TestCaptureReliability.cs`，实际 `CaptureTrack` 与 `TimelineWriter` | 已有故障注入回归；不能代替驱动实测。 |
| 新电脑缺 VC++ → 模型下载前停止、明确官方修复入口、不写配置 | `Test-InstallPrerequisites.ps1`，调用真实 `Install.ps1` 和隔离假解释器 | 本机故障注入通过；由 `src/Test.ps1` 执行，不代表干净 OS 已测。 |
| 无 Python/模型/开发工具 → 实际 EXE 首次设置、速记、全新本地引擎与模型下载 | `Prepare-CleanWindows.ps1`、`Test-CleanWindowsGuest.ps1`、`PrepareOfflineInstall.py`、`VerifyOfflineInstall.py`；[干净 Windows](clean-windows-validation.md) | 验证包已准备；客体实测待执行。 |

源码测试统一放在 `tests/`，由 `src/Test.ps1` 执行；源码运行代码与依赖位于 `src/`。安装包不包含测试或 CI 文件。CI 定义见 [workflow](https://github.com/Qjzn/verbit/blob/main/.github/workflows/ci.yml)，实际通过以对应提交的 Actions 结果为准；尚未启用 required checks，不把“配置了 CI”写成合并门禁。

本轮新用户的实际安装、空环境依赖与真实模型处理结果见 [新用户验证](new-user-validation.md)。实际 EXE 首次启动回归为 `packaging/Test-NewUser.ps1`，要求当前会话正常退出 verbit 且没有其他安装登记；`-InstallOnly` 不等于首次设置和速记已经验证。

## 建议新增的验证

2026-10-17 设备恢复回归补充：`TestTimestampedTimeline` 调用实际 `CaptureFailed`，核对失败来源关闭、健康来源保留、唤醒管理线程及无 Windows 通知时冷却后可重试；`TestCaptureReliability` 检查独立端点退避、60秒上限、成功重置和持续失败通知去重。使用合成对象和私有测试桌面，不访问声卡或剪贴板。真实设备的失败恢复仍列在下面的受控设备验收中。

| 用例/规则 | 类型 | 状态 |
| --- | --- | --- |
| 干净 Windows 10/11 与企业受限用户安装、升级、卸载 | 手动/虚拟机安装验收 | 待执行。 |
| 30/60/120 分钟双路、休眠、磁盘满和设备热切换 | 受控真实设备验收 | 待按[发布清单](release-readiness.md)逐项执行。 |
| 目标供应商 API、HTTPS/代理和错误恢复 | 明确授权后真实服务测试 | 未做所有供应商验收；可能产生上传和费用。 |

## 缺口

2026-10-17 自动保存测试等待修正：CI 曾在图片插入后读取到旧文件，原夹具只固定处理消息 800 毫秒，未等 500 毫秒防抖后的后台持久化完成。用 650 毫秒后台存储延迟复现同一断言失败，此时内存图片仍在、`dirty` 和 `workerBusy` 均为真。现在相关校验等待生产自动保存完成状态，沿用已有 5 秒后台等待上限，不主动调用保存绕过计时器；夹具保留该慢存储场景以及正文、图片像素、撤销重做、恢复与重开的精确断言。本机隔离重放 304 条断言通过。该复现说明固定等待存在竞态，不证明原 CI 只有这一种失败原因；它没有更改编辑器或宣称修复新的产品性能问题。

高优先级缺口是长会议完整性、断电/磁盘满恢复及干净机器覆盖；中文准确率和跨轨身份归属还缺足量人工逐字稿与身份真值。不能用合成测试、界面截图或稳定重复输出替代这些证据。

识别候选另有定向回归：从重叠段中取干净锚点、捕获偏移下只送入正确采样区间；转写窗口不依赖局部人物编号，多人文本保持原文且不冒称单人归属；术语去重、长度限制和每场冻结；候选切片及人物时间线仍在成功后清理。真实长会议的前后比较与工程用例分别报告；没有人工逐字稿和身份标注时不计算或宣称 CER/DER 达标。

识别候选补充回归覆盖旧会议缓存保持旧分段、会议冻结设置优先、显式术语短片段保护与断点策略指纹变更。真实长会议另存副本完成整场初始实验，并对 12 个同音频片段实际调用本地模型做术语/无术语/保护后三方对照；这些不使用竞品输出作标准答案。分数 DPI 下复现 Logo 选择较小图标帧并放大的问题，改为选择不小于目标的已嵌入帧，保留原严格原生断言验证。

2026-10-17 最终本机候选回归：238 项 Python（1 项跳过）、19 组原生夹具及实际安装脚本缺少 VC++ 的故障注入返回 0，最新候选安装器 52 项断言通过。早先一次全量执行遇到便签初次载入断言，独立重放和最终全量均通过，未据此声称修复了新的便签根因。另一次全量严格复现了上述 Logo 帧错误，修复后原断言通过。测试仍不读写共享剪贴板、不调用真实录音设备；真实中文输入法与干净 Windows 客体另行验收。
