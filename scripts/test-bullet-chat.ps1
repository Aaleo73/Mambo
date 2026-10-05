#Requires -Version 7.0
[CmdletBinding()]
param([string]$AppDirectory, [switch]$NoBuild, [switch]$Screenshot)

# 弹幕渲染诊断：独立进程、假数据服务，不读取账号也不联网。
# -Screenshot 只保存本轮诊断窗口自身的内容（PrintWindow），放在 artifacts/ 下供人工查看。
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if (-not $AppDirectory) {
    if (-not $NoBuild) {
        & dotnet build (Join-Path $repository 'src/Mambo.App/Mambo.App.csproj') -p:Platform=x64 --no-restore -p:NuGetAudit=false
        if ($LASTEXITCODE -ne 0) { throw '弹幕诊断构建失败。' }
    }
    $AppDirectory = Join-Path $repository 'src/Mambo.App/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64'
}
$executable = [IO.Path]::GetFullPath((Join-Path $AppDirectory 'Mambo.exe'))
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw '诊断程序尚未构建或发布。' }
$runId = [Guid]::NewGuid().ToString('N')
$root = Join-Path $repository ('artifacts/bullet-chat/' + $runId)
$null = [IO.Directory]::CreateDirectory($root)
$reportPath = Join-Path $root 'app-report.json'
$screenshotPath = Join-Path $root 'window.png'
$panelScreenshotPath = Join-Path $root 'panel.png'

if ($Screenshot -and -not ('MamboBulletChatWindow' -as [type])) {
    Add-Type -AssemblyName System.Drawing
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class MamboBulletChatWindow {
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr deviceContext, uint flags);
    public static int[] Size(IntPtr window) {
        SetProcessDPIAware();
        return GetWindowRect(window, out var rect) ? new[] { rect.Right - rect.Left, rect.Bottom - rect.Top } : new int[0];
    }
    // PW_RENDERFULLCONTENT：让系统把这个窗口自己的合成内容画进来，与它是否被遮挡无关。
    public static bool Print(IntPtr window, IntPtr deviceContext) { return PrintWindow(window, deviceContext, 2); }
}
'@
}
# 只取诊断窗口自身的内容：不读屏幕像素，也不把窗口抢到前台。
# 早先按窗口矩形从屏幕复制的做法，在窗口被遮挡时会把盖在上面的其他窗口截进来。
function Save-WindowCapture([IntPtr]$Window, [string]$Path) {
    $size = [MamboBulletChatWindow]::Size($Window)
    if ($size.Length -ne 2 -or $size[0] -lt 100 -or $size[1] -lt 100) { return $false }
    $bitmap = [Drawing.Bitmap]::new($size[0], $size[1])
    try {
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $printed = $false
        try {
            $deviceContext = $graphics.GetHdc()
            try { $printed = [MamboBulletChatWindow]::Print($Window, $deviceContext) } finally { $graphics.ReleaseHdc($deviceContext) }
        } finally { $graphics.Dispose() }
        if (-not $printed) { return $false }
        $bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
    } finally { $bitmap.Dispose() }
    return $true
}

$process = $null
$captured = @()
$reason = 'NotStarted'
try {
    $start = [Diagnostics.ProcessStartInfo]::new($executable)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $repository
    $start.ArgumentList.Add('--bullet-chat-smoke')
    foreach ($name in @('MAMBO_FAKE', 'MAMBO_UI_LAB_REPORT', 'MAMBO_STARTUP_REPORT', 'MAMBO_FAKE_LIFETIME_REPORT_DIR')) { $null = $start.Environment.Remove($name) }
    $start.Environment['MAMBO_BULLET_CHAT_REPORT'] = $reportPath
    if ($Screenshot) { $start.Environment['MAMBO_BULLET_CHAT_HOLD_MS'] = '4000' }
    $process = [Diagnostics.Process]::Start($start)
    if ($Screenshot) {
        # 诊断在五个阶段各停留一次：弹幕滚动中、选集栏收起时，以及弹幕、倍速、轨道三个面板打开时。
        $pending = [ordered]@{ '停留供截图' = $screenshotPath; '选集收起停留供截图' = (Join-Path $root 'collapsed.png'); '面板停留供截图' = $panelScreenshotPath
            '倍速面板停留供截图' = (Join-Path $root 'rate.png'); '轨道面板停留供截图' = (Join-Path $root 'tracks.png') }
        $waiting = [Diagnostics.Stopwatch]::StartNew()
        while ($pending.Count -gt 0 -and $waiting.Elapsed.TotalSeconds -lt 200 -and -not $process.HasExited) {
            Start-Sleep -Milliseconds 200
            if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) { continue }
            try { $stage = (Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json).Stage } catch { continue }
            if (-not $pending.Contains($stage)) { continue }
            # 等弹出层和列表项的入场动画播完再取，否则面板是半透明的、列表是空的。
            Start-Sleep -Milliseconds 900
            $process.Refresh()
            if ($process.MainWindowHandle -ne [IntPtr]::Zero -and (Save-WindowCapture $process.MainWindowHandle $pending[$stage])) { $captured += $pending[$stage] }
            $pending.Remove($stage)
        }
    }
    if (-not $process.WaitForExit(240000)) { $reason = 'ProcessDeadlineExceeded'; throw [TimeoutException]::new('弹幕诊断超时。') }
    if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) { $reason = 'ReportMissing'; throw [InvalidOperationException]::new('未生成本轮诊断报告。') }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    $reason = if ($report.Passed -and $process.ExitCode -eq 0) { 'Passed' } else { 'AppCheckFailed' }
    [pscustomobject]@{
        Status = if ($reason -eq 'Passed') { 'Passed' } else { 'Failed' }
        Reason = $reason
        ExitCode = $process.ExitCode
        Report = $report
        ReportPath = $reportPath
        Screenshots = $captured
    }
    if ($reason -ne 'Passed') { exit 1 }
} catch {
    # 只结束本脚本启动且仍在运行的诊断进程。
    if ($process -and -not $process.HasExited) { try { $process.Kill($true) } catch { } }
    [pscustomobject]@{ Status = 'Failed'; Reason = $reason; ErrorKind = $_.Exception.GetType().Name; ReportPath = $reportPath }
    exit 1
} finally {
    if ($process) { $process.Dispose() }
}
