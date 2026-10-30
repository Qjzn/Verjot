#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Root)
$ErrorActionPreference = 'Stop'
function Assert-OrdinaryPath([string]$Path) {
    $taskCursor = [IO.Path]::GetFullPath($Path)
    while ($taskCursor) {
        if (Test-Path -LiteralPath $taskCursor) {
            $taskItem = Get-Item -LiteralPath $taskCursor -Force
            if (($taskItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Reparse paths cannot be confirmed safe.' }
        }
        $taskParent = [IO.Path]::GetDirectoryName($taskCursor)
        if ($taskParent -eq $taskCursor) { break }
        $taskCursor = $taskParent
    }
}
function Assert-ExecutableIdle([string]$Path) {
    Assert-OrdinaryPath $Path
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        $taskHandle = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $taskHandle.Dispose()
    }
}
function Read-State([string]$Path) {
    Assert-OrdinaryPath $Path
    $taskInfo = Get-Item -LiteralPath $Path
    if ($taskInfo.Length -gt 1MB) { throw 'Session state exceeds the safety size limit.' }
    $taskValue = [IO.File]::ReadAllText($Path) | ConvertFrom-Json
    if ($null -eq $taskValue -or $taskValue -is [Array] -or $taskValue -isnot [pscustomobject] -or $taskValue.state -isnot [string]) { throw 'Session state format is invalid.' }
    return $taskValue.state
}
try {
    $taskRoot = [IO.Path]::GetFullPath($Root)
    Assert-OrdinaryPath $taskRoot
    foreach ($taskSetupLockName in @('recommended-model.lock', 'runtime-install.lock')) {
        $taskSetupLockPath = Join-Path $taskRoot $taskSetupLockName
        Assert-OrdinaryPath $taskSetupLockPath
        if (Test-Path -LiteralPath $taskSetupLockPath -PathType Leaf) {
            $taskSetupHandle = [IO.File]::Open($taskSetupLockPath, 'Open', 'ReadWrite', 'None')
            $taskSetupHandle.Dispose()
        }
    }
    Assert-ExecutableIdle (Join-Path $taskRoot 'ObsidianMeeting.exe')
    Assert-ExecutableIdle (Join-Path $taskRoot '.venv\Scripts\python.exe')
    Assert-ExecutableIdle (Join-Path $taskRoot '.venv\Scripts\pythonw.exe')
    $taskLockPath = Join-Path $taskRoot 'processing.lock'
    Assert-OrdinaryPath $taskLockPath
    if (Test-Path -LiteralPath $taskLockPath -PathType Leaf) {
        # Interoperates with meeting_worker's msvcrt.locking byte-zero lock.
        # A stale unheld file is harmless; an active worker's lock is rejected.
        $taskLock = [IO.File]::Open($taskLockPath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::ReadWrite)
        try { $taskLock.Lock(0, 1); $taskLock.Unlock(0, 1) } finally { $taskLock.Dispose() }
    }
    $taskWork = Join-Path $taskRoot 'work'
    Assert-OrdinaryPath $taskWork
    if (Test-Path -LiteralPath $taskWork -PathType Container) {
        foreach ($taskSession in @(Get-ChildItem -LiteralPath $taskWork -Directory -Force)) {
            Assert-OrdinaryPath $taskSession.FullName
            $taskMetadata = Join-Path $taskSession.FullName 'session.json'
            $taskStatus = Join-Path $taskSession.FullName 'status.json'
            if (Test-Path -LiteralPath $taskMetadata -PathType Leaf) {
                $taskState = Read-State $taskMetadata
                if ($taskState -notin @('recorded', 'recorded_with_error')) { throw 'Recording remains active or unconfirmed.' }
            }
            if (Test-Path -LiteralPath $taskStatus -PathType Leaf) {
                $taskState = Read-State $taskStatus
                if ($taskState -notin @('completed', 'done', 'success', 'error', 'failed', 'audio_ready')) { throw 'Transcription remains active, queued or unconfirmed.' }
            }
        }
    }
    Write-Output 'Installation is idle. No recordings, notes or runtime files were changed.'
    exit 0
} catch {
    # Do not include captured content, meeting titles, credentials or config.
    Write-Output 'Cannot safely upgrade or uninstall. Close Verlog, wait for workers, and check session states and file permissions. No process or data was changed.'
    exit 1
}
