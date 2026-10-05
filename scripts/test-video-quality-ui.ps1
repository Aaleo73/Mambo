#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$AppDirectory,
    [switch]$NoBuild,
    [ValidateRange(30, 600)][int]$TimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$qualityRepository = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if (-not $AppDirectory) {
    if (-not $NoBuild) {
        & dotnet build (Join-Path $qualityRepository 'src/Mambo.App/Mambo.App.csproj') -p:Platform=x64 --no-restore -p:NuGetAudit=false
        if ($LASTEXITCODE -ne 0) { throw '画质界面诊断构建失败。' }
    }
    $AppDirectory = Join-Path $qualityRepository 'src/Mambo.App/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64'
}
$qualityExecutable = [IO.Path]::GetFullPath((Join-Path $AppDirectory 'Mambo.exe'))
if (-not (Test-Path -LiteralPath $qualityExecutable -PathType Leaf)) { throw '诊断程序尚未构建或发布。' }
$qualityRunId = [Guid]::NewGuid().ToString('N')
$qualityRoot = Join-Path $qualityRepository ('artifacts/video-quality-ui/' + $qualityRunId)
$null = [IO.Directory]::CreateDirectory($qualityRoot)
$qualityReportPath = Join-Path $qualityRoot 'app-report.json'
$qualityResultPath = Join-Path $qualityRoot 'result.json'
$qualityResult = [ordered]@{
    Status = 'Failed'
    Reason = 'NotStarted'
    RunId = $qualityRunId
    ProcessId = $null
    ProcessExited = $false
    ExitCode = $null
    ForcedCleanup = $false
    ErrorKind = $null
    ElapsedMilliseconds = 0
    DataIsolation = 'InMemoryFakeServices'
    ReportPath = $qualityReportPath
    Report = $null
}
$qualityProcess = $null
$qualityClock = [Diagnostics.Stopwatch]::StartNew()
try {
    $qualityStart = [Diagnostics.ProcessStartInfo]::new($qualityExecutable)
    $qualityStart.UseShellExecute = $false
    $qualityStart.CreateNoWindow = $true
    $qualityStart.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $qualityStart.WorkingDirectory = $qualityRoot
    $qualityStart.ArgumentList.Add('--video-quality-ui-smoke')
    foreach ($qualityEnvironmentName in @('MAMBO_FAKE', 'MAMBO_UI_LAB_REPORT', 'MAMBO_STARTUP_REPORT', 'MAMBO_BULLET_CHAT_REPORT', 'MAMBO_FAKE_LIFETIME_REPORT_DIR')) {
        $null = $qualityStart.Environment.Remove($qualityEnvironmentName)
    }
    $qualityStart.Environment['MAMBO_VIDEO_QUALITY_UI_REPORT'] = $qualityReportPath
    $qualityStart.Environment['MAMBO_FAKE_DELAY_MS'] = '10'
    $qualityStart.Environment['MAMBO_FAKE_FAILURE_RATE'] = '0'
    $qualityProcess = [Diagnostics.Process]::Start($qualityStart)
    # 保留此次 CreateProcess 返回的句柄，清理时不按进程名查找，也不影响正常实例。
    $null = $qualityProcess.Handle
    $qualityResult.ProcessId = $qualityProcess.Id
    while (-not $qualityProcess.WaitForExit(1000)) {
        if ($qualityClock.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
            $qualityResult.Reason = 'ProcessDeadlineExceeded'
            throw [TimeoutException]::new('画质界面诊断超时。')
        }
    }
    $qualityResult.ProcessExited = $true
    $qualityResult.ExitCode = $qualityProcess.ExitCode
    if (-not (Test-Path -LiteralPath $qualityReportPath -PathType Leaf)) {
        $qualityResult.Reason = 'ReportMissing'
        throw [InvalidOperationException]::new('未生成本轮诊断报告。')
    }
    $qualityResult.Report = Get-Content -LiteralPath $qualityReportPath -Raw | ConvertFrom-Json
    if ($qualityResult.Report.Passed -and $qualityResult.ExitCode -eq 0) {
        $qualityResult.Status = 'Passed'
        $qualityResult.Reason = 'Passed'
    } else {
        $qualityResult.Reason = 'AppCheckFailed'
    }
} catch {
    $qualityResult.ErrorKind = $_.Exception.GetType().Name
    if ($qualityResult.Reason -eq 'NotStarted') { $qualityResult.Reason = 'RunnerFailed' }
} finally {
    if ($qualityProcess) {
        if (-not $qualityProcess.HasExited) {
            try {
                $qualityProcess.Kill()
                $qualityResult.ForcedCleanup = $true
                $qualityResult.ProcessExited = $qualityProcess.WaitForExit(10000)
                if ($qualityResult.ProcessExited) { $qualityResult.ExitCode = $qualityProcess.ExitCode }
            } catch {
                $qualityResult.ErrorKind = $_.Exception.GetType().Name
            }
        }
        $qualityProcess.Dispose()
    }
    $qualityResult.ElapsedMilliseconds = $qualityClock.ElapsedMilliseconds
    $qualityResult | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $qualityResultPath -Encoding utf8
}
[pscustomobject]$qualityResult
if ($qualityResult.Status -ne 'Passed') { exit 1 }
