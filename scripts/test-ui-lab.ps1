#Requires -Version 7.0
[CmdletBinding()]
param([switch]$Aot, [string]$AppDirectory, [string]$ReportPath, [ValidateRange(1, 5)][int]$MaxAttempts = 3)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$outputRoot = if ($Aot) { Join-Path $repoRoot 'publish/aot' } else { Join-Path $repoRoot 'src/Mambo.App/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64' }
if ($AppDirectory) { $outputRoot = [IO.Path]::GetFullPath($AppDirectory) }
$kind = if ($Aot) { 'aot' } else { 'debug' }
if ($AppDirectory) { $kind = 'release' }
$effectiveReportPath = if ($ReportPath) { [IO.Path]::GetFullPath($ReportPath) } else { Join-Path $repoRoot "artifacts/ui-$kind.json" }
$oldReport = $env:MAMBO_UI_LAB_REPORT
$oldDelay = $env:MAMBO_FAKE_DELAY_MS
$oldFailure = $env:MAMBO_FAKE_FAILURE_RATE
$process = $null

function Get-UiInterruption($Report) {
    # 物理输入要求窗口全程在前台。只认报告自己记下的证据：输入被拒绝，或采样到前台属于其他进程。
    # 普通的检查失败、异常和超时都不算打断，不会因此重试。
    $interference = if ($Report.PSObject.Properties['InputInterference']) { [string]$Report.InputInterference } else { '' }
    $motionKind = if ($null -ne $Report.Motion) { [string]$Report.Motion.FailureKind } else { '' }
    foreach ($kind in @([string]$Report.ErrorKind, $motionKind)) {
        if ($kind -notin @('UiInputForegroundUnavailable', 'UiInputTargetOccluded', 'UiInputKeyAlreadyHeld', 'UiInputButtonAlreadyHeld')) { continue }
        # 前台落在本进程自己的窗口上是程序自身的问题，不能当成外部打断。
        if ($interference -eq 'self' -or $interference.StartsWith('self/', [StringComparison]::Ordinal)) { return $null }
        $who = if ($interference) { "，占用者 $interference" } else { '' }
        return "$kind$who"
    }
    $foreground = if ($Report.PSObject.Properties['Foreground']) { $Report.Foreground } else { $null }
    if ($null -ne $foreground -and $foreground.ForeignSamples -gt 0) {
        return "前台被 $($foreground.FirstHolder) 占用，阶段 $($foreground.FirstStage)"
    }
    return $null
}

