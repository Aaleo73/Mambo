#Requires -Version 7.0
[CmdletBinding()]
param([string]$AppDirectory, [switch]$NoBuild)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if (-not $AppDirectory) {
    if (-not $NoBuild) {
        & dotnet build (Join-Path $repository 'src/Mambo.App/Mambo.App.csproj') -p:Platform=x64 --no-restore
        if ($LASTEXITCODE -ne 0) { throw '输入光标诊断构建失败。' }
    }
    $AppDirectory = Join-Path $repository 'src/Mambo.App/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64'
}
$run = Join-Path $repository ('artifacts/input-caret/' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($run)
$report = Join-Path $run 'report.json'
$start = [Diagnostics.ProcessStartInfo]::new([IO.Path]::GetFullPath((Join-Path $AppDirectory 'Mambo.exe')))
$start.UseShellExecute = $false
$start.WorkingDirectory = $run
$start.ArgumentList.Add('--ui-smoke')
$start.ArgumentList.Add('--input-caret-only')
$start.Environment['MAMBO_UI_LAB_REPORT'] = $report
$start.Environment['MAMBO_FAKE_DELAY_MS'] = '10'
$start.Environment['MAMBO_FAKE_FAILURE_RATE'] = '0'
$process = [Diagnostics.Process]::Start($start)
$clock = [Diagnostics.Stopwatch]::StartNew()
try {
    while (-not $process.WaitForExit(1000)) {
        if ($clock.Elapsed.TotalSeconds -gt 200) { throw '输入光标诊断超时。' }
    }
    if (-not (Test-Path -LiteralPath $report)) { throw '输入光标诊断没有生成报告。' }
    $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    if (-not $result.Passed -or $process.ExitCode -ne 0) {
        throw "输入光标诊断未通过：$($result.Stage)，$($result.FailureKind)。报告：$report"
    }
    Write-Host "输入光标检查通过：$(@($result.Checks.PSObject.Properties).Count) 项。报告：$report"
} finally {
    # 只清理本次假数据进程，不触及用户正在运行的实例。
    if (-not $process.HasExited) { $process.Kill(); $null = $process.WaitForExit(10000) }
    $process.Dispose()
}
