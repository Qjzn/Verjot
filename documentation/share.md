# Verjot：产品介绍与分享材料

## 一句话介绍

Verjot（片言）是一款开源 Windows 轻量便签与会议录音工具：快捷键随手写、粘贴截图、多标签切换，会议原音本地保存，需要时再连接自己的转写 API。

## 可直接使用的介绍

工作时冒出一个想法，不必找笔记软件：按 `Ctrl+Alt+J` 打开置顶便签，输入文字或贴一张截图，停顿后自动保存。贴边标签可以切换最近笔记，也能先悬停预览；`Ctrl+滚轮` 调整阅读大小。点击顶部名称直接改名，点击别处自动提交，同名不会覆盖。

开会时按 `Ctrl+Alt+L` 记录麦克风和电脑声音，`Alt+Space` 可单独关麦克风，电脑声不停。结束后生成一条 MP3，保存在自己选择的目录；没有配置 AI 也能完成速记与录音。文字转写是可选项，由用户选择兼容的音频 API，上传前再次确认。

基础安装包约 6 MB，不需要 Python、GPU、模型或 Obsidian，也不要求注册 Verjot 账号。笔记是 Markdown，录音是 MP3；文件可以独立打开、备份和带走。源码采用 MIT 许可，当前支持 Windows x64。

下载与校验：[官方 Releases](https://github.com/Qjzn/Verjot/releases/latest)。操作步骤：[五分钟上手](install-windows.md)。截图只使用演示便签，不使用个人笔记、真实会议或数据测试窗口。

![带多标签、正文和图片的 Verjot 原生便签](../assets/screenshots/quick-note.png)

## English description

Verjot is an open-source Windows sticky notes and meeting recorder. Capture ideas with global hotkeys, paste images, switch notes through edge tabs, and keep notes as local Markdown. Record microphone and system audio to MP3, mute only the microphone when needed, and optionally transcribe through your own compatible audio API. Basic recording and notes require no Python, local model, GPU, Obsidian, or Verjot account.

## 适合展示的操作顺序

1. 快捷键打开便签，写一条想法，贴入演示图片。
2. 展示多标签切换与悬停预览，再用 Ctrl＋滚轮调整阅读大小。
3. 修改名称后点击正文，展示自动保存与同名保护。
4. 展示轻量录音浮窗、麦克风独立开关和最终文件位置；录音演示前取得参与者同意。

## 发布边界

只使用已验证的功能和安装包体积，不宣称“零占用”“全平台”“识别最准”或真实设备从不漏音。安装包尚未签名，干净 Windows、完整设备组合和第三方转写质量仍有未验证项。没有内置转写模型；选择外部 API 会在确认后上传完整会议 MP3。

仓库描述、主题词、中英文 README、真实组件展示和清晰下载入口有助于用户与工具理解项目；不能保证搜索排名、推荐量或 AI 推荐优先级。站外推广应遵守各社区规则，避免重复灌水；本材料不表示已在任何社区、社交平台或邮件中发布。
