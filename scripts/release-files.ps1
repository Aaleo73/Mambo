#Requires -Version 7.0
# 发布资料只复制到独立 staging；应用根目录的 Subtitles 属于用户，不遍历或删除。
function Get-MamboReleaseFiles {
    param([Parameter(Mandatory)][string]$Directory)
    foreach ($entry in (Get-ChildItem -LiteralPath $Directory -Force)) {
        if ($entry.Name.Equals('Subtitles', [StringComparison]::OrdinalIgnoreCase)) { continue }
        if ($entry.PSIsContainer) { Get-ChildItem -LiteralPath $entry.FullName -Recurse -File -Force }
        else { $entry }
    }
}

function Copy-MamboReleaseFiles {
    param([Parameter(Mandatory)][string]$SourceDirectory, [Parameter(Mandatory)][string]$DestinationDirectory)
    $sourceRoot = [IO.Path]::GetFullPath($SourceDirectory)
    $destinationRoot = [IO.Path]::GetFullPath($DestinationDirectory)
    if (Test-Path -LiteralPath $destinationRoot) { throw '打包 staging 已存在，拒绝覆盖。' }
    if ($destinationRoot.StartsWith($sourceRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw '打包 staging 不得位于发布源目录内。'
    }
    New-Item -ItemType Directory -Path $destinationRoot | Out-Null
    foreach ($file in (Get-MamboReleaseFiles -Directory $sourceRoot)) {
        $relative = [IO.Path]::GetRelativePath($sourceRoot, $file.FullName)
        $target = [IO.Path]::GetFullPath((Join-Path $destinationRoot $relative))
        if (-not $target.StartsWith($destinationRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw '发布文件越出打包目录。'
        }
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
}

function Get-MamboReleaseFileManifest {
    param([Parameter(Mandatory)][string]$Directory)
    $root = [IO.Path]::GetFullPath($Directory)
    Get-MamboReleaseFiles -Directory $root | Sort-Object FullName | ForEach-Object {
        [ordered]@{
            path = [IO.Path]::GetRelativePath($root, $_.FullName).Replace('\', '/')
            bytes = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
}
