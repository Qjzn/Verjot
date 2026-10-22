#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$NotesPath,
    [ValidateSet('local', 'api')][string]$Mode = 'local',
    [ValidateSet('cpu', 'cuda128')][string]$Backend = 'cpu',
    [string]$PythonExe,
    [switch]$UseObsidian,
    [switch]$StartWithWindows,
    [switch]$EnablePlaybackLinks,
    [switch]$SkipDeviceCheck,
    [switch]$RuntimeOnly,
    [switch]$Plan
)

$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$taskConfigPath = Join-Path $taskRoot 'config.json'
$taskVenv = Join-Path $taskRoot '.venv'
$taskPython = Join-Path $taskVenv 'Scripts\python.exe'
$taskInstallation = Join-Path $taskRoot 'installation'
$taskLibrary = Join-Path $taskRoot 'lib\NAudio.dll'
$taskLibraryHash = 'BC4BACC3B8B28D898F1671B79F216CCA439F95EB60CD32D3E3ECAFBECAC42780'
$taskUtf8 = New-Object System.Text.UTF8Encoding($false)
$taskLog = Join-Path $taskInstallation 'install.log'
$taskStatus = Join-Path $taskInstallation 'status.json'
$taskPhase = 'preflight'
$taskStatusStarted = $false
$taskModeExplicit = $PSBoundParameters.ContainsKey('Mode')
$taskRuntimeLock = $null

