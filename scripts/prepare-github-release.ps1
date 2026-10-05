#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')] [string]$Version,
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')] [string]$UpdateRepository = 'Aaleo73/Mambo',
    [switch]$IncludeBinaries
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$readiness = Get-Content -LiteralPath (Join-Path $repoRoot 'LICENSES/release-readiness.json') -Raw | ConvertFrom-Json
if ($IncludeBinaries -and ($readiness.schemaVersion -ne 1 -or $readiness.binaryDistributionApproved -ne $true)) {
    throw '完整原生对应源码尚未通过审查，不能公开发布二进制。详见 LICENSES/release-readiness.json。'
}
if ($IncludeBinaries) {
    foreach ($binding in @(@{ path = 'third_party/libmpv/libmpv.lock.json'; hash = $readiness.runtimeLockSha256 }, @{ path = 'LICENSES/native-sources.lock.json'; hash = $readiness.sourceLockSha256 })) {
        if ((Get-FileHash -LiteralPath (Join-Path $repoRoot $binding.path) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $binding.hash) { throw '原生依赖已改变，必须重新核验发布证据。' }
    }
}
$output = Join-Path $repoRoot 'artifacts/github-release'
if (Test-Path -LiteralPath $output) { throw '发布暂存目录已存在，请使用新的工作区或手动归档旧目录。' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
Push-Location $repoRoot
try {
    if (@(& git status --porcelain).Count -ne 0) { throw '发布必须使用已提交且干净的源码。' }
    if ($IncludeBinaries) {
        $package = & (Join-Path $PSScriptRoot 'publish.ps1') -Version $Version -Installer -UpdateRepository $UpdateRepository
        foreach ($file in @($package.PortableZip, $package.SourceZip, $package.Installer) + @($package.NativeSourceBundles.path)) {
            Copy-Item -LiteralPath $file -Destination $output
        }
        $notes = "Windows x64 稳定版。安装版可以在设置页检查、下载并安装更新。`n`n安装请下载 setup.exe；免安装请下载 portable.zip。源码由 sources.zip 和全部 native-sources 分包组成，SHA256SUMS.txt 提供校验值。`n`n已改用有匹配源码、补丁及构建记录的 MSYS2 libmpv 组件；原生依赖和 Rust 源码随本 Release 提供。"
    } else {
        & git archive --format=zip "--output=$(Join-Path $output "Mambo-$Version-sources.zip")" HEAD
        if ($LASTEXITCODE -ne 0) { throw '源码归档失败。' }
        $notes = "本次仅发布 Mambo 开发源码。`n`n完整 libmpv 对应源码尚未补齐，暂无安装包和便携包。应用会提示新版尚未提供 Windows x64 安装包。详见仓库 LICENSES/release-readiness.json 与 docs/decisions/P8-packaging.md。"
    }
} finally { Pop-Location }
$checksums = @(Get-ChildItem -LiteralPath $output -File | Sort-Object Name | ForEach-Object {
    (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.Name
})
$checksums | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding utf8NoBOM
$notes | Set-Content -LiteralPath (Join-Path $repoRoot 'artifacts/github-release-notes.md') -Encoding utf8NoBOM
