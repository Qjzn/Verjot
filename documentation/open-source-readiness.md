# 开源前检查与系统边界

这是源码与发布准备记录，不代表数小时录音、说话人准确率或独立安全审计已通过。源码许可已采用 MIT；仓库可见性仍由维护者单独决定，本轮不更改为公开。

## beta.10 私有候选收尾

- 本轮修复浮窗告警标题高度不足、DPI 计算混用及设置窗口超出工作区的问题；补查就绪、录音、处理中、告警与成品状态，保留轻量窗口和默认仅录音。
- 完整 `src/Test.ps1 -NativeOnly` 通过，包含浮窗 495、设置 584、速记编辑 664、定向输入 1219 项断言，以及时间线、合成 MP3、恢复、热键、图片和自动保存检查。权限不足的链接类测试及未提供固定 Python 包的 bootstrap 实测明确跳过，不记通过。
- 最终补充的速记最小尺寸约束通过 `--geometry-only` 109 项检查；模型准备窗口的异步 DPI 回调增加关闭保护，辅助窗口渲染复测通过。
- Python 合成回归 270 项，1 项跳过；不调用真实云服务，不把测试后端输出当作中文或声纹质量证据。
- 对待提交内容与扫描时本地全部 refs 的 50 个提交、654 个唯一历史 blob 检查：未发现个人配置、真实音频、模型或运行目录入库，高置信密钥、私钥、私人路径与本场已知人物姓名模式未命中；本地 Markdown 链接与 Python 语法检查通过。扫描只输出类别和文件位置，不输出匹配密钥。这不是穷尽安全或法律审计。
- README、说明、设置及录音/速记截图已统一到当前行为，截图只含合成文字、图片与音量。版本构建缺省统一为 `0.1.0-beta.10`；安装包继续采用明确载荷清单和逐文件 SHA256，模型/Python/FFmpeg 不进入基础包。
- GitHub 可见性、源码提交、对应 CI 和分发资产必须分别确认。没有把构建完成等同已同步或已发布；没有变更仓库可见性。

公开仍需维护者确认：英文名称与同领域既有产品的混淆、参考图形公开使用权和 Git 作者邮箱披露。干净 Windows 全流程、长会议、真实蓝牙/设备切换及中文/跨轨身份质量仍按发布验收单独列为待测，安装包未签名。它们不会因为本轮显示修复或回归通过而自动消失。

## 当前审查结论

