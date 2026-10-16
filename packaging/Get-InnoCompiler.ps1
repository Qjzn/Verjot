#requires -Version 5.1
[CmdletBinding()]
param([string]$Destination)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
if (-not $Destination) { $Destination = Join-Path $taskRoot 'validation\installer-toolchain' }
$taskDestination = [IO.Path]::GetFullPath($Destination)
$taskVersion = '6.7.3'
$taskUrl = 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe'
$taskExpectedHash = '9C73C3BAE7ED48D44112A0F48E66742C00090BDB5BEF71D9D3C056C66E97B732'
$taskDownload = Join-Path $taskDestination "innosetup-$taskVersion.exe"
$taskPortable = Join-Path $taskDestination "inno-$taskVersion"
[IO.Directory]::CreateDirectory($taskDestination) | Out-Null
if (-not (Test-Path -LiteralPath $taskDownload -PathType Leaf)) {
    $taskPreviousProtocol = [Net.ServicePointManager]::SecurityProtocol
    try {
        [Net.ServicePointManager]::SecurityProtocol = $taskPreviousProtocol -bor [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -UseBasicParsing -Uri $taskUrl -OutFile ($taskDownload + '.partial')
        Move-Item -LiteralPath ($taskDownload + '.partial') -Destination $taskDownload
    } finally { [Net.ServicePointManager]::SecurityProtocol = $taskPreviousProtocol }
}
if ((Get-FileHash -LiteralPath $taskDownload -Algorithm SHA256).Hash -ne $taskExpectedHash) { throw 'Inno Setup download SHA256 does not match the pinned official release.' }
$taskSignature = Get-AuthenticodeSignature -LiteralPath $taskDownload
if ($taskSignature.Status -ne 'Valid' -or $taskSignature.SignerCertificate.Subject -notmatch 'CN=Pyrsys B\.V\.') { throw 'Inno Setup must have a valid Authenticode signature from Pyrsys B.V.; it was not executed.' }
$taskCompiler = Join-Path $taskPortable 'ISCC.exe'
if (-not (Test-Path -LiteralPath $taskCompiler -PathType Leaf)) {
    $taskProcess = Start-Process -FilePath $taskDownload -ArgumentList @('/PORTABLE=1', '/CURRENTUSER', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/TASKS=""', ('/DIR="' + $taskPortable + '"'), ('/LOG="' + (Join-Path $taskDestination 'portable-install.log') + '"')) -WindowStyle Hidden -PassThru -Wait
    if ($taskProcess.ExitCode -ne 0) { throw "Inno Setup portable extraction failed: $($taskProcess.ExitCode)" }
}
if (-not (Test-Path -LiteralPath $taskCompiler -PathType Leaf)) { throw 'Portable extraction did not produce ISCC.exe.' }
[ordered]@{ source = $taskUrl; version = $taskVersion; sha256 = $taskExpectedHash; authenticode = [string]$taskSignature.Status; publisher = $taskSignature.SignerCertificate.Subject; compiler = $taskCompiler; portable = $true } | ConvertTo-Json | Write-Output
