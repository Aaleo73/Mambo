#Requires -Version 7.0
[CmdletBinding()]
param([switch]$VerifyOnly)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$toolRoot = Join-Path $repoRoot 'artifacts/tools/inno-setup'
New-Item -ItemType Directory -Path $toolRoot -Force | Out-Null
$installer = Join-Path $toolRoot 'innosetup-6.7.3.exe'
# 官方发行资产的固定摘要；不依赖 Chocolatey 的同步进度。
$sha256 = '9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732'
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) {
    Invoke-WebRequest -Uri 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe' -OutFile $installer -TimeoutSec 120
}
if ((Get-Item -LiteralPath $installer).Length -ne 10592232 -or
    (Get-FileHash -LiteralPath $installer).Hash.ToLowerInvariant() -ne $sha256) { throw 'Inno Setup 官方安装器校验失败。' }
if ($VerifyOnly) { Write-Host 'Inno Setup 6.7.3 官方安装器 SHA-256 校验通过，未安装。'; return }
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows' -or -not $env:GITHUB_ENV) { throw '此安装步骤仅允许在 Windows GitHub Actions 临时运行器执行；本地请使用 -VerifyOnly。' }
$installDirectory = Join-Path $toolRoot 'compiler'
$process = Start-Process -FilePath $installer -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/SP-','/NORESTART','/CURRENTUSER',('/DIR="' + $installDirectory + '"')) -WindowStyle Hidden -PassThru
try {
    if (-not $process.WaitForExit(120000) -or $process.ExitCode -ne 0) { throw 'Inno Setup 云端安装失败。' }
} finally { $process.Dispose() }
$compiler = Join-Path $installDirectory 'ISCC.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'Inno Setup 编译器未安装。' }
"MAMBO_ISCC_PATH=$compiler" | Add-Content -LiteralPath $env:GITHUB_ENV -Encoding utf8
Write-Host 'Inno Setup 6.7.3 云端编译器已就绪。'
