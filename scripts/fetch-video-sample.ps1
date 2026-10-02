#Requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$sampleRoot = Join-Path $repoRoot 'artifacts/samples'
$samplePath = Join-Path $sampleRoot 'jellyfin-4k-hevc-hdr10.mp4'
$sampleUri = 'https://nyc1.mirror.jellyfin.org/main/test-videos/HDR/HDR10/HEVC/Test%20Jellyfin%204K%20HEVC%20HDR10%2040M.mp4'
$expectedHash = 'da108da499153ae4816b6cd50296a6dc56996ae3d30a08c7a14a1108b25a4221'
New-Item -ItemType Directory -Path $sampleRoot -Force | Out-Null
if (-not (Test-Path -LiteralPath $samplePath)) {
    Invoke-WebRequest -Uri $sampleUri -OutFile ($samplePath + '.partial')
    Move-Item -LiteralPath ($samplePath + '.partial') -Destination $samplePath
}
if ((Get-FileHash -LiteralPath $samplePath -Algorithm SHA256).Hash -ne $expectedHash) {
    throw 'HDR 样片 SHA-256 校验失败，请检查下载缓存。'
}
Write-Host "Jellyfin / Gnattu 4K HEVC HDR10 样片已就绪：$samplePath"
Write-Host '样片许可：CC BY-SA；出处：https://repo.jellyfin.org/main/test-videos/'
