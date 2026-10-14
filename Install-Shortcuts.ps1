$ErrorActionPreference = 'Stop'
$taskExe = Join-Path $PSScriptRoot 'ObsidianMeeting.exe'
if (-not (Test-Path -LiteralPath $taskExe -PathType Leaf)) { throw 'Meeting recorder executable missing' }
$taskShell = New-Object -ComObject WScript.Shell
foreach ($taskPair in @(
    @{ folder=[Environment]::GetFolderPath('Startup'); name='听记.lnk'; legacy='Obsidian 会议录音快捷键.lnk' },
    @{ folder=[Environment]::GetFolderPath('Programs'); name='听记.lnk'; legacy='Obsidian 会议录音.lnk' }
)) {
    $taskPath = Join-Path $taskPair.folder $taskPair.name
    $taskShortcut = $taskShell.CreateShortcut($taskPath)
    $taskShortcut.TargetPath = $taskExe
    $taskShortcut.WorkingDirectory = $PSScriptRoot
    $taskShortcut.Description = 'Ctrl+Alt+L 开始或结束本地中文会议录音'
    $taskShortcut.IconLocation = "$taskExe,0"
    $taskShortcut.WindowStyle = 7
    $taskShortcut.Save()
    # Remove only this recorder's old alias, preventing duplicate login starts.
    $taskLegacyPath = Join-Path $taskPair.folder $taskPair.legacy
    if (Test-Path -LiteralPath $taskLegacyPath -PathType Leaf) {
        $taskLegacy = $taskShell.CreateShortcut($taskLegacyPath)
        if ([IO.Path]::GetFullPath($taskLegacy.TargetPath) -ieq [IO.Path]::GetFullPath($taskExe)) {
            Remove-Item -LiteralPath $taskLegacyPath
        }
    }
    Write-Output $taskPath
}
