#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$MpvPath,
    [Parameter(Mandatory)][string]$SamplePath,
    [string]$AppDirectory
)
$ErrorActionPreference = 'Stop'
$handoffRepo = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$handoffMpv = [IO.Path]::GetFullPath($MpvPath)
if (Test-Path -LiteralPath $handoffMpv -PathType Container) { $handoffMpv = Join-Path $handoffMpv 'mpv.exe' }
$handoffSample = [IO.Path]::GetFullPath($SamplePath)
if (-not (Test-Path -LiteralPath $handoffMpv -PathType Leaf) -or -not (Test-Path -LiteralPath $handoffSample -PathType Leaf)) { throw 'MPV 或本地片源不存在。' }
if (-not $AppDirectory) { $AppDirectory = Join-Path $handoffRepo 'src/Mambo.App/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64' }
$handoffExecutable = Join-Path ([IO.Path]::GetFullPath($AppDirectory)) 'Mambo.exe'
$handoffRoot = Join-Path $handoffRepo ('artifacts/external-handoff/' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($handoffRoot)
$handoffReport = Join-Path $handoffRoot 'result.json'
$handoffPrevious = @{}
foreach ($name in @('MAMBO_EXTERNAL_MPV_PATH', 'MAMBO_EXTERNAL_SAMPLE', 'MAMBO_EXTERNAL_REPORT')) { $handoffPrevious[$name] = [Environment]::GetEnvironmentVariable($name) }
$handoffProcess = $null
try {
    $env:MAMBO_EXTERNAL_MPV_PATH = $handoffMpv
    $env:MAMBO_EXTERNAL_SAMPLE = $handoffSample
    $env:MAMBO_EXTERNAL_REPORT = $handoffReport
    $handoffProcess = Start-Process -FilePath $handoffExecutable -ArgumentList '--external-handoff-smoke' -WorkingDirectory $handoffRepo -WindowStyle Hidden -PassThru
    $null = $handoffProcess.Handle
    if (-not $handoffProcess.WaitForExit(100000)) { throw '外置接管验证超时。' }
    if (-not (Test-Path -LiteralPath $handoffReport -PathType Leaf)) { throw "外置接管验证未写出报告，退出码 $($handoffProcess.ExitCode)。" }
    $handoffResult = Get-Content -LiteralPath $handoffReport -Raw | ConvertFrom-Json
    if (-not $handoffResult.Passed) { throw "外置接管验证未通过：$($handoffResult.Stage) / $($handoffResult.ErrorKind)。报告：$handoffReport" }
    if ($handoffProcess.ExitCode -ne 0) { throw "外置接管验证退出码 $($handoffProcess.ExitCode)。" }
    Write-Host "外置 MPV 配置 / 控制 / 连播 / 浏览页面 / 退出清理通过：$handoffReport"
} finally {
    if ($handoffProcess -and -not $handoffProcess.HasExited) { $handoffProcess.Kill($true); $null = $handoffProcess.WaitForExit(5000) }
    if ($handoffProcess) { $handoffProcess.Dispose() }
    foreach ($name in $handoffPrevious.Keys) { [Environment]::SetEnvironmentVariable($name, $handoffPrevious[$name]) }
}
