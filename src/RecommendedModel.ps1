#requires -Version 5.1
[CmdletBinding()]
param([switch]$Worker, [switch]$Start, [switch]$Plan, [switch]$FunctionsOnly)
$ErrorActionPreference = 'Stop'
$taskModelRoot = $PSScriptRoot
$taskModelDirectory = Join-Path $taskModelRoot 'installation'
$taskModelStatus = Join-Path $taskModelDirectory 'recommended-model.json'
$taskModelUtf8 = New-Object Text.UTF8Encoding($false)

function Assert-ModelPath([string]$Path) {
    $taskCursor = [IO.Path]::GetFullPath($Path)
    while ($taskCursor) {
        if (Test-Path -LiteralPath $taskCursor) {
            if (((Get-Item -LiteralPath $taskCursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw '模型安装路径不能经过符号链接或重解析目录。' }
        }
        $taskParent = [IO.Path]::GetDirectoryName($taskCursor)
        if ($taskParent -eq $taskCursor) { break }; $taskCursor = $taskParent
    }
}
function Assert-ModelIdle([string]$Root) {
    $taskWork = Join-Path $Root 'work'; Assert-ModelPath $taskWork
    $taskSessions = @()
    if (Test-Path -LiteralPath $taskWork -PathType Container) { $taskSessions = @(Get-ChildItem -LiteralPath $taskWork -Directory -ErrorAction Stop) }
    foreach ($taskSession in $taskSessions) {
        if ((Test-Path -LiteralPath (Join-Path $taskSession.FullName 'session.json')) -and -not (Test-Path -LiteralPath (Join-Path $taskSession.FullName 'status.json'))) {
            throw '会议仍待处理或状态未确认，请先检查恢复资料再下载模型。'
        }
        foreach ($taskStateFile in @('session.json', 'status.json')) {
            $taskPath = Join-Path $taskSession.FullName $taskStateFile
            Assert-ModelPath $taskPath
            if (Test-Path -LiteralPath $taskPath -PathType Leaf) {
                if ((Get-Item -LiteralPath $taskPath).Length -gt 1MB) { throw '会议状态过大，请先检查恢复资料。' }
                $taskState = ([IO.File]::ReadAllText($taskPath) | ConvertFrom-Json).state
                $taskAllowed = if ($taskStateFile -eq 'session.json') { @('recorded', 'recorded_with_error') } else { @('completed', 'done', 'success', 'error', 'failed') }
                if ($taskState -notin $taskAllowed) { throw '请先结束录音并等待转写完成，再下载模型；速记仍可使用。' }
            }
        }
    }
    $taskProcessing = Join-Path $Root 'processing.lock'
    Assert-ModelPath $taskProcessing
    if (Test-Path -LiteralPath $taskProcessing -PathType Leaf) {
        $taskHandle = [IO.File]::Open($taskProcessing, 'Open', 'ReadWrite', 'ReadWrite')
        try { $taskHandle.Lock(0, 1); $taskHandle.Unlock(0, 1) } finally { $taskHandle.Dispose() }
    }
}
function Set-ModelStatus([string]$State, [string]$Message) {
    $taskTemp = $taskModelStatus + '.' + $PID + '.tmp'
    [IO.File]::WriteAllText($taskTemp, (@{ state=$State; message=$Message; pid=$PID; updated_at=[DateTimeOffset]::Now.ToString('o') } | ConvertTo-Json -Compress), $taskModelUtf8)
    if (Test-Path -LiteralPath $taskModelStatus) { [IO.File]::Replace($taskTemp, $taskModelStatus, ($taskModelStatus + '.previous')) }
    else { [IO.File]::Move($taskTemp, $taskModelStatus) }
}
function Get-RecommendedPython([string]$Root) {
    $taskVenvPython = Join-Path $Root '.venv\Scripts\python.exe'
    Assert-ModelPath $taskVenvPython
    if (Test-Path -LiteralPath $taskVenvPython -PathType Leaf) { return $taskVenvPython }
    if (Test-Path -LiteralPath (Join-Path $Root '.venv')) { throw '已有未完成的 .venv，未下载或覆盖其他环境；请查看安装说明修复。' }
    # The official CPython NuGet runtime includes venv and ensurepip. It is
    # application-local: no registry, PATH, file association or global launcher.
    $taskPythonHome = Join-Path $Root 'runtime-python-3.12.10'
    $taskPython = Join-Path $taskPythonHome 'tools\python.exe'
    Assert-ModelPath $taskPython
    if (Test-Path -LiteralPath (Join-Path $taskPythonHome 'verified.sha256') -PathType Leaf) {
        if (-not (Test-Path -LiteralPath $taskPython -PathType Leaf)) { throw '私有 Python 不完整，请保留目录并查看安装日志。' }
        return $taskPython
    }
    if (Test-Path -LiteralPath $taskPythonHome) { throw '已有未确认的私有 Python 目录；不会覆盖它，请检查恢复资料。' }
    $taskPackage = Join-Path $Root 'installation\python.3.12.10.nupkg'
    $taskHash = '0EB85C2DFCCCCF1B17352DE4C397F69194035B7D37149EACC16F1147D93DE3B8'
    Assert-ModelPath $taskPackage
    if (-not (Test-Path -LiteralPath $taskPackage -PathType Leaf) -or (Get-FileHash -LiteralPath $taskPackage -Algorithm SHA256).Hash -ne $taskHash) {
        Set-ModelStatus 'preparing' '下载应用私有 Python（约 15 MB），不会修改系统 Python'
        $taskPartial = $taskPackage + '.partial'
        Assert-ModelPath $taskPartial
        Invoke-WebRequest -UseBasicParsing -Uri 'https://api.nuget.org/v3-flatcontainer/python/3.12.10/python.3.12.10.nupkg' -OutFile $taskPartial
        if ((Get-FileHash -LiteralPath $taskPartial -Algorithm SHA256).Hash -ne $taskHash) { throw 'Python 下载校验失败，未执行下载内容。请重试。' }
        Move-Item -LiteralPath $taskPartial -Destination $taskPackage -Force
    }
    Set-ModelStatus 'preparing' '校验并准备应用私有 Python'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $taskStaging = Join-Path $Root ('installation\python-stage-' + [Guid]::NewGuid().ToString('N'))
    [IO.Compression.ZipFile]::ExtractToDirectory($taskPackage, $taskStaging)
    # Keep interrupted staging for diagnosis. A complete directory is published
    # once, so retries never execute a half-extracted Python interpreter.
    if (-not (Test-Path -LiteralPath (Join-Path $taskStaging 'tools\Lib\ensurepip\__init__.py'))) { throw '官方 Python 包缺少 ensurepip。' }
    [IO.File]::WriteAllText((Join-Path $taskStaging 'verified.sha256'), $taskHash, $taskModelUtf8)
    Move-Item -LiteralPath $taskStaging -Destination $taskPythonHome
    return $taskPython
}
function Get-RecommendedPlan {
    [ordered]@{ model='Qwen3-ASR-1.7B-hf'; license='Apache-2.0'; backend='cpu'; includes='必要的说话人组件与固定推理依赖'; download='数 GB，实际以下载进度为准'; audio_uploaded=$false; changes_system_python=$false; changes_existing_config=$false; sources=@('api.nuget.org','download.pytorch.org','pypi.org','huggingface.co','github.com/k2-fsa/sherpa-onnx') }
}
if ($FunctionsOnly) { return }
if ($Plan) { Get-RecommendedPlan | ConvertTo-Json -Depth 4; exit 0 }
Assert-ModelPath $taskModelRoot
Assert-ModelPath $taskModelDirectory
[IO.Directory]::CreateDirectory($taskModelDirectory) | Out-Null

if ($Worker) {
    $taskLock = $null
    try {
        $taskLockPath = Join-Path $taskModelRoot 'recommended-model.lock'; Assert-ModelPath $taskLockPath
        $taskLock = [IO.File]::Open($taskLockPath, 'OpenOrCreate', 'ReadWrite', 'None')
        Assert-ModelIdle $taskModelRoot
        Set-ModelStatus 'preparing' '准备千问中文转写，下载只涉及程序和模型，不读取会议音频'
        $taskPrivatePython = Get-RecommendedPython $taskModelRoot
        $taskBackend = 'cpu'
        if (Test-Path -LiteralPath (Join-Path $taskModelRoot '.venv\Scripts\python.exe')) {
            $taskBackendInfo = & $taskPrivatePython -B -c "import importlib.util; print(__import__('torch').version.cuda or 'cpu' if importlib.util.find_spec('torch') else 'cpu')"
            if ($LASTEXITCODE -ne 0) { throw '已有本地推理环境无法导入，未覆盖它。请检查恢复资料。' }
            if (($taskBackendInfo -join '').Trim() -eq '12.8') { $taskBackend = 'cuda128' }
        }
        Set-ModelStatus 'installing' '安装固定 CPU 依赖与推荐模型；速记可继续使用'
        $taskInstallScript = Join-Path $taskModelRoot 'Install.ps1'
        $taskInstallLog = Join-Path $taskModelDirectory 'recommended-install.log'
        $taskPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
        & $taskPowerShell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $taskInstallScript -RuntimeOnly -Backend $taskBackend -PythonExe $taskPrivatePython *> $taskInstallLog
        if ($LASTEXITCODE -ne 0) { throw '准备未完成。可重试并复用已下载文件；请查看 installation\recommended-install.log。若缺少 VC++ 运行库，按日志中的 Microsoft 官方入口安装。' }
        Set-ModelStatus 'completed' '推荐模型与依赖已准备。真实设备与转写效果尚未验收；API 模式不会自动切换为本地。'
        exit 0
    } catch {
        if ($taskLock) { Set-ModelStatus 'error' $_.Exception.Message }
        exit 1
    } finally { if ($taskLock) { $taskLock.Dispose() } }
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type 'using System; using System.Runtime.InteropServices; public static class VerbitSetupDpi { [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value); }'
$null = [VerbitSetupDpi]::SetProcessDpiAwarenessContext([IntPtr](-4))
[Windows.Forms.Application]::EnableVisualStyles()
$taskWindow = New-Object Windows.Forms.Form
$taskWindow.Text = 'verbit · 下载推荐模型'
$taskWindow.AutoScaleMode = 'Dpi'; $taskWindow.AutoScaleDimensions = New-Object Drawing.SizeF(96,96); $taskWindow.ClientSize = New-Object Drawing.Size(480, 300)
$taskWindow.StartPosition = 'CenterScreen'; $taskWindow.MinimumSize = $taskWindow.Size
$taskWindow.TopMost = $true
$taskIcon = Join-Path $taskModelRoot 'assets\tingji.ico'
if (Test-Path -LiteralPath $taskIcon) { $taskWindow.Icon = New-Object Drawing.Icon($taskIcon) }
$taskWindow.Font = New-Object Drawing.Font('Microsoft YaHei UI',12,[Drawing.FontStyle]::Regular,[Drawing.GraphicsUnit]::Pixel)
$taskLayout = New-Object Windows.Forms.TableLayoutPanel
$taskLayout.Dock = 'Fill'; $taskLayout.Padding = New-Object Windows.Forms.Padding(18)
$taskLayout.ColumnCount = 1; $taskLayout.RowCount = 4
$taskLayout.ColumnStyles.Add((New-Object Windows.Forms.ColumnStyle('Percent',100))) | Out-Null
$taskLayout.RowStyles.Add((New-Object Windows.Forms.RowStyle('Absolute',120))) | Out-Null
$taskLayout.RowStyles.Add((New-Object Windows.Forms.RowStyle('Percent',100))) | Out-Null
$taskLayout.RowStyles.Add((New-Object Windows.Forms.RowStyle('Absolute',12))) | Out-Null
$taskLayout.RowStyles.Add((New-Object Windows.Forms.RowStyle('Absolute',44))) | Out-Null
$taskInfo = New-Object Windows.Forms.Label
$taskInfo.Dock = 'Fill'; $taskInfo.Margin = New-Object Windows.Forms.Padding(0,0,0,12); $taskInfo.Text = "千问中文转写 · Qwen3-ASR-1.7B`r`n`r`n一次准备所需 CPU 环境和说话人组件，下载与安装占用数 GB。模型在本机运行，不上传会议音频。无需另装系统 Python。"
$taskMessage = New-Object Windows.Forms.Label
$taskMessage.Dock = 'Fill'; $taskMessage.Text = '未开始下载。速记可以直接使用；可在方便时回来继续。'
$taskProgress = New-Object Windows.Forms.ProgressBar
$taskProgress.Dock = 'Fill'; $taskProgress.Style = 'Marquee'; $taskProgress.Visible = $false
$taskButtons = New-Object Windows.Forms.FlowLayoutPanel
$taskButtons.Dock = 'Fill'; $taskButtons.FlowDirection = 'RightToLeft'
$taskDownload = New-Object Windows.Forms.Button
$taskDownload.Text = '开始下载'; $taskDownload.AutoSize = $true; $taskDownload.Name = 'download_recommended'
$taskClose = New-Object Windows.Forms.Button
$taskClose.Text = '关闭'; $taskClose.AutoSize = $true
$taskLogs = New-Object Windows.Forms.Button
$taskLogs.Text = '查看日志'; $taskLogs.AutoSize = $true
$taskButtons.Controls.AddRange(@($taskClose, $taskDownload, $taskLogs))
$taskLayout.Controls.Add($taskInfo,0,0); $taskLayout.Controls.Add($taskMessage,0,1); $taskLayout.Controls.Add($taskProgress,0,2); $taskLayout.Controls.Add($taskButtons,0,3)
$taskWindow.Controls.Add($taskLayout)
$taskState = @{ child=$null; began=$null }
$taskClose.Add_Click({ $taskWindow.Close() })
$taskLogs.Add_Click({ Start-Process -FilePath 'explorer.exe' -ArgumentList ('"' + $taskModelDirectory + '"') })
$taskDownload.Add_Click({
    try {
        Assert-ModelIdle $taskModelRoot
        $taskLockPath = Join-Path $taskModelRoot 'recommended-model.lock'; Assert-ModelPath $taskLockPath
        $taskProbe = [IO.File]::Open($taskLockPath, 'OpenOrCreate', 'ReadWrite', 'None'); $taskProbe.Dispose()
        $taskExe = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $taskState.began = [DateTimeOffset]::Now
        $taskState.child = Start-Process -FilePath $taskExe -ArgumentList ('-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $PSCommandPath + '" -Worker') -WindowStyle Hidden -PassThru
        $taskDownload.Enabled = $false; $taskClose.Text = '后台继续'; $taskProgress.Visible = $true
        $taskMessage.Text = '正在准备下载。关闭此窗口后任务继续，可从设置重新打开查看。'
    } catch { $taskMessage.Text = $_.Exception.Message }
})
$taskTimer = New-Object Windows.Forms.Timer
$taskTimer.Interval = 1000
$taskTimer.Add_Tick({
    try {
        if (Test-Path -LiteralPath $taskModelStatus -PathType Leaf) {
            $taskValue = [IO.File]::ReadAllText($taskModelStatus) | ConvertFrom-Json
            if ($taskState.began -and [DateTimeOffset]::Parse($taskValue.updated_at) -lt $taskState.began) { return }
            $taskMessage.Text = [string]$taskValue.message
            $taskActive = $taskValue.state -in @('preparing','installing')
            # The lock, not a stale JSON flag/PID, determines a live installation.
            $taskProbe = $null
            try { $taskProbe = [IO.File]::Open((Join-Path $taskModelRoot 'recommended-model.lock'), 'OpenOrCreate', 'ReadWrite', 'None'); $taskActive = $false }
            catch [IO.IOException] { $taskActive = $true }
            finally { if ($taskProbe) { $taskProbe.Dispose() } }
            $taskDownload.Enabled = -not $taskActive; $taskProgress.Visible = $taskActive
            $taskClose.Text = if ($taskActive) { '后台继续' } else { '关闭' }
            $taskDownload.Text = if ($taskValue.state -eq 'completed') { '检查并修复' } else { '重试下载' }
            if ($taskActive -and $taskValue.state -eq 'installing') {
                $taskStep = Join-Path $taskModelDirectory 'status.json'
                if (Test-Path -LiteralPath $taskStep) { $taskMessage.Text = ([IO.File]::ReadAllText($taskStep) | ConvertFrom-Json).message }
                $taskBytes = Join-Path $taskModelRoot 'models\download-status.json'
                if (Test-Path -LiteralPath $taskBytes) {
                    $taskData = [IO.File]::ReadAllText($taskBytes) | ConvertFrom-Json
                    if ($taskData.state -eq 'downloading' -and $taskData.total -gt 0) { $taskMessage.Text += (' · 当前文件 {0:N0}%' -f (100 * $taskData.received / $taskData.total)) }
                }
            }
            if (-not $taskActive -and $taskValue.state -in @('preparing','installing')) { $taskMessage.Text = '上次准备意外中断，可重试并复用已下载文件。' }
        }
        if ($taskState.child -and $taskState.child.HasExited -and $taskState.child.ExitCode -ne 0) { $taskDownload.Enabled = $true; $taskProgress.Visible = $false }
    } catch { } # Atomic status replacement may race one tick; retry next tick.
})
$taskWindow.Add_Shown({ $taskTimer.Start(); if ($Start) { $taskDownload.PerformClick() } })
try { [Windows.Forms.Application]::Run($taskWindow) } finally { $taskTimer.Dispose(); $taskWindow.Dispose() }
