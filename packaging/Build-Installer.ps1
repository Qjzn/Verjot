#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$CompilerPath,
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')][string]$Version = '0.1.0-beta.23',
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $taskRoot 'validation\release' }
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $CompilerPath) {
    $CompilerPath = @( (Join-Path $taskRoot 'validation\installer-toolchain\inno-6.7.3\ISCC.exe'), (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'), (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe') ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}
if (-not $CompilerPath -or -not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) { throw 'Inno Setup 6 ISCC.exe is required. Supply -CompilerPath, or explicitly run packaging\Get-InnoCompiler.ps1 to download the pinned signed portable compiler.' }
$taskLibrary = Join-Path $taskRoot 'src\lib\NAudio.dll'
if (-not (Test-Path -LiteralPath $taskLibrary -PathType Leaf) -or (Get-FileHash -LiteralPath $taskLibrary -Algorithm SHA256).Hash -ne 'BC4BACC3B8B28D898F1671B79F216CCA439F95EB60CD32D3E3ECAFBECAC42780') { throw 'The reviewed NAudio 1.10.0 DLL is missing or has a different SHA256; restore it using Install.ps1.' }

# Explicit reviewed allowlist. No checkout wildcard, personal config, models,
# Python virtual environment, recording, notes, logs, or test fixture is packed.
$taskFiles = @(
    'LICENSE', 'THIRD_PARTY_NOTICES.md', 'README.md', 'SECURITY.md', 'CONTRIBUTING.md',
    'config.example.json', 'config.schema.json', 'ObsidianMeeting.exe.config',
    'tingji.py', 'tingji_config.py', 'meeting_worker.py', 'asr_engine.py', 'api_asr.py',
    'prepare_audio.py', 'merge_audio.py', 'audio_sources.py', 'mp3_audio.py',
    'download_models.py', 'download_torch.py', 'requirements.txt', 'requirements-api.txt',
    'Install.ps1', 'check_local_engine.py', 'Build.ps1', 'Install-Shortcuts.ps1', 'Register-Playback.ps1',
    'ObsidianMeeting.cs', 'SessionCatalog.cs', 'MeetingActions.cs', 'NativeAudioExport.cs', 'Restore-Encoder.ps1', 'MeetingSettings.cs', 'DesktopServices.cs', 'VerbitUpdates.cs',
    'QuickNoteStore.cs', 'QuickNoteWindow.cs', 'NativeNoteDocument.cs', 'app.manifest',
    'assets\tingji.ico', 'assets\tingji-recording.ico', 'assets\verbit-logo.png', 'assets\github-mark.png', 'assets\engine-check.wav', 'assets\engine-check-source.md',
    'documentation\changelog.md',
    'documentation\install-windows.md', 'documentation\first-use.md',
    'documentation\configuration.md', 'documentation\ai-install.md',
    'documentation\brand.md', 'documentation\product-scope.md',
    'documentation\quality.md', 'documentation\release-readiness.md',
    'documentation\recording-export-acceptance.md', 'documentation\device-revalidation.md',
    'documentation\local-model-evaluation.md', 'documentation\private-beta-acceptance.md',
    'documentation\risk-review.md', 'documentation\open-source-readiness.md',
    'documentation\new-user-validation.md',
    'documentation\clean-windows-validation.md',
    'documentation\architecture.md', 'documentation\flows.md', 'documentation\permissions.md',
    'documentation\variables.md', 'documentation\tests.md', 'documentation\cron.md', 'documentation\automation.md',
    'assets\screenshots\recording.png', 'assets\screenshots\quick-note.png', 'assets\screenshots\settings.png',
    'packaging\licenses\NAudio.txt', 'packaging\licenses\LAME.txt', 'packaging\licenses\LAME-SOURCE.md', 'packaging\quick-start.html', 'packaging\Check-InstallSafety.ps1'
)
function Get-PackageInput([string]$Relative) {
    if ($Relative -notmatch '[\\/]' -and $Relative -notin @('LICENSE', 'THIRD_PARTY_NOTICES.md', 'README.md', 'SECURITY.md', 'CONTRIBUTING.md')) {
        return (Join-Path (Join-Path $taskRoot 'src') $Relative)
    }
    return (Join-Path $taskRoot $Relative)
}
foreach ($taskRelative in $taskFiles) {
    if (-not (Test-Path -LiteralPath (Get-PackageInput $taskRelative) -PathType Leaf)) { throw "Package allowlist input is missing: $taskRelative" }
}
$taskBuildRoot = Join-Path $taskOutput ('build-' + [Guid]::NewGuid().ToString('N'))
$taskPayload = Join-Path $taskBuildRoot 'payload'
[IO.Directory]::CreateDirectory($taskPayload) | Out-Null
foreach ($taskRelative in $taskFiles) {
    $taskTarget = Join-Path $taskPayload $taskRelative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($taskTarget)) | Out-Null
    Copy-Item -LiteralPath (Get-PackageInput $taskRelative) -Destination $taskTarget
}
# README links follow the repository layout online and the flat runtime offline.
$taskReadme = Join-Path $taskPayload 'README.md'
$taskReadmeText = [IO.File]::ReadAllText($taskReadme).Replace('](src/', '](')
[IO.File]::WriteAllText($taskReadme, $taskReadmeText, (New-Object Text.UTF8Encoding($false)))
# Install.ps1 restores lib/NAudio.dll only if needed; retaining the reviewed
# copy also lets a user rebuild the controller without an additional download.
[IO.Directory]::CreateDirectory((Join-Path $taskPayload 'lib')) | Out-Null
Copy-Item -LiteralPath $taskLibrary -Destination (Join-Path $taskPayload 'lib\NAudio.dll')
& (Join-Path $taskPayload 'Build.ps1') -OutputPath (Join-Path $taskPayload 'ObsidianMeeting.exe') -Version $Version
if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed.' }
& (Join-Path $taskPayload 'Restore-Encoder.ps1') -Destination $taskPayload -IncludeSource
$taskActual = @(Get-ChildItem -LiteralPath $taskPayload -File -Recurse | ForEach-Object { $_.FullName.Substring($taskPayload.Length + 1) } | Sort-Object)
$taskExpected = @($taskFiles + @('ObsidianMeeting.exe', 'NAudio.dll', 'lib\NAudio.dll', 'libmp3lame.32.dll', 'libmp3lame.64.dll', 'packaging\licenses\LAME-3.100-source.tar.gz') | Sort-Object)
if (@(Compare-Object $taskExpected $taskActual).Count) { throw 'Staged payload differs from the reviewed allowlist.' }
$taskUpdateFiles = @(Get-ChildItem -LiteralPath $taskPayload -File -Recurse | Sort-Object FullName | ForEach-Object { [ordered]@{path=$_.FullName.Substring($taskPayload.Length+1).Replace('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()} })
[IO.File]::WriteAllText((Join-Path $taskPayload 'update-manifest.json'), ([ordered]@{version=$Version;files=$taskUpdateFiles}|ConvertTo-Json -Depth 5),(New-Object Text.UTF8Encoding($false)))
$taskManifestFiles = @(Get-ChildItem -LiteralPath $taskPayload -File -Recurse | Sort-Object FullName | ForEach-Object { [ordered]@{ path = $_.FullName.Substring($taskPayload.Length + 1).Replace('\', '/'); bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } })
$taskUtf8 = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $taskBuildRoot 'payload-manifest.json'), ([ordered]@{ product = 'verbit'; version = $Version; files = $taskManifestFiles; personal_config_included = $false; models_included = $false; python_environment_included = $false } | ConvertTo-Json -Depth 5), $taskUtf8)
$taskNumericVersion = ($Version -split '-')[0] + '.0'
& $CompilerPath '/Qp' ('/DPayloadDir=' + $taskPayload) ('/DOutputDir=' + $taskOutput) ('/DReleaseVersion=' + $Version) ('/DNumericVersion=' + $taskNumericVersion) (Join-Path $PSScriptRoot 'verbit.iss')
if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed: $LASTEXITCODE" }
$taskInstaller = Join-Path $taskOutput "verbit-$Version-setup-x64.exe"
if (-not (Test-Path -LiteralPath $taskInstaller -PathType Leaf)) { throw 'Compiler did not produce the expected installer.' }
$taskHash = (Get-FileHash -LiteralPath $taskInstaller -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $taskOutput 'SHA256SUMS.txt'), ($taskHash + '  ' + [IO.Path]::GetFileName($taskInstaller) + "`n"), $taskUtf8)
[ordered]@{ installer = $taskInstaller; version = $Version; bytes = (Get-Item -LiteralPath $taskInstaller).Length; sha256 = $taskHash; manifest = Join-Path $taskBuildRoot 'payload-manifest.json'; payload = $taskPayload; code_signed = $false } | ConvertTo-Json | Write-Output
