param(
    [string]$OutputPath = 'ObsidianMeeting.exe'
)
$ErrorActionPreference = 'Stop'

# Build the desktop controller only. Python packages and models are installed separately.
$taskRoot = $PSScriptRoot
$taskSource = Join-Path $taskRoot 'ObsidianMeeting.cs'
$taskLibrary = Join-Path $taskRoot 'lib\NAudio.dll'
$taskIdleIcon = Join-Path $taskRoot 'assets\tingji.ico'
$taskRecordingIcon = Join-Path $taskRoot 'assets\tingji-recording.ico'
$taskCompiler = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (-not $taskCompiler) { throw '.NET Framework 4.x C# compiler was not found.' }
if (-not (Test-Path -LiteralPath $taskLibrary -PathType Leaf)) {
    throw 'NAudio 1.10.0 is missing. Download the official NAudio 1.10.0 NuGet package and extract lib/net35/NAudio.dll to this project''s lib/NAudio.dll; see README.md.'
}
foreach ($taskInput in @($taskSource, $taskLibrary, $taskIdleIcon, $taskRecordingIcon)) {
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
    "/resource:$taskIdleIcon,Tingji.Idle.ico",
    "/resource:$taskRecordingIcon,Tingji.Recording.ico",
    $taskSource
)
& $taskCompiler @taskArguments
if ($LASTEXITCODE -ne 0) { throw "C# compilation failed with exit code $LASTEXITCODE." }
$taskRuntimeLibrary = Join-Path $taskOutputFolder 'NAudio.dll'
if ($taskRuntimeLibrary -ine [IO.Path]::GetFullPath($taskLibrary)) {
    $taskLibraryMatches = (Test-Path -LiteralPath $taskRuntimeLibrary -PathType Leaf) -and
        ((Get-FileHash -LiteralPath $taskRuntimeLibrary).Hash -eq (Get-FileHash -LiteralPath $taskLibrary).Hash)
    if (-not $taskLibraryMatches) { Copy-Item -LiteralPath $taskLibrary -Destination $taskRuntimeLibrary -Force }
}
Write-Output "Built: $taskOutput"
Write-Output 'Keep the executable in the project folder when running it, alongside config.json, Python scripts, .venv and models.'
