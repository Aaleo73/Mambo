#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SetupPath,
    [Parameter(Mandatory)][string]$PublishDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$installerRepository = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$installerValidationRoot = Join-Path $installerRepository 'artifacts/installer-validation'
$installerRunRoot = Join-Path $installerValidationRoot ([Guid]::NewGuid().ToString('N'))
$installerAppDirectory = Join-Path $installerRunRoot 'app'
$installerAppId = '{A36B6A2B-B280-4C39-9517-C22230A61E8C}_is1'
$installerRegistrationPaths = @(
    "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$installerAppId",
    "HKCU:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\$installerAppId",
    "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$installerAppId",
    "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\$installerAppId"
)
$installerOwnedProcesses = [Collections.Generic.List[object]]::new()
$installerOwnedInstallation = $false
$installerUninstallCompleted = $false
$installerMarker = $null
$installerMarkerHash = $null
$installerMarkerCreated = $false
$installerUserDataRoot = $null
$installerUserSnapshot = $null
$installerPreUninstallSnapshot = $null
$installerManifest = $null
$installerFakeProcess = $null
$installerStage = '检查参数与现有安装'
$installerResult = [ordered]@{
    schemaVersion = 1
    status = 'Failed'
    reason = $null
    startedUtc = [DateTimeOffset]::UtcNow
    completedUtc = $null
    setupSha256 = $null
    manifestSha256 = $null
    version = $null
    installedManifestFileCount = 0
    firstInstall = $false
    initialHashesMatched = $false
    installedUiSmoke = $false
    installedUiFailureReason = $null
    LifecyclePassed = $false
    upgradedOwnedProcessId = $null
    upgradedProcessExitCode = $null
    upgradeReadinessOutcome = 'NotStarted'
    upgradeReadinessWaitMilliseconds = 0L
    upgradeClosedOwnedProcess = $false
    upgradeHashesMatched = $false
    uninstall = $false
    installedFilesRemoved = $false
    uninstallerFilesRemoved = $false
    registrationRemoved = $false
    originalUserFileCount = 0
    originalUserFilesPreserved = $false
    originalUserFilesChangedBeforeUninstall = 0
    originalUserFilesMissingBeforeUninstall = 0
    userFilesAddedBeforeUninstall = 0
    originalUserFilesChangedAtEnd = 0
    originalUserFilesMissingAtEnd = 0
    userFilesAddedAtEnd = 0
    preUninstallSnapshotTaken = $false
    preUninstallUserFileCount = 0
    preUninstallUserFilesChangedAtEnd = 0
    preUninstallUserFilesMissingAtEnd = 0
    userFilesAddedAfterUninstall = 0
    preUninstallUserFilesPreserved = $false
    uninstallUserDataPreserved = $false
    userDataPreservationScope = '全部卸载前文件按哈希、长度和文件集合严格核验；安装前基线及其删改聚合保留，不推断修改原因。'
    markerPreservedByUninstall = $false
    markerRemoved = $false
    cleanupForcedOwnedProcess = $false
    cleanupUninstallAttempted = $false
    cleanupUninstallCompleted = $false
    cleanupVerificationAttempted = $false
    cleanupUninstallVerified = $false
    cleanupVerificationFailureReason = $null
    unconfirmedInstallationLeft = $false
}

function Stop-InstallerUnsupported([string]$Code) {
    $failure = [InvalidOperationException]::new('此环境不支持安全完成安装器验收。')
    $failure.Data['InstallerUnsupported'] = $Code
    throw $failure
}

function Assert-InstallerChildPath([string]$Path, [string]$Parent) {
    if ($Path -match '[\x00-\x1f"]') { Stop-InstallerUnsupported 'InvalidPath' }
    $absolute = [IO.Path]::GetFullPath($Path)
    $boundary = [IO.Path]::GetFullPath($Parent).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $absolute.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { Stop-InstallerUnsupported 'PathOutsideRepository' }
    # Do not accept junction/symlink ancestors that redirect owned file operations elsewhere.
    $current = $absolute
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $entry = Get-Item -LiteralPath $current -Force
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { Stop-InstallerUnsupported 'ReparsePath' }
        }
        $parentPath = [IO.Path]::GetDirectoryName($current)
        if ($parentPath -eq $current) { break }
        $current = $parentPath
    }
    return $absolute
}

