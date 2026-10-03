#Requires -Version 7.0
[CmdletBinding()]
param([switch]$Aot, [string]$AppDirectory)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$outputRoot = if ($Aot) { Join-Path $repoRoot 'publish/aot' } else { Join-Path $repoRoot 'src/Mambo.App/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64' }
if ($AppDirectory) { $outputRoot = [IO.Path]::GetFullPath($AppDirectory) }
$executable = Join-Path $outputRoot 'Mambo.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw '程序尚未构建或发布。' }
$kind = if ($Aot) { 'aot' } else { 'debug' }
if ($AppDirectory) { $kind = 'release' }
$reportPath = Join-Path $repoRoot "artifacts/p7-external-ipc-$kind.json"
$reportStamp = if (Test-Path -LiteralPath $reportPath) { (Get-Item -LiteralPath $reportPath).LastWriteTimeUtc } else { [DateTime]::MinValue }
$previousReport = $env:MAMBO_EXTERNAL_LAB_REPORT
$previousFake = $env:MAMBO_FAKE
$process = $null
try {
    $env:MAMBO_EXTERNAL_LAB_REPORT = $reportPath
    $env:MAMBO_FAKE = $null
    $process = Start-Process -FilePath $executable -ArgumentList @('--video-lab', '--p7-smoke') -WorkingDirectory $repoRoot -WindowStyle Hidden -PassThru
    $null = $process.Handle
    if (-not $process.WaitForExit(40000)) {
        throw '外部 IPC 验证超过 40 秒。'
    }
    if ($process.ExitCode -ne 0) { throw "外部 IPC 验证进程失败，退出码 $($process.ExitCode)。" }
    if (-not (Test-Path -LiteralPath $reportPath) -or (Get-Item -LiteralPath $reportPath).LastWriteTimeUtc -le $reportStamp) { throw '外部 IPC 验证没有写出新报告。' }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if (-not $report.Passed) { throw "外部 IPC 验证未通过（$($report.Stage)，$($report.ErrorKind)，$($report.HResult)）。" }
    Write-Host "IPC 初始化 / UTF-8 / 文件选项 / 原生条目标识 / 类型化节点 / 关闭通过：$reportPath"
} finally {
    try {
        if ($process -and -not $process.HasExited) { $process.Kill($false); $null = $process.WaitForExit(5000) }
    } finally {
        try { if ($process) { $process.Dispose() } }
        finally {
            $env:MAMBO_EXTERNAL_LAB_REPORT = $previousReport
            $env:MAMBO_FAKE = $previousFake
        }
    }
}
