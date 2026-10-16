# verbit

项目名称统一写作小写 `verbit`。Logo 只包含图形，不含中文、英文或其他文字；需要组合展示时，图形与名称分别作为独立元素排版，托盘仅使用图形。

此前显示名为片言，原名为听记 / Tingji。用户于 2026-10-16 确认使用无文字 Logo，并将托盘、浮窗及独立名称统一为 `verbit`；不把名称烘焙进 Logo 图片。

显示名称、托盘、速记标题和设置窗口使用新名；速记左上角的系统标题栏图标与托盘、浮窗共用确认后的 Logo 资源，由 Windows 绘制，不增加自绘标题栏。保留 `ObsidianMeeting.exe`、`tingji.py`、`TINGJI_API_KEY`、图标资源名称及草稿头格式以兼容已有安装与数据，不批量改写用户笔记。GitHub 仓库地址暂时不变。

Verbit 已有同领域语音转录产品使用，参见 [现有产品官网](https://verbit.ai/about-us/)。这里按用户指定用于项目显示，不构成名称可公开商用或商标可用的判断。公开发布前需确认英文名称；不模仿该产品的品牌图形。

## Logo 素材

确认原图：`assets/verbit-logo.png`。透明背景内保留不透明的白色折角纸页，以青色声波、靛紫输入线与圆环、少量珊瑚色线段表达声音记录；图形保留原稿的浅紫轮廓与轻阴影。不含名称、字母、数字、水印或标语，软件中的 `verbit` 使用独立文字控件显示。

原图由当前内置图像生成工具参照用户提供的视觉方向生成，再去除名称形成独立图形。参考图及本机确认材料不随仓库分发。工具没有暴露模型版本选择，不能证明由用户最初指定的 image2.5 生成。

无文字图形的生成提示词摘录：

> Asset type: standalone app icon / logo symbol on a genuinely transparent background. Primary request: separate the symbol from the name. Remove all Chinese and English lettering completely. Retain only the upper graphic: a rounded white paper page with a folded upper-right corner, cyan waveform, indigo input line and circular recording marker, and tiny coral output line. Preserve the existing icon design, palette, polished restrained cool-lavender outline and light shadow. Keep the white paper surface opaque. Center the graphic alone on a square transparent canvas, enlarge it to occupy about 85% of the canvas width and 75% of its height; balanced padding, no leftover blank region for text. Clean alpha edges and a compact readable silhouette. No words, no letters, no numbers, no name, no watermark, no caption, no new symbols or additional versions. The product name is separate from this image.

`assets/Create-Icons.ps1` 从确认原图分别生成 16/20/24/32/48/64/128/256 像素图标，每个尺寸直接缩放原图，避免逐级缩放产生模糊；小尺寸保留一像素边距并正确合成透明通道。保留 `tingji.ico`、`tingji-recording.ico` 资源文件名；录音态在显示图标叠加红色状态点，原始 Logo 不被修改。托盘不放文字，浮窗将图形与 `verbit` 分开布局；图标不因录音状态重新设计纸页与声波几何。`tingji*.svg` 为旧图形，不是当前源素材。生成脚本只参与开发构建，日常运行不加载图像模型或转换器。

小尺寸验收分别观察浅色和深色背景中的实际 16/20/24/32 像素显示，以及 100%/125%/150%/200% Windows 缩放下的托盘和浮窗。检查图形无文字残留、边缘无底色块、录音状态点可辨，以及名称清晰且不挤压控制按钮；测试结果单独记录，不用高分辨率预览代替实际小图验证。
