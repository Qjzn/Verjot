# 验证映射

## 已有覆盖

| 用例 → 规则 → 预期 | 测试与文档来源 | 状态 |
| --- | --- | --- |
| 首次设置 → 取消不落盘、已有配置不覆盖、冲突拒绝 | `TestMeetingSettings.cs`；[流程](flows.md) | 已有，Windows CI 执行。 |
| 双路采集 → 暂停/分段/时间线与短录音保护 | `TestTimestampedTimeline.cs`、`TestSegmentedTimeline.cs`、`TestRecordingProtection.cs`；[质量](quality.md) | 已有合成回归；原生测试由 CI 执行。 |
| 会后恢复 → 当前尝试可完成、旧状态不冒充成功、异常仍提醒 | `TestSessionCatalog.cs`、`TestMeetingExperience.cs`、`test_meeting_worker.py` | 已有；CI 执行前两个，worker 测试为本地完整回归。 |
| 速记 → 图片/自动保存、外部改写保护、原生输入与菜单关闭 | `TestQuickNote*.cs`、`TestNativeNoteDocument.cs` | 已有；CI 执行。程序化输入不能证明所有输入法手感。 |
| API → 危险 URL/重定向拒绝、响应限制与密钥不泄漏 | `test_api_asr.py`；[AI 边界](automation.md) | 已有模拟服务测试，CI 执行；不调用真实服务。 |
| 安装/升级/卸载 → 归属、占用拒绝与个人数据保留 | `packaging/Test-Installer.ps1`；[发布验收](release-readiness.md) | 已有本机隔离安装器回归，不在当前 CI 中。 |

CI 定义见 [workflow](../.github/workflows/ci.yml)，实际通过以对应提交的 Actions 结果为准；尚未启用 required checks，不把“配置了 CI”写成合并门禁。

## 建议新增的验证

| 用例/规则 | 类型 | 状态 |
| --- | --- | --- |
| 干净 Windows 10/11 与企业受限用户安装、升级、卸载 | 手动/虚拟机安装验收 | 待执行。 |
| 30/60/120 分钟双路、休眠、磁盘满和设备热切换 | 受控真实设备验收 | 待按[发布清单](release-readiness.md)逐项执行。 |
| 目标供应商 API、HTTPS/代理和错误恢复 | 明确授权后真实服务测试 | 未做所有供应商验收；可能产生上传和费用。 |

## 缺口

高优先级缺口是长会议完整性、断电/磁盘满恢复及干净机器覆盖；中文准确率和跨轨身份归属还缺足量人工逐字稿与身份真值。不能用合成测试、界面截图或稳定重复输出替代这些证据。
