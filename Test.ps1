#requires -Version 5.1
[CmdletBinding()]
param([switch]$NativeOnly)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
# Fail before starting any fixture if a test tries to use shared user input.
$taskUnsafeTestCalls = '\b(?:Clipboard\s*\.\s*(?:Get\w*|Set\w*|Clear|Contains\w*)|SendInput|SendKeys\s*\.\s*\w+|keybd_event|SetForegroundWindow|OpenClipboard|EmptyClipboard|SetClipboardData|GetClipboardData)\s*\('
$taskUnsafeSources = Get-ChildItem -LiteralPath $taskRoot -Filter 'Test*.cs' -File | Select-String -Pattern $taskUnsafeTestCalls
if ($taskUnsafeSources) { throw 'A native test contains a shared clipboard or global input call. Use a local DataObject or targeted window message.' }
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $taskCompiler)) { $taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path -LiteralPath $taskCompiler)) { throw '.NET Framework C# compiler was not found.' }
$taskOutput = Join-Path $taskRoot ('validation\experience-tests-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($taskOutput) | Out-Null
$taskDesktopRunner = Join-Path $taskOutput 'TestDesktopRunner.exe'
& $taskCompiler /nologo /target:exe "/out:$taskDesktopRunner" (Join-Path $taskRoot 'TestDesktopRunner.cs')
if ($LASTEXITCODE -ne 0) { throw "Isolated test desktop runner compilation failed: $LASTEXITCODE" }
foreach ($taskTest in @('TestSessionCatalog', 'TestJsonReadSharing', 'TestRecorderPanel', 'TestRecorderMotion', 'TestMeetingExperience', 'TestCompletionFeedback', 'TestMeetingActions', 'TestMeetingHotkeys', 'TestTimestampedTimeline', 'TestCaptureReliability', 'TestSegmentedTimeline', 'TestQuickNoteStore', 'TestQuickNoteEditor', 'TestQuickNoteResponsiveness', 'TestQuickNoteInput')) {
    $taskFolder = Join-Path $taskOutput $taskTest
    [IO.Directory]::CreateDirectory($taskFolder) | Out-Null
    $taskBinary = Join-Path $taskFolder ($taskTest + '.exe')
    $taskArguments = @('/nologo', '/utf8output', '/target:exe', "/main:$taskTest", "/out:$taskBinary", '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Web.Extensions.dll',
        ('/r:' + (Join-Path $taskRoot 'lib\NAudio.dll')), ('/resource:' + (Join-Path $taskRoot 'assets\tingji.ico') + ',Tingji.Idle.ico'),
        ('/resource:' + (Join-Path $taskRoot 'assets\tingji-recording.ico') + ',Tingji.Recording.ico'), (Join-Path $taskRoot 'ObsidianMeeting.cs'),
        (Join-Path $taskRoot 'SessionCatalog.cs'), (Join-Path $taskRoot 'MeetingActions.cs'), (Join-Path $taskRoot 'QuickNoteStore.cs'), (Join-Path $taskRoot 'QuickNoteWindow.cs'),
        '/r:System.Xaml.dll', ('/win32manifest:' + (Join-Path $taskRoot 'app.manifest')), (Join-Path $taskRoot ($taskTest + '.cs')))
    foreach ($taskAssembly in @('PresentationCore', 'PresentationFramework', 'WindowsBase', 'WindowsFormsIntegration')) {
        $taskArguments += '/r:' + (Join-Path (Split-Path $taskCompiler -Parent) ('WPF\' + $taskAssembly + '.dll'))
    }
    & $taskCompiler @taskArguments
    if ($LASTEXITCODE -ne 0) { throw "$taskTest compilation failed: $LASTEXITCODE" }
    Copy-Item -LiteralPath (Join-Path $taskRoot 'lib\NAudio.dll') -Destination $taskFolder
    Copy-Item -LiteralPath (Join-Path $taskRoot 'config.schema.json') -Destination $taskFolder
    Copy-Item -LiteralPath (Join-Path $taskRoot 'ObsidianMeeting.exe.config') -Destination ($taskBinary + '.config')
    # Each fixture gets a separate desktop without switching the user's screen.
    # Native UI tests may activate their own windows, never the user's windows.
    & $taskDesktopRunner $taskBinary $taskFolder
    if ($LASTEXITCODE -ne 0) { throw "$taskTest failed: $LASTEXITCODE; fixture: $taskFolder" }
}
if (-not $NativeOnly) {
    $taskPython = Join-Path $taskRoot '.venv\Scripts\python.exe'
    if (-not (Test-Path -LiteralPath $taskPython)) { throw 'Install the selected Python dependencies before running worker regressions.' }
    $taskPreviousTemp = $env:TEMP
    $taskPreviousTmp = $env:TMP
    $taskPythonTemp = Join-Path $taskOutput 'python-temp'
    [IO.Directory]::CreateDirectory($taskPythonTemp) | Out-Null
    Push-Location $taskRoot
    try {
        $env:TEMP = $env:TMP = $taskPythonTemp
        & $taskPython -B -m unittest test_tingji_config test_api_asr test_meeting_worker test_merge_audio test_prepare_audio test_audio_sources test_multi_capture_worker test_legacy_migration test_download_models test_mp3_audio
        if ($LASTEXITCODE -ne 0) { throw "Python regressions failed: $LASTEXITCODE" }
    } finally {
        $env:TEMP = $taskPreviousTemp; $env:TMP = $taskPreviousTmp
        Pop-Location
    }
}
Write-Output "All requested regressions passed. Local fixtures: $taskOutput"
Write-Output 'Native tests use synthetic metadata/input levels and harmless child processes; no live recording, model inference, cloud transcription or user note is opened.'
Write-Output 'Native windows run on an isolated test desktop; no system clipboard access or global keyboard injection.'
