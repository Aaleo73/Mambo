#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageJson,
    [Parameter(Mandatory)][string]$PreviousAppDirectory,
    [Parameter(Mandatory)][string]$IsccPath,
    [string]$UpdaterPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$updateRepository = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$updateRun = Join-Path $updateRepository ('artifacts/component-update-validation/' + [Guid]::NewGuid().ToString('N'))
$updateRegistration = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{A36B6A2B-B280-4C39-9517-C22230A61E8C}_is1'
$updateOwnedProcesses = [Collections.Generic.List[object]]::new()
$updateInstalledDirectory = $null
$updateOldFake = $env:MAMBO_FAKE
$updateResults = [Collections.Generic.List[object]]::new()
$updatePackage = Get-Content -LiteralPath $PackageJson -Raw | ConvertFrom-Json

function Assert-UpdateTestPath([string]$Path) {
    $absolute = [IO.Path]::GetFullPath($Path)
    if (-not $absolute.StartsWith($updateRepository + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '验收路径必须在仓库内。' }
    $current = $absolute
    while ($current) {
        if (([IO.File]::Exists($current) -or [IO.Directory]::Exists($current)) -and ([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint)) { throw '验收路径包含链接。' }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    return $absolute
}
function Start-UpdateTestProcess([string]$Executable, [string[]]$Arguments) {
    $Executable = Assert-UpdateTestPath $Executable
    $info = [Diagnostics.ProcessStartInfo]::new($Executable)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $info.WorkingDirectory = [IO.Path]::GetDirectoryName($Executable)
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($info)
    $null = $process.Handle
    $updateOwnedProcesses.Add([pscustomobject]@{ Process = $process; Executable = $Executable })
    return $process
}
function Wait-UpdateTestWindow($Process) {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        $Process.Refresh()
        if ($Process.HasExited) { throw '验收应用提前退出。' }
        if ($Process.MainWindowHandle -ne 0) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw '验收应用没有创建窗口。'
}
function Assert-UpdateTestFiles([string]$Directory, $Manifest) {
    foreach ($component in $Manifest.components) {
        foreach ($file in $component.files) {
            $path = Assert-UpdateTestPath (Join-Path $Directory $file.path)
            if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -ne $file.bytes -or
                (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256) { throw '更新后的文件与目标清单不一致。' }
        }
    }
}
function Invoke-UpdateTestUninstall {
    if (-not $updateInstalledDirectory) { return }
    $registration = Get-ItemProperty -LiteralPath $updateRegistration
    if (-not ([IO.Path]::GetFullPath($registration.InstallLocation).TrimEnd('\')).Equals($updateInstalledDirectory.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) { throw '卸载注册项不属于本轮验收。' }
    $uninstall = Start-UpdateTestProcess (Join-Path $updateInstalledDirectory 'unins000.exe') @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
    if (-not $uninstall.WaitForExit(60000) -or $uninstall.ExitCode -ne 0) { throw '本轮卸载失败。' }
}

try {
    foreach ($hive in @('HKCU:\Software', 'HKCU:\Software\WOW6432Node', 'HKLM:\SOFTWARE', 'HKLM:\SOFTWARE\WOW6432Node')) {
        if (Test-Path -LiteralPath ($hive + '\Microsoft\Windows\CurrentVersion\Uninstall\{A36B6A2B-B280-4C39-9517-C22230A61E8C}_is1')) {
            throw '已有正式安装，不能在本机执行安装版生命周期验收。便携与单元测试可单独执行。'
        }
    }
    $initial = Assert-UpdateTestPath (Join-Path $updateRun 'initial')
    $PreviousAppDirectory = Assert-UpdateTestPath $PreviousAppDirectory
    $published = Assert-UpdateTestPath $updatePackage.PublishDirectory
    New-Item -ItemType Directory -Path $updateRun -Force | Out-Null
    Copy-Item -LiteralPath $PreviousAppDirectory -Destination $initial -Recurse
    # The old public build has no updater. Add only the new bootstrap to this isolated migration fixture.
    Copy-Item -LiteralPath (Join-Path $published 'Mambo.Updater.exe') -Destination $initial -Force
    $oldManifest = Get-Content -LiteralPath (Join-Path $initial 'release-manifest.json') -Raw | ConvertFrom-Json
    # An unused diagnostic page stands in for a file first introduced by the new version.
    # The test installer will not own it; only current-manifest cleanup can remove it later.
    $addedPage = 'Debug/VideoLab.xbf'
    Remove-Item -LiteralPath (Assert-UpdateTestPath (Join-Path $initial $addedPage))
    $oldManifest.files = @($oldManifest.files | Where-Object { $_.path -ne $addedPage })
    $oldManifest.files += [pscustomobject]@{ path = 'Mambo.Updater.exe'; bytes = (Get-Item -LiteralPath (Join-Path $initial 'Mambo.Updater.exe')).Length; sha256 = (Get-FileHash -LiteralPath (Join-Path $initial 'Mambo.Updater.exe')).Hash.ToLowerInvariant() }
    $oldManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $initial 'release-manifest.json') -Encoding utf8NoBOM
    $manifestAsset = @($updatePackage.UpdateAssets | Where-Object { $_ -like '*-update.json' })[0]
    $manifest = Get-Content -LiteralPath $manifestAsset -Raw | ConvertFrom-Json
    $env:MAMBO_FAKE = '1'
    foreach ($kind in @('portable', 'installed')) {
        Write-Host "正在验证 $kind 的更新与重启。"
        $appDirectory = Assert-UpdateTestPath (Join-Path $updateRun ($kind + '/app'))
        $staging = Assert-UpdateTestPath (Join-Path $updateRun ($kind + '/staging'))
        New-Item -ItemType Directory -Path $staging -Force | Out-Null
        if ($kind -eq 'installed') {
            $setupOutput = Join-Path $updateRun 'setup'
            New-Item -ItemType Directory -Path $setupOutput -Force | Out-Null
            & $IsccPath '/Qp' "/DMyAppVersion=$($oldManifest.version)" "/DPublishDir=$initial" "/DInstallerOutputDir=$setupOutput" (Join-Path $updateRepository 'installer/Mambo.iss') | Out-Host
            if ($LASTEXITCODE -ne 0) { throw '验收安装包编译失败。' }
            $setup = Start-UpdateTestProcess (Join-Path $setupOutput "Mambo-$($oldManifest.version)-win-x64-setup.exe") @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=$appDirectory", "/GROUP=Mambo validation $([IO.Path]::GetFileName($updateRun))", '/TASKS=')
            if (-not $setup.WaitForExit(60000) -or $setup.ExitCode -ne 0) { throw '验收安装失败。' }
            $updateInstalledDirectory = $appDirectory
        } else { Copy-Item -LiteralPath $initial -Destination $appDirectory -Recurse }
        Set-Content -LiteralPath (Join-Path $appDirectory 'custom-update-test.ini') -Value 'preserve custom file' -Encoding utf8NoBOM
        $record = [ordered]@{ kind = $kind; passed = $false; downloadedBytes = 0L; reusedBytes = 0L; components = @(); gracefulExit = $false; restarted = $false; verified = $false; uninstalled = $false }
        $updateResults.Add($record)
        foreach ($component in $manifest.components) {
            $matches = $true
            foreach ($file in $component.files) {
                $local = Assert-UpdateTestPath (Join-Path $appDirectory $file.path)
                if (-not (Test-Path -LiteralPath $local -PathType Leaf) -or (Get-Item -LiteralPath $local).Length -ne $file.bytes -or
                    (Get-FileHash -LiteralPath $local).Hash.ToLowerInvariant() -ne $file.sha256) { $matches = $false; break }
            }
            if ($matches) { $record.reusedBytes += ($component.files | Measure-Object -Property bytes -Sum).Sum; continue }
            $zip = Join-Path ([IO.Path]::GetDirectoryName($manifestAsset)) "Mambo-$($manifest.version)-win-x64-update-$($component.name).zip"
            if ((Get-FileHash -LiteralPath $zip).Hash.ToLowerInvariant() -ne $component.sha256) { throw '验收组件包校验失败。' }
            [IO.Compression.ZipFile]::ExtractToDirectory($zip, (Join-Path $staging 'payload'))
            $record.downloadedBytes += $component.bytes
            $record.components += $component.name
        }
        Copy-Item -LiteralPath $manifestAsset -Destination (Join-Path $staging 'update.json')
        $helperSource = if ($UpdaterPath) { Assert-UpdateTestPath $UpdaterPath } else { Join-Path $published 'Mambo.Updater.exe' }
        Copy-Item -LiteralPath $helperSource -Destination $staging
        Write-Host "复用检查完成；需要更新组件：$($record.components -join ', ')。"
        $parent = Start-UpdateTestProcess (Join-Path $appDirectory 'Mambo.exe') @('--fake')
        Wait-UpdateTestWindow $parent
        [ordered]@{ applicationDirectory = $appDirectory; parentProcessId = $parent.Id; parentStartTimeUtcTicks = $parent.StartTime.ToUniversalTime().Ticks; manifestSha256 = (Get-FileHash -LiteralPath $manifestAsset).Hash.ToLowerInvariant() } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $staging 'request.json') -Encoding utf8NoBOM
        $helper = Start-UpdateTestProcess (Join-Path $staging 'Mambo.Updater.exe') @('--apply', $staging)
        $deadline = [DateTime]::UtcNow.AddSeconds(60)
        while (-not (Test-Path -LiteralPath (Join-Path $staging 'ready'))) {
            if ($helper.HasExited -or [DateTime]::UtcNow -ge $deadline) { throw '更新程序没有就绪。' }
            Start-Sleep -Milliseconds 100
        }
        if (-not $parent.CloseMainWindow() -or -not $parent.WaitForExit(30000) -or $parent.ExitCode -ne 0) { throw '旧进程没有正常关闭。' }
        $record.gracefulExit = $true
        if (-not $helper.WaitForExit(60000) -or $helper.ExitCode -ne 0) {
            if (Test-Path -LiteralPath (Join-Path $staging 'failure.json')) { Get-Content -LiteralPath (Join-Path $staging 'failure.json') -Raw | Out-Host }
            throw '独立更新程序没有成功完成。'
        }
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        $restarted = $null
        do {
            $restarted = Get-Process Mambo -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $parent.Id -and $_.Path -eq (Join-Path $appDirectory 'Mambo.exe') } | Select-Object -First 1
            if ($restarted) { break }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $deadline)
        if (-not $restarted) { throw '新版没有重新启动。' }
        $null = $restarted.Handle
        $updateOwnedProcesses.Add([pscustomobject]@{ Process = $restarted; Executable = (Join-Path $appDirectory 'Mambo.exe') })
        Wait-UpdateTestWindow $restarted
        $record.restarted = $true
        Assert-UpdateTestFiles $appDirectory $manifest
        $record.verified = $true
        if (-not $restarted.CloseMainWindow() -or -not $restarted.WaitForExit(30000) -or $restarted.ExitCode -ne 0) { throw '新版退出失败。' }
        if ($kind -eq 'installed') {
            if ((Get-ItemProperty -LiteralPath $updateRegistration).DisplayVersion -ne $manifest.version) { throw '安装版本注册信息没有更新。' }
            Invoke-UpdateTestUninstall
            if (Test-Path -LiteralPath $updateRegistration) { throw '卸载注册项仍然存在。' }
            foreach ($component in $manifest.components) {
                foreach ($file in $component.files) {
                    if (Test-Path -LiteralPath (Join-Path $appDirectory $file.path)) { throw '卸载后仍残留新版应用文件。' }
                }
            }
            $record.uninstalled = $true
            $updateInstalledDirectory = $null
        }
        if ((Get-Content -LiteralPath (Join-Path $appDirectory 'custom-update-test.ini') -Raw).Trim() -ne 'preserve custom file') { throw '自定义文件被改变。' }
        $record.passed = $true
    }
} finally {
    foreach ($entry in $updateOwnedProcesses) {
        try {
            if (-not $entry.Process.HasExited -and $entry.Process.MainModule.FileName -eq $entry.Executable) {
                $null = $entry.Process.CloseMainWindow()
                if (-not $entry.Process.WaitForExit(3000)) { $entry.Process.Kill(); $null = $entry.Process.WaitForExit(5000) }
            }
        } finally { $entry.Process.Dispose() }
    }
    if ($updateInstalledDirectory -and (Test-Path -LiteralPath $updateRegistration)) {
        try { Invoke-UpdateTestUninstall } catch { Write-Warning '本轮测试安装的清理未完成，请查看验收目录。' }
    }
    $env:MAMBO_FAKE = $updateOldFake
    if (Test-Path -LiteralPath $updateRun) {
        $updateResults | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $updateRun 'result.json') -Encoding utf8NoBOM
        $updateResults | ConvertTo-Json -Depth 5
        Write-Host "更新验收目录：$updateRun"
    }
}