| 项目 | 实际结果 | 后续要求 |
| --- | --- | --- |
| Git 内容与历史 | 安装候选 `3966231` 检查本地全部 refs：28 个提交、389 个唯一 blob、107 个历史路径；未发现录音、模型、个人 `config.json`、工作目录、验证材料、EXE 或 DLL 入库。高置信 provider token、私钥和个人目录模式未命中；唯一带凭据 URL 是拒绝该输入的合成测试占位。 | 这是明确模式扫描和路径清单检查，不是所有秘密或隐私的穷尽证明。发布前对最终 diff、历史新增提交和安装包再检查；更换凭据时需先撤销旧值。 |
| Git 元数据 | 提交作者包含个人邮箱；文件忽略规则不能隐藏作者信息。 | 公开前由维护者确认是否接受披露。未擅自重写历史。 |
| 自有与第三方许可 | 根目录 MIT 完整；确认 NAudio **1.10.0 是 Ms-PL**，已修正旧 MIT 说明并加入原版全文。当前图形为本项目按用户参考生成、独立文字显示的图标。 | 安装包保留 `LICENSE`、`THIRD_PARTY_NOTICES.md` 和 `packaging/licenses/NAudio.txt`。图形参考来源及公开使用权需维护者确认；没有作第三方授权或独占性保证。 |
| 运行时分发 | 本机 imageio-ffmpeg wheel 的 FFmpeg 7.1 启用 GPL/version3；本次轻量安装包不含它、Python 环境或模型，按说明从官方来源单独安装。 | 全离线/全运行时包需逐个确认原生库、模型、完整许可证和对应源码的提供方式，不能从 `.venv` 直接打包发布。详见 [第三方通知](../THIRD_PARTY_NOTICES.md)。 |
| 名称 | [Verbit 官网](https://verbit.ai/about-us/)及其[转录产品](https://verbit.ai/solutions-transcription/)已使用相同英文名称，且业务领域接近。 | 存在可核实的品牌混淆风险；这不是已确认侵权结论。公开推广前决定是否换名或进行专业名称/商标检索，未擅自更改用户选定名称。 |
| CI 与仓库门禁 | Windows workflow 固定 action commit、只读 contents 权限、官方 NAudio 固定 DLL SHA-256，执行桌面构建、19 组原生合成回归与 66 项配置/API/首次使用测试。实际执行发现并修复首次窗口定位、结果文件时间精度和英文 Windows 中文输出问题；最终结果见 [Actions](https://github.com/Qjzn/verbit/actions/workflows/ci.yml)。 | 发布要求对应源码提交的 CI 通过。主分支保护 API 返回 403，提示当前私有仓库需要 GitHub Pro 或公开才可使用该功能；没有声称已启用保护。安全分析元数据返回空，私密报告、依赖/secret 扫描及 required checks 也未确认启用。 |
| 真实使用质量 | 现有测试、首次使用步骤与局限均有记录。 | 干净 Windows 10/11、30/60/120 分钟会议、休眠/断电/磁盘满、蓝牙热切换、已标注中文/跨轨身份准确率仍按 [发布验收](release-readiness.md) 分别验证，不由 CI 绿色替代。 |

本地历史扫描报告保存于 `validation/open-source-audit-20261016/final-history-scan.json`，不随源码发布。不把真实用户音频、转录或完整日志作为公开测试素材。模块交接与审查入口见 [架构](architecture.md)、[流程](flows.md)、[权限](permissions.md)、[配置与密钥](variables.md)、[测试映射](tests.md)、[后台任务](cron.md) 和 [AI 边界](automation.md)。

本轮进一步将仓库改名为 `Qjzn/verbit` 并保持私有，源码与测试分别移入 `src/`、`tests/`，根目录仅保留 6 个产品说明与政策文件。目录迁移后的回归、新装 API/本地 CPU 环境与真实模型转写证据见 [新用户验证](new-user-validation.md)。实际 EXE 首次设置、速记和重启路径已完成 28 项断言，不以程序集回归替代；本轮按维护者选择不进行真实录音，硬件与回听仍保留后续验收。

## 架构与信任边界

| 模块/边界 | 输入与副作用 | 实施约束 |
| --- | --- | --- |
| Windows 当前用户 → 桌面控制器 | 快捷键、托盘、浮窗和设置；读取默认设备，录制麦克风/电脑声，写配置和恢复信息。 | 无登录服务器、角色或跨用户权限；使用当前 Windows 账户文件权限，不提升管理员权限。快捷键冲突和路径校验失败不应用设置。 |
| 原生录音 → Python worker | 无损分段与时间线元数据交给本地进程；合并后输出 MP3 与 Markdown，成功后清理临时轨。 | 默认 5 秒保护；异常保留恢复资料。路径和原始会话元数据属于敏感本地内容。源码入口为 `ObsidianMeeting.cs`、`audio_sources.py`、`merge_audio.py`、`meeting_worker.py`。 |
| 速记窗口 → 文件系统/剪贴板 | 用户主动粘贴文字或图片，原生编辑器自动保存，图片存附件。 | 不把速记内容上传。测试仅使用局部 DataObject 与合成图，禁止读取/覆盖用户剪贴板；实际粘贴由用户主动触发。 |
| worker → 本地模型 | 从本机已下载目录加载分组与 ASR 模型。 | 本地 ASR 使用 `local_files_only=True`、`trust_remote_code=False` 和离线环境设置；安装下载属于独立网络边界。本地转录与远端 API 不能混称。 |
| worker → API 服务商 | 仅选择 API 模式后，把本地准备的语音分段和提示上下文发送到配置前缀的 `/audio/transcriptions`。 | `api_asr.py` 从指定环境变量读密钥，不持久化；拒绝带凭据/查询/fragment的 URL，拒绝重定向转发；错误信息不回显响应体。HTTP 远端明文仍被允许，应配置 HTTPS。 |
| 安装脚本 → 官方下载源 | 下载 NAudio、Python 包和选定模型；写当前用户安装目录。可选安装 Startup/开始菜单快捷方式及 `pianyan://` 协议。 | 显式文件清单打包；个人配置/模型/音频不进入安装包。NAudio DLL 校验固定哈希；模型下载保存大小、revision/哈希，但所有转依赖尚无完整哈希锁。 |

配置与密钥映射见 [configuration.md](configuration.md)：`vault_root`/子目录决定数据落点，四组快捷键影响本机全局键位，`api_base_url`/`api_model` 决定上传目的地，`api_key_env` 只保存变量名。`TINGJI_FFMPEG_EXE` 允许显式选本地编码器；它改变执行文件来源，使用者应只指向可信文件。密钥轮换在服务商完成，随后更新环境变量并重启程序；不要以更改变量名替代撤销旧密钥。

没有远端用户账户、数据库、付款、邮件、公开网页或内嵌自主工具调用 Agent。会后转写是本地桌面触发的后台任务；它处理指定音频，写结果/检查点，失败保留可重试状态，不有权自行发送邮件、调用其他应用或发布内容。

## 流程、权限与验证映射

| 关键流程/规则 | 现有验证 | 未验证部分 |
| --- | --- | --- |
| 首次启动配置、输出路径和热键失败不破坏旧配置 | `TestMeetingSettings.cs`、`test_tingji_config.py`，首次使用桥 `test_first_use.py`；均为隔离目录/合成数据。 | 新安装器在干净 Windows、受限企业环境和多账户下的实际行为。 |
| 双路录音、暂停、分段、时间线与短录音保护 | `TestCaptureReliability.cs`、`TestTimestampedTimeline.cs`、`TestSegmentedTimeline.cs`、`TestRecordingProtection.cs`，Python `test_audio_sources`、`test_multi_capture_worker`、`test_merge_audio`。 | 测试不能听见用户实际外放，真实驱动、长会和设备组合需分别验收。 |
| 成品一致、异常恢复、用户删除历史后的目录显示 | `TestSessionCatalog.cs`、`TestMeetingActions.cs`、`test_meeting_worker.py`、`test_legacy_migration.py`、`test_mp3_audio.py`。 | 真实磁盘满/休眠/掉电、共享盘和第三方同步软件同时改写。 |
| 速记输入、单行回车、带图保存和菜单关闭 | `TestQuickNoteStore.cs`、`TestNativeNoteDocument.cs`、`TestQuickNoteEditor.cs`、`TestQuickNoteInput.cs`、`TestQuickNoteNewlines.cs`，用户曾验收正式入口输入/拖动/贴图与重开。 | Windows/中文输入法版本和长文档组合仍需扩大；程序化耗时不能等同所有人的输入手感。 |
| API 不泄密、拒绝危险地址、限制音频/响应、断点续转 | `test_api_asr.py` 使用合成 WAV、localhost HTTP 与模拟服务；不使用真实密钥和云服务。 | 未对所有供应商、TLS/代理配置、服务商保留策略或费用做实际验收。 |
| 分组、回声、降噪与中文质量 | `test_prepare_audio.py` 和 `documentation/quality.md` 中已明确的样本与证据。 | 缺少足量逐字稿/身份标注会议，不能宣传跨轨身份全对、重叠讲话全识别或固定准确率。 |

现有 CI 只覆盖 workflow 中列出的测试，不执行真实录音、API 上传或模型推理，也不发布 Release。所有标为“未验证”的事项必须留下原状态，不能因安装包生成成功改为通过。

## 公开前需维护者决定的事项

1. 英文名称与同领域既有产品的混淆、Git 作者邮箱披露、参考图形公开权利确认。
2. 干净机器安装、更新保留配置、卸载保留个人内容和首次设置窗口的实际结果；发布页如有未验证项明确说明。
3. 启用私密漏洞报告、限制主分支、要求 CI 通过；核对最终截图只有合成数据，并确认 GitHub 可见性是否要改变。
4. 发布时提供版本/源码提交、安装包 SHA-256、第三方许可、Windows/Python/模型/API 条件及真实质量限制。代码签名尚未提供，不承诺绕过 Windows SmartScreen。

源码同步、私有预发布与仓库公开是三个不同动作。可先发布带明确信息的私有测试版；正式开放仓库需维护者明确决定。
