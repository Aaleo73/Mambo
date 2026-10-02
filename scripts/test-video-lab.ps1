#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$SamplePath,
    [switch]$Aot,
    [switch]$MissingLibrary,
    [switch]$NoAudio,
    [switch]$RequireStableResources,
    [ValidateRange(1,100)][int]$Cycles = 20
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $SamplePath) { $SamplePath = Join-Path $repoRoot 'artifacts/samples/jellyfin-4k-hevc-hdr10.mp4' }
$SamplePath = [IO.Path]::GetFullPath($SamplePath)
if (-not (Test-Path -LiteralPath $SamplePath -PathType Leaf)) { throw '本地测试样片不存在，请先运行 fetch-video-sample.ps1。' }
$outputRoot = if ($Aot) { Join-Path $repoRoot 'publish/aot' } else { Join-Path $repoRoot 'src/Mambo.App/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64' }
$executable = Join-Path $outputRoot 'Mambo.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw '程序尚未构建或发布。' }
$kind = if ($Aot) { 'aot' } else { 'debug' }
if ($MissingLibrary) { $kind += '-missing-library' }
if ($NoAudio) { $kind += '-no-audio' }
$reportPath = Join-Path $repoRoot "artifacts/p0-$kind-smoke.json"
$reportStamp = if (Test-Path -LiteralPath $reportPath) { (Get-Item -LiteralPath $reportPath).LastWriteTimeUtc } else { [DateTime]::MinValue }
$previousSample = $env:MAMBO_VIDEO_LAB_SAMPLE
$previousReport = $env:MAMBO_VIDEO_LAB_REPORT
$previousCycles = $env:MAMBO_VIDEO_LAB_CYCLES
$dllPath = Join-Path $outputRoot 'mpv/libmpv-2.dll'
$disabledPath = $dllPath + '.disabled'
$renamed = $false
try {
    $env:MAMBO_VIDEO_LAB_SAMPLE = $SamplePath
    $env:MAMBO_VIDEO_LAB_REPORT = $reportPath
    $env:MAMBO_VIDEO_LAB_CYCLES = $Cycles.ToString()
    if ($MissingLibrary) {
        if (Test-Path -LiteralPath $disabledPath) { throw '存在未恢复的 libmpv DLL，请先恢复。' }
        Move-Item -LiteralPath $dllPath -Destination $disabledPath
        $renamed = $true
    }
    $smokeArguments = @('--smoke')
    if ($NoAudio) { $smokeArguments += '--no-audio' }
    $process = Start-Process -FilePath $executable -ArgumentList $smokeArguments -WorkingDirectory $repoRoot -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(60000)) { throw 'Video Lab 冒烟超过 60 秒，请手动关闭测试窗口。' }
    if ($process.ExitCode -ne 0) { throw "Video Lab 进程失败，退出码 $($process.ExitCode)。" }
    if (-not (Test-Path -LiteralPath $reportPath) -or (Get-Item -LiteralPath $reportPath).LastWriteTimeUtc -le $reportStamp) { throw 'Video Lab 没有写出新的诊断报告。' }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if ($MissingLibrary) {
        if ($report.PipelinePassed -or $report.Error -notlike '*内置播放器组件缺失*') { throw 'DLL 缺失没有得到预期的中文错误。' }
    } elseif (-not $report.PipelinePassed) { throw "Video Lab 画面验证失败：$($report.Error)" }
    if ($RequireStableResources -and -not $report.ResourcesStable) { throw "Video Lab 资源稳定性未通过，详见 $reportPath。" }
    Write-Host "Video Lab 画面或 DLL 缺失检查通过：$reportPath"
    if (-not $MissingLibrary) { Write-Host "资源稳定性：$($report.ResourcesStable)" }
} finally {
    if ($renamed) { Move-Item -LiteralPath $disabledPath -Destination $dllPath }
    $env:MAMBO_VIDEO_LAB_SAMPLE = $previousSample
    $env:MAMBO_VIDEO_LAB_REPORT = $previousReport
    $env:MAMBO_VIDEO_LAB_CYCLES = $previousCycles
}
