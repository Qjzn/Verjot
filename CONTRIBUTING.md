# 参与开发

Verjot 使用原生 Windows Forms/.NET Framework 实现桌面、录音、MP3 导出与选配 API 转写；Python 仅用于历史兼容和研究流程。项目自身源码采用 [MIT](LICENSE)；第三方组件见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。提交的代码应有权按项目许可分发。

以下命令在克隆后的源码仓库执行，安装目录不是完整开发检出，不应借用其中的旧环境作为新测试基线。

1. 桌面开发准备 Windows x64 和 .NET Framework 4.8，按 [CI 的 Restore verified NAudio 步骤](https://github.com/Qjzn/Verjot/blob/main/.github/workflows/ci.yml)恢复官方 NAudio 1.10.0 到 `src/lib/NAudio.dll` 并核对 SHA256，再进入 `src` 构建。Python 3.12 只用于兼容流程或 Python 合约测试；桌面构建及原生回归不依赖它。配置复制自 `config.example.json`，个人 `config.json` 留在本机。
2. 在 `src` 用 `powershell -NoProfile -File .\Build.ps1` 构建；原生回归运行 `powershell -NoProfile -File .\Test.ps1 -NativeOnly`。安装所选 Python 依赖后可运行完整 `Test.ps1`，也可用 `-PythonExe` 显式选择已有测试环境。测试统一在 `tests/`。
3. 修复只涉及目标行为；PR 写清具体问题、改变后的行为、验证命令/退出码，以及未覆盖的真实设备或模型。新设置同时更新 schema、示例、说明和相关验证。
4. 测试使用临时目录、合成音频和隔离桌面。不读写系统剪贴板，不注入全局输入，不改默认音频设备，不打开正式笔记；真实录音、回听、API 上传需先取得当次授权。已有测试入口对剪贴板及全局输入调用有守卫。
5. 不提交 `.venv`、模型、音频、会议正文、个人配置、日志、验证截图或凭据。文档使用截图前确认仅含合成示例；需要增加新文件时审查 `.gitignore` 的精确白名单。

[CI](https://github.com/Qjzn/Verjot/blob/main/.github/workflows/ci.yml) 构建桌面程序并运行原生回归及配置/API/首次使用的合成测试，不访问真实设备或云服务。CI 通过不能替代真实麦克风、蓝牙切换、长会和中文说话人质量验收，详见 [发布边界](documentation/release-readiness.md)。

源码目录中保留 `ObsidianMeeting.exe`、`tingji.py`、`TINGJI_API_KEY` 与 `pianyan://` 等兼容标识；界面名称使用 `Verjot`。不要仅为更名改动用户文件格式、回放链接或环境变量。
