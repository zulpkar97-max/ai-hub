param(
    [Parameter(Mandatory)] [int]$RunningProcessId,
    [Parameter(Mandatory)] [long]$RunningStartTicks,
    [Parameter(Mandatory)] [ValidatePattern('^[0-9A-Fa-f]{64}$')] [string]$ExpectedHash,
    [Parameter(Mandatory)] [ValidatePattern('^[0-9a-f]{32}$')] [string]$ReadyToken,
    [switch]$ReportToConsole
)

$ErrorActionPreference = 'Stop'
$updateRoot = $PSScriptRoot
$installedProgram = Join-Path $updateRoot 'AI BASE.exe'
$pendingProgram = Join-Path $updateRoot 'AI BASE.next.exe'
$updateTemporary = Join-Path $updateRoot ('.aihub-update-' + $ReadyToken + '.tmp')
$rollbackProgram = $updateTemporary + '.previous'
$updateReady = $null
$updatedProcess = $null
$waitingFor = $null
$switched = $false
$committed = $false
$parentExited = $RunningProcessId -eq 0
$hasUpdateLock = $false
$updateLock = [Threading.Mutex]::new($false, 'Local\AIHubLocalUpdate')

function Read-UpdateHash([string]$path) {
    $hash = [Security.Cryptography.SHA256]::Create()
    try {
        $file = [IO.File]::OpenRead($path)
        try { [BitConverter]::ToString($hash.ComputeHash($file)).Replace('-', '') }
        finally { $file.Dispose() }
    }
    finally { $hash.Dispose() }
}

try {
    try { $hasUpdateLock = $updateLock.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $hasUpdateLock = $true }
    if (-not $hasUpdateLock) { return }

    if ($RunningProcessId -gt 0) {
        $waitingFor = Get-Process -Id $RunningProcessId -ErrorAction SilentlyContinue
        if ($waitingFor -and $waitingFor.StartTime.ToUniversalTime().Ticks -eq $RunningStartTicks) {
            if (-not $waitingFor.WaitForExit(20000)) { throw 'AI Hub did not exit. The pending update is ready to retry.' }
        }
        $parentExited = $true
    }

    if ((Read-UpdateHash $pendingProgram) -ne $ExpectedHash) {
        throw 'The staged update changed. Prepare it again and retry.'
    }
    [IO.File]::Copy($pendingProgram, $updateTemporary)
    if ((Read-UpdateHash $updateTemporary) -ne $ExpectedHash) {
        throw 'Could not verify the copied update.'
    }

    $updateReady = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset, ('Local\AIHubUpdateReady-' + $ReadyToken))
    [IO.File]::Replace($updateTemporary, $installedProgram, $rollbackProgram)
    $switched = $true
    $updatedProcess = Start-Process -FilePath $installedProgram -ArgumentList @('--update-ready', $ReadyToken) -WorkingDirectory $updateRoot -PassThru

    $healthCheck = [Diagnostics.Stopwatch]::StartNew()
    while (-not $updateReady.WaitOne(200)) {
        if ($updatedProcess.HasExited -or $healthCheck.ElapsedMilliseconds -ge 30000) {
            throw 'The new version did not finish opening its interface.'
        }
    }
    $committed = $true
    [IO.File]::Delete($rollbackProgram)
    if ([IO.File]::Exists($pendingProgram) -and (Read-UpdateHash $pendingProgram) -eq $ExpectedHash) {
        [IO.File]::Delete($pendingProgram)
    }
}
catch {
    $message = $_.Exception.Message
    if ($switched -and -not $committed) {
        try {
            if ($updatedProcess -and -not $updatedProcess.HasExited) {
                $updatedProcess.Kill()
                if (-not $updatedProcess.WaitForExit(5000)) { throw 'New process did not release the executable.' }
            }
            [IO.File]::Replace($rollbackProgram, $installedProgram, [NullString]::Value)
            $switched = $false
        }
        catch {
            $message += [Environment]::NewLine + 'Recovery also failed: ' + $_.Exception.Message
            $message += [Environment]::NewLine + $rollbackProgram
        }
    }

    if ($parentExited -and -not $committed -and -not $switched -and (Test-Path -LiteralPath $installedProgram -PathType Leaf)) {
        Start-Process -FilePath $installedProgram -WorkingDirectory $updateRoot
    }
    if ($ReportToConsole) { Write-Error $message -ErrorAction Continue }
    else {
        Add-Type -AssemblyName System.Windows.Forms
        [void][Windows.Forms.MessageBox]::Show($message, 'AI Hub Update')
    }
    exit 1
}
finally {
    if ($waitingFor) { $waitingFor.Dispose() }
    if ($updatedProcess) { $updatedProcess.Dispose() }
    if ($updateReady) { $updateReady.Dispose() }
    if (Test-Path -LiteralPath $updateTemporary -PathType Leaf) { Remove-Item -LiteralPath $updateTemporary -Force -ErrorAction SilentlyContinue }
    if ($hasUpdateLock) { $updateLock.ReleaseMutex() }
    $updateLock.Dispose()
}

