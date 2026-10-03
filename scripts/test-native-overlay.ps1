#Requires -Version 7.0
[CmdletBinding()]
param([string]$SamplePath, [string]$AppDirectory, [switch]$NoBuild)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$overlayRepository = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if (-not $SamplePath) { $SamplePath = Join-Path $overlayRepository 'artifacts/samples/jellyfin-4k-hevc-hdr10.mp4' }
$overlaySample = [IO.Path]::GetFullPath($SamplePath)
if (-not (Test-Path -LiteralPath $overlaySample -PathType Leaf)) { throw '本地测试样片不存在。' }
if (-not $AppDirectory) {
    if (-not $NoBuild) {
        & dotnet build (Join-Path $overlayRepository 'src/Mambo.App/Mambo.App.csproj') -p:Platform=x64 --no-restore -p:NuGetAudit=false
        if ($LASTEXITCODE -ne 0) { throw '正式播放界面诊断构建失败。' }
    }
    $AppDirectory = Join-Path $overlayRepository 'src/Mambo.App/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64'
}
$overlayExecutable = [IO.Path]::GetFullPath((Join-Path $AppDirectory 'Mambo.exe'))
if (-not (Test-Path -LiteralPath $overlayExecutable -PathType Leaf)) { throw '诊断程序尚未构建或发布。' }
$overlayRunId = [Guid]::NewGuid().ToString('N')
$overlayRoot = Join-Path $overlayRepository ('artifacts/native-overlay-validation/' + $overlayRunId)
$null = [IO.Directory]::CreateDirectory($overlayRoot)
$overlayReportPath = Join-Path $overlayRoot 'app-report.json'
$overlayResultPath = Join-Path $overlayRoot 'result.json'
$overlayResult = [ordered]@{
    schemaVersion = 2
    status = 'Failed'
    reason = 'NotStarted'
    runId = $overlayRunId
    processId = $null
    identityVerified = $false
    processExited = $false
    processExitCode = $null
    forcedCleanup = $false
    elapsedMilliseconds = 0
    reportIdentityMatched = $false
    realEmbeddedEngine = $false
    productionEngineParameters = $false
    audioFixtureGenerated = $false
    audioOutputAvailable = $false
    audioOutputDriver = $null
    audioTrackSelected = $false
    externalAudioTrackSelected = $false
    selectedAudioTrackId = 0
    audioOutputSampleRate = 0
    audioOutputChannels = 0
    audioPlaybackAdvanced = $false
    volumeControl = $false
    nativeVolume = 0
    muteButton = $false
    unmuteButton = $false
    nativeUnmuted = $false
    appPassed = $false
    appStage = $null
    appErrorKind = $null
    appHResult = $null
    errorKind = $null
    hResult = $null
}

# Query the retained process handle; Process.MainModule is not yet reliable immediately
# after CreateProcess. This reads only the identity of the process started below.
if (-not ('MamboNativeOverlayProcessIdentity' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
public static class MamboNativeOverlayProcessIdentity {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref uint size);
    public static string Image(SafeProcessHandle process) {
        var text = new StringBuilder(32768);
        uint size = (uint)text.Capacity;
        return QueryFullProcessImageName(process, 0, text, ref size) ? text.ToString() : null;
    }
}
'@
}

$overlayProcess = $null
$overlayStartTicks = 0L
$overlayClock = [Diagnostics.Stopwatch]::StartNew()
function Test-OverlayOwnedProcess {
    if (-not $overlayProcess -or $overlayStartTicks -eq 0) { return $false }
    try {
        $overlayProcess.Refresh()
        if ($overlayProcess.StartTime.ToUniversalTime().Ticks -ne $overlayStartTicks) { return $false }
        $image = [MamboNativeOverlayProcessIdentity]::Image($overlayProcess.SafeHandle)
        return $image -and [IO.Path]::GetFullPath($image).Equals($overlayExecutable, [StringComparison]::OrdinalIgnoreCase)
    } catch { return $false }
}

