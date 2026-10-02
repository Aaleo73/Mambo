#Requires -Version 7.0
[CmdletBinding()]
param([switch]$Aot,[switch]$EnvironmentMode)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$outputRoot = if ($Aot) { Join-Path $repoRoot 'publish/aot' } else { Join-Path $repoRoot 'src/Mambo.App/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64' }
$executable = Join-Path $outputRoot 'Mambo.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw '程序尚未构建或发布。' }
$kind = if ($Aot) { 'aot' } else { 'debug' }
if ($EnvironmentMode) { $kind += '-environment' }
$reportPath = Join-Path $repoRoot "artifacts/p1a-fake-$kind.json"
$reportStamp = if (Test-Path -LiteralPath $reportPath) { (Get-Item -LiteralPath $reportPath).LastWriteTimeUtc } else { [DateTime]::MinValue }
$previousFake = $env:MAMBO_FAKE
$previousReport = $env:MAMBO_FAKE_LAB_REPORT
$previousDelay = $env:MAMBO_FAKE_DELAY_MS
$previousFailure = $env:MAMBO_FAKE_FAILURE_RATE
try {
    $env:MAMBO_FAKE_LAB_REPORT = $reportPath
    $env:MAMBO_FAKE_DELAY_MS = '30'
    $env:MAMBO_FAKE_FAILURE_RATE = '0'
    $arguments = @('--fake-smoke')
    if ($EnvironmentMode) { $env:MAMBO_FAKE = '1' } else { $env:MAMBO_FAKE = $null; $arguments += '--fake' }
    $process = Start-Process -FilePath $executable -ArgumentList $arguments -WorkingDirectory $repoRoot -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(30000)) { throw '假数据冒烟超过 30 秒，请关闭测试窗口。' }
    if ($process.ExitCode -ne 0) { throw "假数据进程失败，退出码 $($process.ExitCode)。" }
    if (-not (Test-Path -LiteralPath $reportPath) -or (Get-Item -LiteralPath $reportPath).LastWriteTimeUtc -le $reportStamp) { throw '没有新的假数据诊断报告。' }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if (-not $report.Passed) { throw "假数据冒烟未通过（$($report.Stage)，$($report.ErrorKind)）：$($report.Error)" }
    Write-Host "假数据分页 / 图片 / 播放 / 关闭通过，未加载 libmpv：$reportPath"
} finally {
    $env:MAMBO_FAKE = $previousFake
    $env:MAMBO_FAKE_LAB_REPORT = $previousReport
    $env:MAMBO_FAKE_DELAY_MS = $previousDelay
    $env:MAMBO_FAKE_FAILURE_RATE = $previousFailure
}
