#Requires -Version 7.0
[CmdletBinding()]
param([string]$AppDirectory, [switch]$NoBuild)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$filterRepository = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if (-not $AppDirectory) {
    if (-not $NoBuild) {
        & dotnet build (Join-Path $filterRepository 'src/Mambo.App/Mambo.App.csproj') -p:Platform=x64 --no-restore
        if ($LASTEXITCODE -ne 0) { throw '筛选界面诊断构建失败。' }
    }
    $AppDirectory = Join-Path $filterRepository 'src/Mambo.App/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64'
}
$filterRun = Join-Path $filterRepository ('artifacts/library-filters/' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($filterRun)
$filterReport = Join-Path $filterRun 'report.json'
$filterStart = [Diagnostics.ProcessStartInfo]::new([IO.Path]::GetFullPath((Join-Path $AppDirectory 'Mambo.exe')))
$filterStart.UseShellExecute = $false
$filterStart.CreateNoWindow = $true
$filterStart.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$filterStart.WorkingDirectory = $filterRun
$filterStart.ArgumentList.Add('--ui-smoke')
$filterStart.ArgumentList.Add('--library-filters-only')
$filterStart.Environment['MAMBO_UI_LAB_REPORT'] = $filterReport
$filterStart.Environment['MAMBO_FAKE_DELAY_MS'] = '10'
$filterStart.Environment['MAMBO_FAKE_FAILURE_RATE'] = '0'
$filterProcess = [Diagnostics.Process]::Start($filterStart)
$filterClock = [Diagnostics.Stopwatch]::StartNew()
try {
    while (-not $filterProcess.WaitForExit(1000)) {
        if ($filterClock.Elapsed.TotalSeconds -gt 90) { throw '筛选界面诊断超时。' }
    }
    if (-not (Test-Path -LiteralPath $filterReport)) { throw '筛选界面诊断没有生成报告。' }
    $filterResult = Get-Content -LiteralPath $filterReport -Raw | ConvertFrom-Json
    if (-not $filterResult.Passed -or $filterProcess.ExitCode -ne 0) {
        throw "筛选界面诊断未通过：$($filterResult.Stage)，$($filterResult.FailureKind)。报告：$filterReport"
    }
    Write-Host "筛选界面检查通过：$(@($filterResult.Checks.PSObject.Properties).Count) 项。报告：$filterReport"
} finally {
    # 只清理本次启动的诊断进程，不干扰用户正在运行的 Mambo。
    if (-not $filterProcess.HasExited) { $filterProcess.Kill(); $null = $filterProcess.WaitForExit(10000) }
    $filterProcess.Dispose()
}
