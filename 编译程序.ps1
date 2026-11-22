param([string]$OutputPath, [switch]$PrepareUpdate)
if ($PrepareUpdate -and $OutputPath) { throw '不能同时指定 -PrepareUpdate 和 -OutputPath。' }

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$msbuild = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe'
$project = Join-Path $root 'AIHub.csproj'
$stageDir = Join-Path $root ('.aihub-build-' + [guid]::NewGuid().ToString('N') + '.tmp')
$stage = Join-Path $stageDir 'AI BASE.exe'
$output = if ($PrepareUpdate) { Join-Path $root 'AI BASE.next.exe' }
    elseif ($OutputPath) { $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath) }
    else { Join-Path $root 'AI BASE.exe' }

try {
    & $msbuild $project '/nologo' '/verbosity:minimal' "/p:OutputFile=$stage"
    if ($LASTEXITCODE -ne 0) { throw "编译失败，退出码：$LASTEXITCODE" }

    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output)) | Out-Null
    if ($PrepareUpdate) {
        if ([IO.File]::Exists($output)) { [IO.File]::Replace($stage, $output, [NullString]::Value) }
        else { [IO.File]::Move($stage, $output) }
        Write-Output "已生成待应用更新包：$output"
    } else {
        [IO.File]::Copy($stage, $output, $true)
        Write-Output "已完成 AI BASE 编译：$output"
    }
} finally {
    if ([IO.File]::Exists($stage)) { [IO.File]::Delete($stage) }
    if ([IO.Directory]::Exists($stageDir)) { [IO.Directory]::Delete($stageDir) }
}
