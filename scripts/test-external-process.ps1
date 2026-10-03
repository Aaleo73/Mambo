#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$SamplePath,
    [switch]$PrepareOnly,
    [switch]$NoBuild,
    [switch]$Aot,
    [string]$AppDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$p7Repository = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$p7ProjectDirectory = Join-Path $p7Repository 'scripts/diagnostics/Mambo.ExternalSmoke'
$p7Project = Join-Path $p7ProjectDirectory 'Mambo.ExternalSmoke.csproj'
$p7Lock = Get-Content -LiteralPath (Join-Path $p7ProjectDirectory 'mpv-smoke.lock.json') -Raw | ConvertFrom-Json
$p7Url = [Uri]$p7Lock.url
if ($p7Url.Scheme -ne 'https' -or $p7Url.Host -ne 'github.com' -or $p7Url.AbsolutePath -notlike '/shinchiro/mpv-winbuild-cmake/releases/download/*') {
    throw '诊断资产必须来自固定的 shinchiro 官方 GitHub Release。'
}
$p7Cache = Join-Path $p7Repository "artifacts/tools/external-mpv/$($p7Lock.release)"
New-Item -ItemType Directory -Path $p7Cache -Force | Out-Null
$p7Archive = Join-Path $p7Cache ([IO.Path]::GetFileName($p7Url.AbsolutePath))
if (-not (Test-Path -LiteralPath $p7Archive -PathType Leaf)) {
    $p7Partial = Join-Path $p7Cache (([IO.Path]::GetFileName($p7Archive)) + '.' + [Guid]::NewGuid().ToString('N') + '.partial')
    Invoke-WebRequest -Uri $p7Url -OutFile $p7Partial
    if ((Get-FileHash -LiteralPath $p7Partial -Algorithm SHA256).Hash.ToLowerInvariant() -ne $p7Lock.archiveSha256) {
        throw '官方 MPV 下载资产 SHA-256 不匹配，禁止提取或执行。'
    }
    Move-Item -LiteralPath $p7Partial -Destination $p7Archive
}
if ((Get-FileHash -LiteralPath $p7Archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $p7Lock.archiveSha256) {
    throw 'MPV 缓存资产 SHA-256 不匹配，禁止提取或执行。'
}
$p7Verified = Join-Path $p7Cache 'verified'
New-Item -ItemType Directory -Path $p7Verified -Force | Out-Null
# Extract only the two hash-pinned runtime files; never run updater/install scripts in the archive.
& "$env:SystemRoot/System32/tar.exe" -xf $p7Archive -C $p7Verified mpv.exe d3dcompiler_43.dll
if ($LASTEXITCODE -ne 0) { throw '无法提取已校验 MPV 资产。' }
foreach ($p7File in $p7Lock.files.PSObject.Properties) {
    $p7FilePath = Join-Path $p7Verified $p7File.Name
    if ((Get-FileHash -LiteralPath $p7FilePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $p7File.Value) {
        throw 'MPV 运行文件 SHA-256 不匹配，禁止执行。'
    }
}
$p7Executable = Join-Path $p7Verified 'mpv.exe'
if ($PrepareOnly) {
    [pscustomobject]@{ Executable = $p7Executable; Archive = $p7Archive; ArchiveSha256 = $p7Lock.archiveSha256; ExecutableSha256 = $p7Lock.files.'mpv.exe' }
    return
}
if (-not $SamplePath) { $SamplePath = Join-Path $p7Repository 'artifacts/samples/jellyfin-4k-hevc-hdr10.mp4' }
$SamplePath = [IO.Path]::GetFullPath($SamplePath)
if (-not (Test-Path -LiteralPath $SamplePath -PathType Leaf)) { throw '本地样片不存在，请通过 -SamplePath 指定。' }
if ($AppDirectory -and -not $Aot) { throw '-AppDirectory 仅用于 -Aot 运行。' }
if ($Aot) {
    if (-not $AppDirectory) { $AppDirectory = Join-Path $p7Repository 'publish/external-smoke-aot' }
    $AppDirectory = [IO.Path]::GetFullPath($AppDirectory)
}
if (-not $NoBuild) {
    & dotnet restore $p7Project --locked-mode -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw '外部进程诊断 locked restore 失败。' }
    if ($Aot) {
        & dotnet publish $p7Project -c Release -p:Platform=x64 --no-restore -p:PublishAot=true --self-contained true -o $AppDirectory
    }
    else { & dotnet build $p7Project -c Debug -p:Platform=x64 --no-restore }
    if ($LASTEXITCODE -ne 0) { throw '外部进程诊断构建失败。' }
}
$p7RunRoot = Join-Path $p7Repository ('artifacts/p7-external-smoke/' + [Guid]::NewGuid().ToString('N'))
if ($Aot) {
    $p7AotExecutable = Join-Path $AppDirectory 'Mambo.ExternalSmoke.exe'
    if (-not (Test-Path -LiteralPath $p7AotExecutable -PathType Leaf)) { throw '未找到已发布的外部进程 AOT 诊断程序，请先集中 publish 或指定 -AppDirectory。' }
    & $p7AotExecutable $p7Executable $SamplePath $p7RunRoot $p7Repository
}
else { & dotnet run --project $p7Project -c Debug -p:Platform=x64 --no-build --no-restore -- $p7Executable $SamplePath $p7RunRoot $p7Repository }
if ($LASTEXITCODE -ne 0) { throw "外部进程诊断失败；本轮隔离目录保留在 $p7RunRoot。" }
Get-Content -LiteralPath (Join-Path $p7RunRoot 'result.json') -Raw
