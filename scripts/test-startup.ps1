#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AppDirectory,
    [ValidateRange(1, 20)][int]$Runs = 3
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$startupRepository = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$startupRunRoot = Join-Path $startupRepository ('artifacts/startup-validation/' + [Guid]::NewGuid().ToString('N'))
$startupOldFake = $env:MAMBO_FAKE
$startupOldReport = $env:MAMBO_STARTUP_REPORT
$startupRecords = [Collections.Generic.List[object]]::new()
$startupSummary = [ordered]@{
    schemaVersion = 1
    status = 'NotRun'
    reason = $null
    scope = '每轮为新进程、当前 Windows 账号与已有查询快照；不清空操作系统文件缓存，不输出账号身份或内容。'
    startedUtc = [DateTimeOffset]::UtcNow
    completedUtc = $null
    runsRequested = $Runs
    completedRuns = 0
    measuredRuns = 0
    unsupportedRuns = 0
    failedRuns = 0
    targetFailureRuns = 0
    windowTargetMilliseconds = 600
    cachedHomeTargetMilliseconds = 800
    results = @()
}

function Assert-StartupRepositoryPath([string]$Path) {
    if ($Path -match '[\x00-\x1f"]') { throw '启动验收路径无效。' }
    $absolute = [IO.Path]::GetFullPath($Path)
    $boundary = $startupRepository.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $absolute.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw '启动验收路径必须位于本仓库内。' }
    $current = $absolute
    while ($current) {
        if ((Test-Path -LiteralPath $current) -and
            ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw '启动验收不接受重解析点路径。' }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ($parent -eq $current) { break }
        $current = $parent
    }
    return $absolute
}

function Read-StartupNumber($Value) {
    $number = 0L
    if (-not [long]::TryParse([string]$Value, [Globalization.NumberStyles]::Integer, [Globalization.CultureInfo]::InvariantCulture, [ref]$number) -or $number -lt 0) {
        throw [FormatException]::new('启动报告数字字段无效。')
    }
    return $number
}

function Read-StartupReport([string]$Path, $Record) {
    # Read only the explicit diagnostic report. Never read credentials, cache contents or logs.
    $report = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($report.Status -notin @('Measured', 'Unsupported', 'Failed') -or
        $report.Reason -notmatch '^(?:[A-Za-z][A-Za-z0-9_.]{0,95})?$' -or
        $report.WindowTargetMet -isnot [bool] -or $report.CachedHomeTargetMet -isnot [bool]) {
        throw [FormatException]::new('启动报告状态字段无效。')
    }
    $Record.Status = [string]$report.Status
    $Record.Reason = [string]$report.Reason
    $Record.WindowLoadedMilliseconds = Read-StartupNumber $report.WindowLoadedMilliseconds
    $Record.HomeContentMilliseconds = Read-StartupNumber $report.HomeContentMilliseconds
    $Record.RestoredSnapshotsAtHomeContent = Read-StartupNumber $report.RestoredSnapshotsAtHomeContent
    $Record.RestoredHomeCardsVisibleAtFirstContent = Read-StartupNumber $report.RestoredHomeCardsVisibleAtFirstContent
    # Older published probes lack these supplemental diagnostics; null means not collected.
    if ($report.PSObject.Properties['HomeIsLoadedAtFirstContent']) {
        if ($report.HomeIsLoadedAtFirstContent -isnot [bool]) { throw [FormatException]::new('首页布局报告字段无效。') }
        $Record.HomeIsLoadedAtFirstContent = $report.HomeIsLoadedAtFirstContent
    }
    foreach ($countField in @('RealizedHomeCardsAtFirstContent', 'VisibleHomeCardsAtFirstContent')) {
        if ($report.PSObject.Properties[$countField]) { $Record[$countField] = Read-StartupNumber $report.$countField }
    }
    $Record.WindowTargetMet = $report.WindowTargetMet
    $Record.CachedHomeTargetMet = $report.CachedHomeTargetMet
    $Record.Phases = [ordered]@{}
    foreach ($phase in $report.Phases.PSObject.Properties) {
        if ($phase.Name -notmatch '^[A-Za-z][A-Za-z0-9]{0,63}$') { throw [FormatException]::new('启动报告阶段名称无效。') }
        $Record.Phases[$phase.Name] = Read-StartupNumber $phase.Value
    }
    if ($Record.Status -eq 'Measured') {
        if ($Record.WindowLoadedMilliseconds -eq 0 -or $Record.HomeContentMilliseconds -eq 0) { throw [FormatException]::new('启动报告未给出实际采样。') }
        $windowMet = $Record.WindowLoadedMilliseconds -le 600
        $cacheMet = $Record.RestoredHomeCardsVisibleAtFirstContent -gt 0 -and $Record.HomeContentMilliseconds -le 800
        if ($Record.WindowTargetMet -ne $windowMet -or $Record.CachedHomeTargetMet -ne $cacheMet) { throw [FormatException]::new('启动报告目标与数字不一致。') }
        $Record.TargetResult = if ($windowMet -and $cacheMet) { 'Passed' } else { 'ResultsFailed' }
        if ($Record.TargetResult -eq 'ResultsFailed') {
            $Record.TargetReason = if ($Record.RestoredHomeCardsVisibleAtFirstContent -eq 0) { 'NoVisibleRestoredHomeCards' }
                elseif (-not $windowMet -and -not $cacheMet) { 'BothTargetsMissed' }
                elseif (-not $windowMet) { 'WindowTargetMissed' }
                else { 'CachedHomeTargetMissed' }
        }
    }
    else { $Record.TargetResult = $Record.Status }
}

