# AI Hub

AI Hub 是面向 Windows 11 的本地 AI 聚合中枢与 Codex 多账号隔离启动器，采用原生 Windows 11 云母通透（Mica）宿主窗口与嵌入式 WebView2 界面引擎构建。

工作台集中展示代理状态、Codex 多账号及额度、Antigravity 额度和常用 AI 工具。侧栏提供「工作台」与「设置」两项导航，截图、AI 录音笔、快速记账、批量退出和更新日志均可直接访问。全站使用透明底几何图标和冷纸石色版画样式。

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

## 配置快速记账

在 `%USERPROFILE%\.codex-launcher\quick-ledger.json` 中保存一个 UTF-8 JSON 对象，键为入口名称，值为飞书 HTTPS 地址。支持租户域名下的 `/wiki/`、`/base/`、`/sheets/` 和 `/docx/` 链接，可带表和视图参数。例如：

```json
{
  "日常记账": "https://my.feishu.cn/base/add/你的表单链接标识"
}
```

侧栏「快捷工具 → 快速记账」和 Ctrl+K 读取本机配置。保存修改后，AI Hub 在下一次状态刷新时更新入口。点击入口会用系统默认浏览器打开飞书。

## 额度自动刷新

「设置 → 快捷键与通知」可调整 Codex 和 Antigravity 的额度自动刷新与回满提醒，默认开启。隐藏到系统托盘时，额度检测继续以低频运行。

## 离线提字

「提字」优先使用 RapidOCR 提供的 PP-OCRv6 模型，通过 ONNX Runtime 在本机识别。安装 Python 3.12 后，在 PowerShell 中为 AI Hub 安装独立环境：

```powershell
$ocrEnv = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.codex-launcher\ocr\rapidocr-3.10.0'
py -3.12 -m venv $ocrEnv
& (Join-Path $ocrEnv 'Scripts\python.exe') -I -m pip --isolated install --index-url https://pypi.org/simple rapidocr==3.10.0 onnxruntime==1.32.0
```

模型随 RapidOCR 包安装。未安装该环境、模型启动失败或未识别到文字时，AI Hub 会使用 Windows OCR。结果窗口会显示所用引擎。

## 目录

- `页面代码/`：前端界面层，包含 `index.html`、`界面样式.css`、`工作台交互.js` 与 `协议版本.txt`。
- `核心源码/`：C# 原生宿主与系统能力实现，按职责拆分为：
  - `程序入口.cs`：Per-Monitor DPI V2 初始化、单实例唤醒、命令行参数与运行库加载。
  - `主窗口.cs`：Windows 11 云母无框窗口、系统托盘常驻、WebView2 通信桥与界面热重载。
  - `核心服务.cs`：Codex 三隔离目录多开、主号配置克隆/软链接、实时额度查询、Clash Verge 状态探测与软件扫描。
  - `数据模型.cs`：账号存储结构、JWT 凭证解析、内置 `auth.json` 会话合成与精确倒计时计算。
  - `截图与标注引擎.cs`：多屏异倍 DPI 物理像素截图、窗口自动吸附、放大镜取色、矩形/箭头/画笔/马赛克/文字标注与桌面置顶贴图。
  - `离线文字识别.cs`：在本机调用 PP-OCRv6 或 Windows OCR 提取中英文文字。
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
