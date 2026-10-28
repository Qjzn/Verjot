#requires -Version 5.1
[CmdletBinding()]
param([switch]$NativeOnly, [string]$PythonExe)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$taskRepo = Split-Path $taskRoot -Parent
$taskTests = Join-Path $taskRepo 'tests'
# Fail before starting any fixture if a test tries to use shared user input.
$taskUnsafeTestCalls = '\b(?:Clipboard\s*\.\s*(?:Get\w*|Set\w*|Clear|Contains\w*)|SendInput|SendKeys\s*\.\s*\w+|keybd_event|SetForegroundWindow|OpenClipboard|EmptyClipboard|SetClipboardData|GetClipboardData)\s*\('
$taskUnsafeSources = Get-ChildItem -LiteralPath $taskTests -Filter 'Test*.cs' -File | Select-String -Pattern $taskUnsafeTestCalls
if ($taskUnsafeSources) { throw 'A native test contains a shared clipboard or global input call. Use a local DataObject or targeted window message.' }
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $taskCompiler)) { $taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path -LiteralPath $taskCompiler)) { throw '.NET Framework C# compiler was not found.' }
$taskOutput = Join-Path $taskRoot ('validation\experience-tests-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($taskOutput) | Out-Null
$taskDesktopRunner = Join-Path $taskOutput 'TestDesktopRunner.exe'
& $taskCompiler /nologo /target:exe "/out:$taskDesktopRunner" (Join-Path $taskTests 'TestDesktopRunner.cs')
if ($LASTEXITCODE -ne 0) { throw "Isolated test desktop runner compilation failed: $LASTEXITCODE" }
foreach ($taskTest in @('TestVerbitMenus', 'TestVerbitUpdates', 'TestDesktopServices', 'TestSessionCatalog', 'TestJsonReadSharing', 'TestRecorderPanel', 'TestRecorderMotion', 'TestMeetingExperience', 'TestCompletionFeedback', 'TestMeetingActions', 'TestMeetingHotkeys', 'TestMeetingSettings', 'TestRecordingProtection', 'TestTimestampedTimeline', 'TestCaptureReliability', 'TestSegmentedTimeline', 'TestRecordingExport', 'TestNativeApiTranscription', 'TestQuickNoteStore', 'TestNativeNoteDocument', 'TestQuickNoteEditor', 'TestQuickNoteResponsiveness', 'TestQuickNoteInput', 'TestQuickNoteNewlines')) {
    $taskFolder = Join-Path $taskOutput $taskTest
    [IO.Directory]::CreateDirectory($taskFolder) | Out-Null
    $taskBinary = Join-Path $taskFolder ($taskTest + '.exe')
    $taskArguments = @('/nologo', '/utf8output', '/target:exe', "/main:$taskTest", "/out:$taskBinary", '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Web.Extensions.dll',
        ('/r:' + (Join-Path $taskRoot 'lib\NAudio.dll')), ('/resource:' + (Join-Path $taskRepo 'assets\tingji.ico') + ',Tingji.Idle.ico'),
        ('/resource:' + (Join-Path $taskRepo 'assets\tingji-recording.ico') + ',Tingji.Recording.ico'), (Join-Path $taskRoot 'ObsidianMeeting.cs'),
        (Join-Path $taskRoot 'SessionCatalog.cs'), (Join-Path $taskRoot 'MeetingActions.cs'), (Join-Path $taskRoot 'NativeAudioExport.cs'), (Join-Path $taskRoot 'MeetingSettings.cs'), (Join-Path $taskRoot 'DesktopServices.cs'), (Join-Path $taskRoot 'VerbitUpdates.cs'), (Join-Path $taskRoot 'QuickNoteStore.cs'), (Join-Path $taskRoot 'QuickNoteWindow.cs'), (Join-Path $taskRoot 'NativeNoteDocument.cs'),
        ('/resource:' + (Join-Path $taskRepo 'assets\github-mark.png') + ',Verbit.GitHub.png'),
        ('/win32manifest:' + (Join-Path $taskRoot 'app.manifest')), (Join-Path $taskTests ($taskTest + '.cs')))
    & $taskCompiler @taskArguments
    if ($LASTEXITCODE -ne 0) { throw "$taskTest compilation failed: $LASTEXITCODE" }
    Copy-Item -LiteralPath (Join-Path $taskRoot 'lib\NAudio.dll') -Destination $taskFolder
    if ($taskTest -in @('TestRecordingExport', 'TestNativeApiTranscription')) { & (Join-Path $taskRoot 'Restore-Encoder.ps1') -Destination $taskFolder }
    Copy-Item -LiteralPath (Join-Path $taskRoot 'config.schema.json') -Destination $taskFolder
    Copy-Item -LiteralPath (Join-Path $taskRoot 'ObsidianMeeting.exe.config') -Destination ($taskBinary + '.config')
    # Each fixture gets a separate desktop without switching the user's screen.
    # Native UI tests may activate their own windows, never the user's windows.
    & $taskDesktopRunner $taskBinary $taskFolder
    if ($LASTEXITCODE -ne 0) { throw "$taskTest failed: $LASTEXITCODE; fixture: $taskFolder" }
}
& (Join-Path $taskTests 'Test-InstallPrerequisites.ps1') -SourceRoot $taskRoot -OutputRoot $taskOutput
if ($LASTEXITCODE -ne 0) { throw "Install prerequisite regression failed: $LASTEXITCODE" }
& (Join-Path $taskTests 'Test-RecommendedModel.ps1') -SourceRoot $taskRoot -OutputRoot $taskOutput
$taskModelUiRoot = Join-Path $taskOutput 'model-ui'
[IO.Directory]::CreateDirectory((Join-Path $taskModelUiRoot 'assets')) | Out-Null
Copy-Item -LiteralPath (Join-Path $taskRoot 'RecommendedModel.ps1') -Destination $taskModelUiRoot
Copy-Item -LiteralPath (Join-Path $taskRepo 'assets\tingji.ico') -Destination (Join-Path $taskModelUiRoot 'assets')
& $taskDesktopRunner (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $taskTests 'Test-RecommendedModelUi.ps1') -Helper (Join-Path $taskModelUiRoot 'RecommendedModel.ps1') -Output (Join-Path $taskModelUiRoot 'recommended-model.png')
if ($LASTEXITCODE -ne 0) { throw 'Recommended model download UI failed in the isolated desktop.' }
if (-not $NativeOnly) {
    $taskPython = if ($PythonExe) { [IO.Path]::GetFullPath($PythonExe) } else { Join-Path $taskRoot '.venv\Scripts\python.exe' }
    if (-not (Test-Path -LiteralPath $taskPython)) { throw 'Install the selected Python dependencies before running worker regressions.' }
    $taskPreviousPythonPath = $env:PYTHONPATH
    $taskPreviousTemp = $env:TEMP
    $taskPreviousTmp = $env:TMP
    $taskPythonTemp = Join-Path $taskOutput 'python-temp'
    [IO.Directory]::CreateDirectory($taskPythonTemp) | Out-Null
    Push-Location $taskRoot
    try {
        $env:TEMP = $env:TMP = $taskPythonTemp
        $env:PYTHONPATH = $taskRoot + ';' + $taskTests
        [IO.Directory]::CreateDirectory((Join-Path $taskTests 'validation')) | Out-Null
        & $taskPython -B -m unittest test_tingji_config test_check_local_engine test_api_asr test_meeting_worker test_merge_audio test_prepare_audio test_audio_sources test_multi_capture_worker test_legacy_migration test_download_models test_mp3_audio test_first_use test_speaker_evaluation test_speaker_refinement
        if ($LASTEXITCODE -ne 0) { throw "Python regressions failed: $LASTEXITCODE" }
    } finally {
        $env:PYTHONPATH = $taskPreviousPythonPath
        $env:TEMP = $taskPreviousTemp; $env:TMP = $taskPreviousTmp
        Pop-Location
    }
}
Write-Output "All requested regressions passed. Local fixtures: $taskOutput"
Write-Output 'Native tests use synthetic metadata/input levels and harmless child processes; no live recording, model inference, cloud transcription or user note is opened.'
Write-Output 'Native windows run on an isolated test desktop; no system clipboard access or global keyboard injection.'
