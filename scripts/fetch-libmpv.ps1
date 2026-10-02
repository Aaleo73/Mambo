#Requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$mpvRoot = Join-Path $repoRoot 'third_party/libmpv'
$lockPath = Join-Path $mpvRoot 'libmpv.lock.json'
$mpvLock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
if ($mpvLock.schemaVersion -ne 1) { throw '不支持的 libmpv lock 版本。' }
$releaseUri = [Uri]$mpvLock.url
if ($releaseUri.Scheme -ne 'https' -or $releaseUri.Host -ne 'github.com' -or $releaseUri.AbsolutePath -notlike '/shinchiro/mpv-winbuild-cmake/releases/download/*') { throw 'libmpv 下载地址必须来自指定的 GitHub Releases。' }
if (([bool]$mpvLock.archiveSha256) -ne ([bool]$mpvLock.dllSha256)) { throw 'libmpv lock 校验值不完整。' }
$downloadRoot = if ($env:MAMBO_LIBMPV_CACHE) { [IO.Path]::GetFullPath($env:MAMBO_LIBMPV_CACHE) } else { Join-Path $mpvRoot 'download' }
$archivePath = Join-Path $downloadRoot ([IO.Path]::GetFileName($releaseUri.AbsolutePath))
New-Item -ItemType Directory -Path $downloadRoot -Force | Out-Null
if (-not (Test-Path -LiteralPath $archivePath)) {
    $partialPath = $archivePath + '.partial'
    Invoke-WebRequest -Uri $releaseUri -OutFile $partialPath
    Move-Item -LiteralPath $partialPath -Destination $archivePath -Force
}
$archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($mpvLock.archiveSha256 -and $archiveHash -ne $mpvLock.archiveSha256) { throw 'libmpv 压缩包 SHA-256 校验失败；请检查或清除下载缓存。' }
$extractRoot = Join-Path $mpvRoot 'download/extracted'
New-Item -ItemType Directory -Path $extractRoot -Force | Out-Null
& "$env:SystemRoot/System32/tar.exe" -xf $archivePath -C $extractRoot libmpv-2.dll include/mpv
if ($LASTEXITCODE -ne 0) { throw 'libmpv 解压失败。' }
$extractedDll = Join-Path $extractRoot 'libmpv-2.dll'
$dllHash = (Get-FileHash -LiteralPath $extractedDll -Algorithm SHA256).Hash.ToLowerInvariant()
if ($mpvLock.dllSha256 -and $dllHash -ne $mpvLock.dllSha256) { throw 'libmpv DLL SHA-256 校验失败。' }
if (-not $mpvLock.archiveSha256) {
    # 首次引导采用 GitHub 发布包；有 lock 之后绝不自动刷新校验值。
    $mpvLock.archiveSha256 = $archiveHash
    $mpvLock.dllSha256 = $dllHash
    $mpvLock | ConvertTo-Json | Set-Content -LiteralPath ($lockPath + '.tmp') -Encoding utf8NoBOM
    Move-Item -LiteralPath ($lockPath + '.tmp') -Destination $lockPath -Force
}
New-Item -ItemType Directory -Path (Join-Path $mpvRoot 'bin'),(Join-Path $mpvRoot 'include/mpv') -Force | Out-Null
Copy-Item -LiteralPath $extractedDll -Destination (Join-Path $mpvRoot 'bin/libmpv-2.dll') -Force
Get-ChildItem -LiteralPath (Join-Path $extractRoot 'include/mpv') -Filter '*.h' | Copy-Item -Destination (Join-Path $mpvRoot 'include/mpv') -Force
Write-Host "libmpv $($mpvLock.release) 已就绪，压缩包和 DLL 校验通过。"
