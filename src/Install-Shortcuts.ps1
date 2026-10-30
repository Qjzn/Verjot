param([switch]$StartWithWindows)
$ErrorActionPreference = 'Stop'
$taskExe = Join-Path $PSScriptRoot 'ObsidianMeeting.exe'
if (-not (Test-Path -LiteralPath $taskExe -PathType Leaf)) { throw 'Meeting recorder executable missing' }
$taskShell = New-Object -ComObject WScript.Shell
foreach ($taskPair in @(
    $(if ($StartWithWindows) { @{ folder=[Environment]::GetFolderPath('Startup'); name='Verlog.lnk'; legacy='Obsidian 会议录音快捷键.lnk' } }),
    @{ folder=[Environment]::GetFolderPath('Programs'); name='Verlog.lnk'; legacy='Obsidian 会议录音.lnk' }
) | Where-Object { $_ }) {
    $taskPath = Join-Path $taskPair.folder $taskPair.name
    $taskShortcut = $taskShell.CreateShortcut($taskPath)
    $taskShortcut.TargetPath = $taskExe
    $taskShortcut.WorkingDirectory = $PSScriptRoot
    if ($taskPair.folder -eq [Environment]::GetFolderPath('Programs')) { $taskShortcut.Arguments = '--show' }
    $taskShortcut.Description = 'Ctrl+Alt+L 开始或结束本地中文会议录音'
    $taskShortcut.IconLocation = "$taskExe,0"
    $taskShortcut.WindowStyle = 7
    $taskShortcut.Save()
    # Remove only this recorder's old alias, preventing duplicate login starts.
    foreach ($taskLegacyName in @($taskPair.legacy, '听记.lnk', '片言.lnk', 'verbit.lnk', 'verbit installed.lnk')) {
        $taskLegacyPath = Join-Path $taskPair.folder $taskLegacyName
        if (Test-Path -LiteralPath $taskLegacyPath -PathType Leaf) {
            $taskLegacy = $taskShell.CreateShortcut($taskLegacyPath)
            if ($taskLegacy.TargetPath -and [IO.Path]::GetFullPath($taskLegacy.TargetPath) -ieq [IO.Path]::GetFullPath($taskExe)) {
                Remove-Item -LiteralPath $taskLegacyPath
            }
        }
    }
    Write-Output $taskPath
}
