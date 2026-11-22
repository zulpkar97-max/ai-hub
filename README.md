# AI Hub

AI Hub 是面向 Windows 11 的本地 AI 聚合中枢与 Codex 多账号隔离启动器，采用原生 Windows 11 云母通透（Mica）宿主窗口与嵌入式 WebView2 界面引擎构建。

工作台集中展示代理状态、Codex 多账号及额度、Antigravity 额度和常用 AI 工具。侧栏提供「工作台」与「设置」两项导航，截图、AI 录音笔、快速记账、批量退出和更新日志均可直接访问。全站使用透明底几何图标和冷纸石色版画样式。

仓库：<https://github.com/zulpkar97-max/ai-hub>。本地项目目录统一命名为 `ai-hub`。完整演进记录见 [更新日志.md](./更新日志.md)。

## 启动与编译

直接运行根目录下的 `CodexLauncher.exe` 即可启动。源码编译使用 Windows 11 自带的 64 位 .NET Framework MSBuild 和 WinRT 元数据。`AIHub.csproj` 声明编译源码、嵌入资源及仓库自带的 WebView2 SDK 引用。在 Windows PowerShell 中进入项目目录，运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\编译程序.ps1
```

若当前 `CodexLauncher.exe` 正在运行，可生成热替换更新包并由应用内一键平滑切换：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\编译程序.ps1 -PrepareUpdate
```

修改 `页面代码/` 下的界面结构、样式或交互逻辑时，运行中的窗口通过内置文件监听自动热重载刷新，无需重新编译。

## 项目预览与排序

项目卡上的「预览」在后台启动已接入的本机服务，确认就绪后打开默认浏览器。运行期间可以打开现有预览，或用卡片上的停止按钮结束 AI Hub 启动的服务。端口被占用时会提示，项目不会换到其他端口。

AF-unknown-Astra、留学总盘、AF Runtime 和 Portfolio V2 的卡片分别固定使用 `1000`、`3000`、`4000` 和 `5000`。AI Hub 是桌面应用，`2000` 预留。AF Runtime 单独运行原有启动脚本时仍使用它的 `5001` 正式入口。

拖动卡片右上角的把手即可调整顺序。聚焦把手后也可用方向键移动。顺序保存在本机项目配置中。

## 配置快速记账

在 `%USERPROFILE%\.codex-launcher\quick-ledger.json` 中保存一个 UTF-8 JSON 对象，键为入口名称，值为飞书 HTTPS 地址。支持租户域名下的 `/wiki/`、`/base/`、`/sheets/` 和 `/docx/` 链接，可带表和视图参数。例如：

```json
{
  "日常记账": "https://my.feishu.cn/base/add/你的表单链接标识"
}
```

侧栏「快捷工具 → 记账」和 Ctrl+K 会用默认浏览器直接打开配置中的首条有效链接。保存修改后，AI Hub 在下一次状态刷新时更新入口。

## 额度自动刷新

「设置 → 快捷键与通知」可调整 Codex 和 Antigravity 的额度自动刷新与回满提醒，默认开启。窗口隐藏到系统托盘后，仍按低频调度。仅当 AI Hub 正在运行、Clash 进程和本地代理端口可用且网络状态未标为停用时，才执行自动刷新。手动刷新仍可独立尝试。连续自动失败的等待时间依次为 10、30、120 分钟，任意成功会重置等待。

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
- `核心源码/宿主/`：程序入口、WPF 窗口、托盘、WebView 消息桥和本地更新协调。
- `核心源码/工作台/`：页面动作、状态组装与轮询、账号额度调度。
- `核心源码/账号/`：Antigravity CLI 额度读取与缓存。
- `核心源码/网络/`：Clash 节点状态、测速与订阅信息。
- `核心源码/截图/`：多屏截图、标注、置顶贴图与离线 OCR。
- `核心源码/桌面/`：桌面图标收纳与恢复。
- `核心源码/核心服务.cs`、`核心源码/数据模型.cs`：共用的本机服务、账号隔离和数据结构。
- `资源/`：透明底几何图标、多分辨率 ICO、Windows 应用清单、OCR 脚本和 WebView2 SDK 依赖文件。
- `AIHub.csproj`、`编译程序.ps1`、`应用更新.ps1`：构建声明、编译入口与运行期更新脚本。
- `CodexLauncher.exe`：当前随仓库提供的启动程序，由编译脚本生成。桌面快捷方式指向这个文件。
- `更新日志.md`：从 v1.0 至当前版本的完整演进说明。

## 核心机制与数据隔离

- **本机数据与代码完全分离**：所有账号配置、隔离运行目录（`profiles/`）、图标缓存与桌面图标收纳夹（`desktop-stash/`）均独立存放在用户目录下的 `~/.codex-launcher/`，仓库代码内不包含任何个人账号或本地凭证。
- **Codex 隔离实例**：启动器为附加账号设置各自的 `CODEX_HOME` 和 Electron 用户数据目录，主账号沿用默认目录。
- **工作台与设置**：工作台显示工作工具和网络状态，设置页管理快捷键、通知和应用。`Ctrl+K` 打开快捷命令面板。
