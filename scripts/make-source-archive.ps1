#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$OutputDirectory,
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')] [string]$Version = '0.1.4'
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
if ($env:MAMBO_NATIVE_SOURCES_CACHE) { $sourceCache = [IO.Path]::GetFullPath($env:MAMBO_NATIVE_SOURCES_CACHE) }
New-Item -ItemType Directory -Path $sourceCache -Force | Out-Null
& (Join-Path $PSScriptRoot 'fetch-native-sources.ps1')

# 每份独立 ZIP 保持在 GitHub 单资产大小上限内；源码归档原字节不再压缩。
$nativeBundles = @()
$partition = @(); $partitionBytes = 0L; $partitionNumber = 0
$partitions = @()
foreach ($native in $nativeSources) {
    if ($partitionBytes + $native.bytes -gt 1500MB -and $partition.Count -gt 0) {
        $partitions += ,$partition; $partition = @(); $partitionBytes = 0L
    }
    $partition += $native; $partitionBytes += $native.bytes
}
if ($partition.Count -gt 0) { $partitions += ,$partition }
foreach ($entries in $partitions) {
    $partitionNumber++
    $bundlePath = Join-Path $outputRoot "Mambo-$Version-native-sources-$partitionNumber.zip"
    $bundle = [IO.Compression.ZipFile]::Open($bundlePath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($native in $entries) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($bundle, (Join-Path $sourceCache $native.filename), 'native/' + $native.filename, [IO.Compression.CompressionLevel]::NoCompression) | Out-Null
        }
    } finally { $bundle.Dispose() }
    $nativeBundles += [pscustomobject]@{ path = $bundlePath; filename = [IO.Path]::GetFileName($bundlePath); bytes = (Get-Item -LiteralPath $bundlePath).Length; sha256 = (Get-FileHash -LiteralPath $bundlePath -Algorithm SHA256).Hash.ToLowerInvariant() }
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
    nativeBundles = @($nativeBundles | Select-Object filename,bytes,sha256)
    completeness = '原生运行时由 MSYS2 精确版本包提供；全部实际 DLL 对应源码、构建配方/补丁、静态和头文件输入及 Rust 锁定依赖在 native-sources ZIP 中提供。构建范围与工具例外详见 mambo/docs/decisions/native-distribution.md。'
    files = @()
}
$sourceReadme = @'
# Mambo 对应源码包

`mambo/` 保存生成包时的仓库工作区文件，包括源代码、锁文件、字体、测试、脚本和许可记录，不包含 Git 数据、构建产物或用户设置。是否有未提交修改和 HEAD 标识见 source-manifest.json，内容以逐文件哈希为准。

同一 Release 的 `Mambo-<version>-native-sources-*.zip` 附带全部锁定原生源码归档，文件名、字节数和 SHA-256 见 source-manifest.json。解压所有分包可得到 `native/`：MSYS2 `.src.tar.zst` 包含原始上游源码、PKGBUILD、.SRCINFO 和补丁，`.crate` 为 Rust 原始源码与许可。每个 DLL 的包归属与哈希、每份源码的哈希及原构建环境版本均在 mambo/LICENSES 与 mambo/third_party/libmpv/libmpv.lock.json 中记录。

构建 Mambo：在 Windows x64 安装 .NET 10 SDK 与 C++ Native AOT 构建工具，运行 `pwsh scripts/fetch-libmpv.ps1`、`dotnet restore --locked-mode`、`dotnet build -p:Platform=x64`、`dotnet test`，再使用 `pwsh scripts/publish.ps1 -Version <version>`。外部 mpv.exe 由使用者自行选择批准，源码包不附送未知外部播放器。

原生重建请遵循 mambo/docs/decisions/native-distribution.md，使用归档内的 PKGBUILD、补丁、.BUILDINFO 及 Rust Cargo.lock；应用下载脚本不执行第三方配方。Mambo 采用 GPL-3.0-or-later；各原生组件、SDK、字体与 shader 保留自己的完整许可与归属。
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
    foreach ($document in @(@{name='SOURCE_README.md';text=$sourceReadme}, @{name='source-manifest.json';text=($sourceManifest | ConvertTo-Json -Depth 8)})) {
        $entry = $zipArchive.CreateEntry($document.name)
        $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
        try { $writer.Write($document.text) } finally { $writer.Dispose() }
    }
} finally {
    $zipArchive.Dispose()
    $zipStream.Dispose()
}
[pscustomobject]@{ SourceZip = $zipPath; SourceBytes = (Get-Item -LiteralPath $zipPath).Length; SourceSha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant(); NativeSourceBundles = $nativeBundles }
