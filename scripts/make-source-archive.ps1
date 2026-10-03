#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$OutputDirectory,
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')] [string]$Version = '0.1.0'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$publishRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'publish'))
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory, $repoRoot)
if (-not $outputRoot.StartsWith($publishRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '源码包输出必须位于仓库 publish/ 下的子目录。' }
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$nativeSources = Get-Content -LiteralPath (Join-Path $repoRoot 'LICENSES/native-sources.lock.json') -Raw | ConvertFrom-Json
$sourceCache = Join-Path $repoRoot 'third_party/libmpv/download/sources'
New-Item -ItemType Directory -Path $sourceCache -Force | Out-Null
foreach ($native in $nativeSources) {
    $sourceUri = [Uri]$native.url
    if ($native.filename -notmatch '^[a-z0-9-]+-[a-f0-9]{40}\.tar\.gz$' -or $sourceUri.Scheme -ne 'https' -or $sourceUri.Host -ne 'codeload.github.com' -or -not $sourceUri.AbsolutePath.EndsWith('/tar.gz/' + $native.revision, [StringComparison]::Ordinal)) { throw '原生源码 lock 格式无效。' }
    $cachePath = Join-Path $sourceCache $native.filename
    if (-not (Test-Path -LiteralPath $cachePath -PathType Leaf)) { Invoke-WebRequest -Uri $sourceUri -OutFile $cachePath }
    if ((Get-FileHash -LiteralPath $cachePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $native.sha256 -or (Get-Item -LiteralPath $cachePath).Length -ne $native.bytes) { throw "原生源码 SHA-256 或大小校验失败：$($native.name)" }
}

Push-Location $repoRoot
try {
    $sourceFiles = @(& git -c core.quotepath=false ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw '无法取得仓库源文件清单。' }
    $sourceCommit = & git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw '无法取得源码提交标识。' }
    $sourceStatus = @(& git status --porcelain)
    if ($LASTEXITCODE -ne 0) { throw '无法取得源码工作区状态。' }
    $sourceDirty = $sourceStatus.Count -gt 0
} finally { Pop-Location }
$zipPath = Join-Path $outputRoot "Mambo-$Version-sources.zip"
$sourceManifest = [ordered]@{
    schemaVersion = 1
    version = $Version
    mamboCommit = $sourceCommit
    mamboWorkingTreeModified = $sourceDirty
    nativeSources = $nativeSources
    completeness = '含当前 Mambo 源码及准确 mpv/FFmpeg/构建脚本修订；所有原生依赖与补丁的精确对应源码尚待补齐。'
    files = @()
}
$sourceReadme = @'
# Mambo 本地源码包

`mambo/` 保存生成包时的仓库工作区文件，包括源代码、锁文件、字体、测试、脚本和许可记录，不包含 Git 数据、构建产物或用户设置。是否有未提交修改和 HEAD 标识见 source-manifest.json，内容以逐文件哈希为准。

`native/` 附带已通过 lock 的 SHA-256 验证的 mpv、FFmpeg 和 shinchiro 构建脚本源代码 tar.gz。压缩包按固定完整修订下载，没有执行或修改其中内容。它们可供查看对应项目源代码；不是整个 libmpv DLL 的完整对应源码包，因为静态依赖修订、构建补丁与生成文件仍有未确认项。

构建 Mambo：在 Windows x64 安装 .NET 10 SDK 与 C++ Native AOT 构建工具，运行 `pwsh scripts/fetch-libmpv.ps1`、`dotnet restore --locked-mode`、`dotnet build -p:Platform=x64`、`dotnet test`，再使用 `pwsh scripts/publish.ps1 -Version <version>`。外部 mpv.exe 由使用者自行选择批准，源码包不附送未知外部播放器。

Mambo 采用 GPL-3.0-or-later；其他组件条款保持原许可。完整归属、精确修订和剩余源码缺口见 mambo/THIRD_PARTY_NOTICES.md、mambo/LICENSES/libmpv-components.json。此源码包可在本地与二进制验收包一起提供，不声称已经完成公开 GPL 分发要求。
'@

$zipStream = [IO.File]::Open($zipPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
$zipArchive = [IO.Compression.ZipArchive]::new($zipStream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($relative in ($sourceFiles | Sort-Object -Unique)) {
        if (-not $relative -or $relative -match '(^|/)\.\.?(/|$)|(^|/)(bin|obj|\.git|publish|artifacts|TestResults)/' -or [IO.Path]::IsPathRooted($relative)) { throw '源码清单含不允许的路径。' }
        $fullPath = [IO.Path]::GetFullPath((Join-Path $repoRoot $relative))
        if (-not $fullPath.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '源码路径超出工作区。' }
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { continue }
        $file = Get-Item -LiteralPath $fullPath
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "不归档重解析文件：$relative" }
        $before = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zipArchive, $fullPath, 'mambo/' + $relative.Replace('\', '/'), [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        if ((Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $before) { throw "归档期间源文件发生变化：$relative" }
        $sourceManifest.files += [ordered]@{ path = $relative.Replace('\', '/'); bytes = $file.Length; sha256 = $before }
    }
    foreach ($native in $nativeSources) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zipArchive, (Join-Path $sourceCache $native.filename), 'native/' + $native.filename, [IO.Compression.CompressionLevel]::NoCompression) | Out-Null
    }
    foreach ($document in @(@{name='SOURCE_README.md';text=$sourceReadme}, @{name='source-manifest.json';text=($sourceManifest | ConvertTo-Json -Depth 8)})) {
        $entry = $zipArchive.CreateEntry($document.name)
        $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
        try { $writer.Write($document.text) } finally { $writer.Dispose() }
    }
} finally {
    $zipArchive.Dispose()
    $zipStream.Dispose()
}
[pscustomobject]@{ SourceZip = $zipPath; SourceBytes = (Get-Item -LiteralPath $zipPath).Length; SourceSha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant() }