try {
    $overlayStart = [Diagnostics.ProcessStartInfo]::new($overlayExecutable)
    $overlayStart.UseShellExecute = $false
    $overlayStart.CreateNoWindow = $true
    $overlayStart.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $overlayStart.WorkingDirectory = $overlayRepository
    $overlayStart.ArgumentList.Add('--native-overlay-smoke')
    $null = $overlayStart.Environment.Remove('MAMBO_FAKE')
    $null = $overlayStart.Environment.Remove('MAMBO_UI_LAB_REPORT')
    $null = $overlayStart.Environment.Remove('MAMBO_STARTUP_REPORT')
    $null = $overlayStart.Environment.Remove('MAMBO_FAKE_LIFETIME_REPORT')
    $overlayStart.Environment['MAMBO_NATIVE_OVERLAY_SAMPLE'] = $overlaySample
    $overlayStart.Environment['MAMBO_NATIVE_OVERLAY_REPORT'] = $overlayReportPath
    $overlayStart.Environment['MAMBO_NATIVE_OVERLAY_RUN'] = $overlayRunId
    $overlayProcess = [Diagnostics.Process]::Start($overlayStart)
    $null = $overlayProcess.Handle
    $overlayStartTicks = $overlayProcess.StartTime.ToUniversalTime().Ticks
    $overlayResult.processId = $overlayProcess.Id
    $overlayResult.identityVerified = [bool](Test-OverlayOwnedProcess)
    if (-not $overlayResult.identityVerified) {
        $overlayResult.reason = 'OwnedProcessIdentityNotConfirmed'
        throw [InvalidOperationException]::new('无法确认本轮验证进程。')
    }
    if (-not $overlayProcess.WaitForExit(75000)) {
        $overlayResult.reason = 'ProcessDeadlineExceeded'
        throw [TimeoutException]::new('正式播放界面诊断超时。')
    }
    $overlayResult.processExited = $true
    $overlayResult.processExitCode = $overlayProcess.ExitCode
    if (-not (Test-Path -LiteralPath $overlayReportPath -PathType Leaf)) {
        $overlayResult.reason = 'FreshReportMissing'
        throw [InvalidOperationException]::new('未生成本轮验证报告。')
    }
    # Read only after exit, so atomic checkpoints cannot conflict with a reader.
    $overlayReport = Get-Content -LiteralPath $overlayReportPath -Raw | ConvertFrom-Json
    $overlayResult.reportIdentityMatched = $overlayReport.RunId -ceq $overlayRunId -and
        $overlayReport.ProcessId -eq $overlayResult.processId -and $overlayReport.ProcessStartUtcTicks -eq $overlayStartTicks
    if (-not $overlayResult.reportIdentityMatched) {
        $overlayResult.reason = 'ReportIdentityMismatch'
        throw [InvalidOperationException]::new('验证报告不属于本轮进程。')
    }
    $overlayResult.appPassed = [bool]$overlayReport.Passed
    $overlayResult.realEmbeddedEngine = [bool]$overlayReport.RealEmbeddedEngine
    $overlayResult.productionEngineParameters = [bool]$overlayReport.ProductionEngineParameters
    $overlayResult.audioFixtureGenerated = [bool]$overlayReport.AudioFixtureGenerated
    $overlayResult.audioOutputAvailable = [bool]$overlayReport.AudioOutputAvailable
    $overlayResult.audioOutputDriver = $overlayReport.AudioOutputDriver
    $overlayResult.audioTrackSelected = [bool]$overlayReport.AudioTrackSelected
    $overlayResult.externalAudioTrackSelected = [bool]$overlayReport.ExternalAudioTrackSelected
    $overlayResult.selectedAudioTrackId = $overlayReport.SelectedAudioTrackId
    $overlayResult.audioOutputSampleRate = $overlayReport.AudioOutputSampleRate
    $overlayResult.audioOutputChannels = $overlayReport.AudioOutputChannels
    $overlayResult.audioPlaybackAdvanced = [bool]$overlayReport.AudioPlaybackAdvanced
    $overlayResult.volumeControl = [bool]$overlayReport.VolumeControl
    $overlayResult.nativeVolume = $overlayReport.NativeVolume
    $overlayResult.muteButton = [bool]$overlayReport.MuteButton
    $overlayResult.unmuteButton = [bool]$overlayReport.UnmuteButton
    $overlayResult.nativeUnmuted = [bool]$overlayReport.NativeUnmuted
    $overlayResult.appStage = $overlayReport.Stage
    $overlayResult.appErrorKind = $overlayReport.ErrorKind
    $overlayResult.appHResult = $overlayReport.HResult
    $overlayChecks = @('IsolatedServicesVerified', 'ProductionEngineParameters', 'FormalOverlayLoaded', 'RealEmbeddedEngine',
        'TitleBound', 'Playing', 'Bound', 'SizeMatched', 'ViewportMatched', 'FullscreenViewportMatched', 'AudioFixtureGenerated', 'AudioOutputAvailable',
        'AudioTrackSelected', 'ExternalAudioTrackSelected', 'AudioPlaybackAdvanced', 'VolumeControl', 'MuteButton', 'UnmuteButton', 'NativeUnmuted',
        'PauseButton', 'SeekControl', 'ResumeButton', 'Closed', 'Detached',
        'Stopped', 'OutboxEmpty', 'ReportSequenceOrdered', 'ShutdownCompleted')
    $overlayAllChecks = $true
    foreach ($overlayCheck in $overlayChecks) { $overlayAllChecks = $overlayAllChecks -and $overlayReport.$overlayCheck -eq $true }
    if ($overlayProcess.ExitCode -ne 0 -or -not $overlayReport.Passed -or -not $overlayAllChecks -or
        $overlayReport.InitialEngineWidth -ne 1 -or $overlayReport.InitialEngineHeight -ne 1 -or
        $overlayReport.EngineAudioEnabled -ne $true -or $overlayReport.ExpectedPixelWidth -le 200 -or $overlayReport.ExpectedPixelHeight -le 200 -or
        [string]::IsNullOrWhiteSpace($overlayReport.AudioOutputDriver) -or $overlayReport.AudioOutputDriver -ieq 'null' -or
        $overlayReport.SelectedAudioTrackId -le 0 -or $overlayReport.AudioOutputSampleRate -le 0 -or $overlayReport.AudioOutputChannels -le 0 -or
        [Math]::Abs($overlayReport.NativeVolume - 10) -ge 0.01 -or
        $overlayReport.BufferWidth -ne $overlayReport.ExpectedPixelWidth -or $overlayReport.BufferHeight -ne $overlayReport.ExpectedPixelHeight) {
        $overlayResult.reason = 'NativeOverlayChecksFailed'
        throw [InvalidOperationException]::new('正式真实播放界面验证未通过。')
    }
    $overlayResult.status = 'Passed'
    $overlayResult.reason = 'RealShellOverlayCompositionAudioClosed'
} catch {
    $overlayResult.errorKind = $_.Exception.GetType().Name
    $overlayResult.hResult = $_.Exception.HResult.ToString('X8')
} finally {
    try {
        if ($overlayProcess -and -not $overlayProcess.HasExited) {
            if ($overlayResult.identityVerified -and (Test-OverlayOwnedProcess)) {
                $overlayResult.forcedCleanup = $true
                $overlayResult.status = 'Failed'
                $overlayProcess.Kill($false)
                $null = $overlayProcess.WaitForExit(5000)
            } else {
                $overlayResult.status = 'Unsupported'
                $overlayResult.reason = 'CleanupIdentityNotConfirmed'
            }
        }
    } finally {
        if ($overlayProcess) { $overlayProcess.Dispose() }
        $overlayResult.elapsedMilliseconds = $overlayClock.ElapsedMilliseconds
        $overlayResult | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $overlayResultPath -Encoding utf8
    }
}
if ($overlayResult.status -ne 'Passed') { throw '正式真实播放界面诊断失败；详见本轮 result.json 的固定阶段和错误代码。' }
Write-Host "正式 Shell / Overlay / 原生交换链 / 音频输出与音量静音控件 / 正常关闭通过：$overlayResultPath"