try {
    if (-not $IsWindows) { throw '启动验收需要 Windows。' }
    $AppDirectory = Assert-StartupRepositoryPath $AppDirectory
    $startupExecutable = Assert-StartupRepositoryPath (Join-Path $AppDirectory 'Mambo.exe')
    $startupRunRoot = Assert-StartupRepositoryPath $startupRunRoot
    if (-not (Test-Path -LiteralPath $startupExecutable -PathType Leaf)) { throw '启动验收找不到指定目录的 Mambo.exe。' }
    if (Test-Path -LiteralPath $startupRunRoot) { throw '启动验收轮次目录已存在。' }
    [IO.Directory]::CreateDirectory($startupRunRoot) | Out-Null
    $env:MAMBO_FAKE = $null

    for ($run = 1; $run -le $Runs; $run++) {
        $reportPath = Assert-StartupRepositoryPath (Join-Path $startupRunRoot "run$run.json")
        $record = [ordered]@{
            Run = $run
            Status = 'Failed'
            Reason = 'ReportNotWritten'
            TargetResult = 'Failed'
            TargetReason = $null
            WindowLoadedMilliseconds = 0L
            HomeContentMilliseconds = 0L
            RestoredSnapshotsAtHomeContent = 0L
            RestoredHomeCardsVisibleAtFirstContent = 0L
            HomeIsLoadedAtFirstContent = $null
            RealizedHomeCardsAtFirstContent = $null
            VisibleHomeCardsAtFirstContent = $null
            WindowTargetMet = $false
            CachedHomeTargetMet = $false
            Phases = [ordered]@{}
            ProcessId = $null
            ProcessExited = $false
            ProcessExitCode = $null
            ForcedCleanup = $false
        }
        $process = $null
        $stage = 'ProcessStart'
        try {
            $env:MAMBO_STARTUP_REPORT = $reportPath
            $process = Start-Process -FilePath $startupExecutable -ArgumentList '--startup-smoke' -WorkingDirectory $AppDirectory -WindowStyle Hidden -PassThru
            # Retain this launch's exact handle; no lookup, name scan or tree termination.
            $null = $process.Handle
            $record.ProcessId = $process.Id
            $stage = 'ProcessWait'
            if (-not $process.WaitForExit(45000)) { throw [TimeoutException]::new('启动验收进程超时。') }
            $record.ProcessExited = $true
            $record.ProcessExitCode = $process.ExitCode
            $stage = 'ReportRead'
            if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) { throw [IO.FileNotFoundException]::new('启动验收未生成报告。') }
            $null = Assert-StartupRepositoryPath $reportPath
            Read-StartupReport $reportPath $record
            if ($process.ExitCode -ne 0) { $record.Status = 'Failed'; $record.TargetResult = 'Failed'; $record.Reason = 'ProcessExitNonZero' }
        }
        catch {
            $record.Status = 'Failed'
            $record.TargetResult = 'Failed'
            # Fixed stage/type values cannot reveal a private exception message or stack.
            $record.Reason = $stage + '.' + $_.Exception.GetType().Name
        }
        finally {
            try {
                if ($process -and -not $process.HasExited) {
                    if (-not ([IO.Path]::GetFullPath($process.MainModule.FileName)).Equals($startupExecutable, [StringComparison]::OrdinalIgnoreCase)) {
                        throw [InvalidOperationException]::new('本轮进程身份无法确认。')
                    }
                    $process.Kill($false)
                    $record.ForcedCleanup = $true
                    $record.ProcessExited = $process.WaitForExit(5000)
                    if ($record.ProcessExited) { $record.ProcessExitCode = $process.ExitCode }
                    $record.Status = 'Failed'
                    $record.TargetResult = 'Failed'
                    $record.Reason = 'ProcessWait.TimeoutForcedCleanup'
                }
            }
            catch {
                $record.Status = 'Failed'
                $record.TargetResult = 'Failed'
                $record.Reason = 'OwnedProcessCleanupFailed'
            }
            finally {
                if ($process) { $process.Dispose() }
                $startupRecords.Add($record)
            }
        }
        if ($record.Status -eq 'Measured') {
            Write-Host "启动采样 $run/$Runs：窗口 $($record.WindowLoadedMilliseconds)ms；首页 $($record.HomeContentMilliseconds)ms；恢复快照 $($record.RestoredSnapshotsAtHomeContent)；目标结果 $($record.TargetResult)"
        }
        else { Write-Host "启动采样 $run/$Runs：$($record.Status) / $($record.Reason)；缓存首页目标未完成测量。" }
    }

    $startupSummary.completedRuns = $startupRecords.Count
    $startupSummary.measuredRuns = @($startupRecords | Where-Object { $_.Status -eq 'Measured' }).Count
    $startupSummary.unsupportedRuns = @($startupRecords | Where-Object { $_.Status -eq 'Unsupported' }).Count
    $startupSummary.failedRuns = @($startupRecords | Where-Object { $_.Status -eq 'Failed' }).Count
    $startupSummary.targetFailureRuns = @($startupRecords | Where-Object { $_.TargetResult -eq 'ResultsFailed' }).Count
    $startupSummary.results = @($startupRecords.ToArray())
    $startupSummary.status = if ($startupSummary.failedRuns -gt 0) { 'Failed' }
        elseif ($startupSummary.targetFailureRuns -gt 0) { 'ResultsFailed' }
        elseif ($startupSummary.unsupportedRuns -gt 0) { 'Unsupported' }
        elseif ($startupSummary.measuredRuns -eq $Runs) { 'Passed' }
        else { 'Failed' }
    $startupSummary.reason = switch ($startupSummary.status) {
        'Failed' { 'AtLeastOneRunFailed' }
        'ResultsFailed' { 'AtLeastOneStartupTargetMissed' }
        'Unsupported' { 'CachedStartupNotMeasuredForEveryRun' }
        default { $null }
    }
}
catch {
    $startupSummary.status = 'Failed'
    $startupSummary.reason = 'Script.' + $_.Exception.GetType().Name
    $startupSummary.completedRuns = $startupRecords.Count
    $startupSummary.results = @($startupRecords.ToArray())
}
finally {
    $env:MAMBO_FAKE = $startupOldFake
    $env:MAMBO_STARTUP_REPORT = $startupOldReport
    $startupSummary.completedUtc = [DateTimeOffset]::UtcNow
    if (Test-Path -LiteralPath $startupRunRoot -PathType Container) {
        $summaryPath = Assert-StartupRepositoryPath (Join-Path $startupRunRoot 'summary.json')
        $startupSummary | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $summaryPath -Encoding utf8NoBOM
    }
    $startupSummary | ConvertTo-Json -Depth 7
    Write-Host "启动验收状态：$($startupSummary.status)；数字报告：$startupRunRoot"
}

if ($startupSummary.status -ne 'Passed') { throw "启动验收未通过：$($startupSummary.status) / $($startupSummary.reason)。" }
