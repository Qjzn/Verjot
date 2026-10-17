#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$SourceRoot,[Parameter(Mandatory=$true)][string]$OutputRoot)
$ErrorActionPreference = 'Stop'
$taskHome = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) ('missing-vc-runtime-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($taskHome) | Out-Null
$taskFakeSource = Join-Path $taskHome 'ProbePython.cs'
$taskFakeBinary = Join-Path $taskHome 'probe-python.exe'
$taskUtf8 = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText($taskFakeSource, @'
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;
class ProbePython {
    static int Main(string[] args) {
        string executable = Assembly.GetExecutingAssembly().Location;
        int venv = Array.IndexOf(args,"venv");
        if(venv>=0) {string folder=Path.Combine(args[venv+1],"Scripts");Directory.CreateDirectory(folder);File.Copy(executable,Path.Combine(folder,"python.exe"));return 0;}
        if(args.Any(x=>x.Contains("ctypes.WinDLL"))) {Console.Error.WriteLine("Injected missing Microsoft VC runtime");return 2;}
        if(args.Contains("pip")) {Console.WriteLine("pip fixture (no network)");return 0;}
        Console.WriteLine(new JavaScriptSerializer().Serialize(new {executable=executable,version=new int[]{3,12},bits=64,platform="win32"}));return 0;
    }
}
'@, $taskUtf8)
$taskCsc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $taskCsc /nologo /target:exe /r:System.Web.Extensions.dll ("/out:" + $taskFakeBinary) $taskFakeSource
if ($LASTEXITCODE -ne 0) { throw 'Preflight fixture compile failed.' }
foreach ($taskName in @('Install.ps1','Build.ps1','requirements.txt','download_models.py','config.example.json','tingji.py','config.schema.json','Install-Shortcuts.ps1')) {
    Copy-Item -LiteralPath (Join-Path $SourceRoot $taskName) -Destination $taskHome
}
[IO.Directory]::CreateDirectory((Join-Path $taskHome 'lib')) | Out-Null
Copy-Item -LiteralPath (Join-Path $SourceRoot 'lib\NAudio.dll') -Destination (Join-Path $taskHome 'lib\NAudio.dll')
$taskNotes = Join-Path $taskHome 'notes'
$taskPrevious = $ErrorActionPreference
try {
    $ErrorActionPreference = 'Continue'
    $taskOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $taskHome 'Install.ps1') -NotesPath $taskNotes -PythonExe $taskFakeBinary -Mode local -Backend cpu 2>&1
    $taskExit = $LASTEXITCODE
} finally { $ErrorActionPreference = $taskPrevious }
$taskText = $taskOutput -join [Environment]::NewLine
[IO.File]::WriteAllText((Join-Path $taskHome 'fixture.log'),$taskText,$taskUtf8)
$taskState = Get-Content -LiteralPath (Join-Path $taskHome 'installation\status.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($taskExit -eq 0 -or $taskState.phase -ne 'native-runtime' -or $taskState.state -ne 'error' -or
    $taskText -notmatch 'latest-supported-vc-redist' -or (Test-Path -LiteralPath (Join-Path $taskHome 'models')) -or
    (Test-Path -LiteralPath (Join-Path $taskHome 'config.json'))) { throw "Missing VC prerequisite did not fail safely: $taskExit / $($taskState.phase) / $($taskState.state)" }
Write-Output 'PASS: actual Install.ps1 missing-VC fault injection; clear official remedy, no model download/config write/network.'
