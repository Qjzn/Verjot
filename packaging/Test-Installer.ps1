#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$InstallerPath)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskInstaller = [IO.Path]::GetFullPath($InstallerPath)
if (-not (Test-Path -LiteralPath $taskInstaller -PathType Leaf)) { throw 'InstallerPath does not exist.' }
$taskRegistryPath = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{DE124FA5-8E3D-4B80-A62E-AF8FA54B1E43}_is1'
$taskExisting = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($taskRegistryPath)
if ($taskExisting) { $taskExisting.Dispose(); throw 'An EXE installation is already registered for this user. Run installer smoke in a clean disposable Windows user profile; an existing installation will never be replaced by this test.' }
$taskTestRoot = [IO.Path]::GetFullPath((Join-Path $taskRoot ('validation\installer-smoke-' + [Guid]::NewGuid().ToString('N'))))
$taskValidationRoot = [IO.Path]::GetFullPath((Join-Path $taskRoot 'validation')) + [IO.Path]::DirectorySeparatorChar
if (-not $taskTestRoot.StartsWith($taskValidationRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Test root must stay under this repository validation folder.' }
$taskApp = Join-Path $taskTestRoot 'app'
$taskNotes = Join-Path $taskTestRoot 'notes'
$taskGroup = 'verbit validation ' + [Guid]::NewGuid().ToString('N')
$taskMenu = Join-Path ([Environment]::GetFolderPath('Programs')) ($taskGroup + '\verbit.lnk')
if (Test-Path -LiteralPath (Split-Path -Parent $taskMenu)) { throw 'Existing verbit Start Menu group found. Run in a disposable profile; existing entries will not be overwritten by a test.' }
$taskStartup = Join-Path ([Environment]::GetFolderPath('Startup')) 'verbit installed.lnk'
$taskStartupHash = if (Test-Path -LiteralPath $taskStartup) { (Get-FileHash -LiteralPath $taskStartup).Hash } else { '' }
$taskUtf8 = New-Object Text.UTF8Encoding($false)
$taskAssertions = 0
$taskEvents = New-Object 'Collections.Generic.List[object]'
function Assert-Installer([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:taskAssertions++
}
function Invoke-Installer([string]$Executable, [string[]]$ExtraArguments, [string]$Phase) {
    $taskArguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', ('/LOG="' + (Join-Path $taskTestRoot ($Phase + '.log')) + '"')) + $ExtraArguments
    if ($Executable -ieq $taskInstaller) { $taskArguments += ('/GROUP="' + $taskGroup + '"') }
    $taskProcess = Start-Process -FilePath $Executable -ArgumentList $taskArguments -WindowStyle Hidden -PassThru -Wait
    $taskEvents.Add([ordered]@{ phase = $Phase; exit_code = $taskProcess.ExitCode })
    return $taskProcess.ExitCode
}
function Read-Registration {
    $taskKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($taskRegistryPath)
    try {
        if (-not $taskKey) { return $null }
        return [ordered]@{ name = [string]$taskKey.GetValue('DisplayName'); location = [string]$taskKey.GetValue('InstallLocation'); uninstall = [string]$taskKey.GetValue('UninstallString') }
    } finally { if ($taskKey) { $taskKey.Dispose() } }
}
function Get-ExistingEntriesSnapshot {
    $taskItems = @()
    foreach ($taskFolder in @([Environment]::GetFolderPath('Startup'), [Environment]::GetFolderPath('Programs'))) {
        $taskPath = Join-Path $taskFolder 'verbit.lnk'
        $taskItems += [ordered]@{ path = $taskPath; hash = $(if (Test-Path -LiteralPath $taskPath -PathType Leaf) { (Get-FileHash -LiteralPath $taskPath).Hash } else { '' }) }
    }
    $taskProtocolKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Classes\pianyan')
    try { $taskItems += [ordered]@{ protocol_owner = $(if ($taskProtocolKey) { [string]$taskProtocolKey.GetValue('OwnerExecutable', '') } else { '' }) } }
    finally { if ($taskProtocolKey) { $taskProtocolKey.Dispose() } }
    return ($taskItems | ConvertTo-Json -Compress)
}
[IO.Directory]::CreateDirectory($taskTestRoot) | Out-Null
$taskBeforeEntries = Get-ExistingEntriesSnapshot
$taskSucceeded = $false
try {
    Assert-Installer ((Invoke-Installer $taskInstaller @(('/DIR="' + $taskApp + '"'), '/TASKS=""') 'clean-install') -eq 0) 'Clean silent install failed.'
    $taskExe = Join-Path $taskApp 'ObsidianMeeting.exe'
    $taskUninstall = Join-Path $taskApp 'unins000.exe'
    Assert-Installer (Test-Path -LiteralPath $taskExe -PathType Leaf) 'Installed controller missing.'
    Assert-Installer (Test-Path -LiteralPath $taskUninstall -PathType Leaf) 'Uninstaller missing.'
    Assert-Installer (Test-Path -LiteralPath $taskMenu -PathType Leaf) 'Installed Start Menu entry missing.'
    $taskShell = New-Object -ComObject WScript.Shell
    $taskLink = $taskShell.CreateShortcut($taskMenu)
    Assert-Installer ($taskLink.TargetPath -ieq $taskExe) 'Start Menu points to another executable.'
    Assert-Installer ($taskLink.Arguments -eq '--show') 'Start Menu does not activate the existing instance.'
    Assert-Installer ($taskLink.WorkingDirectory -ieq $taskApp) 'Start Menu working directory incorrect.'
    Assert-Installer (-not (Test-Path -LiteralPath (Join-Path $taskApp 'config.json'))) 'Clean installer bundled or created a personal config.'
    foreach ($taskAbsent in @('.venv', 'models', 'work', 'installation', 'recorder.log', 'last-session.json')) {
        Assert-Installer (-not (Test-Path -LiteralPath (Join-Path $taskApp $taskAbsent))) "Clean installer included user state: $taskAbsent"
    }
    foreach ($taskRequired in @('NAudio.dll', 'packaging\licenses\NAudio.txt', 'LICENSE', 'THIRD_PARTY_NOTICES.md', 'documentation\install-windows.md', 'documentation\first-use.md', 'config.example.json', 'config.schema.json')) {
        Assert-Installer (Test-Path -LiteralPath (Join-Path $taskApp $taskRequired) -PathType Leaf) "Required installed file missing: $taskRequired"
    }
    $taskRegistration = Read-Registration
    Assert-Installer ($null -ne $taskRegistration -and $taskRegistration.name -eq 'verbit' -and $taskRegistration.location.TrimEnd('\') -ieq $taskApp) 'Per-user uninstall registration does not identify this exact isolated app.'
    $taskOtherApp = Join-Path $taskTestRoot 'other-app'
    Assert-Installer ((Invoke-Installer $taskInstaller @(('/DIR="' + $taskOtherApp + '"'), '/NOICONS', '/TASKS=""') 'reject-directory-change') -ne 0) 'Same AppId was installed to a second directory.'
    Assert-Installer (-not (Test-Path -LiteralPath (Join-Path $taskOtherApp 'ObsidianMeeting.exe'))) 'Rejected directory change still installed files.'
    Assert-Installer ((Read-Registration).location -ceq $taskRegistration.location) 'Rejected directory change replaced the original install registration.'
    $taskOwnedKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($taskRegistryPath, $true)
    try { $taskOwnedKey.SetValue('InstallLocation', $taskOtherApp + '\') } finally { $taskOwnedKey.Dispose() }
    try {
        Assert-Installer ((Invoke-Installer $taskUninstall @() 'reject-old-uninstaller') -ne 0) 'An old uninstaller removed another registered installation.'
        Assert-Installer (Test-Path -LiteralPath $taskExe -PathType Leaf) 'Rejected old uninstaller still removed its program files.'
        Assert-Installer ((Read-Registration).location.TrimEnd('\') -ceq $taskOtherApp) 'Rejected old uninstaller changed the other owner registration.'
    } finally {
        $taskOwnedKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($taskRegistryPath, $true)
        try { if ($taskOwnedKey) { $taskOwnedKey.SetValue('InstallLocation', $taskRegistration.location) } } finally { if ($taskOwnedKey) { $taskOwnedKey.Dispose() } }
    }
    [IO.Directory]::CreateDirectory($taskNotes) | Out-Null
    $taskConfig = Get-Content -LiteralPath (Join-Path $taskApp 'config.example.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $taskConfig.vault_root = $taskNotes
    $taskConfig.open_in_obsidian = $false
    $taskConfigFile = Join-Path $taskApp 'config.json'
    [IO.File]::WriteAllText($taskConfigFile, ($taskConfig | ConvertTo-Json -Depth 8), $taskUtf8)
    $taskPreserved = @($taskConfigFile)
    foreach ($taskSubdirectory in @('.venv', 'models', 'work')) {
        $taskDirectory = Join-Path $taskApp $taskSubdirectory
        [IO.Directory]::CreateDirectory($taskDirectory) | Out-Null
        $taskSentinel = Join-Path $taskDirectory 'installer-test-sentinel.txt'
        [IO.File]::WriteAllText($taskSentinel, 'Synthetic preservation fixture. No user data.', $taskUtf8)
        $taskPreserved += $taskSentinel
    }
    $taskNote = Join-Path $taskNotes 'note.md'
    [IO.File]::WriteAllText($taskNote, '# Synthetic installer fixture', $taskUtf8)
    $taskPreserved += $taskNote
    $taskHashes = @{}
    foreach ($taskPreservedFile in $taskPreserved) { $taskHashes[$taskPreservedFile] = (Get-FileHash -LiteralPath $taskPreservedFile).Hash }
    $taskCheck = Start-Process -FilePath $taskExe -ArgumentList @('--check-config', ('"' + (Join-Path $taskTestRoot 'native-config-check.json') + '"')) -WindowStyle Hidden -PassThru -Wait
    Assert-Installer ($taskCheck.ExitCode -eq 0 -and (Get-Content -LiteralPath (Join-Path $taskTestRoot 'native-config-check.json') -Raw | ConvertFrom-Json).ok) 'Installed executable did not validate synthetic config.'
    $taskExeHash = (Get-FileHash -LiteralPath $taskExe).Hash
    $taskSession = Join-Path $taskApp 'work\20261016-000000'
    [IO.Directory]::CreateDirectory($taskSession) | Out-Null
    $taskStatusFile = Join-Path $taskSession 'status.json'
    foreach ($taskState in @('recording', 'paused', 'processing', 'queued')) {
        [IO.File]::WriteAllText($taskStatusFile, ('{"state":"' + $taskState + '"}'), $taskUtf8)
        Assert-Installer ((Invoke-Installer $taskInstaller @(('/DIR="' + $taskApp + '"'), '/NOICONS', '/TASKS=""') ('reject-' + $taskState)) -ne 0) "Upgrade ignored active session state: $taskState"
    }
    [IO.File]::WriteAllText($taskStatusFile, '{"state":', $taskUtf8)
    Assert-Installer ((Invoke-Installer $taskInstaller @(('/DIR="' + $taskApp + '"'), '/NOICONS', '/TASKS=""') 'reject-unknown-state') -ne 0) 'Upgrade ignored unreadable session state.'
    [IO.File]::WriteAllText($taskStatusFile, '{"state":"completed"}', $taskUtf8)
    $taskProcessingLock = Join-Path $taskApp 'processing.lock'
    [IO.File]::WriteAllText($taskProcessingLock, '0', $taskUtf8)
    $taskWorkerLock = [IO.File]::Open($taskProcessingLock, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::ReadWrite)
    try {
        $taskWorkerLock.Lock(0, 1)
        Assert-Installer ((Invoke-Installer $taskInstaller @(('/DIR="' + $taskApp + '"'), '/NOICONS', '/TASKS=""') 'reject-worker-byte-lock') -ne 0) 'Upgrade ignored a held worker byte-zero lock after controller exit.'
        Assert-Installer ((Invoke-Installer $taskUninstall @() 'reject-worker-uninstall') -ne 0) 'Uninstall ignored a held worker byte-zero lock after controller exit.'
    } finally { $taskWorkerLock.Dispose() }
    $taskLock = [IO.File]::Open($taskExe, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        Assert-Installer ((Invoke-Installer $taskInstaller @(('/DIR="' + $taskApp + '"'), '/NOICONS', '/TASKS=""') 'locked-upgrade') -ne 0) 'Installer overwrote a locked controller.'
        Assert-Installer ((Invoke-Installer $taskUninstall @() 'locked-uninstall') -ne 0) 'Uninstaller removed a locked controller.'
    } finally { $taskLock.Dispose() }
    Assert-Installer ((Get-FileHash -LiteralPath $taskExe).Hash -eq $taskExeHash) 'Locked operations changed controller bytes.'
    Assert-Installer ((Invoke-Installer $taskInstaller @(('/DIR="' + $taskApp + '"'), '/NOICONS', '/TASKS=""') 'upgrade') -eq 0) 'Unlocked upgrade failed.'
    [IO.File]::WriteAllText($taskStatusFile, '{"state":"error"}', $taskUtf8)
    Assert-Installer ((Invoke-Installer $taskInstaller @(('/DIR="' + $taskApp + '"'), '/NOICONS', '/TASKS=""') 'upgrade-after-error') -eq 0) 'An unheld stale lock and failed terminal state blocked upgrade.'
    foreach ($taskPreservedFile in $taskPreserved) { Assert-Installer ((Get-FileHash -LiteralPath $taskPreservedFile).Hash -eq $taskHashes[$taskPreservedFile]) 'Upgrade changed a preserved fixture.' }
    if (-not $taskStartupHash) {
        $taskStartupLink = $taskShell.CreateShortcut($taskStartup)
        $taskStartupLink.TargetPath = $taskExe; $taskStartupLink.WorkingDirectory = $taskApp; $taskStartupLink.Save()
        Assert-Installer (Test-Path -LiteralPath $taskStartup -PathType Leaf) 'Settings-created startup fixture missing.'
    }
    Assert-Installer ((Invoke-Installer $taskUninstall @() 'uninstall') -eq 0) 'Uninstall failed.'
    Assert-Installer (-not (Test-Path -LiteralPath $taskMenu)) 'Uninstall left the owned Start Menu shortcut.'
    if (-not $taskStartupHash) { Assert-Installer (-not (Test-Path -LiteralPath $taskStartup)) 'Uninstall left settings-created autostart.' }
    else { Assert-Installer ((Get-FileHash -LiteralPath $taskStartup).Hash -eq $taskStartupHash) 'Uninstall changed another installation autostart.' }
    Assert-Installer (-not (Test-Path -LiteralPath $taskExe)) 'Uninstall left installed controller.'
    Assert-Installer ($null -eq (Read-Registration)) 'Uninstall left this app registration.'
    foreach ($taskPreservedFile in $taskPreserved) { Assert-Installer ((Get-FileHash -LiteralPath $taskPreservedFile).Hash -eq $taskHashes[$taskPreservedFile]) 'Uninstall changed a preserved fixture.' }
    Assert-Installer ((Get-ExistingEntriesSnapshot) -ceq $taskBeforeEntries) 'Installer changed existing source-install shortcuts or playback protocol.'
    $taskSucceeded = $true
} finally {
    [ordered]@{ passed = $taskSucceeded; assertions = $taskAssertions; installer_sha256 = (Get-FileHash -LiteralPath $taskInstaller).Hash.ToLowerInvariant(); test_root = $taskTestRoot; phases = $taskEvents.ToArray(); clipboard_access = $false; audio_recorded = $false; audio_played = $false; models_downloaded = $false } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $taskTestRoot 'report.json') -Encoding UTF8
}
Get-Content -LiteralPath (Join-Path $taskTestRoot 'report.json') -Raw
