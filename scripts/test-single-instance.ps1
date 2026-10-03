#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$AppDirectory, [switch]$SkipLifetimeTrace)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$singleRepository = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$singleRunRoot = Join-Path $singleRepository ('artifacts/single-instance-validation/' + [Guid]::NewGuid().ToString('N'))
$singleAppDirectory = Join-Path $singleRunRoot 'app'
$singleReportPath = Join-Path $singleRunRoot 'result.json'
$singleOwnedProcesses = [Collections.Generic.List[object]]::new()
$singleStage = 'Prepare'
$singleClock = [Diagnostics.Stopwatch]::StartNew()
$singleResult = [ordered]@{
    schemaVersion = 1
    status = 'Failed'
    reason = $null
    stage = $singleStage
    startedUtc = [DateTimeOffset]::UtcNow
    completedUtc = $null
    elapsedMilliseconds = 0
    copiedFileCount = 0
    executableCopiedUnchanged = $false
    fakeModeRequested = $true
    firstProcessId = $null
    firstIdentityVerified = $false
    firstWindowObserved = $false
    firstWindowCountPeak = 0
    firstStartupMilliseconds = $null
    secondProcessId = $null
    secondIdentityVerified = $false
    secondExitCode = $null
    secondExitMilliseconds = $null
    secondWindowObservationCount = 0
    secondWindowCountPeak = 0
    firstStillRunningAfterRedirect = $false
    gracefulCloseRequested = $false
    gracefulExitWithinDeadline = $false
    firstExitCode = $null
    firstCloseMilliseconds = $null
    cleanupForcedOwnedProcess = $false
    cleanupIdentityNotVerified = $false
    errorKind = $null
    hResult = $null
}

function Stop-SingleValidation([string]$Code, [switch]$Unsupported) {
    $failure = [InvalidOperationException]::new('单实例验证未完成。')
    $failure.Data['SingleInstanceReason'] = $Code
    $failure.Data['SingleInstanceUnsupported'] = $Unsupported.IsPresent
    throw $failure
}

function Assert-SinglePlainPath([string]$Path) {
    if ($Path -match '[\x00-\x1f"]') { Stop-SingleValidation 'InvalidPath' -Unsupported }
    $absolute = [IO.Path]::GetFullPath($Path)
    $current = $absolute
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if (((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                Stop-SingleValidation 'ReparsePath' -Unsupported
            }
        }
        $parentPath = [IO.Path]::GetDirectoryName($current)
        if ($parentPath -eq $current) { break }
        $current = $parentPath
    }
    return $absolute
}

function Assert-SingleOwnedPath([string]$Path) {
    $absolute = Assert-SinglePlainPath $Path
    $boundary = $singleRepository.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $absolute.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) {
        Stop-SingleValidation 'RunPathOutsideRepository' -Unsupported
    }
    return $absolute
}

function Copy-SingleApplication([string]$Source, [string]$Destination) {
    # Walk entries without following junctions or links; never copy personal data from an indirect path.
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($Source)
    $count = 0
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($directory)) {
            $attributes = [IO.File]::GetAttributes($entry)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                Stop-SingleValidation 'ReparseApplicationEntry' -Unsupported
            }
            $relative = [IO.Path]::GetRelativePath($Source, $entry)
            $target = Assert-SingleOwnedPath (Join-Path $Destination $relative)
            if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                $null = [IO.Directory]::CreateDirectory($target)
                $pending.Push($entry)
            } else {
                [IO.File]::Copy($entry, $target, $false)
                $count++
            }
        }
    }
    return $count
}

