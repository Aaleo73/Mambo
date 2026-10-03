#Requires -Version 7.0
[CmdletBinding()]
param([switch]$Aot, [string]$AppDirectory, [string]$ReportPath)
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
$stamp = if (Test-Path -LiteralPath $effectiveReportPath) { (Get-Item -LiteralPath $effectiveReportPath).LastWriteTimeUtc } else { [DateTime]::MinValue }
$process = $null

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
            'NavigationLocked', 'Controls', 'Fullscreen', 'Closed')) {
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
    foreach ($component in @('PlayerControls', 'Navigation', 'PageRecovery', 'PlaybackRefresh')) {
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
    $process = Start-Process -FilePath (Join-Path $outputRoot 'Mambo.exe') -ArgumentList '--ui-smoke' -WorkingDirectory $repoRoot -WindowStyle Hidden -PassThru
    # Retain this launch's process handle; cleanup never looks up or terminates another process by name.
    $null = $process.Handle
    if (-not $process.WaitForExit(210000)) { throw '界面验证超时。' }
    if ($process.ExitCode -ne 0) { throw "界面验证退出码 $($process.ExitCode)。" }
    if (-not (Test-Path -LiteralPath $effectiveReportPath) -or (Get-Item -LiteralPath $effectiveReportPath).LastWriteTimeUtc -le $stamp) { throw '界面验证没有写出新报告。' }
    $report = Get-Content -LiteralPath $effectiveReportPath -Raw | ConvertFrom-Json
    if (-not $report.Passed) {
        if (Test-UiOnlyRetainedFailure $report ([DateTimeOffset]$process.StartTime.ToUniversalTime())) {
            # Still fail the UI command. The installer may test its independent lifecycle without declaring UI success.
            $failure = [InvalidOperationException]::new('界面功能完成，但 50 次关闭后的对象释放验收未通过。')
            $failure.Data['UiOnlyRetainedFailure'] = $true
            throw $failure
        }
        throw "界面验证未通过（$($report.Stage)，$($report.ErrorKind)，$($report.HResult)）。"
    }
    Write-Host "页面 / 主题 / 播放控制 / 全屏 / 50次关闭通过：$effectiveReportPath"
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
