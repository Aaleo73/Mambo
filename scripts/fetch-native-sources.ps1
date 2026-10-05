#Requires -Version 7.0
[CmdletBinding()]
param([switch] $VerifyOnly)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$sources = Get-Content -LiteralPath (Join-Path $repoRoot 'LICENSES/native-sources.lock.json') -Raw | ConvertFrom-Json
$cache = if ($env:MAMBO_NATIVE_SOURCES_CACHE) { [IO.Path]::GetFullPath($env:MAMBO_NATIVE_SOURCES_CACHE) } else { Join-Path $repoRoot 'third_party/libmpv/download/sources' }
if ($VerifyOnly) {
    foreach ($source in $sources) {
        $path = Join-Path $cache $source.filename
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -ne $source.bytes -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $source.sha256) { throw "原生源码缺失或校验失败：$($source.filename)" }
    }
} else {
    $helper = Join-Path $PSScriptRoot 'native-archive.ps1'
    $downloads = @($sources | ForEach-Object -Parallel {
        try {
            . ($using:helper)
            $null = Get-LockedNativeArchive -Record $_ -CacheRoot $using:cache -Kind Source
            [pscustomobject]@{ filename = $_.filename; verified = $true }
        } catch { [pscustomobject]@{ verified = $false } }
    } -ThrottleLimit 8)
    if ($downloads.Count -ne $sources.Count -or @($downloads | Where-Object verified -NE $true).Count) { throw '部分原生源码未完成下载或校验；拒绝生成对应源码包。' }
}
Write-Host "$($sources.Count) 份原生与 Rust 源码归档校验通过。"
