# AI Hub

AI Hub 是面向 Windows 11 的本地 AI 聚合中枢与 Codex 多账号隔离启动器，采用原生 Windows 11 云母通透（Mica）宿主窗口与嵌入式 WebView2 界面引擎构建。

项目把日常高频的本地代理通道状态、OpenAI Codex 多账号隔离多开、实时剩余额度与 25 天周期回收倒计时、桌面 AI 应用（Gemini、Antigravity、Open Design 等）、Chrome 多账号直达、全局快捷截图与本机离线 OCR 提字，以及桌面快捷方式可逆收纳集中到同一工作台内。

仓库：<https://github.com/zulpkar97-max/ai-hub>。本地项目目录统一命名为 `ai-hub`。完整演进记录见 [更新日志.md](./更新日志.md)。

## 启动与编译

直接运行根目录下的 `CodexLauncher.exe` 即可启动。在 Windows PowerShell 中进入项目目录，运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\编译程序.ps1
```

若当前 `CodexLauncher.exe` 正在运行，可生成热替换更新包并由应用内一键平滑切换：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\编译程序.ps1 -PrepareUpdate
```

修改 `页面代码/` 下的界面结构、样式或交互逻辑时，运行中的窗口通过内置文件监听自动热重载刷新，无需重新编译。

## 目录

- `页面代码/`：前端界面层，包含 `index.html`、`界面样式.css`、`工作台交互.js` 与 `协议版本.txt`。
- `核心源码/`：C# 原生宿主与系统能力实现，按职责拆分为：
  - `程序入口.cs`：Per-Monitor DPI V2 初始化、单实例唤醒、命令行参数与运行库加载。
  - `主窗口.cs`：Windows 11 云母无框窗口、系统托盘常驻、WebView2 通信桥与界面热重载。
  - `核心服务.cs`：Codex 三隔离目录多开、主号配置克隆/软链接、实时额度查询、Clash Verge 状态探测与软件扫描。
  - `数据模型.cs`：账号存储结构、JWT 凭证解析、内置 `auth.json` 会话合成与精确倒计时计算。
  - `截图与标注引擎.cs`：多屏异倍 DPI 物理像素截图、窗口自动吸附、放大镜取色、矩形/箭头/画笔/马赛克/文字标注与桌面置顶贴图。
  - `离线文字识别.cs`：直接调用 Windows 11 原生 `Windows.Media.Ocr` 引擎的本机离线中英文文字提取。
  - `桌面图标收纳.cs`：将已收纳软件的桌面 `.lnk` 快捷方式安全转移至本地收纳夹，支持一键隐藏与随时恢复。
  - `托盘与浮窗.cs`：自定义圆角托盘右键菜单与内置独立网页登录自动取号窗口。
  - `界面辅助.cs` 与 `热更新协调.cs`：WPF 视觉辅助与无损热更新切换。
- `资源/`：原创深炭黑「精密双轨拨动开关」多分辨率图标资源，以及 `资源/第三方/` 下的微软官方签名 WebView2 运行库。
- `编译程序.ps1`、`应用更新.ps1`：本地编译与运行期平滑热更新脚本。
- `更新日志.md`：从 v1.0 至当前版本的完整演进说明。

## 核心机制与数据隔离

- **本机数据与代码完全分离**：所有账号配置、隔离运行目录（`profiles/`）、图标缓存与桌面图标收纳夹（`desktop-stash/`）均独立存放在用户目录下的 `~/.codex-launcher/`，仓库代码内不包含任何个人账号或本地凭证。
- **Codex 官方三隔离多开**：通过同时指定 `CODEX_HOME`、`CODEX_ELECTRON_USER_DATA_PATH` 与 `--user-data-dir`，支持原生主号（`~/.codex`）与多个独立账号同时运行且互不串号。
- **一屏两栏工作流**：左侧仅保留「工作台」与「管理与设置」两项导航，日常启动与低频卸载/整理操作严格分离，支持 `Ctrl + K` 快捷命令面板全局秒搜。
