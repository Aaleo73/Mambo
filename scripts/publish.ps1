#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version = '0.1.1',
    [switch]$Installer,
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$UpdateRepository = 'Aaleo73/Mambo',
    [string]$IsccPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'publish/releases'))
$buildId = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$outputRoot = Join-Path $releaseRoot "$Version/$buildId"
$appRoot = Join-Path $outputRoot 'app'
$project = Join-Path $repoRoot 'src/Mambo.App/Mambo.App.csproj'
$mpvLock = Get-Content -LiteralPath (Join-Path $repoRoot 'third_party/libmpv/libmpv.lock.json') -Raw | ConvertFrom-Json
$nativeDll = Join-Path $repoRoot 'third_party/libmpv/bin/libmpv-2.dll'
if (-not (Test-Path -LiteralPath $nativeDll -PathType Leaf)) { throw '缺少 libmpv；请先运行 scripts/fetch-libmpv.ps1。' }
if ((Get-FileHash -LiteralPath $nativeDll -Algorithm SHA256).Hash.ToLowerInvariant() -ne $mpvLock.dllSha256) { throw 'libmpv DLL 与 lock 校验值不一致。' }
& (Join-Path $PSScriptRoot 'verify-native-runtime.ps1')
& (Join-Path $PSScriptRoot 'build-video-shaders.ps1') -Verify
if (Test-Path -LiteralPath $outputRoot) { throw '发布目录已存在，请稍后重试以创建新的构建目录。' }
New-Item -ItemType Directory -Path $appRoot -Force | Out-Null

Push-Location $repoRoot
try {
    & dotnet restore $project -p:Platform=x64 --locked-mode | Out-Host
    if ($LASTEXITCODE -ne 0) { throw '锁定依赖还原失败。' }
    $fileVersion = ($Version -split '-')[0] + '.0'
    & dotnet publish $project --no-restore -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:PublishAot=true -p:WindowsAppSDKSelfContained=true "-p:Version=$Version" "-p:FileVersion=$fileVersion" "-p:InformationalVersion=$Version" "-p:UpdateRepository=$UpdateRepository" -p:TrimmerSingleWarn=false -o $appRoot | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'AOT 发布失败。' }
} finally { Pop-Location }

