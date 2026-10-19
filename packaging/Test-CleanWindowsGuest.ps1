#requires -Version 5.1
[CmdletBinding()]
param([ValidateSet('Sandbox','VM')][string]$Environment = 'Sandbox')
$ErrorActionPreference = 'Stop'
function Assert-DisposableGuest([string]$Kind,[string]$UserName,[string]$Model,[bool]$HasManifest) {
    if (-not $HasManifest) { throw 'Missing C:\verbit-input\SHA256.json; prepare explicit validation inputs first.' }
    if ($Kind -eq 'Sandbox') {
        if ($UserName -ne 'WDAGUtilityAccount') { throw 'Run in Windows Sandbox, or explicitly select -Environment VM in a disposable virtual machine.' }
    } elseif ($Model -notmatch '^(Virtual Machine|VMware Virtual Platform|VirtualBox|KVM|HVM domU|QEMU.*|Parallels.*)$') {
        throw 'VM validation refused: machine model is not a supported virtual machine. Never run this installer on the host.'
    }
}
# VM fallback is explicit and checks the machine model, never merely whether
# Hyper-V is enabled (a physical host can also have a hypervisor).
$taskGuestModel = if ($Environment -eq 'VM') { [string](Get-CimInstance Win32_ComputerSystem -ErrorAction Stop).Model } else { '' }
Assert-DisposableGuest $Environment $env:USERNAME $taskGuestModel (Test-Path -LiteralPath 'C:\verbit-input\SHA256.json')
$taskInput = 'C:\verbit-input'
$taskReports = 'C:\verbit-reports'
$taskHome = 'C:\verbit-validation'
if (Test-Path -LiteralPath $taskHome) { throw 'Guest directory already exists; start a new Sandbox for a clean baseline.' }
[IO.Directory]::CreateDirectory($taskHome) | Out-Null
$taskResult = [ordered]@{ passed=$false; stage='baseline'; environment=$Environment; machine_model=$taskGuestModel; clean_windows_guest=$true; clean_baseline_verified=$false; audio_recorded=$false; cloud_api_called=$false; clipboard_access=$false }
function Invoke-GuestChecked([string]$File, [string[]]$Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Guest stage $($taskResult.stage) failed: $LASTEXITCODE" }
}
function Get-SignedInstaller([string]$Uri,[string]$Name,[string]$Publisher) {
    $taskDownload = Join-Path $taskHome $Name
    Invoke-WebRequest -UseBasicParsing -Uri $Uri -OutFile $taskDownload
    $taskSignature = Get-AuthenticodeSignature -LiteralPath $taskDownload
    if ($taskSignature.Status -ne 'Valid' -or $taskSignature.SignerCertificate.Subject -notmatch $Publisher) { throw "Invalid installer signature: $Name" }
    $taskResult[$Name] = @{ sha256=(Get-FileHash -LiteralPath $taskDownload).Hash; publisher=$taskSignature.SignerCertificate.Subject; uri=$Uri }
    return $taskDownload
}
Start-Transcript -LiteralPath (Join-Path $taskReports 'guest.log') | Out-Null
try {
    $taskHashes = Get-Content -LiteralPath (Join-Path $taskInput 'SHA256.json') -Raw | ConvertFrom-Json
    foreach ($taskEntry in $taskHashes.PSObject.Properties) {
        if ($taskEntry.Name -ne [IO.Path]::GetFileName($taskEntry.Name) -or
            (Get-FileHash -LiteralPath (Join-Path $taskInput $taskEntry.Name)).Hash -ne $taskEntry.Value) { throw 'Test input hash mismatch.' }
    }
    $taskPython = @(Get-Command py.exe,python.exe -ErrorAction SilentlyContinue | Where-Object { $_.Source -notlike '*\WindowsApps\*' })
    $taskDevTools = @(Get-Command git.exe,cl.exe,node.exe,dotnet.exe -ErrorAction SilentlyContinue)
    $taskResult.baseline = @{ os=[Environment]::OSVersion.VersionString; python_commands=@($taskPython | ForEach-Object Source); development_commands=@($taskDevTools | ForEach-Object Source); models_present=$false }
    if ($taskPython.Count -or $taskDevTools.Count) { throw 'Guest is not clean: existing Python or development commands detected.' }
    $taskResult.clean_baseline_verified = $true
    $taskApp = Join-Path $taskHome 'app'
    $taskNotes = Join-Path $taskHome 'notes'
    $taskResult.stage = 'desktop-without-python'
    $taskInstall = Start-Process -FilePath (Join-Path $taskInput 'setup.exe') -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/NOICONS','/TASKS=""',('/DIR="' + $taskApp + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($taskInstall.ExitCode -ne 0) { throw "EXE install failed: $($taskInstall.ExitCode)" }
    Invoke-GuestChecked (Join-Path $taskInput 'TestDesktopRunner.exe') @((Join-Path $taskInput 'TestInstalledJourney.exe'),$taskApp,$taskNotes)
    Copy-Item -LiteralPath (Join-Path $taskApp 'new-user-journey.json') -Destination $taskReports
    $taskResult.stage = 'python-and-native-runtime'
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $taskPythonInstaller = Get-SignedInstaller 'https://www.python.org/ftp/python/3.12.10/python-3.12.10-amd64.exe' 'python-installer.exe' 'Python Software Foundation'
    $taskPythonFolder = Join-Path $taskHome 'Python312'
    $taskPythonInstall = Start-Process -FilePath $taskPythonInstaller -ArgumentList @('/quiet','/norestart','InstallAllUsers=0','Include_launcher=0','PrependPath=0','Include_test=0',('TargetDir="' + $taskPythonFolder + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($taskPythonInstall.ExitCode -ne 0) { throw "Python install failed: $($taskPythonInstall.ExitCode)" }
    $taskVcInstaller = Get-SignedInstaller 'https://aka.ms/vc14/vc_redist.x64.exe' 'vc-redist.exe' 'Microsoft Corporation'
    $taskVcInstall = Start-Process -FilePath $taskVcInstaller -ArgumentList @('/install','/quiet','/norestart') -WindowStyle Hidden -Wait -PassThru
    if ($taskVcInstall.ExitCode -eq 3010) { throw 'VC++ requires a guest restart. This run is incomplete; restart guest and follow the manual clean-VM checklist.' }
    if ($taskVcInstall.ExitCode -notin @(0,1638)) { throw "VC++ install failed: $($taskVcInstall.ExitCode)" }
    $taskResult.stage = 'fresh-local-runtime-and-model-download'
    $env:PIP_NO_CACHE_DIR = '1'
    $env:HF_HOME = Join-Path $taskHome 'hf-cache'
    $env:PYTHONIOENCODING = 'utf-8'
    Invoke-GuestChecked 'powershell.exe' @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $taskApp 'Install.ps1'),'-PythonExe',(Join-Path $taskPythonFolder 'python.exe'),'-Mode','local','-Backend','cpu','-SkipDeviceCheck')
    Copy-Item -LiteralPath (Join-Path $taskApp 'installation\doctor.json') -Destination $taskReports
    foreach ($taskSample in @('known-a.wav','known-b.wav')) { Copy-Item -LiteralPath (Join-Path $taskInput $taskSample) -Destination $taskApp }
    $taskResult.stage = 'actual-offline-chinese-transcription'
    $taskVenvPython = Join-Path $taskApp '.venv\Scripts\python.exe'
    Invoke-GuestChecked $taskVenvPython @('-B',(Join-Path $taskInput 'PrepareOfflineInstall.py'),$taskApp)
    $taskSession = Get-Content -LiteralPath (Join-Path $taskApp 'validation-session.txt') -Raw -Encoding UTF8
    $taskClock = [Diagnostics.Stopwatch]::StartNew()
    Invoke-GuestChecked $taskVenvPython @('-B',(Join-Path $taskApp 'meeting_worker.py'),'--session',$taskSession)
    Invoke-GuestChecked $taskVenvPython @('-B',(Join-Path $taskInput 'VerifyOfflineInstall.py'),$taskApp)
    Copy-Item -LiteralPath (Join-Path $taskApp 'new-user-transcription-report.json') -Destination $taskReports
    $taskResult.elapsed_transcription_seconds = $taskClock.Elapsed.TotalSeconds
    $taskResult.passed = $true
    $taskResult.stage = 'completed'
    $taskResult.scope = 'Clean Sandbox desktop and synthetic local transcription; no live devices, listening or real IME claim.'
} catch {
    $taskResult.error = $_.Exception.Message
    Write-Host $_.Exception.Message
} finally {
    $taskResult | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskReports 'result.json') -Encoding UTF8
    Stop-Transcript | Out-Null
}
Write-Host 'Reports are saved to C:\verbit-reports. Do not close Sandbox until the report has been inspected.'
if (-not $taskResult.passed) { exit 2 }
exit 0
