#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$InstallerPath, [switch]$InstallOnly)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskRegistration = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{DE124FA5-8E3D-4B80-A62E-AF8FA54B1E43}_is1'
$taskExisting = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($taskRegistration)
if ($taskExisting) { $taskExisting.Dispose(); throw 'An installer is already registered. Use a disposable profile; existing installations are never replaced.' }
if (-not $InstallOnly -and (Get-Process -Name ObsidianMeeting -ErrorAction SilentlyContinue)) { throw 'verbit is running. Finish meetings and exit normally before this isolated actual-EXE test.' }
$taskFolder = Join-Path $taskRoot ('validation\new-user-' + [guid]::NewGuid().ToString('N'))
$taskApp = Join-Path $taskFolder 'app'
$taskNotes = Join-Path $taskFolder 'notes'
$taskMenuGroup = 'verbit new user ' + [guid]::NewGuid().ToString('N')
[IO.Directory]::CreateDirectory($taskFolder) | Out-Null
$taskProcess = Start-Process -FilePath ([IO.Path]::GetFullPath($InstallerPath)) -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/NOICONS','/TASKS=""',('/GROUP="' + $taskMenuGroup + '"'),('/DIR="' + $taskApp + '"'),('/LOG="' + (Join-Path $taskFolder 'install.log') + '"')) -WindowStyle Hidden -Wait -PassThru
if ($taskProcess.ExitCode -ne 0) { throw "Clean installation failed: $($taskProcess.ExitCode)" }
[ordered]@{ app=$taskApp; notes=$taskNotes; installer_sha256=(Get-FileHash -LiteralPath $InstallerPath).Hash.ToLowerInvariant(); gui_report=(Join-Path $taskApp 'new-user-journey.json'); uninstall=(Join-Path $taskApp 'unins000.exe'); retained_for_runtime_validation=$true; gui_pending=$true } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskFolder 'receipt.json') -Encoding UTF8
if ($InstallOnly) { Write-Output "Installed only; GUI pending: $taskFolder\receipt.json"; return }
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$taskRunner = Join-Path $taskFolder 'TestDesktopRunner.exe'
& $taskCompiler /nologo /target:exe "/out:$taskRunner" (Join-Path $taskRoot 'tests\TestDesktopRunner.cs')
if ($LASTEXITCODE -ne 0) { throw 'Desktop runner compilation failed.' }
$taskJourney = Join-Path $taskFolder 'TestInstalledJourney.exe'
& $taskCompiler /nologo /target:exe /utf8output /r:System.Web.Extensions.dll "/out:$taskJourney" (Join-Path $taskRoot 'tests\TestInstalledJourney.cs')
if ($LASTEXITCODE -ne 0) { throw 'Journey compilation failed.' }
& $taskRunner $taskJourney $taskApp $taskNotes
if ($LASTEXITCODE -ne 0) { throw "Actual installed EXE journey failed. Evidence retained: $taskFolder" }
[ordered]@{ app=$taskApp; notes=$taskNotes; installer_sha256=(Get-FileHash -LiteralPath $InstallerPath).Hash.ToLowerInvariant(); gui_report=(Join-Path $taskApp 'new-user-journey.json'); uninstall=(Join-Path $taskApp 'unins000.exe'); retained_for_runtime_validation=$true } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskFolder 'receipt.json') -Encoding UTF8
Write-Output "PASS: installed EXE journey; retained for runtime setup/transcription: $taskFolder\receipt.json"
