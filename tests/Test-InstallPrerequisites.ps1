#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$SourceRoot,[Parameter(Mandatory=$true)][string]$OutputRoot)
$ErrorActionPreference = 'Stop'
$taskHome = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) ('missing-vc-runtime-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($taskHome) | Out-Null
$taskFakeSource = Join-Path $taskHome 'ProbePython.cs'
$taskFakeBinary = Join-Path $taskHome 'probe-python.exe'
$taskUtf8 = New-Object Text.UTF8Encoding($false)
# Exercise the actual guest guard without executing guest installation code.
$taskGuestScript = Join-Path (Split-Path $SourceRoot -Parent) 'packaging\Test-CleanWindowsGuest.ps1'
if (Test-Path -LiteralPath $taskGuestScript) {
    $taskTokens=$null; $taskParseErrors=$null
    $taskAst=[Management.Automation.Language.Parser]::ParseFile($taskGuestScript,[ref]$taskTokens,[ref]$taskParseErrors)
    if ($taskParseErrors.Count) { throw 'Clean guest script has parse errors.' }
    $taskGuard=$taskAst.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Assert-DisposableGuest'},$true)
    . ([ScriptBlock]::Create($taskGuard.Extent.Text))
    Assert-DisposableGuest 'Sandbox' 'WDAGUtilityAccount' '' $true
    foreach($taskModel in @('Virtual Machine','VMware Virtual Platform','VirtualBox','QEMU Standard PC','Parallels ARM Virtual Machine')) {
        Assert-DisposableGuest 'VM' 'new-user' $taskModel $true
    }
    foreach($taskRejected in @(@('Sandbox','SW','',$true),@('VM','SW','Desktop Computer',$true),@('VM','SW','Virtual Machine',$false))) {
        $taskRefused=$false
        try { Assert-DisposableGuest $taskRejected[0] $taskRejected[1] $taskRejected[2] $taskRejected[3] } catch { $taskRefused=$true }
        if (-not $taskRefused) { throw 'Guest guard accepted a physical host or missing manifest.' }
    }
    Write-Output 'PASS: guest guard accepts explicit disposable VM/Sandbox, rejects host/missing inputs; no guest launched.'
}
[IO.File]::WriteAllText($taskFakeSource, @'
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;
class ProbePython {
    static int Main(string[] args) {
        string executable = Assembly.GetExecutingAssembly().Location;
        string root = Path.GetDirectoryName(executable);
        if (Path.GetFileName(root) == "Scripts") root = Directory.GetParent(root).Parent.FullName;
        string missingPip = Path.Combine(root,"missing-pip");
        if(args.Contains("ensurepip")) {
            File.Delete(missingPip);
            File.WriteAllText(Path.Combine(root,"ensurepip-invoked"),"local bundled wheels only");
            return 0;
        }
        if(args.Any(x=>x.Contains("find_spec('pip')"))) return File.Exists(missingPip) ? 3 : 0;
        int venv = Array.IndexOf(args,"venv");
        if(venv>=0) {string folder=Path.Combine(args[venv+1],"Scripts");Directory.CreateDirectory(folder);File.Copy(executable,Path.Combine(folder,"python.exe"));return 0;}
        if(args.Any(x=>x.Contains("ctypes.WinDLL"))) {Console.Error.WriteLine("Injected missing Microsoft VC runtime");return 2;}
        if(args.Contains("pip")) {
            if(File.Exists(missingPip) || File.Exists(Path.Combine(root,"broken-pip"))) {
                Console.Error.WriteLine("Injected interrupted pip bootstrap"); return 9;
            }
            Console.WriteLine("pip fixture (no network)");return 0;
        }
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
$taskInvokeFixture = Join-Path $taskHome 'InvokeFixture.ps1'
# This fixture never installs a real runtime/controller. Control the process
# inventory in this child only, so another user's tray process cannot hide the
# prerequisite failure being tested. Production process checks stay unchanged.
[IO.File]::WriteAllText($taskInvokeFixture, @'
param([string]$InstallScript,[string]$Notes,[string]$Python)
function Get-Process { [CmdletBinding()] param([string]$Name) return @() }
& $InstallScript -NotesPath $Notes -PythonExe $Python -Mode local -Backend cpu
exit $LASTEXITCODE
'@, $taskUtf8)
$taskPrevious = $ErrorActionPreference
try {
    $ErrorActionPreference = 'Continue'
    $taskOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $taskInvokeFixture -InstallScript (Join-Path $taskHome 'Install.ps1') -Notes $taskNotes -Python $taskFakeBinary 2>&1
    $taskExit = $LASTEXITCODE
} finally { $ErrorActionPreference = $taskPrevious }
$taskText = $taskOutput -join [Environment]::NewLine
[IO.File]::WriteAllText((Join-Path $taskHome 'fixture.log'),$taskText,$taskUtf8)
$taskState = Get-Content -LiteralPath (Join-Path $taskHome 'installation\status.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($taskExit -eq 0 -or $taskState.phase -ne 'native-runtime' -or $taskState.state -ne 'error' -or
    $taskText -notmatch 'latest-supported-vc-redist' -or (Test-Path -LiteralPath (Join-Path $taskHome 'models')) -or
    (Test-Path -LiteralPath (Join-Path $taskHome 'config.json'))) { throw "Missing VC prerequisite did not fail safely: $taskExit / $($taskState.phase) / $($taskState.state)" }
Write-Output 'PASS: actual Install.ps1 missing-VC fault injection; clear official remedy, no model download/config write/network.'
foreach ($taskCase in @('missing-pip','broken-pip')) {
    [IO.File]::WriteAllText((Join-Path $taskHome $taskCase),'interrupted installation',$taskUtf8)
    $taskPrevious = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $taskOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $taskInvokeFixture -InstallScript (Join-Path $taskHome 'Install.ps1') -Notes $taskNotes -Python $taskFakeBinary 2>&1
        $taskExit = $LASTEXITCODE
    } finally { $ErrorActionPreference = $taskPrevious }
    [IO.File]::WriteAllText((Join-Path $taskHome ($taskCase + '.log')),($taskOutput -join [Environment]::NewLine),$taskUtf8)
    $taskState = Get-Content -LiteralPath (Join-Path $taskHome 'installation\status.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $taskExpectedPhase = if ($taskCase -eq 'missing-pip') { 'native-runtime' } else { 'venv' }
    if ($taskExit -eq 0 -or $taskState.phase -ne $taskExpectedPhase -or $taskState.state -ne 'error' -or
        (Test-Path -LiteralPath (Join-Path $taskHome 'models')) -or
        (Test-Path -LiteralPath (Join-Path $taskHome 'config.json'))) {
        throw "Interrupted pip bootstrap did not recover or fail safely: $taskCase / $taskExit / $($taskState.phase)"
    }
    if ($taskCase -eq 'missing-pip') {
        if ((Test-Path -LiteralPath (Join-Path $taskHome 'missing-pip')) -or
            -not (Test-Path -LiteralPath (Join-Path $taskHome 'ensurepip-invoked'))) { throw 'Missing pip was not bootstrapped locally.' }
        Remove-Item -LiteralPath (Join-Path $taskHome 'ensurepip-invoked')
    } elseif (Test-Path -LiteralPath (Join-Path $taskHome 'ensurepip-invoked')) { throw 'Existing broken pip was overwritten.' }
    Write-Output "PASS: actual Install.ps1 $taskCase retry; no models/config/network."
}
# The rejected child is the expected outcome, not this fixture's exit status.
# GitHub's PowerShell wrapper propagates LASTEXITCODE after script completion.
exit 0