function Assert-OrdinaryDirectory([string]$Path) {
    $taskPathCursor = [IO.Path]::GetFullPath($Path)
    while ($taskPathCursor) {
        try { $taskPathItem = Get-Item -LiteralPath $taskPathCursor -Force -ErrorAction Stop }
        catch [System.Management.Automation.ItemNotFoundException] { $taskPathItem = $null }
        if ($taskPathItem) {
            if (($taskPathItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "安装与保存路径不能经过符号链接或 Windows 重解析目录：$taskPathCursor"
            }
            if (-not $taskPathItem.PSIsContainer) { throw "安装与保存路径必须是目录，现有路径是文件：$taskPathCursor" }
        }
        $taskPathParent = ([IO.DirectoryInfo]$taskPathCursor).Parent
        $taskPathCursor = if ($taskPathParent) { $taskPathParent.FullName } else { $null }
    }
}

function Get-RecorderProcesses {
    foreach ($taskProcess in @(Get-Process -Name 'ObsidianMeeting*' -ErrorAction SilentlyContinue)) {
        try { $taskProcessPath = $taskProcess.Path }
        catch { throw "无法确认录音进程 $($taskProcess.Id) 的路径；请正常退出 verbit 后再安装。" }
        if (-not $taskProcessPath) { throw "无法确认录音进程 $($taskProcess.Id) 的路径；请正常退出 verbit 后再安装。" }
        if ([IO.Path]::GetDirectoryName($taskProcessPath) -ieq $taskRoot) { $taskProcess }
    }
}

function Set-InstallStatus([string]$State, [string]$Message) {
    $taskValue = [ordered]@{
        state = $State; phase = $taskPhase; message = $Message; mode = $Mode; backend = $Backend
        pid = $PID; updated_at = [DateTimeOffset]::Now.ToString('o')
    } | ConvertTo-Json -Compress
    [IO.File]::WriteAllText($taskStatus, $taskValue, $taskUtf8)
    [IO.File]::AppendAllText($taskLog, ([DateTimeOffset]::Now.ToString('o') + ' ' + $Message + [Environment]::NewLine), $taskUtf8)
    Write-Host $Message
}

function Invoke-Checked([string]$Executable, [string[]]$Arguments) {
    $taskPreviousPreference = $ErrorActionPreference
    try {
        # Native stderr includes ordinary pip progress under Windows PowerShell 5.1.
        $ErrorActionPreference = 'Continue'
        & $Executable @Arguments 2>&1 | ForEach-Object {
            $taskLine = $_.ToString()
            [IO.File]::AppendAllText($taskLog, $taskLine + [Environment]::NewLine, $taskUtf8)
            Write-Host $taskLine
        }
        $taskExitCode = $LASTEXITCODE
    } finally { $ErrorActionPreference = $taskPreviousPreference }
    if ($taskExitCode -ne 0) { throw "$taskPhase 失败，退出码 $taskExitCode。日志：$taskLog" }
}

try {
    if ($RuntimeOnly -and ($Mode -ne 'local' -or $StartWithWindows -or $EnablePlaybackLinks)) { throw 'RuntimeOnly 仅准备推荐本地环境，不修改模式、自启或回放协议。' }
    $taskExistingConfig = Test-Path -LiteralPath $taskConfigPath -PathType Leaf
    if ($taskExistingConfig) {
        $taskConfig = Get-Content -LiteralPath $taskConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $taskConfiguredMode = [string]$taskConfig.transcription_mode
        if ([string]::IsNullOrWhiteSpace($taskConfiguredMode)) { $taskConfiguredMode = 'local' }
        if ($taskConfiguredMode -notin @('local', 'api')) { throw '已有 config.json 的 transcription_mode 必须是 local 或 api。' }
        if ($taskModeExplicit -and $Mode -ne $taskConfiguredMode -and -not $RuntimeOnly) { throw "已有配置使用 $taskConfiguredMode 模式；-Mode $Mode 不会覆盖它。请核对配置后重试。" }
        if (-not $RuntimeOnly) { $Mode = $taskConfiguredMode }
        $taskNotes = [string]$taskConfig.vault_root
        if ($NotesPath -and [IO.Path]::GetFullPath($NotesPath) -ine [IO.Path]::GetFullPath($taskNotes)) {
            throw '已有 config.json；-NotesPath 不会覆盖现有配置。请先核对配置，或省略该参数。'
        }
    } else {
        $taskNotes = $NotesPath
        if ([string]::IsNullOrWhiteSpace($taskNotes)) {
            if ($RuntimeOnly) { $taskNotes = $taskRoot }
            else { throw '首次安装必须提供 -NotesPath <完整 Windows 输出路径>。' }
        }
    }
    if ($taskNotes -notmatch '^(?:[A-Za-z]:[\\/]|\\\\[^\\]+\\[^\\]+(?:\\|$))') {
        throw 'NotesPath / config.json 的 vault_root 必须是完整 Windows 路径，例如 D:\Notes\会议。'
    }
    $taskNotes = [IO.Path]::GetFullPath($taskNotes)
    Assert-OrdinaryDirectory $taskNotes
    Assert-OrdinaryDirectory $taskRoot
    foreach ($taskRuntimeDirectory in @('.venv', 'lib', 'models', 'runtime-wheels', 'installation')) {
        Assert-OrdinaryDirectory (Join-Path $taskRoot $taskRuntimeDirectory)
    }
    $taskRunning = @(Get-RecorderProcesses)
    $taskTorchVersion = if ($Backend -eq 'cpu') { '2.11.0+cpu' } else { '2.11.0+cu128' }
    $taskRequirements = if ($Mode -eq 'api') { 'requirements-api.txt' } else { 'requirements.txt' }
    if ($EnablePlaybackLinks -and -not (Test-Path -LiteralPath (Join-Path $taskRoot 'Register-Playback.ps1') -PathType Leaf)) {
        throw '安装源码缺失：Register-Playback.ps1'
    }

    if ($Plan) {
        [ordered]@{
            action = 'plan'; mode = $Mode; root = $taskRoot; notes_path = $taskNotes; backend = $Backend
            python = $(if ($PythonExe) { $PythonExe } else { 'py -3.12 (已安装的 Python 3.12 x64)' })
            config = $(if ($RuntimeOnly) { '不创建或修改 config.json，不构建 EXE，不修改快捷方式' } elseif ($taskExistingConfig) { '保留现有 config.json' } else { '从 config.example.json 创建，写入 vault_root / open_in_obsidian / transcription_mode' })
            open_in_obsidian_on_first_install = [bool]$UseObsidian
            start_with_windows = [bool]$StartWithWindows
            enable_playback_links = [bool]$EnablePlaybackLinks
            skip_device_check = [bool]$SkipDeviceCheck
            running_recorder_pids = @($taskRunning | ForEach-Object { $_.Id })
            steps = @(
                '检查 Windows x64、Python 3.12 x64；不自动安装系统 Python',
                '创建或复用 .venv；恢复官方 NAudio 1.10.0 的 lib/net35/NAudio.dll 并校验固定 SHA256',
                $(if ($Mode -eq 'local') { "检查/安装官方 torch==$taskTorchVersion；后端不匹配则失败，不改装已有 torch" } else { 'API 模式跳过 torch / transformers；密钥只读取配置指定的环境变量' }),
                "安装 $taskRequirements，pip check",
                $(if ($Mode -eq 'local') { '断点下载中文识别和说话人模型' } else { 'download_models.py --speaker-only，仅下载本地说话人模型（约 50 MB）' }),
                $(if ($RuntimeOnly) { '不构建 EXE，不创建或修改配置，不修改自启和快捷方式' } else { '构建 ObsidianMeeting.exe；已有配置原样保留；首次输出目录可新建' }),
                $(if ($Mode -eq 'api') { '首次 API 配置需填写 api_base_url / api_model，并设置 TINGJI_API_KEY；未填写时 doctor 会失败，补齐后重跑' } else { '本地转写使用已下载模型，不上传音频' }),
                $(if ($RuntimeOnly) { '验证本地依赖可导入；不录音，不调用 API，不宣称设备和识别质量通过' } else { '运行 tingji.py doctor --full --json；不启动应用、不录音' }),
                $(if ($StartWithWindows) { '健康后运行 Install-Shortcuts.ps1，建立开始菜单与 Startup 快捷方式' } else { '不修改 Startup' }),
                $(if ($EnablePlaybackLinks) { '健康后为当前用户注册 HKCU Software\Classes\pianyan 时间点回听协议；不覆盖其他安装' } else { '不注册时间点回听协议' })
            )
            runtime_evidence = $taskInstallation
            network_sources = $(if ($Mode -eq 'local') { @('api.nuget.org', 'download.pytorch.org', 'pypi.org', 'huggingface.co', 'github.com/k2-fsa/sherpa-onnx') } else { @('api.nuget.org', 'pypi.org', 'github.com/k2-fsa/sherpa-onnx') })
        } | ConvertTo-Json -Depth 5
        exit 0
    }

    if ($RuntimeOnly) {
        . (Join-Path $taskRoot 'RecommendedModel.ps1') -FunctionsOnly
        $taskRuntimeLockPath = Join-Path $taskRoot 'runtime-install.lock'
        Assert-ModelPath $taskRuntimeLockPath
        $taskRuntimeLock = [IO.File]::Open($taskRuntimeLockPath, 'OpenOrCreate', 'ReadWrite', 'None')
        Assert-ModelIdle $taskRoot
    }
    $OutputEncoding = $taskUtf8
    [Console]::OutputEncoding = $taskUtf8
    $env:PYTHONIOENCODING = 'utf-8'
    if ($env:OS -ne 'Windows_NT' -or -not [Environment]::Is64BitOperatingSystem) { throw '安装仅支持 64 位 Windows。' }
    if ($taskRunning.Count -and -not $RuntimeOnly) { throw '本目录 verbit 正在运行。请从托盘正常退出后再安装；不要中断录音。' }
    foreach ($taskRequired in @('Build.ps1', $taskRequirements, 'download_models.py', 'config.example.json', 'tingji.py', 'config.schema.json', 'Install-Shortcuts.ps1')) {
        if (-not (Test-Path -LiteralPath (Join-Path $taskRoot $taskRequired) -PathType Leaf)) { throw "安装源码缺失：$taskRequired" }
    }
    if ($Mode -eq 'local' -and $Backend -eq 'cuda128' -and -not (Test-Path -LiteralPath (Join-Path $taskRoot 'download_torch.py') -PathType Leaf)) { throw '安装源码缺失：download_torch.py' }
    $taskCompiler = @(
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (-not $taskCompiler) { throw '.NET Framework 4.x 编译器缺失；请先安装 Windows 的 .NET Framework 4.x。' }

    $taskPythonProbe = @'
import json,struct,sys
print(json.dumps({'executable':sys.executable,'version':list(sys.version_info[:2]),'bits':struct.calcsize('P')*8,'platform':sys.platform}))
'@
    if ($PythonExe) {
        $taskBasePython = [IO.Path]::GetFullPath($PythonExe)
        if (-not (Test-Path -LiteralPath $taskBasePython -PathType Leaf)) { throw "PythonExe 不存在：$taskBasePython" }
    } else {
        $taskLauncher = Get-Command 'py.exe' -ErrorAction SilentlyContinue
        if (-not $taskLauncher) { throw '未找到 Python launcher。请安装 Python 3.12 x64，或传入 -PythonExe <python.exe 完整路径>。' }
        $taskProbe = & $taskLauncher.Source -3.12 -B -c $taskPythonProbe
        if ($LASTEXITCODE -ne 0) { throw 'py -3.12 探测失败；请安装 Python 3.12 x64，或传入 -PythonExe。' }
        $taskBasePython = ($taskProbe | ConvertFrom-Json).executable
    }
    $taskProbe = & $taskBasePython -B -c $taskPythonProbe
    if ($LASTEXITCODE -ne 0) { throw '无法运行所选 Python。' }
    $taskPythonInfo = $taskProbe | ConvertFrom-Json
    if ($taskPythonInfo.version[0] -ne 3 -or $taskPythonInfo.version[1] -ne 12 -or $taskPythonInfo.bits -ne 64 -or $taskPythonInfo.platform -ne 'win32') {
        throw '需要 Windows Python 3.12 x64；不会自动切换或安装系统 Python。'
    }
    if ((Test-Path -LiteralPath $taskVenv) -and -not (Test-Path -LiteralPath $taskPython -PathType Leaf)) {
        throw '已有 .venv 但缺少 Scripts\python.exe；请核对未完成环境后再安装，脚本不会删除它。'
    }
    $taskTorchInstalled = $false
    if (Test-Path -LiteralPath $taskPython -PathType Leaf) {
        $taskVenvProbe = & $taskPython -B -c $taskPythonProbe
        if ($LASTEXITCODE -ne 0) { throw '已有 .venv 的 Python 无法运行。' }
        $taskVenvInfo = $taskVenvProbe | ConvertFrom-Json
        if ($taskVenvInfo.version[0] -ne 3 -or $taskVenvInfo.version[1] -ne 12 -or $taskVenvInfo.bits -ne 64) { throw '已有 .venv 不是 Python 3.12 x64；不会覆盖它。' }
        if ($Mode -eq 'local') {
            $taskTorchProbe = @'
import importlib.util,json
value={'installed':False}
if importlib.util.find_spec('torch'):
    import torch
    value={'installed':True,'version':torch.__version__,'cuda':torch.version.cuda}
print(json.dumps(value))
'@
            $taskTorchOutput = & $taskPython -B -c $taskTorchProbe
            if ($LASTEXITCODE -ne 0) { throw '已有 torch 无法导入；请核对环境，脚本不会覆盖安装。' }
            $taskTorchInfo = $taskTorchOutput | ConvertFrom-Json
            $taskTorchInstalled = [bool]$taskTorchInfo.installed
            if ($taskTorchInstalled) {
                $taskCorrectVersion = ($taskTorchInfo.version -split '\+')[0] -eq '2.11.0'
                $taskCorrectBackend = if ($Backend -eq 'cpu') { -not $taskTorchInfo.cuda } else { $taskTorchInfo.cuda -eq '12.8' -and $taskTorchInfo.version -like '*+cu128' }
                if (-not $taskCorrectVersion -or -not $taskCorrectBackend) {
                    throw "已有 torch=$($taskTorchInfo.version), CUDA=$($taskTorchInfo.cuda)，与 -Backend $Backend / torch 2.11.0 不匹配。请选匹配后端；脚本不会默默重装已有 torch。"
                }
            }
        }
    }

    [IO.Directory]::CreateDirectory($taskInstallation) | Out-Null
    $taskStatusStarted = $true
    $taskPhase = 'venv'
    Set-InstallStatus 'installing' '检查并准备本目录 Python 虚拟环境'
    if (-not (Test-Path -LiteralPath $taskPython -PathType Leaf)) { Invoke-Checked $taskBasePython @('-B', '-m', 'venv', $taskVenv) }
    # venv may have been interrupted after creating python.exe but before pip.
    # Bootstrap only an absent module from Python's bundled wheels; do not
    # overwrite an installed pip that fails to import or execute.
    & $taskPython -B -c "import importlib.util; raise SystemExit(0 if importlib.util.find_spec('pip') else 3)"
    $taskPipProbeExit = $LASTEXITCODE
    if ($taskPipProbeExit -eq 3) {
        Set-InstallStatus 'installing' '补齐中断安装缺失的 pip（使用 Python 自带离线包）'
        Invoke-Checked $taskPython @('-B', '-m', 'ensurepip', '--upgrade')
    } elseif ($taskPipProbeExit -ne 0) {
        throw '无法检查虚拟环境中的 pip；请检查 installation/install.log，不会覆盖已有环境。'
    }
    Invoke-Checked $taskPython @('-B', '-m', 'pip', '--version')

    $taskPhase = 'naudio'
    Set-InstallStatus 'installing' '恢复并验证 NAudio 1.10.0'
    if (-not (Test-Path -LiteralPath $taskLibrary -PathType Leaf) -or (Get-FileHash -LiteralPath $taskLibrary -Algorithm SHA256).Hash -ne $taskLibraryHash) {
        $taskPackage = Join-Path $taskInstallation 'naudio.1.10.0.nupkg'
        $taskPackagePartial = $taskPackage + '.partial'
        $taskPreviousProtocol = [Net.ServicePointManager]::SecurityProtocol
        try {
            [Net.ServicePointManager]::SecurityProtocol = $taskPreviousProtocol -bor [Net.SecurityProtocolType]::Tls12
            Invoke-WebRequest -UseBasicParsing -Uri 'https://api.nuget.org/v3-flatcontainer/naudio/1.10.0/naudio.1.10.0.nupkg' -OutFile $taskPackagePartial
        } finally { [Net.ServicePointManager]::SecurityProtocol = $taskPreviousProtocol }
        Move-Item -LiteralPath $taskPackagePartial -Destination $taskPackage -Force
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $taskArchive = [IO.Compression.ZipFile]::OpenRead($taskPackage)
        $taskCandidate = Join-Path $taskInstallation 'NAudio.dll'
        try {
            $taskEntry = $taskArchive.GetEntry('lib/net35/NAudio.dll')
            if (-not $taskEntry) { throw '官方 NAudio 包缺少 lib/net35/NAudio.dll。' }
            $taskSource = $taskEntry.Open()
            $taskDestination = [IO.File]::Create($taskCandidate)
            try { $taskSource.CopyTo($taskDestination) } finally { $taskDestination.Dispose(); $taskSource.Dispose() }
        } finally { $taskArchive.Dispose() }
        if ((Get-FileHash -LiteralPath $taskCandidate -Algorithm SHA256).Hash -ne $taskLibraryHash) { throw 'NAudio.dll 固定 SHA256 校验失败；未写入运行库。' }
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($taskLibrary)) | Out-Null
        Copy-Item -LiteralPath $taskCandidate -Destination $taskLibrary -Force
    }

    if ($Mode -eq 'local') {
        $taskPhase = 'native-runtime'
        Set-InstallStatus 'installing' '检查本地识别需要的 Microsoft VC++ x64 运行库'
        $taskRuntimePreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            $taskRuntimeProbe = & $taskPython -B -c "import ctypes; [ctypes.WinDLL(n) for n in ('vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll')]" 2>&1
            $taskRuntimeExit = $LASTEXITCODE
        } finally { $ErrorActionPreference = $taskRuntimePreference }
        if ($taskRuntimeExit -ne 0) {
            throw 'Microsoft Visual C++ v14 Redistributable x64 缺失或无法加载。请从 https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist 安装官方 x64 包；若提示重启请先重启，然后重跑本命令。无需 Visual Studio，不从 DLL 下载站复制文件。'
        }
        $taskPhase = 'torch'
        Set-InstallStatus 'installing' "检查或安装官方 torch==$taskTorchVersion"
        if (-not $taskTorchInstalled) {
            if ($Backend -eq 'cuda128') {
                $taskWheelDirectory = Join-Path $taskRoot 'runtime-wheels'
                $taskDownloadStatus = Join-Path $taskInstallation 'torch-download.json'
                Invoke-Checked $taskPython @('-B', (Join-Path $taskRoot 'download_torch.py'), '--version', '2.11.0', '--output-dir', $taskWheelDirectory, '--status', $taskDownloadStatus)
                $taskDownload = Get-Content -LiteralPath $taskDownloadStatus -Raw -Encoding UTF8 | ConvertFrom-Json
                $taskWheel = [IO.Path]::GetFullPath([string]$taskDownload.file_path)
                if ($taskDownload.state -ne 'completed' -or [IO.Path]::GetDirectoryName($taskWheel) -ine $taskWheelDirectory -or [IO.Path]::GetFileName($taskWheel) -ne 'torch-2.11.0+cu128-cp312-cp312-win_amd64.whl') { throw 'CUDA wheel 下载结果不符合指定版本和本目录。' }
                if ((Get-FileHash -LiteralPath $taskWheel -Algorithm SHA256).Hash.ToLowerInvariant() -ne $taskDownload.sha256) { throw 'CUDA wheel SHA256 复核失败。' }
                Invoke-Checked $taskPython @('-B', '-m', 'pip', 'install', $taskWheel, '--index-url', 'https://pypi.org/simple')
            } else {
                Invoke-Checked $taskPython @('-B', '-m', 'pip', 'install', 'torch==2.11.0+cpu', '--index-url', 'https://download.pytorch.org/whl/cpu')
            }
        }
    }
    $taskPhase = 'dependencies'
    Set-InstallStatus 'installing' '安装固定识别依赖并检查依赖一致性'
    Invoke-Checked $taskPython @('-B', '-m', 'pip', 'install', '-r', (Join-Path $taskRoot $taskRequirements), '--index-url', 'https://pypi.org/simple')
    Invoke-Checked $taskPython @('-B', '-m', 'pip', 'check')
    $taskPhase = 'models'
    Set-InstallStatus 'installing' '下载或复用所选模式的官方本地模型（中断可重试）'
    $taskModelArguments = @('-B', (Join-Path $taskRoot 'download_models.py'))
    if ($Mode -eq 'api') { $taskModelArguments += '--speaker-only' }
    Invoke-Checked $taskPython $taskModelArguments

    if ($RuntimeOnly) {
        # The desktop download entry prepares only this installation's engine.
        # Never rebuild a running EXE, create first-run config or change shortcuts.
        $taskPhase = 'runtime-check'
        Set-InstallStatus 'installing' '检查本地转写依赖；不录音、不修改配置'
        Invoke-Checked $taskPython @('-B', '-c', "import torch,transformers,accelerate,sherpa_onnx,numpy,scipy,soundfile,imageio_ffmpeg; print('Local runtime imports OK; device and transcription quality not tested')")
        $taskPhase = 'completed'
        Set-InstallStatus 'completed' '推荐模型及依赖已准备；尚未进行真实录音和转写验收。'
        exit 0
    }
    $taskPhase = 'build'
    Set-InstallStatus 'installing' '构建 verbit 桌面程序'
    if (@(Get-RecorderProcesses).Count) { throw 'verbit 在安装过程中启动了。请从托盘正常退出后重试；未覆盖运行中的程序。' }
    & (Join-Path $taskRoot 'Build.ps1')
    $taskPhase = 'config'
    if (-not $taskExistingConfig) {
        if (Test-Path -LiteralPath $taskConfigPath) { throw '安装过程中出现了 config.json；为保留配置已停止，请重试。' }
        [IO.Directory]::CreateDirectory($taskNotes) | Out-Null
        $taskConfig = Get-Content -LiteralPath (Join-Path $taskRoot 'config.example.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $taskConfig | Add-Member -NotePropertyName 'vault_root' -NotePropertyValue $taskNotes -Force
        $taskConfig | Add-Member -NotePropertyName 'open_in_obsidian' -NotePropertyValue ([bool]$UseObsidian) -Force
        $taskConfig | Add-Member -NotePropertyName 'transcription_mode' -NotePropertyValue $Mode -Force
        $taskConfigStream = [IO.File]::Open($taskConfigPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $taskConfigBytes = $taskUtf8.GetBytes(($taskConfig | ConvertTo-Json -Depth 8))
            $taskConfigStream.Write($taskConfigBytes, 0, $taskConfigBytes.Length)
        } finally { $taskConfigStream.Dispose() }
        Set-InstallStatus 'installing' '已创建首次配置；后续安装保留它'
    } else { Set-InstallStatus 'installing' '保留现有 config.json 原始内容；-UseObsidian 不改变已有配置' }

    $taskPhase = 'doctor'
    Set-InstallStatus 'installing' '执行完整环境检查（不录音、不启动主界面）'
    $taskDoctorArguments = @('-B', (Join-Path $taskRoot 'tingji.py'), 'doctor', '--full', '--json')
    if ($SkipDeviceCheck) { $taskDoctorArguments += '--skip-devices' }
    $taskDoctorOutput = & $taskPython @taskDoctorArguments
    $taskDoctorExit = $LASTEXITCODE
    $taskDoctorText = $taskDoctorOutput -join [Environment]::NewLine
    [IO.File]::WriteAllText((Join-Path $taskInstallation 'doctor.json'), $taskDoctorText, $taskUtf8)
    Write-Output $taskDoctorText
    if ($taskDoctorExit -ne 0) {
        $taskNextAction = if ($Mode -eq 'api') { '请由 AI 核对 config.json 的 api_base_url / api_model，并在当前用户环境设置配置指定的密钥变量（默认 TINGJI_API_KEY），再重跑。脚本不收集密钥，也未上传音频验证服务。' } else { '请根据 doctor.json 修复失败检查后重跑。' }
        throw "完整环境检查失败，退出码 $taskDoctorExit。证据：$(Join-Path $taskInstallation 'doctor.json') $taskNextAction"
    }
    $null = $taskDoctorText | ConvertFrom-Json
    if ($StartWithWindows) {
        $taskPhase = 'shortcuts'
        & (Join-Path $taskRoot 'Install-Shortcuts.ps1') -StartWithWindows
    }
    if ($EnablePlaybackLinks) {
        $taskPhase = 'playback-links'
        & (Join-Path $taskRoot 'Register-Playback.ps1')
        if ($LASTEXITCODE -ne 0) { throw "时间点回放协议注册失败：$LASTEXITCODE" }
    }
    $taskPhase = 'completed'
    Set-InstallStatus 'completed' '安装与环境检查完成；应用尚未启动。请人工完成首次麦克风和电脑声音录音验收。'
    exit 0
} catch {
    if ($taskStatusStarted) { Set-InstallStatus 'error' $_.Exception.Message }
    Write-Error $_
    exit 1
} finally { if ($taskRuntimeLock) { $taskRuntimeLock.Dispose() } }
