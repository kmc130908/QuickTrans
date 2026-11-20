# QuickTrans

Windows 轻量桌面划词翻译工具（.NET 8 + WPF + llama.cpp Vulkan），支持本地离线大模型与在线接口，极低待机内存（约 15–18 MB）。

## 主要功能

- **多种触发方式**：划词翻译（`Ctrl+Q`）、手动输入（未选文字按 `Ctrl+Q`）、多屏离线截图 OCR（`Alt+S`）、一键替换翻译（`Alt+R`）、可选快速连按两次 `Ctrl+C` 翻译。
- **本地 & 在线双通道**：内置 Vulkan 通用推理引擎（NVIDIA / AMD / Intel 显卡加速 + 纯 CPU 自动兜底，CPU 模式零显存），支持任意 `.gguf` 模型与 OpenAI 兼容 / 百度 / 有道 / 谷歌接口。
- **阅读与写作增强**：英文单词自动词典模式（音标、词性、释义、双语例句）、浮窗 `译 / 润 / 解` 一键切换、PDF / 文档智能去换行、Windows 离线语音朗读。
- **界面与免打扰**：浅色 / 深色 / 跟随系统实时主题、浮窗钉住 `📌`、本地翻译历史（最近 100 条）、全屏游戏 / 全屏视频分项免打扰与运行程序黑名单。
- **极低内存与自动卸载**：浮窗、设置页、历史窗独立子进程运行，关闭即归还内存；支持闲置定时卸载本地模型，释放显存。

## 基础使用

1. **下载安装**：前往 [Releases](https://github.com/kmc130908/QuickTrans/releases) 下载：
   - **轻量版**（`QuickTrans-Vulkan-Lite-Setup-*.exe`，约 20 MB）：需系统已装 .NET 8 桌面运行时。
   - **标准版**（`QuickTrans-Vulkan-Setup-*.exe`，约 63 MB）：内置 .NET 8 运行时，开箱即用。
2. **准备模型 / 接口**：安装后在「组件管理」一键下载本地模型（推荐 `Hy-MT2-1.8B`），或将任意 `.gguf` 文件放入安装目录 `models\`；也可在「设置 → 在线接口」填入 API 使用。
3. **常用操作**：

| 操作 | 说明 |
| --- | --- |
| 选中文字 → `Ctrl+Q` | 弹出译文浮窗（再次按 `Ctrl+Q` 或 `Esc` 关闭） |
| 未选文字 → `Ctrl+Q` | 打开手动输入翻译（`Ctrl+Enter` 提交） |
| `Alt+S` | 框选任意显示器区域进行离线 OCR 翻译 |
| 选中文字 → `Alt+R` | 翻译并直接粘贴替换原选中文字 |
| 双击托盘图标 | 打开偏好设置（支持自定义全部快捷键、主题与免打扰） |

> 提示：管理员权限窗口受 Windows UIPI 限制无法直接划词，可使用 `Alt+S` 截图翻译。

## 参考与致谢

本项目引擎及部分新增功能参考了以下开源项目的实现与交互思路：

- [llama.cpp](https://github.com/ggml-org/llama.cpp)：本地 GGUF 模型推理与 Vulkan / CPU 引擎
- [Qwen3](https://github.com/QwenLM/Qwen3)：轻量开源本地翻译模型
- [pot-desktop](https://github.com/pot-app/pot-desktop)：划词翻译、截图 OCR、多模式与历史记录交互参考
- [Easydict](https://github.com/tisfeng/Easydict)：单词词典展示、浮窗钉住、替换翻译与 PDF 智能去换行思路参考