function Start-SingleOwnedProcess([string]$Executable) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.WorkingDirectory = $singleAppDirectory
    $start.ArgumentList.Add('--fake')
    # Environment belongs only to this child; fake composition never opens the actual account store.
    $start.Environment['MAMBO_FAKE'] = '1'
    $start.Environment['MAMBO_FAKE_DELAY_MS'] = '10'
    $start.Environment['MAMBO_FAKE_FAILURE_RATE'] = '0'
    if ($SkipLifetimeTrace) { $null = $start.Environment.Remove('MAMBO_FAKE_LIFETIME_REPORT_DIR') }
    else { $start.Environment['MAMBO_FAKE_LIFETIME_REPORT_DIR'] = $singleRunRoot }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $launchedUtc = [DateTime]::UtcNow
    if (-not $process.Start()) { $process.Dispose(); Stop-SingleValidation 'ProcessNotStarted' }
    $owned = [pscustomobject]@{
        Process = $process
        ExpectedPath = $Executable
        Id = $process.Id
        StartTicks = $null
        LaunchedUtc = $launchedUtc
        IdentityVerified = $false
    }
    $singleOwnedProcesses.Add($owned)
    # Retain the original launch handle rather than reopening a PID that might have been reused.
    $null = $process.Handle
    $owned.StartTicks = $process.StartTime.ToUniversalTime().Ticks
    if ($owned.StartTicks -lt $launchedUtc.AddSeconds(-5).Ticks -or $owned.StartTicks -gt [DateTime]::UtcNow.AddSeconds(5).Ticks) {
        Stop-SingleValidation 'UnexpectedProcessStartTime'
    }
    return $owned
}

function Test-SingleOwnedIdentity($Owned) {
    $process = $Owned.Process
    $process.Refresh()
    if ($process.HasExited) { return $false }
    try {
        if ($null -eq $Owned.StartTicks -or $process.Id -ne $Owned.Id -or $process.StartTime.ToUniversalTime().Ticks -ne $Owned.StartTicks) {
            return $false
        }
        # The loader may not have published MainModule immediately after CreateProcess.
        # Query the retained kernel handle so readiness does not weaken ownership checks.
        $image = [IO.Path]::GetFullPath([Mambo.SingleInstanceWindowProbe]::ImagePath($process.Handle))
    } catch {
        # A redirected process can finish between the live check and its image query.
        $process.Refresh()
        if ($process.HasExited) { return $false }
        throw
    }
    if (-not $image.Equals($Owned.ExpectedPath, [StringComparison]::OrdinalIgnoreCase)) { return $false }
    $Owned.IdentityVerified = $true
    return $true
}

function Close-SingleOwnedRemainder($Owned) {
    $process = $Owned.Process
    $process.Refresh()
    if ($process.HasExited) { return }
    if (-not (Test-SingleOwnedIdentity $Owned)) {
        if ($process.HasExited) { return }
        $singleResult.cleanupIdentityNotVerified = $true
        $singleResult.status = 'Failed'
        $singleResult.reason = 'CleanupIdentityNotVerified'
        return
    }
    $null = $process.CloseMainWindow()
    if ($process.WaitForExit(2000)) { return }
    # The exact image/start time and retained original process handle must all still match.
    if (-not (Test-SingleOwnedIdentity $Owned)) {
        if (-not $process.HasExited) {
            $singleResult.cleanupIdentityNotVerified = $true
            $singleResult.status = 'Failed'
            $singleResult.reason = 'CleanupIdentityNotVerified'
        }
        return
    }
    $singleResult.cleanupForcedOwnedProcess = $true
    $singleResult.status = 'Failed'
    $singleResult.reason = 'ForcedOwnedProcessCleanup'
    $process.Kill($false)
    if (-not $process.WaitForExit(5000)) { $singleResult.reason = 'OwnedProcessCleanupTimeout' }
}

