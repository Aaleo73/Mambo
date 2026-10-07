#Requires -Version 7.0
[CmdletBinding()]
param(
    [switch]$Aot,
    [switch]$KeyboardInput,
    [string]$AppDirectory,
    [ValidateRange(30, 600)][int]$TimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$controlsRepository = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if (-not $AppDirectory) {
    $AppDirectory = if ($Aot) { Join-Path $controlsRepository 'publish/aot' } else {
        Join-Path $controlsRepository 'src/Mambo.App/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64'
    }
}
$controlsExecutable = [IO.Path]::GetFullPath((Join-Path $AppDirectory 'Mambo.exe'))
if (-not (Test-Path -LiteralPath $controlsExecutable -PathType Leaf)) { throw '请先构建或发布播放控件诊断程序。' }
$controlsRunRoot = Join-Path $controlsRepository ('artifacts/player-controls/' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($controlsRunRoot)
$controlsReportPath = Join-Path $controlsRunRoot 'report.json'
$controlsProcess = $null
$controlsClock = [Diagnostics.Stopwatch]::StartNew()
try {
    $controlsStart = [Diagnostics.ProcessStartInfo]::new($controlsExecutable)
    $controlsStart.UseShellExecute = $false
    $controlsStart.CreateNoWindow = $true
    $controlsStart.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $controlsStart.WorkingDirectory = $controlsRunRoot
    $controlsStart.ArgumentList.Add('--player-controls-smoke')
    foreach ($controlsEnvironmentName in @('MAMBO_UI_LAB_REPORT', 'MAMBO_STARTUP_REPORT', 'MAMBO_BULLET_CHAT_REPORT', 'MAMBO_VIDEO_QUALITY_UI_REPORT', 'MAMBO_FAKE_LIFETIME_REPORT_DIR')) {
        $null = $controlsStart.Environment.Remove($controlsEnvironmentName)
    }
    $controlsStart.Environment['MAMBO_FAKE'] = '1'
    $controlsStart.Environment['MAMBO_PLAYER_CONTROLS_REPORT'] = $controlsReportPath
    $controlsStart.Environment['MAMBO_PLAYER_KEYBOARD_INPUT'] = if ($KeyboardInput) { '1' } else { '0' }
    $controlsStart.Environment['MAMBO_FAKE_DELAY_MS'] = '10'
    $controlsStart.Environment['MAMBO_FAKE_FAILURE_RATE'] = '0'
    $controlsProcess = [Diagnostics.Process]::Start($controlsStart)
    # 只清理本次 CreateProcess 返回的进程句柄，绝不按应用名称终止其它实例。
    $null = $controlsProcess.Handle
    while (-not $controlsProcess.WaitForExit(1000)) {
        if ($controlsClock.Elapsed.TotalSeconds -ge $TimeoutSeconds) { throw [TimeoutException]::new('播放控件诊断超时。') }
    }
    if (-not (Test-Path -LiteralPath $controlsReportPath -PathType Leaf)) { throw '播放控件诊断没有生成本轮报告。' }
    $controlsReport = Get-Content -LiteralPath $controlsReportPath -Raw | ConvertFrom-Json
    $controlsPassed = $controlsProcess.ExitCode -eq 0 -and $controlsReport.Passed -eq $true -and
        $controlsReport.Status -eq 'Passed' -and $controlsReport.SessionClosed -eq $true -and
        @($controlsReport.Checks.PSObject.Properties | Where-Object { $_.Value -ne $true }).Count -eq 0
    [pscustomobject]@{
        Passed = $controlsPassed
        ExitCode = $controlsProcess.ExitCode
        Stage = $controlsReport.Stage
        Reason = $controlsReport.Reason
        Scope = $controlsReport.Scope
        ElapsedMilliseconds = $controlsClock.ElapsedMilliseconds
        ReportPath = $controlsReportPath
    }
    if (-not $controlsPassed) { throw '播放控件诊断未通过，请查看本轮报告。' }
}
finally {
    if ($controlsProcess) {
        if (-not $controlsProcess.HasExited) {
            $controlsProcess.Kill()
            $null = $controlsProcess.WaitForExit(10000)
        }
        $controlsProcess.Dispose()
    }
}
