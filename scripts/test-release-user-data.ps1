#Requires -Version 7.0
[CmdletBinding()]
param([string]$IsccPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'release-files.ps1')
$testRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('Mambo-release-tests/' + [Guid]::NewGuid().ToString('N'))))
$source = Join-Path $testRoot 'app'
$stage = Join-Path $testRoot 'package'
$output = Join-Path $testRoot 'updates'
$subtitle = Join-Path $source 'sUbTiTlEs/item/episode.ass'
function Assert-ReleaseTest([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
try {
    foreach ($relative in @('Mambo.exe', 'Mambo.Updater.exe', 'Mambo.pri', 'LICENSE', 'mpv/libmpv-2.dll', 'Assets/Subtitles/resource.txt', '.hidden-runtime', 'sUbTiTlEs/item/episode.ass')) {
        $path = Join-Path $source $relative
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($path)) -Force | Out-Null
        [IO.File]::WriteAllText($path, "fixture $relative")
    }
    [IO.File]::SetAttributes((Join-Path $source '.hidden-runtime'), [IO.FileAttributes]::Hidden)
    $savedBytes = [IO.File]::ReadAllBytes($subtitle)
    # An exclusively locked user subtitle must never be read during packaging.
    $locked = [IO.File]::Open($subtitle, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    try {
        $sourceFiles = @(Get-MamboReleaseFileManifest -Directory $source)
        Copy-MamboReleaseFiles -SourceDirectory $source -DestinationDirectory $stage
    } finally { $locked.Dispose() }
    Assert-ReleaseTest (-not ($sourceFiles.path -match '^Subtitles(/|$)')) '发布源清单包含用户字幕。'
    Assert-ReleaseTest (-not (Test-Path -LiteralPath (Join-Path $stage 'Subtitles'))) '安装器 staging 包含用户字幕。'
    Assert-ReleaseTest (Test-Path -LiteralPath (Join-Path $stage 'Assets/Subtitles/resource.txt')) '错误排除了非应用根目录资源。'
    Assert-ReleaseTest (Test-Path -LiteralPath (Join-Path $stage '.hidden-runtime')) '丢失隐藏运行时文件。'
    $manifest = [ordered]@{ schemaVersion = 1; version = '0.2.0'; architecture = 'win-x64'; files = @(Get-MamboReleaseFileManifest -Directory $stage) }
    $manifestPath = Join-Path $stage 'release-manifest.json'
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
    $actual = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    Assert-ReleaseTest (-not ($actual.files.path -match '^Subtitles(/|$)')) '发布清单包含用户字幕。'
    $portable = Join-Path $testRoot 'portable.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $portable, [IO.Compression.CompressionLevel]::Optimal, $false)
    $packages = & (Join-Path $PSScriptRoot 'build-update-packages.ps1') -AppDirectory $stage -OutputDirectory $output -Version '0.2.0'
    foreach ($zipPath in (@($portable) + @($packages.Assets | Where-Object { $_ -like '*.zip' }))) {
        $zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
        try {
            Assert-ReleaseTest (-not ($zip.Entries.FullName -match '^Subtitles(/|$)')) '便携包或组件更新包包含用户字幕。'
        } finally { $zip.Dispose() }
    }
    $updateManifest = Get-Content -LiteralPath ($packages.Assets | Where-Object { $_ -like '*.json' }) -Raw | ConvertFrom-Json
    Assert-ReleaseTest (-not ($updateManifest.components.files.path -match '^Subtitles(/|$)')) '应用更新清单包含用户字幕。'
    foreach ($relative in @('Subtitles', 'sUbTiTlEs/item/episode.ass', 'Subtitles./item/episode.ass', 'Subtitles /item/episode.ass')) {
        $manifest.files = @($actual.files) + @([ordered]@{ path = $relative; bytes = 1; sha256 = ('a' * 64) })
        $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
        $rejectedOutput = Join-Path $testRoot ('rejected-' + [Guid]::NewGuid().ToString('N'))
        $rejected = $false
        try { & (Join-Path $PSScriptRoot 'build-update-packages.ps1') -AppDirectory $stage -OutputDirectory $rejectedOutput -Version '0.2.0' | Out-Null }
        catch { $rejected = $true }
        Assert-ReleaseTest $rejected '更新包生成器接受了保留路径。'
        Assert-ReleaseTest (-not (Test-Path -LiteralPath $rejectedOutput)) '拒绝非法清单之前已经生成了更新资产。'
    }
    Assert-ReleaseTest ([Convert]::ToHexString([IO.File]::ReadAllBytes($subtitle)) -eq [Convert]::ToHexString($savedBytes)) '打包改动了用户字幕。'
    if ($IsccPath) {
        # Bypass staging deliberately: also exercise the installer's own exclusion on the real source tree.
        $repoRoot = Split-Path $PSScriptRoot -Parent
        $compilerOutput = @(& $IsccPath '/DMyAppVersion=0.2.0' "/DPublishDir=$source" "/DInstallerOutputDir=$testRoot" (Join-Path $repoRoot 'installer/Mambo.iss') 2>&1)
        Assert-ReleaseTest ($LASTEXITCODE -eq 0) ('安装器测试样本编译失败：' + ($compilerOutput -join "`n"))
        Assert-ReleaseTest ([bool]($compilerOutput -match 'Compressing:.*Mambo\.exe')) '安装器编译未记录应用文件，无法验证文件枚举。'
        Assert-ReleaseTest (-not [bool]($compilerOutput -match 'Compressing:.*[\\/]sUbTiTlEs[\\/]item[\\/]episode\.ass')) '安装器枚举包含根目录用户字幕。'
        Assert-ReleaseTest ([bool]($compilerOutput -match 'Compressing:.*Assets[\\/]Subtitles[\\/]resource\.txt')) '安装器排除规则影响了非根目录资源。'
        '安装器编译验证通过：实际源树中用户字幕未参与压缩，嵌套资源正常包含；未运行安装器。'
    }
    '发布用户资料保护验证通过：源清单、安装 staging、便携包、更新清单与组件压缩包均排除根目录字幕，源字幕保留。'
} finally {
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $testRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw '测试清理路径越界。' }
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
