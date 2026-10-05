#Requires -Version 7.0
[CmdletBinding()]
param([string]$AppDirectory, [switch]$NoBuild, [switch]$Screenshot)

# 弹幕渲染诊断：独立进程、假数据服务，不读取账号也不联网。
# -Screenshot 只截取本轮诊断窗口自身的矩形，保存在 artifacts/ 下供人工查看。
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
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    public static void Activate(IntPtr window) { SetProcessDPIAware(); SetForegroundWindow(window); }
    public static int[] Bounds(IntPtr window) {
        return GetWindowRect(window, out var rect) ? new[] { rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top } : new int[0];
    }
}
'@
}
function Save-WindowCapture([IntPtr]$Window, [string]$Path) {
    [MamboBulletChatWindow]::Activate($Window)
    Start-Sleep -Milliseconds 500
    $bounds = [MamboBulletChatWindow]::Bounds($Window)
    if ($bounds.Length -ne 4 -or $bounds[2] -lt 100 -or $bounds[3] -lt 100) { return $false }
    $bitmap = [Drawing.Bitmap]::new($bounds[2], $bounds[3])
    try {
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try { $graphics.CopyFromScreen($bounds[0], $bounds[1], 0, 0, $bitmap.Size) } finally { $graphics.Dispose() }
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
        # 诊断在两个阶段各停留一次：弹幕滚动中，以及弹幕面板打开时。
        $pending = [ordered]@{ '停留供截图' = $screenshotPath; '面板停留供截图' = $panelScreenshotPath }
        $waiting = [Diagnostics.Stopwatch]::StartNew()
        while ($pending.Count -gt 0 -and $waiting.Elapsed.TotalSeconds -lt 60 -and -not $process.HasExited) {
            Start-Sleep -Milliseconds 200
            if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) { continue }
            try { $stage = (Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json).Stage } catch { continue }
            if (-not $pending.Contains($stage)) { continue }
            $process.Refresh()
            if ($process.MainWindowHandle -ne [IntPtr]::Zero -and (Save-WindowCapture $process.MainWindowHandle $pending[$stage])) { $captured += $pending[$stage] }
            $pending.Remove($stage)
        }
    }
    if (-not $process.WaitForExit(90000)) { $reason = 'ProcessDeadlineExceeded'; throw [TimeoutException]::new('弹幕诊断超时。') }
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
