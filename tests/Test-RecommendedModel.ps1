#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$SourceRoot, [Parameter(Mandatory=$true)][string]$OutputRoot, [string]$PythonPackage)
$ErrorActionPreference = 'Stop'
$taskFixture = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) ('recommended-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($taskFixture) | Out-Null
$taskHelper = Join-Path $taskFixture 'RecommendedModel.ps1'
Copy-Item -LiteralPath (Join-Path $SourceRoot 'RecommendedModel.ps1') -Destination $taskHelper
$taskUtf8Test = New-Object Text.UTF8Encoding($false)
$taskAssertions = 0
function Check([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message }; $script:taskAssertions++ }
function Reject([scriptblock]$Action, [string]$Message) { $taskRejected=$false; try { & $Action } catch { $taskRejected=$true }; Check $taskRejected $Message }
. $taskHelper -FunctionsOnly
[IO.Directory]::CreateDirectory((Join-Path $taskFixture 'installation')) | Out-Null
$taskBeforePath = $env:PATH
$taskPlan = Get-RecommendedPlan
Check ($taskPlan.model -eq 'Qwen3-ASR-1.7B-hf' -and -not $taskPlan.audio_uploaded -and -not $taskPlan.changes_existing_config) 'One recommended model, local audio and no config mutation'
Assert-ModelIdle $taskFixture
$taskMeeting = Join-Path $taskFixture 'work\synthetic'
[IO.Directory]::CreateDirectory($taskMeeting) | Out-Null
$taskSessionFile = Join-Path $taskMeeting 'session.json'
[IO.File]::WriteAllText($taskSessionFile, '{"state":"recording"}', $taskUtf8Test)
[IO.File]::WriteAllText((Join-Path $taskMeeting 'status.json'), '{"state":"recording"}', $taskUtf8Test)
Reject { Assert-ModelIdle $taskFixture } 'Active recording blocks runtime mutation'
[IO.File]::WriteAllText($taskSessionFile, '{"state":"recorded"}', $taskUtf8Test)
[IO.File]::WriteAllText((Join-Path $taskMeeting 'status.json'), '{"state":"transcribing"}', $taskUtf8Test)
Reject { Assert-ModelIdle $taskFixture } 'Active transcription blocks runtime mutation'
[IO.File]::WriteAllText((Join-Path $taskMeeting 'status.json'), '{"state":"completed"}', $taskUtf8Test)
Assert-ModelIdle $taskFixture
[IO.File]::WriteAllText((Join-Path $taskMeeting 'status.json'), '{"state":"audio_ready"}', $taskUtf8Test)
Assert-ModelIdle $taskFixture
Check $true 'Saved MP3 awaiting configuration does not block model preparation'
[IO.File]::WriteAllText((Join-Path $taskMeeting 'status.json'), '{"state":"completed"}', $taskUtf8Test)
[IO.File]::WriteAllText($taskSessionFile, '{broken', $taskUtf8Test)
Reject { Assert-ModelIdle $taskFixture } 'Malformed state is not treated as idle'
[IO.File]::WriteAllText($taskSessionFile, '{"state":"recorded"}', $taskUtf8Test)
$taskLock = [IO.File]::Open((Join-Path $taskFixture 'processing.lock'), 'OpenOrCreate', 'ReadWrite', 'ReadWrite')
try { $taskLock.Lock(0,1); Reject { Assert-ModelIdle $taskFixture } 'A live byte-zero worker lock blocks setup' } finally { $taskLock.Unlock(0,1); $taskLock.Dispose() }
$taskIncomplete = Join-Path $taskFixture '.venv'
[IO.Directory]::CreateDirectory($taskIncomplete) | Out-Null
Reject { Get-RecommendedPython $taskFixture } 'Incomplete environment preserved without downloading another Python'
# Remove only the verified empty fixture folder created immediately above.
[IO.Directory]::Delete($taskIncomplete)

if ($PythonPackage) {
    Copy-Item -LiteralPath $PythonPackage -Destination (Join-Path $taskFixture 'installation\python.3.12.10.nupkg')
    $taskPython = Get-RecommendedPython $taskFixture
    $taskPythonInfo = & $taskPython -I -B -c "import json,sys,struct,venv,ensurepip; print(json.dumps({'version':sys.version_info[:3],'bits':struct.calcsize('P')*8}))"
    Check ($LASTEXITCODE -eq 0 -and ($taskPythonInfo | ConvertFrom-Json).bits -eq 64) 'Verified private CPython starts without system Python'
    $taskPrivateEnv = Join-Path $taskFixture 'private-venv'
    $taskOldTemp = $env:TEMP; $taskOldTmp = $env:TMP
    $taskPrivateTemp = Join-Path $taskFixture 'temp'; [IO.Directory]::CreateDirectory($taskPrivateTemp) | Out-Null
    try { $env:TEMP=$env:TMP=$taskPrivateTemp; & $taskPython -I -B -m venv $taskPrivateEnv }
    finally { $env:TEMP=$taskOldTemp; $env:TMP=$taskOldTmp }
    Check ($LASTEXITCODE -eq 0) 'Real private CPython creates venv with ensurepip'
    & (Join-Path $taskPrivateEnv 'Scripts\python.exe') -I -B -m pip --version
    Check ($LASTEXITCODE -eq 0) 'Private venv pip works without development tools'
    Check ((Get-RecommendedPython $taskFixture) -eq $taskPython) 'Completed bootstrap is reused'
} else { Write-Output 'SKIP: actual CPython bootstrap; provide -PythonPackage with the pinned official NuGet package.' }

# Run the actual orchestration against an owned fake installer and tiny probe.
# This verifies child exit/status/arguments without installing models or pip.
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$taskProbeSource = Join-Path $taskFixture 'Probe.cs'
$taskProbe = Join-Path $taskFixture '.venv\Scripts\python.exe'
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($taskProbe)) | Out-Null
[IO.File]::WriteAllText($taskProbeSource, 'class Probe { static int Main() { System.Console.WriteLine("cpu"); return 0; } }', $taskUtf8Test)
& $taskCompiler /nologo /target:exe ('/out:' + $taskProbe) $taskProbeSource
Check ($LASTEXITCODE -eq 0) 'Owned fake probe builds'
$taskFakeInstaller = Join-Path $taskFixture 'Install.ps1'
[IO.File]::WriteAllText($taskFakeInstaller, 'param([switch]$RuntimeOnly,[string]$Backend,[string]$PythonExe); if (-not $RuntimeOnly -or $Backend -ne "cpu") { exit 8 }; exit 0', $taskUtf8Test)
$taskConfig = Join-Path $taskFixture 'config.json'
[IO.File]::WriteAllText($taskConfig, '{"synthetic":"must remain exact"}', $taskUtf8Test)
$taskConfigHash = (Get-FileHash -LiteralPath $taskConfig).Hash
$taskPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
& $taskPowerShell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $taskHelper -Worker
Check ($LASTEXITCODE -eq 0 -and (Get-Content -LiteralPath (Join-Path $taskFixture 'installation\recommended-model.json') -Raw -Encoding UTF8 | ConvertFrom-Json).state -eq 'completed') 'Actual helper worker records completed after child success'
Check ((Get-FileHash -LiteralPath $taskConfig).Hash -eq $taskConfigHash) 'Existing config is byte-for-byte preserved'
[IO.File]::WriteAllText($taskFakeInstaller, 'param([switch]$RuntimeOnly,[string]$Backend,[string]$PythonExe); exit 9', $taskUtf8Test)
& $taskPowerShell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $taskHelper -Worker
Check ($LASTEXITCODE -ne 0 -and (Get-Content -LiteralPath (Join-Path $taskFixture 'installation\recommended-model.json') -Raw -Encoding UTF8 | ConvertFrom-Json).state -eq 'error') 'Failed child is not reported as success'
[IO.File]::WriteAllText($taskFakeInstaller, 'param([switch]$RuntimeOnly,[string]$Backend,[string]$PythonExe); exit 0', $taskUtf8Test)
& $taskPowerShell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $taskHelper -Worker
Check ($LASTEXITCODE -eq 0) 'Retry works without deleting recovery files'
$taskSetupLock = [IO.File]::Open((Join-Path $taskFixture 'recommended-model.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
try {
    $taskBeforeStatus = (Get-FileHash -LiteralPath (Join-Path $taskFixture 'installation\recommended-model.json')).Hash
    & $taskPowerShell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $taskHelper -Worker
    Check ($LASTEXITCODE -ne 0 -and (Get-FileHash -LiteralPath (Join-Path $taskFixture 'installation\recommended-model.json')).Hash -eq $taskBeforeStatus) 'Second worker refuses without overwriting active status'
} finally { $taskSetupLock.Dispose() }
Check ($env:PATH -ceq $taskBeforePath) 'System/current process PATH unchanged'

# Exercise the actual UI click, including the timer after a rejected action.
# All child work stays in this fake fixture; the user's desktop is never used.
$taskDesktop = Join-Path $taskFixture 'TestDesktopRunner.exe'
& $taskCompiler /nologo /target:exe ('/out:' + $taskDesktop) (Join-Path (Split-Path $SourceRoot -Parent) 'tests\TestDesktopRunner.cs')
Check ($LASTEXITCODE -eq 0) 'Isolated UI launcher builds'
$taskUiTest = Join-Path (Split-Path $SourceRoot -Parent) 'tests\Test-RecommendedModelUi.ps1'
[IO.File]::WriteAllText($taskFakeInstaller, 'param([switch]$RuntimeOnly,[string]$Backend,[string]$PythonExe); [IO.File]::WriteAllText((Join-Path $PSScriptRoot "installation\clicked-worker.txt"),"started"); exit 0', $taskUtf8Test)
& $taskDesktop $taskPowerShell -NoProfile -ExecutionPolicy Bypass -File $taskUiTest -Helper $taskHelper -Output (Join-Path $taskFixture 'clicked.png') -Action start
Check ($LASTEXITCODE -eq 0) 'Actual UI click launches a worker and reaches completion'
[IO.File]::WriteAllText($taskSessionFile, '{"state":"recording"}', $taskUtf8Test)
[IO.File]::WriteAllText((Join-Path $taskMeeting 'status.json'), '{"state":"recording"}', $taskUtf8Test)
& $taskDesktop $taskPowerShell -NoProfile -ExecutionPolicy Bypass -File $taskUiTest -Helper $taskHelper -Output (Join-Path $taskFixture 'blocked.png') -Action blocked
Check ($LASTEXITCODE -eq 0) 'Rejected action remains visible after subsequent status polls'
[IO.File]::WriteAllText($taskSessionFile, '{"state":"recorded"}', $taskUtf8Test)
[IO.File]::WriteAllText((Join-Path $taskMeeting 'status.json'), '{"state":"completed"}', $taskUtf8Test)
$taskLaunchFailure = Join-Path $taskFixture 'LaunchFailure.ps1'
$taskUiSource = [IO.File]::ReadAllText($taskHelper)
Check ($taskUiSource.Split(@('if ($Worker) {'), [StringSplitOptions]::None).Count -eq 2) 'Worker startup failure fixture has one exact injection point'
[IO.File]::WriteAllText($taskLaunchFailure, $taskUiSource.Replace('if ($Worker) {', 'if ($Worker) { exit 9 }; if ($Worker) {'), (New-Object Text.UTF8Encoding($true)))
& $taskDesktop $taskPowerShell -NoProfile -ExecutionPolicy Bypass -File $taskUiTest -Helper $taskLaunchFailure -Output (Join-Path $taskFixture 'launch-failure.png') -Action launch-error
Check ($LASTEXITCODE -eq 0) 'A worker exiting before a fresh status gives an actionable retry'

# Exercise the real RuntimeOnly branch with harmless process probes. In
# particular a missing first-run config must remain missing, and API settings
# must survive local model preparation unchanged.
$taskRuntimeHome = Join-Path $taskFixture 'actual-runtime-only'
[IO.Directory]::CreateDirectory((Join-Path $taskRuntimeHome 'lib')) | Out-Null
foreach ($taskName in @('Install.ps1','RecommendedModel.ps1','requirements.txt','download_models.py','config.example.json','tingji.py','config.schema.json','Install-Shortcuts.ps1')) {
    Copy-Item -LiteralPath (Join-Path $SourceRoot $taskName) -Destination $taskRuntimeHome
}
Copy-Item -LiteralPath (Join-Path $SourceRoot 'lib\NAudio.dll') -Destination (Join-Path $taskRuntimeHome 'lib\NAudio.dll')
[IO.File]::WriteAllText((Join-Path $taskRuntimeHome 'Build.ps1'), 'throw "RuntimeOnly must not build"', $taskUtf8Test)
$taskActualProbeSource = Join-Path $taskRuntimeHome 'Probe.cs'
[IO.File]::WriteAllText($taskActualProbeSource, @'
using System; using System.IO; using System.Linq; using System.Reflection; using System.Web.Script.Serialization;
class Probe { static int Main(string[] args) {
 string exe=Assembly.GetExecutingAssembly().Location; int v=Array.IndexOf(args,"venv");
 if(v>=0) {string dir=Path.Combine(args[v+1],"Scripts"); Directory.CreateDirectory(dir); File.Copy(exe,Path.Combine(dir,"python.exe")); return 0;}
 if(args.Any(x=>x.Contains("'installed':False"))) {Console.WriteLine("{\"installed\":true,\"version\":\"2.11.0+cpu\",\"cuda\":null}");return 0;}
 Console.WriteLine(new JavaScriptSerializer().Serialize(new {executable=exe,version=new int[]{3,12},bits=64,platform="win32"})); return 0;
} }
'@, $taskUtf8Test)
$taskActualProbe = Join-Path $taskRuntimeHome 'probe.exe'
& $taskCompiler /nologo /target:exe /r:System.Web.Extensions.dll ('/out:' + $taskActualProbe) $taskActualProbeSource
Check ($LASTEXITCODE -eq 0) 'Actual RuntimeOnly process fixture compiles'
& $taskPowerShell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $taskRuntimeHome 'Install.ps1') -RuntimeOnly -PythonExe $taskActualProbe *> (Join-Path $taskRuntimeHome 'first.log')
Check ($LASTEXITCODE -eq 0 -and -not (Test-Path -LiteralPath (Join-Path $taskRuntimeHome 'config.json')) -and -not (Test-Path -LiteralPath (Join-Path $taskRuntimeHome 'ObsidianMeeting.exe'))) 'Real RuntimeOnly completes without creating config or building EXE'
$taskApiConfig = Get-Content -LiteralPath (Join-Path $taskRuntimeHome 'config.example.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$taskApiConfig.vault_root=$taskRuntimeHome; $taskApiConfig.transcription_mode='api'
$taskActualConfig = Join-Path $taskRuntimeHome 'config.json'
[IO.File]::WriteAllText($taskActualConfig, ($taskApiConfig | ConvertTo-Json), $taskUtf8Test)
$taskActualHash = (Get-FileHash -LiteralPath $taskActualConfig).Hash
& $taskPowerShell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $taskRuntimeHome 'Install.ps1') -RuntimeOnly -PythonExe $taskActualProbe *> (Join-Path $taskRuntimeHome 'api.log')
Check ($LASTEXITCODE -eq 0 -and (Get-FileHash -LiteralPath $taskActualConfig).Hash -eq $taskActualHash) 'Real RuntimeOnly preserves existing API config without requiring or uploading a key'
Write-Output "PASS Test-RecommendedModel: $taskAssertions assertions; fixture $taskFixture; no audio, clipboard, global installs or model downloads."
