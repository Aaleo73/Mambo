#Requires -Version 7.0
[CmdletBinding()]
param([switch]$Aot)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$kind = if ($Aot) { 'aot' } else { 'debug' }
$reportPath = Join-Path $repoRoot "artifacts/p1-core-$kind.json"
$dataRoot = Join-Path $repoRoot ("artifacts/p1-core-data-" + [guid]::NewGuid().ToString('N'))
$project = Join-Path $repoRoot 'scripts/diagnostics/Mambo.CoreSmoke/Mambo.CoreSmoke.csproj'
if ($Aot) {
    $program = Join-Path $repoRoot 'publish/core-smoke/Mambo.CoreSmoke.exe'
    if (-not (Test-Path -LiteralPath $program)) { throw '请先发布 CoreSmoke AOT 验收工具。' }
    & $program --self-test --data-root $dataRoot --report $reportPath
} else {
    dotnet run --project $project -p:Platform=x64 --no-restore -- --self-test --data-root $dataRoot --report $reportPath
}
if ($LASTEXITCODE -ne 0) { throw 'Core 平台组合自测失败。' }
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
if (-not $report.Passed) { throw "Core 自测未通过（$($report.Stage)，$($report.Error)）。" }
Write-Host "Core 登录 / 恢复 / 缓存 / 设置 / 发件箱 / 注销通过：$reportPath"