function Test-UiOnlyRetainedFailure($Report, [DateTimeOffset]$ProcessStartUtc) {
    # Keep this predicate aligned with UiLabSmoke's Passed expression, except for the two retained counts.
    # A partial report, unexpected exception or any functional failure must never receive this marker.
    if ($Report.Passed -isnot [bool] -or $Report.Passed -or $Report.Stage -ne '界面检查未通过' -or
        $Report.ErrorKind -isnot [string] -or $Report.ErrorKind -ne '' -or
        $Report.ReportWriteErrorKind -isnot [string] -or $Report.ReportWriteErrorKind -ne '') { return $false }
    if ($Report.PSObject.Properties['CollectionFramesDriven'] -and
        ($Report.CollectionFramesDriven -isnot [bool] -or $Report.CollectionFramesDriven)) { return $false }
    $completed = [DateTimeOffset]::MinValue
    # Newer PowerShell may project JSON timestamps as DateTime; preserve its UTC Kind instead of reformatting it.
    if ($Report.CompletedAtUtc -is [DateTimeOffset]) { $completed = $Report.CompletedAtUtc }
    elseif ($Report.CompletedAtUtc -is [DateTime]) { $completed = [DateTimeOffset]$Report.CompletedAtUtc }
    elseif ($Report.CompletedAtUtc -isnot [string] -or
        -not [DateTimeOffset]::TryParse($Report.CompletedAtUtc, [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::None, [ref]$completed)) { return $false }
    if ($completed -lt $ProcessStartUtc) { return $false }
    foreach ($field in @('Home', 'Library', 'Recent', 'Detail', 'Theme', 'Cache', 'Search', 'Overlay',
            'NavigationLocked', 'Controls', 'Fullscreen', 'Closed', 'PlayerPresentationReleased')) {
        if ($Report.$field -isnot [bool] -or -not $Report.$field) { return $false }
    }
    if (($Report.SessionsClosed -isnot [int] -and $Report.SessionsClosed -isnot [long]) -or $Report.SessionsClosed -ne 50 -or
        $Report.LibMpvLoaded -isnot [bool] -or $Report.LibMpvLoaded) { return $false }
    # 较早的发布候选没有这些字段；新增焦点/实际开关帧验收失败不能被归类为仅对象留存。
    foreach ($field in @('FocusRestoresSucceeded', 'OpenedFrameCycles', 'ClosedFrameCycles')) {
        if ($Report.PSObject.Properties[$field] -and
            (($Report.$field -isnot [int] -and $Report.$field -isnot [long]) -or
             $Report.$field -ne 50)) { return $false }
    }
    if ($Report.AnimationsEnabled -isnot [bool]) { return $false }
    foreach ($component in @('PlayerControls', 'Navigation', 'PageRecovery', 'PlaybackRefresh', 'Motion', 'WindowActivation')) {
        if ($null -eq $Report.$component -or $Report.$component.Passed -isnot [bool] -or -not $Report.$component.Passed) { return $false }
    }
    foreach ($field in @('CaptionCloseCancelled', 'CaptionCloseReentryIgnored', 'CaptionCloseConfirmed', 'CaptionCloseCleanupCompleted')) {
        if (-not $Report.PSObject.Properties[$field] -or $Report.$field -isnot [bool] -or -not $Report.$field) { return $false }
    }
    if (@($Report.Accessibility).Count -ne 5 -or
        @($Report.Accessibility | Where-Object { $_.Status -ne 'Passed' }).Count -ne 0) { return $false }
    foreach ($field in @('RetainedPlayers', 'RetainedSurfaces')) {
        if (($Report.$field -isnot [int] -and $Report.$field -isnot [long]) -or $Report.$field -lt 0 -or $Report.$field -gt 50) { return $false }
    }
    return $Report.RetainedPlayers -gt 0 -or $Report.RetainedSurfaces -gt 0
}

try {
    $env:MAMBO_UI_LAB_REPORT = $effectiveReportPath
    $env:MAMBO_FAKE_DELAY_MS = '10'
    $env:MAMBO_FAKE_FAILURE_RATE = '0'
    $report = $null
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        $stamp = if (Test-Path -LiteralPath $effectiveReportPath) { (Get-Item -LiteralPath $effectiveReportPath).LastWriteTimeUtc } else { [DateTime]::MinValue }
        if ($process) { $process.Dispose() }
        $process = Start-Process -FilePath (Join-Path $outputRoot 'Mambo.exe') -ArgumentList '--ui-smoke' -WorkingDirectory $repoRoot -PassThru
        # Retain this launch's process handle; cleanup never looks up or terminates another process by name.
        $null = $process.Handle
        # 应用内看门狗是 300 秒，之后还要走一遍关窗确认；这里多留 30 秒。
        if (-not $process.WaitForExit(330000)) { throw '界面验证超时。' }
        if ($process.ExitCode -ne 0) { throw "界面验证退出码 $($process.ExitCode)。" }
        if (-not (Test-Path -LiteralPath $effectiveReportPath) -or (Get-Item -LiteralPath $effectiveReportPath).LastWriteTimeUtc -le $stamp) { throw '界面验证没有写出新报告。' }
        $report = Get-Content -LiteralPath $effectiveReportPath -Raw | ConvertFrom-Json
        if ($report.Passed) { break }
        $interruption = Get-UiInterruption $report
        if (-not $interruption) { break }
        if ($attempt -eq $MaxAttempts) {
            throw "界面验证连续 $MaxAttempts 次被桌面上的其他窗口打断（$interruption）。运行期间不要操作这台电脑，也不要让其他程序弹出窗口或抢占前台。"
        }
        Write-Warning "界面验证第 $attempt 次被桌面上的其他窗口打断（$interruption），重新运行。"
    }
    if (-not $report.Passed) {
        if (Test-UiOnlyRetainedFailure $report ([DateTimeOffset]$process.StartTime.ToUniversalTime())) {
            # Still fail the UI command. The installer may test its independent lifecycle without declaring UI success.
            $failure = [InvalidOperationException]::new('界面功能完成，但 50 次关闭后的对象释放验收未通过。')
            $failure.Data['UiOnlyRetainedFailure'] = $true
            throw $failure
        }
        $motionStage = if ($null -ne $report.Motion) { $report.Motion.Stage } else { '未开始' }
        $motionFailure = if ($null -ne $report.Motion) { $report.Motion.FailureKind } else { '' }
        throw "界面验证未通过（$($report.Stage)，$($report.ErrorKind)，$($report.HResult)，$($report.FailureStage)，$motionStage，$motionFailure）。"
    }
    if ($report.AnimationsEnabled -isnot [bool] -or $report.Motion.Passed -isnot [bool] -or
        $report.PlayerPresentationReleased -isnot [bool]) { throw '界面报告缺少完整的动效验收字段。' }
    $seconds = [Math]::Round(([DateTimeOffset]$report.CompletedAtUtc - [DateTimeOffset]$report.StartedAtUtc).TotalSeconds)
    Write-Host "页面 / 动效 / 主题 / 播放控制 / 全屏 / 50次关闭通过（用时 $seconds 秒，上限 300 秒）：$effectiveReportPath"
} finally {
    try {
        if ($process -and -not $process.HasExited) {
            try {
                $process.Kill($false)
                $null = $process.WaitForExit(5000)
            } catch [InvalidOperationException] {
                if (-not $process.HasExited) { throw }
            }
        }
    } finally {
        try { if ($process) { $process.Dispose() } }
        finally {
            $env:MAMBO_UI_LAB_REPORT = $oldReport
            $env:MAMBO_FAKE_DELAY_MS = $oldDelay
            $env:MAMBO_FAKE_FAILURE_RATE = $oldFailure
        }
    }
}
