#Requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path $PSScriptRoot -Parent
$mpvRoot = Join-Path $repoRoot 'third_party/libmpv'
$mpvLock = Get-Content -LiteralPath (Join-Path $mpvRoot 'libmpv.lock.json') -Raw | ConvertFrom-Json
if ($mpvLock.schemaVersion -ne 2 -or $mpvLock.provider -ne 'msys2-ucrt64') { throw '不支持的 libmpv lock 版本或来源。' }
$downloadRoot = if ($env:MAMBO_LIBMPV_CACHE) { [IO.Path]::GetFullPath($env:MAMBO_LIBMPV_CACHE) } else { Join-Path $mpvRoot 'download/packages' }
$helper = Join-Path $PSScriptRoot 'native-archive.ps1'
$downloads = @($mpvLock.packages | ForEach-Object -Parallel {
    try {
        . ($using:helper)
        $null = Get-LockedNativeArchive -Record $_ -CacheRoot $using:downloadRoot -Kind Binary
        [pscustomobject]@{ filename = $_.filename; verified = $true }
    } catch { [pscustomobject]@{ verified = $false } }
} -ThrottleLimit 8)
if ($downloads.Count -ne $mpvLock.packages.Count -or @($downloads | Where-Object verified -NE $true).Count) { throw '部分原生组件未完成下载或校验；拒绝提取运行时。' }
$extractRoot = Join-Path $mpvRoot 'download/verified-runtime'
New-Item -ItemType Directory -Path $extractRoot -Force | Out-Null
foreach ($package in $mpvLock.packages) {
    $dlls = @($mpvLock.files | Where-Object package -EQ $package.name)
    $headers = @($mpvLock.headers | Where-Object package -EQ $package.name)
    $members = @($dlls | ForEach-Object member) + @($headers | ForEach-Object member)
    foreach ($member in $members) { if ($member -notmatch '^ucrt64/(bin/[A-Za-z0-9._+-]+\.dll|include/mpv/[A-Za-z0-9_]+\.h)$') { throw '原生文件路径无效。' } }
    & "$env:SystemRoot/System32/tar.exe" -xf (Join-Path $downloadRoot $package.filename) -C $extractRoot @members
    if ($LASTEXITCODE -ne 0) { throw "原生组件解压失败：$($package.name)" }
    foreach ($file in @($dlls) + @($headers)) {
        $member = $file.member
        $path = Join-Path $extractRoot $member
        if ((Get-Item -LiteralPath $path).Length -ne $file.bytes -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256) { throw "原生文件与 lock 不匹配：$($file.path)" }
    }
}
New-Item -ItemType Directory -Path (Join-Path $mpvRoot 'bin'), (Join-Path $mpvRoot 'include/mpv') -Force | Out-Null
foreach ($file in $mpvLock.files) { Copy-Item -LiteralPath (Join-Path $extractRoot $file.member) -Destination (Join-Path $mpvRoot ('bin/' + $file.path)) -Force }
foreach ($file in $mpvLock.headers) { Copy-Item -LiteralPath (Join-Path $extractRoot $file.member) -Destination (Join-Path $mpvRoot ('include/mpv/' + $file.path)) -Force }
& (Join-Path $PSScriptRoot 'verify-native-runtime.ps1')
Write-Host "libmpv $($mpvLock.release) 与 $($mpvLock.files.Count) 个 DLL 已校验并就绪。"
