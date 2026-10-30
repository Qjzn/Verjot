#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Helper, [Parameter(Mandatory=$true)][string]$Output, [ValidateSet('render','start','blocked','launch-error')][string]$Action='render')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$taskUiTimer = New-Object Windows.Forms.Timer
$taskUiTimer.Interval = 300
$taskUiResult = @{ captured=$false; failure=$null; clicked=$false; clickTime=$null }
$taskUiClock = [Diagnostics.Stopwatch]::StartNew()
$taskUiTimer.Add_Tick({
    $taskForm = @([Windows.Forms.Application]::OpenForms | Where-Object { $_.Text -eq 'Verlog · 下载推荐模型' }) | Select-Object -First 1
    if (-not $taskForm) { return }
    try {
        $taskButton = $taskForm.Controls.Find('download_recommended', $true)
        if ($taskButton.Count -ne 1 -or -not $taskButton[0].Visible -or (-not $taskButton[0].Enabled -and -not $taskUiResult.clicked)) { throw 'Download action missing/invisible' }
        if ($Action -ne 'render') {
            if ($taskUiClock.Elapsed.TotalSeconds -gt 15) { throw 'Actual model action did not publish a worker result' }
            if ($taskUiClock.Elapsed.TotalSeconds -lt 1.5) { return }
            if (-not $taskUiResult.clicked) {
                $taskUiResult.clicked=$true; $taskUiResult.clickTime=$taskUiClock.Elapsed.TotalSeconds
                $taskButton[0].PerformClick()
                return
            }
            if ($taskUiClock.Elapsed.TotalSeconds - $taskUiResult.clickTime -lt 2.5) { return }
            if ($Action -eq 'start') {
                if (-not (Test-Path -LiteralPath (Join-Path (Split-Path $Helper) 'installation\clicked-worker.txt'))) { throw 'Click did not launch the actual worker/installer' }
                $taskStatus=Get-Content -LiteralPath (Join-Path (Split-Path $Helper) 'installation\recommended-model.json') -Raw -Encoding UTF8 | ConvertFrom-Json
                if ($taskStatus.state -ne 'completed') { return }
            } elseif ($Action -eq 'blocked') {
                $taskVisibleMessage=@($taskForm.Controls[0].Controls | Where-Object { $_ -is [Windows.Forms.Label] })[-1].Text
                if ($taskVisibleMessage -notlike '*请先结束录音*') { throw ('Blocked action reason was hidden by stale status: ' + $taskVisibleMessage) }
            } else {
                $taskVisibleMessage=@($taskForm.Controls[0].Controls | Where-Object { $_ -is [Windows.Forms.Label] })[-1].Text
                if ($taskVisibleMessage -notlike '*检查进程未能启动*' -or -not $taskButton[0].Enabled) { throw ('Dead worker must expose a retry instead of waiting forever: ' + $taskVisibleMessage) }
                if ($taskButton[0].Text -ne '重试检查' -or @($taskForm.Controls[0].Controls | Where-Object { $_ -is [Windows.Forms.ProgressBar] -and $_.Visible }).Count -ne 0) { throw 'Dead worker must clear the busy action and progress indicator' }
            }
        }
        $taskLabels = @($taskForm.Controls[0].Controls | Where-Object { $_ -is [Windows.Forms.Label] })
        foreach ($taskLabel in $taskLabels) {
            $taskNeeded = [Windows.Forms.TextRenderer]::MeasureText($taskLabel.Text, $taskLabel.Font, (New-Object Drawing.Size($taskLabel.Width, [int]::MaxValue)), [Windows.Forms.TextFormatFlags]::WordBreak)
            if ($taskNeeded.Height -gt $taskLabel.Height) { throw ('Download helper label is clipped: ' + $taskLabel.Size + '; needs ' + $taskNeeded) }
        }
        if ($taskLabels[0].Bounds.IntersectsWith($taskLabels[1].Bounds)) { throw 'Download helper labels overlap' }
        $taskImage = New-Object Drawing.Bitmap($taskForm.Width, $taskForm.Height)
        try { $taskForm.DrawToBitmap($taskImage, (New-Object Drawing.Rectangle(0,0,$taskForm.Width,$taskForm.Height))); $taskImage.Save($Output) } finally { $taskImage.Dispose() }
        $taskUiResult.captured=$true
    } catch { $taskUiResult.failure=$_.Exception.Message }
    finally { if ($taskUiResult.captured -or $taskUiResult.failure) { $taskUiTimer.Stop(); $taskForm.Close() } }
})
$taskUiTimer.Start()
try { & $Helper } finally { $taskUiTimer.Dispose() }
if ($taskUiResult.failure) { throw $taskUiResult.failure }
if (-not $taskUiResult.captured) { throw 'Actual helper UI did not render' }
Write-Output "PASS: actual recommended-model helper UI ($Action) on isolated desktop; no audio or clipboard."
