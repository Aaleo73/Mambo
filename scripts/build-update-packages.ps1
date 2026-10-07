#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AppDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')][string]$Version
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$updateAppRoot = [IO.Path]::GetFullPath($AppDirectory)
$updateOutputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$releaseManifestPath = Join-Path $updateAppRoot 'release-manifest.json'
$releaseManifest = Get-Content -LiteralPath $releaseManifestPath -Raw | ConvertFrom-Json
if ($releaseManifest.version -ne $Version -or $releaseManifest.architecture -ne 'win-x64') { throw '发布版本与更新清单不一致。' }
$updateGroups = [ordered]@{ app = @(); runtime = @(); mpv = @(); assets = @(); licenses = @() }
$updateFiles = @($releaseManifest.files) + @([pscustomobject]@{
    path = 'release-manifest.json'
    bytes = (Get-Item -LiteralPath $releaseManifestPath).Length
    sha256 = (Get-FileHash -LiteralPath $releaseManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
})
$updateSeen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($file in $updateFiles) {
    if (-not $updateSeen.Add($file.path) -or $file.path -match '(^/|\\|:|(^|/)\.\.?(/|$)|(^|/)unins|(^|/)\.mambo-update|^Subtitles[ .]*(/|$))') { throw '发布文件路径不安全或重复。' }
    $source = [IO.Path]::GetFullPath((Join-Path $updateAppRoot $file.path))
    if (-not $source.StartsWith($updateAppRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '发布文件越出应用目录。' }
    $checkPath = $source
    while ($checkPath) {
        if ((Test-Path -LiteralPath $checkPath) -and ((Get-Item -LiteralPath $checkPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw '发布文件包含链接。' }
        $checkPath = [IO.Path]::GetDirectoryName($checkPath)
    }
    if ((Get-Item -LiteralPath $source -Force).Length -ne $file.bytes -or (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256) { throw '发布文件与清单校验值不一致。' }
    $group = if ($file.path -match '^mpv/') { 'mpv' }
        elseif ($file.path -match '^Assets/') { 'assets' }
        elseif ($file.path -match '^(LICENSES/|LICENSE$|THIRD_PARTY_NOTICES\.md$)') { 'licenses' }
        elseif ($file.path -match '(^Mambo\.|\.xbf$|^release-manifest\.json$)') { 'app' }
        else { 'runtime' }
    $updateGroups[$group] += $file
}
foreach ($required in @('Mambo.exe', 'Mambo.Updater.exe', 'Mambo.pri', 'release-manifest.json')) {
    if (-not $updateSeen.Contains($required)) { throw "发布文件缺失：$required" }
}
New-Item -ItemType Directory -Path $updateOutputRoot -Force | Out-Null
$updateComponents = @()
$updateAssets = @()
foreach ($name in $updateGroups.Keys) {
    $files = @($updateGroups[$name] | Sort-Object path)
    if ($files.Count -eq 0) { continue }
    $zipPath = Join-Path $updateOutputRoot "Mambo-$Version-win-x64-update-$name.zip"
    if (Test-Path -LiteralPath $zipPath) { throw '更新包已存在，拒绝覆盖。' }
    $archive = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $files) {
            $entry = $archive.CreateEntry($file.path, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $entry.ExternalAttributes = 0
            $inputStream = [IO.File]::OpenRead((Join-Path $updateAppRoot $file.path))
            $outputStream = $entry.Open()
            try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose(); $inputStream.Dispose() }
        }
    } finally { $archive.Dispose() }
    $updateComponents += [ordered]@{
        name = $name
        bytes = (Get-Item -LiteralPath $zipPath).Length
        sha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
        files = $files
    }
    $updateAssets += $zipPath
}
$updateManifestPath = Join-Path $updateOutputRoot "Mambo-$Version-win-x64-update.json"
if (Test-Path -LiteralPath $updateManifestPath) { throw '更新清单已存在，拒绝覆盖。' }
[ordered]@{ schemaVersion = 1; version = $Version; architecture = 'win-x64'; components = $updateComponents } |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $updateManifestPath -Encoding utf8NoBOM
if ((Get-Item -LiteralPath $updateManifestPath).Length -gt 2MB) { throw '更新清单超过客户端限制。' }
[pscustomobject]@{ Assets = @($updateManifestPath) + $updateAssets; Components = $updateComponents }
