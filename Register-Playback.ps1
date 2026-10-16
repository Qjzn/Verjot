#requires -Version 5.1
[CmdletBinding()]
param([switch]$Plan, [switch]$Unregister)
$ErrorActionPreference = 'Stop'
$taskExe = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'ObsidianMeeting.exe'))
$taskRegistryPath = 'Software\Classes\pianyan'
$taskCommand = '"' + $taskExe + '" --play-uri "%1"'
$taskKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($taskRegistryPath)
try {
    $taskExistingOwner = if ($taskKey) { [string]$taskKey.GetValue('OwnerExecutable', '') } else { '' }
    if ($taskKey -and $taskExistingOwner -ine $taskExe) { throw 'pianyan 协议已被其他程序或另一个安装目录使用，未覆盖。' }
} finally { if ($taskKey) { $taskKey.Dispose() } }
if ($Plan) {
    [pscustomobject]@{ protocol = 'pianyan'; action = $(if ($Unregister) { 'unregister-owned' } else { 'register-current-user' }); executable = $taskExe; changes_applied = $false } | ConvertTo-Json -Compress
    exit 0
}
if ($Unregister) {
    if ($taskExistingOwner) { [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($taskRegistryPath, $false) }
    Write-Output '已移除本目录的 verbit 时间点回放协议。录音与笔记未修改。'
    exit 0
}
if (-not (Test-Path -LiteralPath $taskExe -PathType Leaf)) { throw '请先运行 Build.ps1 生成正式程序。' }
$taskCreated = -not $taskExistingOwner
try {
    $taskKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($taskRegistryPath)
    try {
        $taskKey.SetValue('OwnerExecutable', $taskExe)
        $taskKey.SetValue('', 'URL:verbit 音频回放')
        $taskSubkey = $taskKey.CreateSubKey('shell\open\command')
        try { $taskSubkey.SetValue('', $taskCommand) } finally { $taskSubkey.Dispose() }
        # Activate the URI protocol only after its quoted command is complete.
        $taskKey.SetValue('URL Protocol', '')
    } finally { $taskKey.Dispose() }
} catch {
    if ($taskCreated) { [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($taskRegistryPath, $false) }
    throw
}
Write-Output '已为当前用户启用 pianyan:// 时间点回放，无需管理员权限。'
exit 0
