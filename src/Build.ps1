param(
    [string]$OutputPath = 'ObsidianMeeting.exe'
)
$ErrorActionPreference = 'Stop'

# Build the desktop controller only. Python packages and models are installed separately.
$taskRoot = $PSScriptRoot
$taskAssets = Join-Path $taskRoot 'assets'
$taskRepo = Split-Path $taskRoot -Parent
$taskCheckout = Test-Path -LiteralPath (Join-Path $taskRepo 'packaging\Build-Installer.ps1') -PathType Leaf
if ($taskCheckout) { $taskAssets = Join-Path $taskRepo 'assets' }
$taskSource = Join-Path $taskRoot 'ObsidianMeeting.cs'
$taskCatalog = Join-Path $taskRoot 'SessionCatalog.cs'
$taskActions = Join-Path $taskRoot 'MeetingActions.cs'
$taskSettings = Join-Path $taskRoot 'MeetingSettings.cs'
$taskNoteStore = Join-Path $taskRoot 'QuickNoteStore.cs'
$taskNoteWindow = Join-Path $taskRoot 'QuickNoteWindow.cs'
$taskNoteDocument = Join-Path $taskRoot 'NativeNoteDocument.cs'
$taskManifest = Join-Path $taskRoot 'app.manifest'
$taskApplicationConfig = Join-Path $taskRoot 'ObsidianMeeting.exe.config'
$taskLibrary = Join-Path $taskRoot 'lib\NAudio.dll'
$taskIdleIcon = Join-Path $taskAssets 'tingji.ico'
$taskRecordingIcon = Join-Path $taskAssets 'tingji-recording.ico'
$taskCompiler = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (-not $taskCompiler) { throw '.NET Framework 4.x C# compiler was not found.' }
if (-not (Test-Path -LiteralPath $taskLibrary -PathType Leaf)) {
    throw 'NAudio 1.10.0 is missing. Download the official NAudio 1.10.0 NuGet package and extract lib/net35/NAudio.dll to this project''s lib/NAudio.dll; see README.md.'
}
foreach ($taskInput in @($taskSource, $taskCatalog, $taskActions, $taskSettings, $taskNoteStore, $taskNoteWindow, $taskNoteDocument, $taskManifest, $taskApplicationConfig, $taskLibrary, $taskIdleIcon, $taskRecordingIcon)) {
    if (-not (Test-Path -LiteralPath $taskInput -PathType Leaf)) { throw "Required build input is missing: $taskInput" }
}
if (-not [IO.Path]::IsPathRooted($OutputPath)) { $OutputPath = Join-Path $taskRoot $OutputPath }
$taskOutput = [IO.Path]::GetFullPath($OutputPath)
if ([IO.Path]::GetExtension($taskOutput) -ine '.exe') { throw 'OutputPath must end with .exe.' }
$taskOutputFolder = [IO.Path]::GetDirectoryName($taskOutput)
[IO.Directory]::CreateDirectory($taskOutputFolder) | Out-Null
$taskArguments = @(
    '/nologo', '/utf8output', '/target:winexe',
    "/out:$taskOutput",
    '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Web.Extensions.dll',
    "/r:$taskLibrary",
    "/win32icon:$taskIdleIcon",
    "/win32manifest:$taskManifest",
    "/resource:$taskIdleIcon,Tingji.Idle.ico",
    "/resource:$taskRecordingIcon,Tingji.Recording.ico",
    $taskSource, $taskCatalog, $taskActions, $taskSettings, $taskNoteStore, $taskNoteWindow, $taskNoteDocument
)
& $taskCompiler @taskArguments
if ($LASTEXITCODE -ne 0) { throw "C# compilation failed with exit code $LASTEXITCODE." }
if (($taskOutput + '.config') -ine $taskApplicationConfig) {
    Copy-Item -LiteralPath $taskApplicationConfig -Destination ($taskOutput + '.config') -Force
}
$taskRuntimeLibrary = Join-Path $taskOutputFolder 'NAudio.dll'
if ($taskRuntimeLibrary -ine [IO.Path]::GetFullPath($taskLibrary)) {
    $taskLibraryMatches = (Test-Path -LiteralPath $taskRuntimeLibrary -PathType Leaf) -and
        ((Get-FileHash -LiteralPath $taskRuntimeLibrary).Hash -eq (Get-FileHash -LiteralPath $taskLibrary).Hash)
    if (-not $taskLibraryMatches) { Copy-Item -LiteralPath $taskLibrary -Destination $taskRuntimeLibrary -Force }
}
Write-Output "Built: $taskOutput"
if ($taskCheckout -and $taskOutputFolder -ieq $taskRoot) {
    # Source installs provide the same offline setup guide as the installer.
    Copy-Item -LiteralPath (Join-Path $taskRepo 'documentation') -Destination $taskRoot -Recurse -Force
    foreach ($taskPolicy in @('LICENSE', 'SECURITY.md', 'THIRD_PARTY_NOTICES.md')) {
        Copy-Item -LiteralPath (Join-Path $taskRepo $taskPolicy) -Destination $taskRoot -Force
    }
    [IO.Directory]::CreateDirectory((Join-Path $taskRoot 'assets')) | Out-Null
    foreach ($taskName in @('verbit-logo.png', 'tingji.ico', 'tingji-recording.ico')) {
        Copy-Item -LiteralPath (Join-Path $taskAssets $taskName) -Destination (Join-Path $taskRoot 'assets') -Force
    }
    [IO.Directory]::CreateDirectory((Join-Path $taskRoot 'packaging')) | Out-Null
    Copy-Item -LiteralPath (Join-Path $taskRepo 'packaging\quick-start.html') -Destination (Join-Path $taskRoot 'packaging') -Force
    Copy-Item -LiteralPath (Join-Path $taskRepo 'packaging\licenses') -Destination (Join-Path $taskRoot 'packaging') -Recurse -Force
}
Write-Output 'Keep the executable in the project folder when running it, alongside config.json, Python scripts, .venv and models.'