function Get-InstallerRegistrations {
    foreach ($registryPath in $installerRegistrationPaths) {
        if (Test-Path -LiteralPath $registryPath -ErrorAction Stop) {
            [pscustomobject]@{ Key = $registryPath; Values = Get-ItemProperty -LiteralPath $registryPath -ErrorAction Stop }
        }
    }
}

function Test-InstallerRegistrationOwnership {
    $registrations = @(Get-InstallerRegistrations)
    if ($registrations.Count -ne 1) { return $false }
    $values = $registrations[0].Values
    if (-not $values.PSObject.Properties['InstallLocation'] -or -not $values.PSObject.Properties['UninstallString']) { return $false }
    $installed = [IO.Path]::GetFullPath($values.InstallLocation).TrimEnd('\', '/')
    $expected = $installerAppDirectory.TrimEnd('\', '/')
    if (-not $installed.Equals($expected, [StringComparison]::OrdinalIgnoreCase)) { return $false }
    $uninstaller = Join-Path $installerAppDirectory 'unins000.exe'
    return $values.UninstallString.Trim().Equals(('"' + $uninstaller + '"'), [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $uninstaller -PathType Leaf)
}

function Start-InstallerOwnedProcess([string]$FilePath, [string[]]$Arguments) {
    $absolute = Assert-InstallerChildPath $FilePath $installerRepository
    if (-not (Test-Path -LiteralPath $absolute -PathType Leaf)) { Stop-InstallerUnsupported 'OwnedExecutableMissing' }
    $process = Start-Process -FilePath $absolute -ArgumentList $Arguments -WorkingDirectory ([IO.Path]::GetDirectoryName($absolute)) -WindowStyle Hidden -PassThru
    # Open and retain the handle returned for this launch; never find/kill processes by name.
    $null = $process.Handle
    $installerOwnedProcesses.Add([pscustomobject]@{ Process = $process; Executable = $absolute })
    return $process
}

function Invoke-InstallerSetup([string]$LogName) {
    $log = Assert-InstallerChildPath (Join-Path $installerRunRoot $LogName) $installerRunRoot
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOICONS', '/TASKS=""', '/CLOSEAPPLICATIONS',
        ('/DIR="' + $installerAppDirectory + '"'), ('/LOG="' + $log + '"'))
    # Deliberately never pass /FORCECLOSEAPPLICATIONS or force-close the old application.
    $setup = Start-InstallerOwnedProcess $SetupPath $arguments
    if (-not $setup.WaitForExit(180000)) { Stop-InstallerUnsupported 'SetupTimedOut' }
    if ($setup.ExitCode -ne 0) { Stop-InstallerUnsupported ('SetupExit' + $setup.ExitCode) }
}

function Assert-InstallerManifest([string]$Directory) {
    foreach ($file in $installerManifest.files) {
        if ([string]::IsNullOrWhiteSpace($file.path) -or [IO.Path]::IsPathRooted($file.path) -or $file.sha256 -notmatch '^[0-9a-fA-F]{64}$') {
            Stop-InstallerUnsupported 'InvalidManifest'
        }
        $path = Assert-InstallerChildPath (Join-Path $Directory $file.path) $Directory
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -ne $file.bytes -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256.ToLowerInvariant()) {
            throw [InvalidOperationException]::new('安装后的发布文件校验不匹配。')
        }
    }
    $manifestCopy = Join-Path $Directory 'release-manifest.json'
    if (-not (Test-Path -LiteralPath $manifestCopy -PathType Leaf) -or
        (Get-FileHash -LiteralPath $manifestCopy -Algorithm SHA256).Hash.ToLowerInvariant() -ne $installerResult.manifestSha256) {
        throw [InvalidOperationException]::new('安装后的发布清单校验不匹配。')
    }
}

function Get-InstallerPrivateSnapshot([string]$Root) {
    $snapshot = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { return ,$snapshot }
    if (((Get-Item -LiteralPath $Root -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { Stop-InstallerUnsupported 'UserDataRootReparsePoint' }
    $directories = [Collections.Generic.Stack[string]]::new()
    $directories.Push($Root)
    try {
        while ($directories.Count -gt 0) {
            foreach ($entry in ([IO.DirectoryInfo]::new($directories.Pop())).GetFileSystemInfos()) {
                if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { Stop-InstallerUnsupported 'UserDataContainsReparsePoint' }
                if ($entry -is [IO.DirectoryInfo]) { $directories.Push($entry.FullName) }
                else {
                    # File paths and hashes stay in RAM only. Never serialize or print them.
                    $snapshot.Add($entry.FullName, [pscustomobject]@{ Bytes = $entry.Length; Hash = (Get-FileHash -LiteralPath $entry.FullName -Algorithm SHA256 -ErrorAction Stop).Hash })
                }
            }
        }
    }
    catch {
        if ($_.Exception.Data.Contains('InstallerUnsupported')) { throw }
        Stop-InstallerUnsupported 'UserDataNotReadable'
    }
    return ,$snapshot
}

function Compare-InstallerPrivateSnapshots($Before, $After) {
    $difference = [ordered]@{ unchanged = 0; changed = 0; missing = 0; added = 0 }
    foreach ($item in $Before.GetEnumerator()) {
        if (-not $After.ContainsKey($item.Key)) { $difference.missing++ }
        elseif ($After[$item.Key].Bytes -ne $item.Value.Bytes -or $After[$item.Key].Hash -ne $item.Value.Hash) { $difference.changed++ }
        else { $difference.unchanged++ }
    }
    foreach ($path in $After.Keys) { if (-not $Before.ContainsKey($path)) { $difference.added++ } }
    # Return counts only. File identities and hashes in both snapshots never leave memory.
    return [pscustomobject]$difference
}

function Initialize-InstallerPreUninstallSnapshot {
    if ($null -ne $script:installerPreUninstallSnapshot) { return }
    $installedExecutable = [IO.Path]::GetFullPath((Join-Path $installerAppDirectory 'Mambo.exe'))
    foreach ($entry in $installerOwnedProcesses) {
        if ($entry.Executable.Equals($installedExecutable, [StringComparison]::OrdinalIgnoreCase) -and -not $entry.Process.HasExited) {
            Stop-InstallerUnsupported 'OwnedApplicationStillRunningBeforeUninstall'
        }
    }
    $snapshot = Get-InstallerPrivateSnapshot $installerUserDataRoot
    $difference = Compare-InstallerPrivateSnapshots $installerUserSnapshot $snapshot
    $script:installerPreUninstallSnapshot = $snapshot
    $installerResult.preUninstallSnapshotTaken = $true
    $installerResult.preUninstallUserFileCount = $snapshot.Count
    $installerResult.originalUserFilesChangedBeforeUninstall = $difference.changed
    $installerResult.originalUserFilesMissingBeforeUninstall = $difference.missing
    $installerResult.userFilesAddedBeforeUninstall = $difference.added
}

function Invoke-InstallerOwnedUninstall([string]$LogName) {
    if (-not $installerOwnedInstallation -or -not (Test-InstallerRegistrationOwnership)) { Stop-InstallerUnsupported 'UninstallOwnershipUnconfirmed' }
    $uninstaller = Assert-InstallerChildPath (Join-Path $installerAppDirectory 'unins000.exe') $installerAppDirectory
    $log = Assert-InstallerChildPath (Join-Path $installerRunRoot $LogName) $installerRunRoot
    # Capture once, after owned applications have exited and immediately before the first uninstall attempt.
    # Cleanup retries reuse the same baseline and cannot hide a partial uninstall's user-data changes.
    Initialize-InstallerPreUninstallSnapshot
    $process = Start-InstallerOwnedProcess $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/LOG="' + $log + '"'))
    if (-not $process.WaitForExit(120000)) { Stop-InstallerUnsupported 'UninstallTimedOut' }
    if ($process.ExitCode -ne 0) { Stop-InstallerUnsupported ('UninstallExit' + $process.ExitCode) }
}

function Wait-InstallerOwnedWindow([Diagnostics.Process]$Process) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $installerResult.upgradeReadinessOutcome = 'WaitingForWindow'
    try {
        # InputIdle can precede WinUI's actual MainWindow. Poll only this retained launch until a window or a fixed deadline.
        while ($clock.ElapsedMilliseconds -lt 30000) {
            $Process.Refresh()
            if ($Process.HasExited) {
                $installerResult.upgradedProcessExitCode = $Process.ExitCode
                $installerResult.upgradeReadinessOutcome = 'ExitedBeforeWindow'
                Stop-InstallerUnsupported 'InstalledApplicationExitedBeforeWindow'
            }
            if ($Process.MainWindowHandle -ne [IntPtr]::Zero -and -not $Process.HasExited) {
                $installerResult.upgradeReadinessOutcome = 'WindowReady'
                return
            }
            Start-Sleep -Milliseconds 100
        }
        if ($Process.HasExited) {
            $installerResult.upgradedProcessExitCode = $Process.ExitCode
            $installerResult.upgradeReadinessOutcome = 'ExitedBeforeWindow'
            Stop-InstallerUnsupported 'InstalledApplicationExitedBeforeWindow'
        }
        $installerResult.upgradeReadinessOutcome = 'WindowTimedOut'
        Stop-InstallerUnsupported 'InstalledApplicationWindowTimedOut'
    }
    finally { $installerResult.upgradeReadinessWaitMilliseconds = $clock.ElapsedMilliseconds; $clock.Stop() }
}

function Test-InstallerUninstallState {
    # Read-only verification, also used after failure cleanup; it never changes the overall result to Passed.
    $uninstallerFiles = @('unins000.exe', 'unins000.dat')
    $removedDeadline = [DateTime]::UtcNow.AddSeconds(10)
    while (@($uninstallerFiles | Where-Object { Test-Path -LiteralPath (Join-Path $installerAppDirectory $_) }).Count -ne 0 -and
        [DateTime]::UtcNow -lt $removedDeadline) { Start-Sleep -Milliseconds 100 }
    $remaining = @($installerManifest.files | Where-Object { Test-Path -LiteralPath (Join-Path $installerAppDirectory $_.path) })
    $installerResult.installedFilesRemoved = $remaining.Count -eq 0 -and -not (Test-Path -LiteralPath (Join-Path $installerAppDirectory 'release-manifest.json'))
    $installerResult.uninstallerFilesRemoved = @($uninstallerFiles | Where-Object { Test-Path -LiteralPath (Join-Path $installerAppDirectory $_) }).Count -eq 0
    $installerResult.registrationRemoved = @(Get-InstallerRegistrations).Count -eq 0
    $afterSnapshot = Get-InstallerPrivateSnapshot $installerUserDataRoot
    $originalDifference = Compare-InstallerPrivateSnapshots $installerUserSnapshot $afterSnapshot
    $installerResult.originalUserFilesPreserved = $originalDifference.changed -eq 0 -and $originalDifference.missing -eq 0
    $installerResult.originalUserFilesChangedAtEnd = $originalDifference.changed
    $installerResult.originalUserFilesMissingAtEnd = $originalDifference.missing
    $installerResult.userFilesAddedAtEnd = $originalDifference.added
    if ($null -ne $installerPreUninstallSnapshot) {
        $uninstallDifference = Compare-InstallerPrivateSnapshots $installerPreUninstallSnapshot $afterSnapshot
        $installerResult.preUninstallUserFilesChangedAtEnd = $uninstallDifference.changed
        $installerResult.preUninstallUserFilesMissingAtEnd = $uninstallDifference.missing
        $installerResult.userFilesAddedAfterUninstall = $uninstallDifference.added
        $installerResult.preUninstallUserFilesPreserved = $uninstallDifference.changed -eq 0 -and $uninstallDifference.missing -eq 0 -and $uninstallDifference.added -eq 0
        $installerResult.uninstallUserDataPreserved = $installerResult.preUninstallUserFilesPreserved -and $installerResult.originalUserFilesMissingBeforeUninstall -eq 0
    }
    $installerResult.markerPreservedByUninstall = (Test-Path -LiteralPath $installerMarker -PathType Leaf) -and
        (Get-FileHash -LiteralPath $installerMarker -Algorithm SHA256).Hash -eq $installerMarkerHash
    return $installerResult.installedFilesRemoved -and $installerResult.uninstallerFilesRemoved -and $installerResult.registrationRemoved -and
        $installerResult.uninstallUserDataPreserved -and $installerResult.markerPreservedByUninstall
}

function Clear-InstallerOwnedProcesses {
    foreach ($entry in $installerOwnedProcesses) {
        $process = $entry.Process
        try {
            if ($process.HasExited) { continue }
            if (-not ([IO.Path]::GetFullPath($process.MainModule.FileName)).Equals($entry.Executable, [StringComparison]::OrdinalIgnoreCase)) {
                $installerResult.reason = 'OwnedProcessIdentityChanged'
                $installerResult.status = 'Failed'
                continue
            }
            $null = $process.CloseMainWindow()
            if (-not $process.WaitForExit(5000)) {
                # Cleanup only; this can never count as passing the graceful upgrade check.
                $process.Kill($false)
                $null = $process.WaitForExit(5000)
                $installerResult.cleanupForcedOwnedProcess = $true
                if ($installerResult.status -eq 'Passed') { $installerResult.status = 'Failed'; $installerResult.reason = 'ForcedCleanupRequired' }
            }
        }
        catch { $installerResult.status = 'Failed'; $installerResult.reason = 'OwnedProcessCleanupFailed' }
    }
}

try {
    if (-not $IsWindows) { Stop-InstallerUnsupported 'WindowsRequired' }
    $SetupPath = Assert-InstallerChildPath $SetupPath $installerRepository
    $PublishDirectory = Assert-InstallerChildPath $PublishDirectory $installerRepository
    $null = Assert-InstallerChildPath $installerRunRoot $installerRepository
    if (Test-Path -LiteralPath $installerRunRoot) { Stop-InstallerUnsupported 'ValidationDirectoryExists' }
    New-Item -ItemType Directory -Path $installerRunRoot | Out-Null
    # Refuse a mismatched sandbox HKCU/profile, rather than missing an existing real-user installation.
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $profileKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\' + $identity.User.Value
    try { $profile = Get-ItemProperty -LiteralPath $profileKey -Name ProfileImagePath -ErrorAction Stop }
    catch { Stop-InstallerUnsupported 'CurrentUserProfileNotReadable' }
    if (-not ([IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($profile.ProfileImagePath))).TrimEnd('\', '/').Equals(
            ([IO.Path]::GetFullPath($env:USERPROFILE)).TrimEnd('\', '/'), [StringComparison]::OrdinalIgnoreCase)) {
        Stop-InstallerUnsupported 'CurrentUserProfileMismatch'
    }
    if (@(Get-InstallerRegistrations).Count -ne 0) { Stop-InstallerUnsupported 'ExistingMamboInstallation' }
    if (-not (Test-Path -LiteralPath $SetupPath -PathType Leaf)) { Stop-InstallerUnsupported 'SetupMissing' }
    $installerManifestPath = Assert-InstallerChildPath (Join-Path $PublishDirectory 'release-manifest.json') $PublishDirectory
    $installerManifest = Get-Content -LiteralPath $installerManifestPath -Raw | ConvertFrom-Json
    if ($installerManifest.schemaVersion -ne 1 -or $installerManifest.architecture -ne 'win-x64' -or -not $installerManifest.nativeAot -or
        -not $installerManifest.selfContained -or @($installerManifest.files).Count -eq 0) { Stop-InstallerUnsupported 'UnsupportedManifest' }
    $installerResult.setupSha256 = (Get-FileHash -LiteralPath $SetupPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $installerResult.manifestSha256 = (Get-FileHash -LiteralPath $installerManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $installerResult.version = $installerManifest.version
    $installerResult.installedManifestFileCount = @($installerManifest.files).Count
    Assert-InstallerManifest $PublishDirectory
    $installerStage = '在内存中保存原有用户数据哈希'
    $knownLocalAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    if ([string]::IsNullOrWhiteSpace($knownLocalAppData) -or
        -not ([IO.Path]::GetFullPath($knownLocalAppData)).TrimEnd('\', '/').Equals(
            ([IO.Path]::GetFullPath($env:LOCALAPPDATA)).TrimEnd('\', '/'), [StringComparison]::OrdinalIgnoreCase)) {
        Stop-InstallerUnsupported 'CurrentUserLocalAppDataMismatch'
    }
    $installerUserDataRoot = [IO.Path]::GetFullPath((Join-Path $knownLocalAppData 'Mambo'))
    # A user data root alias cannot be safely treated as the actual application data directory.
    if ((Test-Path -LiteralPath $installerUserDataRoot) -and
        ((Get-Item -LiteralPath $installerUserDataRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        Stop-InstallerUnsupported 'UserDataRootReparsePoint'
    }
    $installerUserSnapshot = Get-InstallerPrivateSnapshot $installerUserDataRoot
    $installerResult.originalUserFileCount = $installerUserSnapshot.Count
    New-Item -ItemType Directory -Path $installerUserDataRoot -Force | Out-Null
    $installerMarker = Join-Path $installerUserDataRoot ('.installer-validation-' + [Guid]::NewGuid().ToString('N') + '.marker')
    $markerBytes = [Text.Encoding]::UTF8.GetBytes('Mambo installer validation ' + [Guid]::NewGuid().ToString('N'))
    $installerMarkerHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($markerBytes))
    $markerStream = [IO.FileStream]::new($installerMarker, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    $installerMarkerCreated = $true
    try { $markerStream.Write($markerBytes) } finally { $markerStream.Dispose() }

    $installerStage = '首次静默安装与全部发布文件哈希'
    Invoke-InstallerSetup 'install.log'
    if (-not (Test-InstallerRegistrationOwnership)) { Stop-InstallerUnsupported 'InstalledRegistrationMismatch' }
    $installerOwnedInstallation = $true
    $installerResult.firstInstall = $true
    Assert-InstallerManifest $installerAppDirectory
    $installerResult.initialHashesMatched = $true

    $installerStage = '从安装目录运行 UI 验收'
    $uiReport = Assert-InstallerChildPath (Join-Path $installerRunRoot 'ui-smoke.json') $installerRunRoot
    try {
        & (Join-Path $installerRepository 'scripts/test-ui-lab.ps1') -Aot -AppDirectory $installerAppDirectory -ReportPath $uiReport
        $ui = Get-Content -LiteralPath $uiReport -Raw | ConvertFrom-Json
        if ($ui.Passed -isnot [bool] -or -not $ui.Passed) { throw [InvalidOperationException]::new('安装后的 UI 验收未通过。') }
        $installerResult.installedUiSmoke = $true
    }
    catch {
        if ($_.Exception -isnot [InvalidOperationException] -or -not $_.Exception.Data.Contains('UiOnlyRetainedFailure') -or
            $_.Exception.Data['UiOnlyRetainedFailure'] -isnot [bool] -or -not $_.Exception.Data['UiOnlyRetainedFailure']) { throw }
        # This marker is emitted only after a new, complete report and normal exit, with all functional checks passed.
        # Keep the UI result failed while independently exercising upgrade and uninstall.
        $installerResult.installedUiFailureReason = 'RetainedObjects'
        Write-Host '安装目录的 UI 对象释放未通过；继续验证升级和卸载，整体结果仍为失败。'
    }

    $installerStage = '同版本升级优雅关闭本轮应用'
    $installedExecutable = Assert-InstallerChildPath (Join-Path $installerAppDirectory 'Mambo.exe') $installerAppDirectory
    $installerFakeProcess = Start-InstallerOwnedProcess $installedExecutable @('--fake')
    $installerResult.upgradedOwnedProcessId = $installerFakeProcess.Id
    Wait-InstallerOwnedWindow $installerFakeProcess
    if (-not (Test-InstallerRegistrationOwnership)) { Stop-InstallerUnsupported 'UpgradeOwnershipChanged' }
    Invoke-InstallerSetup 'upgrade.log'
    if (-not $installerFakeProcess.WaitForExit(10000)) { Stop-InstallerUnsupported 'UpgradeDidNotCloseOwnedProcess' }
    $installerResult.upgradedProcessExitCode = $installerFakeProcess.ExitCode
    if ($installerFakeProcess.ExitCode -ne 0) { throw [InvalidOperationException]::new('升级时本轮应用没有正常退出。') }
    $installerResult.upgradeClosedOwnedProcess = $true
    Assert-InstallerManifest $installerAppDirectory
    $installerResult.upgradeHashesMatched = $true

    $installerStage = '卸载本轮安装并保留用户数据'
    Invoke-InstallerOwnedUninstall 'uninstall.log'
    $installerUninstallCompleted = $true
    $installerResult.uninstall = $true
    if (-not (Test-InstallerUninstallState)) {
        throw [InvalidOperationException]::new('卸载文件清理或用户数据保留验收未通过。')
    }
    $installerResult.LifecyclePassed = $true
    if ($installerResult.installedUiFailureReason -eq 'RetainedObjects') {
        $installerResult.status = 'Failed'
        $installerResult.reason = 'InstalledUiRetainedObjects'
    }
    else { $installerResult.status = 'Passed' }
}
catch {
    if ($_.Exception.Data.Contains('InstallerUnsupported')) {
        $installerResult.status = 'Unsupported'
        $installerResult.reason = [string]$_.Exception.Data['InstallerUnsupported']
    }
    else {
        $installerResult.status = 'Failed'
        # No exception text/stack: failures reading private files can contain their names.
        $installerResult.reason = $installerStage + ':' + $_.Exception.GetType().Name
    }
}
finally {
    Clear-InstallerOwnedProcesses
    if ($installerOwnedInstallation -and -not $installerUninstallCompleted) {
        try {
            if (Test-InstallerRegistrationOwnership) {
                $installerResult.cleanupUninstallAttempted = $true
                Invoke-InstallerOwnedUninstall 'cleanup-uninstall.log'
                $installerUninstallCompleted = $true
                $installerResult.cleanupUninstallCompleted = $true
            }
        }
        catch { $installerResult.status = 'Failed'; $installerResult.reason = 'OwnedUninstallCleanupFailed' }
        # A timed-out cleanup uninstaller is also this invocation's retained process, not another installation.
        Clear-InstallerOwnedProcesses
    }
    if ($installerOwnedInstallation -and $installerResult.status -ne 'Passed') {
        $installerResult.cleanupVerificationAttempted = $true
        try {
            $installerResult.cleanupUninstallVerified = Test-InstallerUninstallState
            if (-not $installerResult.cleanupUninstallVerified) { $installerResult.cleanupVerificationFailureReason = 'UninstallStateNotPreserved' }
        }
        catch {
            # No private file names, hashes, exception text or stack are serialized.
            $installerResult.cleanupVerificationFailureReason = 'UninstallState.' + $_.Exception.GetType().Name
        }
    }
    if (-not $installerOwnedInstallation) {
        try { $installerResult.unconfirmedInstallationLeft = Test-InstallerRegistrationOwnership }
        catch { $installerResult.status = 'Failed'; $installerResult.reason = 'UnconfirmedInstallationCheckFailed' }
    }
    # The only deletion outside the repository is the exact marker created by this invocation.
    if ($installerMarkerCreated) {
        try {
            $expectedMarkerParent = [IO.Path]::GetFullPath([IO.Path]::GetDirectoryName($installerMarker))
            if (-not $expectedMarkerParent.Equals($installerUserDataRoot, [StringComparison]::OrdinalIgnoreCase)) { throw [InvalidOperationException]::new() }
            if ((Test-Path -LiteralPath $installerUserDataRoot) -and
                ((Get-Item -LiteralPath $installerUserDataRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw [InvalidOperationException]::new() }
            if (Test-Path -LiteralPath $installerMarker -PathType Leaf) {
                if (((Get-Item -LiteralPath $installerMarker -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw [InvalidOperationException]::new() }
                if ((Get-FileHash -LiteralPath $installerMarker -Algorithm SHA256).Hash -ne $installerMarkerHash) { throw [InvalidOperationException]::new() }
                Remove-Item -LiteralPath $installerMarker
            }
            $installerResult.markerRemoved = -not (Test-Path -LiteralPath $installerMarker)
        }
        catch { $installerResult.status = 'Failed'; $installerResult.reason = 'OwnedMarkerCleanupFailed' }
    }
    foreach ($entry in $installerOwnedProcesses) { $entry.Process.Dispose() }
    # Cleanup is part of the lifecycle. A UI-only failure may coexist with a passing installer lifecycle.
    if ($installerResult.cleanupForcedOwnedProcess -or -not $installerResult.markerRemoved -or
        ($installerResult.cleanupVerificationAttempted -and -not $installerResult.cleanupUninstallVerified) -or
        ($installerResult.status -ne 'Passed' -and $installerResult.reason -ne 'InstalledUiRetainedObjects')) {
        $installerResult.LifecyclePassed = $false
    }
    $installerResult.completedUtc = [DateTimeOffset]::UtcNow
    if (Test-Path -LiteralPath $installerRunRoot -PathType Container) {
        $installerResult | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $installerRunRoot 'result.json') -Encoding utf8NoBOM
    }
    # Names, contents and hash values of existing user data files are never emitted.
    $installerResult | ConvertTo-Json -Depth 4
    Write-Host "安装器验收状态：$($installerResult.status)；本轮目录：$installerRunRoot"
}

# A terminating, fixed error makes pwsh and callers fail without printing private exception data.
if ($installerResult.status -ne 'Passed') { throw "安装器验收未通过：$($installerResult.status) / $($installerResult.reason)。" }