try {
    if (-not $IsWindows) { Stop-SingleValidation 'WindowsRequired' -Unsupported }
    $null = Assert-SingleOwnedPath $singleRunRoot
    $null = [IO.Directory]::CreateDirectory($singleRunRoot)
    $source = Assert-SinglePlainPath $AppDirectory
    if (-not [IO.Directory]::Exists($source) -or -not [IO.File]::Exists((Join-Path $source 'Mambo.exe'))) {
        Stop-SingleValidation 'ApplicationNotFound' -Unsupported
    }
    $sourceBoundary = $source.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if ($singleAppDirectory.StartsWith($sourceBoundary, [StringComparison]::OrdinalIgnoreCase)) {
        Stop-SingleValidation 'ApplicationCopyOverlapsSource' -Unsupported
    }
    $null = [IO.Directory]::CreateDirectory((Assert-SingleOwnedPath $singleAppDirectory))
    $singleResult.copiedFileCount = Copy-SingleApplication $source $singleAppDirectory
    $executable = Assert-SingleOwnedPath (Join-Path $singleAppDirectory 'Mambo.exe')
    $sourceHash = (Get-FileHash -LiteralPath (Join-Path $source 'Mambo.exe') -Algorithm SHA256).Hash
    $copiedHash = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
    $singleResult.executableCopiedUnchanged = $sourceHash -ceq $copiedHash
    if (-not $singleResult.executableCopiedUnchanged) { Stop-SingleValidation 'ExecutableCopyMismatch' }

    # Only numeric ownership and visibility are inspected. No title, account data, or window text is read.
    if (-not ('Mambo.SingleInstanceWindowProbe' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
namespace Mambo {
    public static class SingleInstanceWindowProbe {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder path, ref uint size);
        public static string ImagePath(nint process) {
            var path = new StringBuilder(32768);
            uint size = (uint)path.Capacity;
            if (!QueryFullProcessImageName(process, 0, path, ref size) || size == 0)
                throw new InvalidOperationException("ProcessImageUnavailable");
            return path.ToString();
        }
        private delegate bool EnumWindow(nint window, nint parameter);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindow callback, nint parameter);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(nint window);
        public static int CountVisible(uint ownedProcessId) {
            int count = 0;
            EnumWindow callback = (window, _) => {
                GetWindowThreadProcessId(window, out uint processId);
                if (processId == ownedProcessId && IsWindowVisible(window)) count++;
                return true;
            };
            if (!EnumWindows(callback, 0)) throw new InvalidOperationException("WindowEnumerationFailed");
            GC.KeepAlive(callback);
            return count;
        }
    }
}
'@
    }

    $singleStage = 'FirstWindow'
    $startup = [Diagnostics.Stopwatch]::StartNew()
    $first = Start-SingleOwnedProcess $executable
    $singleResult.firstProcessId = $first.Id
    while ($startup.ElapsedMilliseconds -lt 20000) {
        $first.Process.Refresh()
        if ($first.Process.HasExited) {
            $singleResult.firstExitCode = $first.Process.ExitCode
            if ($first.Process.ExitCode -eq 0) { Stop-SingleValidation 'FirstInstanceRedirected' -Unsupported }
            Stop-SingleValidation 'FirstInstanceExitedBeforeWindow'
        }
        if (-not (Test-SingleOwnedIdentity $first)) {
            if ($first.Process.HasExited) { continue }
            Stop-SingleValidation 'FirstIdentityMismatch'
        }
        if ($first.Process.MainWindowHandle -ne [IntPtr]::Zero) {
            $singleResult.firstWindowObserved = $true
            $singleResult.firstWindowCountPeak = [Mambo.SingleInstanceWindowProbe]::CountVisible([uint32]$first.Id)
            break
        }
        Start-Sleep -Milliseconds 25
    }
    $singleResult.firstStartupMilliseconds = $startup.ElapsedMilliseconds
    $singleResult.firstIdentityVerified = $first.IdentityVerified
    if (-not $singleResult.firstWindowObserved) { Stop-SingleValidation 'FirstWindowTimeout' }

    $singleStage = 'RedirectSecond'
    $redirect = [Diagnostics.Stopwatch]::StartNew()
    $second = Start-SingleOwnedProcess $executable
    $singleResult.secondProcessId = $second.Id
    while (-not $second.Process.HasExited -and $redirect.ElapsedMilliseconds -lt 10000) {
        if (-not (Test-SingleOwnedIdentity $second)) {
            if ($second.Process.HasExited) { break }
            Stop-SingleValidation 'SecondIdentityMismatch'
        }
        $windows = [Mambo.SingleInstanceWindowProbe]::CountVisible([uint32]$second.Id)
        $singleResult.secondWindowObservationCount++
        $singleResult.secondWindowCountPeak = [Math]::Max($singleResult.secondWindowCountPeak, $windows)
        if ($windows -gt 0) { Stop-SingleValidation 'SecondWindowCreated' }
        Start-Sleep -Milliseconds 10
        $second.Process.Refresh()
    }
    $singleResult.secondIdentityVerified = $second.IdentityVerified
    $singleResult.secondExitMilliseconds = $redirect.ElapsedMilliseconds
    if (-not $second.Process.HasExited -or $redirect.ElapsedMilliseconds -gt 10000) { Stop-SingleValidation 'SecondExitTimeout' }
    $null = $second.Process.WaitForExit(0)
    $singleResult.secondExitCode = $second.Process.ExitCode
    if ($second.Process.ExitCode -ne 0) { Stop-SingleValidation 'SecondExitNonzero' }
    if (-not $second.IdentityVerified -or $singleResult.secondWindowObservationCount -eq 0) {
        Stop-SingleValidation 'SecondExitedBeforeObservation' -Unsupported
    }
    if (-not (Test-SingleOwnedIdentity $first)) { Stop-SingleValidation 'FirstUnavailableAfterRedirect' }
    $singleResult.firstStillRunningAfterRedirect = $true
    $singleResult.firstWindowCountPeak = [Math]::Max($singleResult.firstWindowCountPeak,
        [Mambo.SingleInstanceWindowProbe]::CountVisible([uint32]$first.Id))

    $singleStage = 'GracefulClose'
    $closing = [Diagnostics.Stopwatch]::StartNew()
    $singleResult.gracefulCloseRequested = $first.Process.CloseMainWindow()
    if (-not $singleResult.gracefulCloseRequested) { Stop-SingleValidation 'GracefulCloseNotAccepted' }
    $singleResult.gracefulExitWithinDeadline = $first.Process.WaitForExit(10000)
    $singleResult.firstCloseMilliseconds = $closing.ElapsedMilliseconds
    if (-not $singleResult.gracefulExitWithinDeadline) { Stop-SingleValidation 'GracefulCloseTimeout' }
    $singleResult.firstExitCode = $first.Process.ExitCode
    if ($first.Process.ExitCode -ne 0) { Stop-SingleValidation 'FirstExitNonzero' }
    $singleResult.status = 'Passed'
    $singleResult.reason = 'TwoLaunchesOneWindow'
    $singleStage = 'Completed'
} catch {
    $errorObject = $_.Exception
    $singleResult.errorKind = $errorObject.GetType().Name
    $singleResult.hResult = $errorObject.HResult
    if ($errorObject.Data.Contains('SingleInstanceReason')) {
        $singleResult.reason = [string]$errorObject.Data['SingleInstanceReason']
        $singleResult.status = if ($errorObject.Data['SingleInstanceUnsupported']) { 'Unsupported' } else { 'Failed' }
    } else {
        $singleResult.reason = 'ValidationException'
        $singleResult.status = 'Failed'
    }
} finally {
    foreach ($owned in $singleOwnedProcesses) {
        try { Close-SingleOwnedRemainder $owned }
        catch {
            $singleResult.status = 'Failed'
            $singleResult.reason = 'OwnedProcessCleanupException'
            $singleResult.errorKind = $_.Exception.GetType().Name
            $singleResult.hResult = $_.Exception.HResult
        } finally { $owned.Process.Dispose() }
    }
    $singleResult.stage = $singleStage
    $singleResult.completedUtc = [DateTimeOffset]::UtcNow
    $singleResult.elapsedMilliseconds = $singleClock.ElapsedMilliseconds
    # Retain the unchanged application copy and a safe report, including failures and unsupported environments.
    try {
        $null = Assert-SingleOwnedPath $singleReportPath
        $null = [IO.Directory]::CreateDirectory($singleRunRoot)
        $singleResult | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $singleReportPath -Encoding utf8
    } catch { throw '单实例验证报告未能写入。' }
}

Write-Host "单实例验证：$($singleResult.status)；报告：$singleReportPath"
if ($singleResult.status -ne 'Passed') { throw '单实例验证未通过，请查看本轮安全报告。' }
