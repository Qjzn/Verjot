#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$InstallerPath,
    [string]$SpeechA,
    [string]$SpeechB,
    [switch]$LegacyLocalTranscription
)
$ErrorActionPreference = 'Stop'
$taskRepo = Split-Path $PSScriptRoot -Parent
$taskBundle = Join-Path $taskRepo ('validation\clean-windows-' + [guid]::NewGuid().ToString('N'))
$taskInput = Join-Path $taskBundle 'input'
$taskReports = Join-Path $taskBundle 'reports'
[IO.Directory]::CreateDirectory($taskInput) | Out-Null
[IO.Directory]::CreateDirectory($taskReports) | Out-Null
# Explicit inputs only: never map the checkout, personal config, models or notes.
Copy-Item -LiteralPath ([IO.Path]::GetFullPath($InstallerPath)) -Destination (Join-Path $taskInput 'setup.exe')
if ($LegacyLocalTranscription) {
    if (-not $SpeechA -or -not $SpeechB) { throw 'Legacy local model validation needs two explicitly provided synthetic speech WAVs.' }
    Copy-Item -LiteralPath ([IO.Path]::GetFullPath($SpeechA)) -Destination (Join-Path $taskInput 'known-a.wav')
    Copy-Item -LiteralPath ([IO.Path]::GetFullPath($SpeechB)) -Destination (Join-Path $taskInput 'known-b.wav')
}
foreach ($taskHelper in @('PrepareOfflineInstall.py','VerifyOfflineInstall.py')) {
    Copy-Item -LiteralPath (Join-Path $taskRepo ('tests\' + $taskHelper)) -Destination $taskInput
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Test-CleanWindowsGuest.ps1') -Destination $taskInput
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
foreach ($taskName in @('TestInstalledJourney','TestDesktopRunner')) {
    & $taskCompiler /nologo /target:exe /r:System.Web.Extensions.dll ("/out:" + (Join-Path $taskInput ($taskName + '.exe'))) (Join-Path $taskRepo ('tests\' + $taskName + '.cs'))
    if ($LASTEXITCODE -ne 0) { throw "Test helper compilation failed: $taskName" }
}
$taskManifest = @{}
Get-ChildItem -LiteralPath $taskInput -File | ForEach-Object { $taskManifest[$_.Name] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
$taskUtf8 = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $taskInput 'SHA256.json'), ($taskManifest | ConvertTo-Json), $taskUtf8)
$taskInputXml = [Security.SecurityElement]::Escape($taskInput)
$taskReportsXml = [Security.SecurityElement]::Escape($taskReports)
$taskMemory = if ($LegacyLocalTranscription) { 16384 } else { 4096 }
$taskNetwork = if ($LegacyLocalTranscription) { 'Enable' } else { 'Disable' }
$taskGuestOption = if ($LegacyLocalTranscription) { ' -LegacyLocalTranscription' } else { '' }
$taskConfiguration = @"
<Configuration>
  <VGpu>Disable</VGpu><MemoryInMB>$taskMemory</MemoryInMB><Networking>$taskNetwork</Networking>
  <ClipboardRedirection>Disable</ClipboardRedirection><AudioInput>Disable</AudioInput>
  <VideoInput>Disable</VideoInput><PrinterRedirection>Disable</PrinterRedirection>
  <MappedFolders>
    <MappedFolder><HostFolder>$taskInputXml</HostFolder><SandboxFolder>C:\Verjot-input</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$taskReportsXml</HostFolder><SandboxFolder>C:\Verjot-reports</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
  </MappedFolders>
  <LogonCommand><Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\Verjot-input\Test-CleanWindowsGuest.ps1$taskGuestOption</Command></LogonCommand>
</Configuration>
"@
[IO.File]::WriteAllText((Join-Path $taskBundle 'Verjot-clean.wsb'), $taskConfiguration, $taskUtf8)
Write-Output "Prepared only; no Sandbox launched and no system features changed: $taskBundle"
Write-Output 'Default: enabled Windows Sandbox, 4 GiB guest memory, networking disabled; EXE/first-settings/notes only, no Python/model download. Recording/API are separate verification items.'
if ($LegacyLocalTranscription) { Write-Output 'Explicit legacy research mode: 16 GiB and network; guest downloads Python/VC++/models without host caches.' }
