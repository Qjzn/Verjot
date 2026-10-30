#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$SpeechPath)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskSpeech = [IO.Path]::GetFullPath($SpeechPath)
if (-not (Test-Path -LiteralPath $taskSpeech -PathType Leaf)) { throw 'A known PCM WAV is required; this preparation never plays it.' }
$taskOutput = Join-Path $taskRoot ('validation\device-revalidation-' + [Guid]::NewGuid().ToString('N'))
$taskRuntime = Join-Path $taskOutput 'runtime'
[IO.Directory]::CreateDirectory($taskRuntime) | Out-Null
& (Join-Path $taskRoot 'src\Build.ps1') -OutputPath (Join-Path $taskRuntime 'ObsidianMeeting.exe') | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Production build failed.' }
$taskSourcePath = Join-Path $taskRoot 'src\ObsidianMeeting.cs'
$taskSource = [IO.File]::ReadAllText($taskSourcePath).Replace("`r`n", "`n")
function Replace-Once([string]$Needle, [string]$Replacement) {
    if ([regex]::Matches($script:taskSource, [regex]::Escape($Needle)).Count -ne 1) { throw "Instrumentation anchor changed; refusing to guess: $Needle" }
    $script:taskSource = $script:taskSource.Replace($Needle, $Replacement)
}
Replace-Once '    readonly bool loopback;' @'
    readonly bool loopback;
    StreamWriter reviewPackets;
    int reviewSequence;
'@
Replace-Once '        writer = new TimelineWriter(path, source.AudioClient.MixFormat, segmentSeconds);' @'
        writer = new TimelineWriter(path, source.AudioClient.MixFormat, segmentSeconds);
        reviewPackets = new StreamWriter(path + ".packets.tsv", false, new UTF8Encoding(false), 65536);
        reviewPackets.WriteLine("seq\traw_position\tframes\tqpc_100ns\traw_start_ms\tarrival_ms\tresolved_start_ms\tresolved_position\tflags\testimated");
'@
Replace-Once '                            bool segmented = writer.AddTimestamped(buffer, buffer.Length, startMs, position, discontinuity || timestampError);' @'
                            reviewPackets.WriteLine(string.Join("\t", new string[] {
                                (reviewSequence++).ToString(CultureInfo.InvariantCulture), reviewRawPosition.ToString(CultureInfo.InvariantCulture),
                                frames.ToString(CultureInfo.InvariantCulture), qpc.ToString(CultureInfo.InvariantCulture),
                                ((qpc - qpcOrigin100ns) / 10000.0).ToString("R", CultureInfo.InvariantCulture), arrivalMs.ToString("R", CultureInfo.InvariantCulture),
                                startMs.ToString("R", CultureInfo.InvariantCulture), position.ToString(CultureInfo.InvariantCulture),
                                ((int)flags).ToString(CultureInfo.InvariantCulture), timestampError ? "1" : "0" }));
                            bool segmented = writer.AddTimestamped(buffer, buffer.Length, startMs, position, discontinuity || timestampError);
'@
Replace-Once '                            bool timestampError;' "                            long reviewRawPosition = position;`n                            bool timestampError;"
Replace-Once '        } finally { ended = true; ready.Set(); stopped.Set(); }' @'
        } finally {
            if (reviewPackets != null) reviewPackets.Dispose();
            ended = true; ready.Set(); stopped.Set();
        }
'@
$taskInstrumented = Join-Path $taskOutput 'ObsidianMeeting.instrumented.cs'
$taskUtf8 = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText($taskInstrumented, $taskSource, $taskUtf8)
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$taskSources = @(Get-ChildItem -LiteralPath (Join-Path $taskRoot 'src') -Filter '*.cs' -File | Where-Object { $_.Name -ne 'ObsidianMeeting.cs' } | ForEach-Object { $_.FullName })
$taskArguments = @('/nologo', '/target:winexe', '/main:DeviceRevalidation', ('/out:' + (Join-Path $taskRuntime 'DeviceRevalidation.exe')),
    '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Web.Extensions.dll', ('/r:' + (Join-Path $taskRoot 'src\lib\NAudio.dll')),
    ('/win32manifest:' + (Join-Path $taskRoot 'src\app.manifest')),
    ('/resource:' + (Join-Path $taskRoot 'assets\tingji.ico') + ',Tingji.Idle.ico'),
    ('/resource:' + (Join-Path $taskRoot 'assets\tingji-recording.ico') + ',Tingji.Recording.ico'),
    ('/resource:' + (Join-Path $taskRoot 'assets\github-mark.png') + ',Verlog.GitHub.png'),
    ('/resource:' + (Join-Path $taskRoot 'assets\Verlog-logo.png') + ',Verlog.Logo.png'),
    $taskInstrumented, (Join-Path $taskRoot 'tests\DeviceRevalidation.cs')) + $taskSources
& $taskCompiler @taskArguments
if ($LASTEXITCODE -ne 0) { throw 'Isolated probe compilation failed.' }
Copy-Item -LiteralPath (Join-Path $taskRuntime 'ObsidianMeeting.exe.config') -Destination (Join-Path $taskRuntime 'DeviceRevalidation.exe.config')
Copy-Item -LiteralPath (Join-Path $taskRoot 'src\config.schema.json') -Destination $taskRuntime
Copy-Item -LiteralPath $taskSpeech -Destination (Join-Path $taskRuntime 'test-input.wav')
$taskConfig = Get-Content -LiteralPath (Join-Path $taskRoot 'src\config.example.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$taskConfig.vault_root = Join-Path $taskRuntime 'output'
[IO.Directory]::CreateDirectory($taskConfig.vault_root) | Out-Null
$taskConfig.open_in_obsidian = $false
$taskConfig.auto_open_completed_note = $false
$taskConfig.recording_segment_seconds = 10
$taskConfig.transcription_mode = 'local'
[IO.File]::WriteAllText((Join-Path $taskRuntime 'config.json'), ($taskConfig | ConvertTo-Json -Depth 10), $taskUtf8)
$taskFiles = @('ObsidianMeeting.exe', 'DeviceRevalidation.exe', 'test-input.wav') | ForEach-Object {
    $taskPath = Join-Path $taskRuntime $_
    [ordered]@{ name = $_; sha256 = (Get-FileHash -LiteralPath $taskPath -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$taskManifest = [ordered]@{ prepared_only = $true; audio_recorded = $false; audio_played = $false; clipboard_access = $false;
    source_commit = (& git -C $taskRoot rev-parse HEAD); production_source_sha256 = (Get-FileHash -LiteralPath $taskSourcePath -Algorithm SHA256).Hash;
    instrumentation = 'Packet metadata only; 5 exact anchored insertions; no audio copied before microphone mute masking';
    instrumented_source_sha256 = (Get-FileHash -LiteralPath $taskInstrumented -Algorithm SHA256).Hash; files = @($taskFiles) }
[IO.File]::WriteAllText((Join-Path $taskOutput 'preparation.json'), ($taskManifest | ConvertTo-Json -Depth 6), $taskUtf8)
[IO.File]::WriteAllText((Join-Path $taskOutput '开始复验.cmd'), ('@echo off' + "`r`n" + 'start "" "%~dp0runtime\DeviceRevalidation.exe" --consent-record-and-play' + "`r`n"), (New-Object Text.ASCIIEncoding))
[ordered]@{ prepared_only=$true; output=$taskOutput; entry=(Join-Path $taskOutput '开始复验.cmd'); seconds=150; clipboard_access=$false; audio_recorded=$false } | ConvertTo-Json
