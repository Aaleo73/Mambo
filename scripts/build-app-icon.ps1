#Requires -Version 7.0
<#
.SYNOPSIS
    生成应用图标：src/Mambo.App/Assets/AppIcon.ico 和矢量母版 design/app-icon.svg。
.DESCRIPTION
    图形在本脚本里按像素尺寸逐个生成 SVG（边缘对齐到整像素），用 Edge 无界面模式栅格化，
    再写成多尺寸 .ico。只在改图标时运行；构建和发布直接使用已提交的 .ico。
#>
[CmdletBinding()]
param(
    [string]$BrowserPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$iconPath = Join-Path $repoRoot 'src/Mambo.App/Assets/AppIcon.ico'
$masterPath = Join-Path $repoRoot 'design/app-icon.svg'
$workRoot = Join-Path $repoRoot 'artifacts/app-icon'
$sizes = 256, 96, 72, 64, 60, 48, 40, 36, 32, 30, 24, 20, 16
$cell = 256

if (-not $BrowserPath) {
    $BrowserPath = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft/Edge/Application/msedge.exe'),
        (Join-Path $env:ProgramFiles 'Microsoft/Edge/Application/msedge.exe'),
        (Join-Path $env:ProgramFiles 'Google/Chrome/Application/chrome.exe')
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}
if (-not $BrowserPath -or -not (Test-Path -LiteralPath $BrowserPath -PathType Leaf)) { throw '未找到 Edge 或 Chrome；请用 -BrowserPath 指定。' }

function Format-Number([double]$value) {
    [Math]::Round($value, 3).ToString([Globalization.CultureInfo]::InvariantCulture)
}

# 深色圆角方块上的白色 M：左肩是实心播放三角，右边是一道斜线和一根竖线。
# 设计基准是 224 的方块：笔画 26，三角与竖线的中线各距中心 56，上下各 44。
function New-IconSvg([int]$Size, [string]$Comment) {
    $inset = if ($Size -le 16) { 0 } else { [Math]::Max(1, [int][Math]::Round($Size * 0.04, [MidpointRounding]::AwayFromZero)) }
    $tile = $Size - 2 * $inset
    $scale = $tile / 224.0
    $center = $Size / 2.0
    $radius = $tile * 0.235
    $stroke = 26 * $scale
    # 小尺寸把笔画取整，竖边才能落在整像素上。
    if ($Size -le 32) { $stroke = [Math]::Max(2, [Math]::Round($stroke, [MidpointRounding]::AwayFromZero)) }
    elseif ($Size -le 64) { $stroke = [Math]::Round($stroke * 2, [MidpointRounding]::AwayFromZero) / 2 }
    $left = [Math]::Round($center - 56 * $scale - $stroke / 2, [MidpointRounding]::AwayFromZero) + $stroke / 2
    $right = [Math]::Round($center + 56 * $scale + $stroke / 2, [MidpointRounding]::AwayFromZero) - $stroke / 2
    $top = [Math]::Round($center - 44 * $scale - $stroke / 2, [MidpointRounding]::AwayFromZero) + $stroke / 2
    $bottom = $Size - $top
    $rim = [Math]::Max(1.0, $Size / 128.0)
    $rimOpacity = if ($Size -le 24) { 0.21 } else { 0.30 }
    $n = { param($v) Format-Number $v }
    $lines = @(
        "<svg xmlns=`"http://www.w3.org/2000/svg`" width=`"$Size`" height=`"$Size`" viewBox=`"0 0 $Size $Size`">"
        $(if ($Comment) { "  <!-- $Comment -->" })
        '  <defs>'
        "    <linearGradient id=`"tile$Size`" x1=`"0`" y1=`"0`" x2=`"0`" y2=`"1`">"
        '      <stop offset="0" stop-color="#3b3d44"/>'
        '      <stop offset="1" stop-color="#17181b"/>'
        '    </linearGradient>'
        "    <linearGradient id=`"rim$Size`" x1=`"0`" y1=`"0`" x2=`"0`" y2=`"1`">"
        "      <stop offset=`"0`" stop-color=`"#fff`" stop-opacity=`"$(& $n $rimOpacity)`"/>"
        "      <stop offset=`".45`" stop-color=`"#fff`" stop-opacity=`"$(& $n ($rimOpacity * 0.22))`"/>"
        "      <stop offset=`"1`" stop-color=`"#fff`" stop-opacity=`"$(& $n ($rimOpacity * 0.3))`"/>"
        '    </linearGradient>'
        '  </defs>'
        "  <rect x=`"$inset`" y=`"$inset`" width=`"$tile`" height=`"$tile`" rx=`"$(& $n $radius)`" fill=`"url(#tile$Size)`"/>"
        "  <rect x=`"$(& $n ($inset + $rim / 2))`" y=`"$(& $n ($inset + $rim / 2))`" width=`"$(& $n ($tile - $rim))`" height=`"$(& $n ($tile - $rim))`" rx=`"$(& $n ($radius - $rim / 2))`" fill=`"none`" stroke=`"url(#rim$Size)`" stroke-width=`"$(& $n $rim)`"/>"
        "  <g stroke=`"#fff`" stroke-width=`"$(& $n $stroke)`" stroke-linecap=`"round`" stroke-linejoin=`"round`">"
        "    <path d=`"M$(& $n $center) $(& $n $center) L$(& $n $right) $(& $n $top) V$(& $n $bottom)`" fill=`"none`"/>"
        "    <path d=`"M$(& $n $left) $(& $n $top) L$(& $n $center) $(& $n $center) L$(& $n $left) $(& $n $bottom) Z`" fill=`"#fff`"/>"
        '  </g>'
        '</svg>'
    ) | Where-Object { $_ }
    $lines -join "`n"
}

Add-Type -AssemblyName System.Drawing
if (Test-Path -LiteralPath $workRoot) { Remove-Item -LiteralPath $workRoot -Recurse -Force }
New-Item -ItemType Directory -Path $workRoot -Force | Out-Null
try {
    # 所有尺寸排成一行，一次截图后再逐个裁出。
    $sprites = for ($i = 0; $i -lt $sizes.Count; $i++) {
        "<div style=`"position:absolute;left:$($i * $cell)px;top:0`">$(New-IconSvg $sizes[$i])</div>"
    }
    $pagePath = Join-Path $workRoot 'sprites.html'
    $shotPath = Join-Path $workRoot 'sprites.png'
    [IO.File]::WriteAllText($pagePath, "<!doctype html><html><style>svg{display:block}</style><body style=`"margin:0`">$($sprites -join '')</body></html>", [Text.UTF8Encoding]::new($false))
    $arguments = @(
        '--headless', '--disable-gpu', '--hide-scrollbars', '--force-device-scale-factor=1',
        "--user-data-dir=$(Join-Path $workRoot 'profile')", '--default-background-color=00000000',
        "--window-size=$($sizes.Count * $cell),$cell", "--screenshot=$shotPath", ([Uri]$pagePath).AbsoluteUri
    )
    & $BrowserPath @arguments 2>&1 | Out-Null
    if (-not (Test-Path -LiteralPath $shotPath -PathType Leaf)) { throw '浏览器没有生成截图。' }

    $sheet = [Drawing.Bitmap]::new($shotPath)
    try {
        if ($sheet.Width -lt $sizes.Count * $cell -or $sheet.Height -lt $cell) { throw "截图尺寸不对：$($sheet.Width)×$($sheet.Height)。" }
        $images = for ($i = 0; $i -lt $sizes.Count; $i++) {
            $size = $sizes[$i]
            $area = [Drawing.Rectangle]::new($i * $cell, 0, $size, $size)
            $tile = $sheet.Clone($area, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
            try {
                if ($tile.GetPixel([int]($size / 2), [int]($size * 0.12)).A -lt 250) { throw "$size px 没有画出来。" }
                if ($size -gt 16 -and $tile.GetPixel(0, 0).A -ne 0) { throw "$size px 的背景不透明。" }
                if ($size -eq 256) {
                    $stream = [IO.MemoryStream]::new()
                    $tile.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
                    $bytes = $stream.ToArray()
                    $stream.Dispose()
                }
                else {
                    # 小于 256 的尺寸存成 32 位位图：信息头、自下而上的 BGRA、再加一层 1 位透明掩码。
                    $data = $tile.LockBits([Drawing.Rectangle]::new(0, 0, $size, $size), [Drawing.Imaging.ImageLockMode]::ReadOnly, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
                    $pixels = [byte[]]::new($size * $size * 4)
                    for ($y = 0; $y -lt $size; $y++) {
                        [Runtime.InteropServices.Marshal]::Copy([IntPtr]::Add($data.Scan0, $y * $data.Stride), $pixels, ($size - 1 - $y) * $size * 4, $size * 4)
                    }
                    $tile.UnlockBits($data)
                    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
                    $mask = [byte[]]::new($maskStride * $size)
                    for ($y = 0; $y -lt $size; $y++) {
                        for ($x = 0; $x -lt $size; $x++) {
                            if ($pixels[($y * $size + $x) * 4 + 3] -eq 0) { $mask[$y * $maskStride + ($x -shr 3)] = $mask[$y * $maskStride + ($x -shr 3)] -bor (0x80 -shr ($x -band 7)) }
                        }
                    }
                    $stream = [IO.MemoryStream]::new()
                    $writer = [IO.BinaryWriter]::new($stream)
                    $writer.Write([uint32]40); $writer.Write([int]$size); $writer.Write([int]($size * 2))
                    $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]0)
                    $writer.Write([uint32]($pixels.Length + $mask.Length))
                    $writer.Write([int]0); $writer.Write([int]0); $writer.Write([uint32]0); $writer.Write([uint32]0)
                    $writer.Write($pixels); $writer.Write($mask); $writer.Flush()
                    $bytes = $stream.ToArray()
                    $writer.Dispose()
                }
                [pscustomobject]@{ Size = $size; Bytes = $bytes }
            }
            finally { $tile.Dispose() }
        }
    }
    finally { $sheet.Dispose() }

    New-Item -ItemType Directory -Path (Split-Path $iconPath -Parent) -Force | Out-Null
    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream)
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$images.Count)
    $offset = 6 + 16 * $images.Count
    foreach ($image in $images) {
        $edge = [byte]($image.Size -band 0xFF) # 256 记作 0
        $writer.Write($edge); $writer.Write($edge); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$image.Bytes.Length); $writer.Write([uint32]$offset)
        $offset += $image.Bytes.Length
    }
    foreach ($image in $images) { $writer.Write($image.Bytes) }
    $writer.Flush()
    [IO.File]::WriteAllBytes($iconPath, $stream.ToArray())
    $writer.Dispose()

    $master = New-IconSvg 256 'Mambo 应用图标母版，由 scripts/build-app-icon.ps1 生成，不要手改。'
    [IO.File]::WriteAllText($masterPath, $master + "`n", [Text.UTF8Encoding]::new($false))
}
finally {
    Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host "已生成 $iconPath（$($sizes -join '、') px）和 $masterPath"
