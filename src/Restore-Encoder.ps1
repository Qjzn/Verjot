#requires -Version 5.1
[CmdletBinding()]
param([string]$Destination = $PSScriptRoot, [switch]$IncludeSource)
$ErrorActionPreference = 'Stop'
$taskPackage = 'https://api.nuget.org/v3-flatcontainer/naudio.lame/1.1.1/naudio.lame.1.1.1.nupkg'
$taskExpected = @{
    'libmp3lame.32.dll' = '91D3C0CC833265066C51408D940FAAF76C5B8FC5BDA854540A9CC6C88B1D1F20'
    'libmp3lame.64.dll' = '71DBA147B6C16D8D0F012B8E657D52E12CB8796A659E1151200D0A6C8B04BCE3'
}
$taskDestination = [IO.Path]::GetFullPath($Destination)
[IO.Directory]::CreateDirectory($taskDestination) | Out-Null
$taskMissing = @($taskExpected.Keys | Where-Object {
    $taskFile = Join-Path $taskDestination $_
    -not (Test-Path -LiteralPath $taskFile -PathType Leaf) -or (Get-FileHash -LiteralPath $taskFile -Algorithm SHA256).Hash -ne $taskExpected[$_]
})
if ($taskMissing.Count -eq 0 -and -not $IncludeSource) { return }
$taskScratch = Join-Path ([IO.Path]::GetTempPath()) ('Verjot-encoder-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($taskScratch) | Out-Null
try {
    $taskZip = Join-Path $taskScratch 'package.zip'
    if ($taskMissing.Count -gt 0) {
        Invoke-WebRequest -UseBasicParsing -Uri $taskPackage -OutFile $taskZip
        Expand-Archive -LiteralPath $taskZip -DestinationPath (Join-Path $taskScratch 'package')
        foreach ($taskName in $taskExpected.Keys) {
            $taskSource = Join-Path $taskScratch ('package\build\' + $taskName)
            if ((Get-FileHash -LiteralPath $taskSource -Algorithm SHA256).Hash -ne $taskExpected[$taskName]) { throw "MP3 encoder SHA256 mismatch: $taskName" }
        }
        foreach ($taskName in $taskExpected.Keys) { Copy-Item -LiteralPath (Join-Path $taskScratch ('package\build\' + $taskName)) -Destination (Join-Path $taskDestination $taskName) -Force }
    }
    if ($IncludeSource) {
        $taskLicenseFolder = Join-Path $taskDestination 'packaging\licenses'
        [IO.Directory]::CreateDirectory($taskLicenseFolder) | Out-Null
        $taskArchive = Join-Path $taskLicenseFolder 'LAME-3.100-source.tar.gz'
        $taskSourceHash = 'DDFE36CAB873794038AE2C1210557AD34857A4B6BDC515785D1DA9E175B1DA1E'
        if (-not (Test-Path -LiteralPath $taskArchive -PathType Leaf) -or (Get-FileHash -LiteralPath $taskArchive).Hash -ne $taskSourceHash) {
            $taskDownload = Join-Path $taskScratch 'source.tar.gz'
            Invoke-WebRequest -UseBasicParsing -Uri 'https://deb.debian.org/debian/pool/main/l/lame/lame_3.100.orig.tar.gz' -OutFile $taskDownload
            if ((Get-FileHash -LiteralPath $taskDownload).Hash -ne $taskSourceHash) { throw 'LAME source SHA256 mismatch' }
            Copy-Item -LiteralPath $taskDownload -Destination $taskArchive -Force
        }
    }
} finally {
    # This exact GUID directory was created by this invocation, under Temp.
    $taskTempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not [IO.Path]::GetFullPath($taskScratch).StartsWith($taskTempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected temporary directory boundary' }
    if (Test-Path -LiteralPath $taskScratch) { Remove-Item -LiteralPath $taskScratch -Recurse -Force }
}
