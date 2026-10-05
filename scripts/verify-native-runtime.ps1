#Requires -Version 7.0
[CmdletBinding()]
param([string] $Directory)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $Directory) { $Directory = Join-Path $repoRoot 'third_party/libmpv/bin' }
$lock = Get-Content -LiteralPath (Join-Path $repoRoot 'third_party/libmpv/libmpv.lock.json') -Raw | ConvertFrom-Json
if ($lock.schemaVersion -ne 2) { throw '原生运行时 lock 版本无效。' }
$names = @($lock.files.path)
$actual = @(Get-ChildItem -LiteralPath $Directory -File -Filter '*.dll' | ForEach-Object Name)
if (@(Compare-Object $names $actual).Count -ne 0) { throw '原生运行时 DLL 集合与 lock 不一致。' }
foreach ($file in $lock.files) {
    $path = Join-Path $Directory $file.path
    if ($file.path -notmatch '^[a-z0-9][a-z0-9._+-]+\.dll$' -or (Get-Item -LiteralPath $path).Length -ne $file.bytes -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256) { throw '原生运行时文件校验失败。' }
    foreach ($dependency in $file.imports) {
        if ($dependency -notin $names -and $dependency -notin $lock.systemImports) { throw '原生运行时有未记录的 DLL 依赖。' }
    }
}
