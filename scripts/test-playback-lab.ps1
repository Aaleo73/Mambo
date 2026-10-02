#Requires -Version 7.0
[CmdletBinding()]
param([string]$SamplePath, [switch]$Aot, [switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $SamplePath) { $SamplePath = Join-Path $repoRoot 'artifacts/samples/jellyfin-4k-hevc-hdr10.mp4' }
$SamplePath = [IO.Path]::GetFullPath($SamplePath)
if (-not (Test-Path -LiteralPath $SamplePath -PathType Leaf)) { throw '本地测试样片不存在。' }
if (-not $Aot -and -not $NoBuild) {
    & dotnet build (Join-Path $repoRoot 'src/Mambo.App/Mambo.App.csproj') -p:Platform=x64 --no-restore -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw '播放验证程序构建失败。' }
}
$outputRoot = if ($Aot) { Join-Path $repoRoot 'publish/aot' } else { Join-Path $repoRoot 'src/Mambo.App/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64' }
$executable = Join-Path $outputRoot 'Mambo.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw '程序尚未构建或发布。' }
$kind = if ($Aot) { 'aot' } else { 'debug' }
$reportPath = Join-Path $repoRoot "artifacts/p3-playback-$kind.json"
$reportStamp = if (Test-Path -LiteralPath $reportPath) { (Get-Item -LiteralPath $reportPath).LastWriteTimeUtc } else { [DateTime]::MinValue }
$previousSample = $env:MAMBO_PLAYBACK_LAB_SAMPLE
$previousReport = $env:MAMBO_PLAYBACK_LAB_REPORT
$previousFake = $env:MAMBO_FAKE
try {
    $env:MAMBO_PLAYBACK_LAB_SAMPLE = $SamplePath
    $env:MAMBO_PLAYBACK_LAB_REPORT = $reportPath
    $env:MAMBO_FAKE = $null
    $process = Start-Process -FilePath $executable -ArgumentList @('--p3-smoke') -WorkingDirectory $repoRoot -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(60000)) { throw '播放验证超过 60 秒，请关闭测试窗口。' }
    if ($process.ExitCode -ne 0) { throw "播放验证进程失败，退出码 $($process.ExitCode)。" }
    if (-not (Test-Path -LiteralPath $reportPath) -or (Get-Item -LiteralPath $reportPath).LastWriteTimeUtc -le $reportStamp) { throw '播放验证没有写出新报告。' }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if (-not $report.Passed) { throw "播放验证未通过（$($report.Stage)，$($report.ErrorKind)，$($report.HResult)）。" }
    Write-Host "真实播放 / 连播 / 上报 / 画面解绑通过：$reportPath"
} finally {
    $env:MAMBO_PLAYBACK_LAB_SAMPLE = $previousSample
    $env:MAMBO_PLAYBACK_LAB_REPORT = $previousReport
    $env:MAMBO_FAKE = $previousFake
}