foreach ($relative in @('Mambo.exe', 'Mambo.pri', 'App.xbf', 'mpv/libmpv-2.dll', 'Microsoft.UI.Xaml.dll', 'Microsoft.WindowsAppRuntime.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $appRoot $relative) -PathType Leaf)) { throw "发布文件缺失：$relative" }
}
$fontSources = Get-ChildItem -LiteralPath (Join-Path $repoRoot 'src/Mambo.App/Assets/Fonts') -Filter '*.ttf' -File
if ($fontSources.Count -eq 0) { throw '仓库没有 MiSans 字体。' }
foreach ($font in $fontSources) {
    $publishedFont = Join-Path $appRoot "Assets/Fonts/$($font.Name)"
    if (-not (Test-Path -LiteralPath $publishedFont -PathType Leaf) -or (Get-FileHash -LiteralPath $publishedFont).Hash -ne (Get-FileHash -LiteralPath $font.FullName).Hash) { throw "字体发布或校验失败：$($font.Name)" }
}
$xbfCount = @(Get-ChildItem -LiteralPath $appRoot -Recurse -File -Filter '*.xbf').Count
if ($xbfCount -lt 2) { throw 'XAML 二进制资源不完整。' }
if ((Get-FileHash -LiteralPath (Join-Path $appRoot 'mpv/libmpv-2.dll') -Algorithm SHA256).Hash.ToLowerInvariant() -ne $mpvLock.dllSha256) { throw '发布包 libmpv 校验失败。' }
& (Join-Path $PSScriptRoot 'verify-native-runtime.ps1') -Directory (Join-Path $appRoot 'mpv')
$qualityManifest = Get-Content -LiteralPath (Join-Path $repoRoot 'third_party/shaders/runtime/manifest.json') -Raw | ConvertFrom-Json
foreach ($qualityFile in $qualityManifest.files) {
    $qualityTarget = Join-Path $appRoot ('mpv/shaders/' + [IO.Path]::GetFileName($qualityFile.path))
    if (-not (Test-Path -LiteralPath $qualityTarget -PathType Leaf) -or
        (Get-FileHash -LiteralPath $qualityTarget -Algorithm SHA256).Hash.ToLowerInvariant() -ne $qualityFile.sha256) {
        throw '发布包画质着色器缺失或校验失败。'
    }
}
foreach ($qualityLicense in @('FidelityFX-CAS-MIT.txt', 'Anime4K-4.0.1-MIT.txt', 'Anime4K-AutoDownscale-Unlicense.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $appRoot ('LICENSES/' + $qualityLicense)) -PathType Leaf)) {
        throw '发布包画质着色器许可证缺失。'
    }
}

foreach ($document in @('THIRD_PARTY_NOTICES.md', 'LICENSE')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot $document) -Destination $appRoot
}
# csproj 已可能复制 LICENSES；逐文件合并，避免 Copy-Item 再创建同名目录失败。
$licenseRoot = Join-Path $repoRoot 'LICENSES'
foreach ($license in (Get-ChildItem -LiteralPath $licenseRoot -Recurse -File)) {
    $licenseTarget = Join-Path $appRoot ('LICENSES/' + [IO.Path]::GetRelativePath($licenseRoot, $license.FullName))
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($licenseTarget)) -Force | Out-Null
    Copy-Item -LiteralPath $license.FullName -Destination $licenseTarget -Force
}
$manifest = [ordered]@{
    schemaVersion = 1
    version = $Version
    architecture = 'win-x64'
    selfContained = $true
    nativeAot = $true
    libmpvRelease = $mpvLock.release
    libmpvRevision = $mpvLock.revision
    libmpvSha256 = $mpvLock.dllSha256
    xbfCount = $xbfCount
    files = @(Get-ChildItem -LiteralPath $appRoot -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path = [IO.Path]::GetRelativePath($appRoot, $_.FullName).Replace('\', '/'); bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $appRoot 'release-manifest.json') -Encoding utf8NoBOM
$zipPath = Join-Path $outputRoot "Mambo-$Version-win-x64-portable.zip"
# ZipFile 会包括隐藏文件，不依赖 Compress-Archive 的默认过滤行为。
[IO.Compression.ZipFile]::CreateFromDirectory($appRoot, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)
$sourceArchive = & (Join-Path $PSScriptRoot 'make-source-archive.ps1') -OutputDirectory $outputRoot -Version $Version
$installerPath = $null
if ($Installer) {
    if (-not $IsccPath) {
        $compiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if ($compiler) { $IsccPath = $compiler.Source }
        else {
            $compilerCandidates = @((Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'), (Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 6/ISCC.exe'))
            # Inno Setup 6.7.3 installed by winget records its per-user directory in HKCU.
            foreach ($registryPath in @(
                'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
                'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
                'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1'
            )) {
                $installation = Get-ItemProperty -LiteralPath $registryPath -Name InstallLocation -ErrorAction SilentlyContinue
                if ($installation -and $installation.InstallLocation) {
                    $compilerCandidates += Join-Path $installation.InstallLocation 'ISCC.exe'
                }
            }
            foreach ($candidate in ($compilerCandidates | Select-Object -Unique)) {
                if (Test-Path -LiteralPath $candidate -PathType Leaf) { $IsccPath = $candidate; break }
            }
        }
    }
    if (-not $IsccPath -or -not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) { throw '未找到或当前进程无法读取 Inno Setup 编译器。便携包已生成；请核对安装目录和当前账户的读取权限，也可用 -IsccPath 指定已安装的 ISCC.exe。' }
    & $IsccPath "/DMyAppVersion=$Version" "/DPublishDir=$appRoot" "/DInstallerOutputDir=$outputRoot" (Join-Path $repoRoot 'installer/Mambo.iss') | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup 编译失败；便携包仍可审阅。' }
    $installerPath = Join-Path $outputRoot "Mambo-$Version-win-x64-setup.exe"
    if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) { throw '未找到安装器输出。' }
}
[pscustomobject]@{
    Version = $Version
    PublishDirectory = $appRoot
    PortableZip = $zipPath
    PortableBytes = (Get-Item -LiteralPath $zipPath).Length
    PortableSha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    SourceZip = $sourceArchive.SourceZip
    SourceBytes = $sourceArchive.SourceBytes
    SourceSha256 = $sourceArchive.SourceSha256
    NativeSourceBundles = $sourceArchive.NativeSourceBundles
    Installer = $installerPath
    InstallerBytes = if ($installerPath) { (Get-Item -LiteralPath $installerPath).Length } else { $null }
    InstallerSha256 = if ($installerPath) { (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
}
