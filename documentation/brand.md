# Verjot

产品英文显示名称统一为 `Verjot`，中文仍为「片言」。机器检索与共享记忆的项目键使用 `verjot`。Logo 保持原确认图形，图形和名称分开排版，托盘仅使用图形。

历史英文名为 verbit，早期名为听记 / Tingji。用户于 2026-10-30 将本轮名称最终纠正为 `Verjot`（不是临时使用的 Verlog），产品、仓库与记忆以此为准；中文片言和 Logo 不变，不把名称烘焙进图形。

显示名称、托盘和设置窗口使用新名；速记采用原生无边框窗口，顶部以独立 Logo 与当前文件名组成导航，不重复显示产品标题。各入口共用确认后的 Logo 资源。保留 `ObsidianMeeting.exe`、`tingji.py`、`TINGJI_API_KEY`、图标资源名称及草稿头格式以兼容已有安装与数据，不批量改写用户笔记。GitHub 仓库为 `Qjzn/Verjot`。

原名 verbit 与[同领域既有产品](https://verbit.ai/about-us/)接近，现按用户决定更名为 Verjot。本次改名不构成商标或名称可商用的法律检索；Logo 沿用原确认图，不模仿其他产品品牌图形。

## 窗口与菜单

五个应用窗口共用 `MeetingBrand.IdleIcon`；录音时托盘使用同一图形加红色状态点。窗口名称如下：

| 窗口 | 显示标题 |
| --- | --- |
| 录音浮窗 | `Verjot · 会议录音`，内部图形与名称分开显示 |
| 速记 | `Verjot · 速记` |
| 设置 | `Verjot · 设置` |
| 首次启动的设置 | `Verjot · 首次设置`，保存成功后才创建配置并启动托盘 |
| 会后命名 | `Verjot · 命名会议` |
| 回听 | `Verjot · 录音回听` |

托盘与浮窗共享操作菜单，成品路径、速记保存和速记编辑使用原生右键菜单；菜单项按操作命名，不增加重复品牌装饰。系统错误提示保留错误语义图标，标题使用 `Verjot`；文件夹选择器由设置窗口拥有，说明显示 `Verjot · 选择会议与速记的保存目录`。

## 安装入口与兼容边界

快捷方式、程序标题、托盘、设置、安装器、自检报告和新会议前缀统一使用 `Verjot`。旧 `verbit-v1` / 临时 `Verlog-v1` 会话及文件名继续读取，旧输出目录不迁移；开机启动识别旧入口，仅在确认归属本安装时迁移。更新默认读取 `VERJOT_GITHUB_TOKEN`，未设置时依次兼容 `VERLOG_GITHUB_TOKEN` / `VERBIT_GITHUB_TOKEN`。安装 AppId、单实例锁、草稿头、`TINGJI_API_KEY` 与 `pianyan://` 保留，避免损坏旧安装、恢复资料或时间点链接。原图只改资源文件名，图像字节不变。

旧名仅在明确兼容或历史引用中保留，例如旧快捷方式的定向清理名单、内部类名、临时文件前缀、`TINGJI_API_KEY`、`pianyan://`，以及带日期的原始测试文字。旧 SVG 没有被当前构建与运行入口引用；当前图标仍以确认的 PNG 生成。历史证据不批量替换产品名，用户笔记、草稿格式和已有配置也不随显示品牌改写。

## Logo 素材

确认原图：`assets/Verjot-logo.png`。透明背景内保留不透明的白色折角纸页，以青色声波、靛紫输入线与圆环、少量珊瑚色线段表达声音记录；图形保留原稿的浅紫轮廓与轻阴影。不含名称、字母、数字、水印或标语，软件中的 `Verjot` 使用独立文字控件显示。

原图由当前内置图像生成工具参照用户提供的视觉方向生成，再去除名称形成独立图形。参考图及本机确认材料不随仓库分发。工具没有暴露模型版本选择，不能证明由用户最初指定的 image2.5 生成。

无文字图形的生成提示词摘录：

> Asset type: standalone app icon / logo symbol on a genuinely transparent background. Primary request: separate the symbol from the name. Remove all Chinese and English lettering completely. Retain only the upper graphic: a rounded white paper page with a folded upper-right corner, cyan waveform, indigo input line and circular recording marker, and tiny coral output line. Preserve the existing icon design, palette, polished restrained cool-lavender outline and light shadow. Keep the white paper surface opaque. Center the graphic alone on a square transparent canvas, enlarge it to occupy about 85% of the canvas width and 75% of its height; balanced padding, no leftover blank region for text. Clean alpha edges and a compact readable silhouette. No words, no letters, no numbers, no name, no watermark, no caption, no new symbols or additional versions. The product name is separate from this image.

`assets/Create-Icons.ps1` 从确认原图分别生成 16/20/24/32/48/64/128/256 像素图标，每个尺寸直接缩放原图，避免逐级缩放产生模糊；小尺寸保留一像素边距并正确合成透明通道。缺少 `Verjot-logo.png` 时在生成前明确报错，需恢复确认稿后再执行。保留 `tingji.ico`、`tingji-recording.ico` 资源文件名；录音态在显示图标叠加红色状态点，原始 Logo 不被修改。托盘不放文字，浮窗将图形与 `Verjot` 分开布局；图标不因录音状态重新设计纸页与声波几何。`tingji*.svg` 为旧图形，不是当前源素材。生成脚本只参与开发构建，日常运行不加载图像模型或转换器。

小尺寸验收分别观察浅色和深色背景中的实际 16/20/24/32 像素显示，以及 100%/125%/150%/200% Windows 缩放下的托盘和浮窗。检查图形无文字残留、边缘无底色块、录音状态点可辨，以及名称清晰且不挤压控制按钮；测试结果单独记录，不用高分辨率预览代替实际小图验证。
