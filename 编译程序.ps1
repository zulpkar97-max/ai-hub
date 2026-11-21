param([string]$OutputPath, [switch]$PrepareUpdate)
if ($PrepareUpdate -and $OutputPath) { throw '不能同时指定 -PrepareUpdate 和 -OutputPath。' }
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$net  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$csc  = Join-Path $net 'csc.exe'
$wpf  = Join-Path $net 'WPF'
$lib  = Join-Path $root '资源\第三方'
$ico  = Join-Path $root '资源\aihub.ico'
$manifest = Join-Path $root '资源\AIHub.manifest'
$live = Join-Path $root 'CodexLauncher.exe'
$pending = Join-Path $root 'CodexLauncher.next.exe'
$stage = if ($PrepareUpdate) { Join-Path $root ('.aihub-build-' + [guid]::NewGuid().ToString('N') + '.tmp') }
$out = if ($stage) { $stage } elseif ($OutputPath) { [IO.Path]::GetFullPath($OutputPath) } else { $live }
$ui = Join-Path $root '页面代码'

$winmd = 'C:\Windows\System32\WinMetadata'

$sources = @(
    (Join-Path $root '核心源码\数据模型.cs'),
    (Join-Path $root '核心源码\核心服务.cs'),
    (Join-Path $root '核心源码\桌面图标收纳.cs'),
    (Join-Path $root '核心源码\离线文字识别.cs'),
    (Join-Path $root '核心源码\界面辅助.cs'),
    (Join-Path $root '核心源码\截图与标注引擎.cs'),
    (Join-Path $root '核心源码\托盘与浮窗.cs'),
    (Join-Path $root '核心源码\主窗口.cs'),
    (Join-Path $root '核心源码\热更新协调.cs'),
    (Join-Path $root '核心源码\程序入口.cs')
)

try {
& $csc /nologo /target:winexe /optimize+ /utf8output `
    "/resource:$ui\index.html,AIHub.Ui.index.html" `
    "/resource:$ui\界面样式.css,AIHub.Ui.界面样式.css" `
    "/resource:$ui\工作台交互.js,AIHub.Ui.工作台交互.js" `
    "/win32icon:$ico" `
    "/win32manifest:$manifest" `
    "/out:$out" `
    "/r:$net\System.dll" `
    "/r:$net\System.Core.dll" `
    "/r:$net\System.Drawing.dll" `
    "/r:$net\System.Management.dll" `
    "/r:$net\System.Runtime.dll" `
    "/r:$net\System.Web.Extensions.dll" `
    "/r:$net\System.Windows.Forms.dll" `
    "/r:$net\System.Xaml.dll" `
    "/r:$wpf\WindowsBase.dll" `
    "/r:$wpf\PresentationCore.dll" `
    "/r:$wpf\PresentationFramework.dll" `
    "/r:$winmd\Windows.Foundation.winmd" `
    "/r:$winmd\Windows.Globalization.winmd" `
    "/r:$winmd\Windows.Graphics.winmd" `
    "/r:$winmd\Windows.Media.winmd" `
    "/r:$winmd\Windows.Storage.winmd" `
    "/r:$lib\Microsoft.Web.WebView2.Core.dll" `
    "/r:$lib\Microsoft.Web.WebView2.Wpf.dll" `
    $sources

if ($LASTEXITCODE -ne 0) {
    throw "编译失败，退出码：$LASTEXITCODE"
}

if ($PrepareUpdate) {
    if ([IO.File]::Exists($pending)) { [IO.File]::Replace($stage, $pending, [NullString]::Value) }
    else { [IO.File]::Move($stage, $pending) }
    Write-Output "已生成待应用更新包：$pending"
} else {
    Write-Output "已完成 AI Hub 编译：$out"
}
} finally {
    if ($stage -and (Test-Path -LiteralPath $stage -PathType Leaf)) { Remove-Item -LiteralPath $stage -Force }
}
