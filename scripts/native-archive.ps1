#Requires -Version 7.0
function Get-LockedNativeArchive {
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Record, [Parameter(Mandatory)] [string] $CacheRoot, [ValidateSet('Binary','Source')] [string] $Kind)
    $ErrorActionPreference = 'Stop'
    $uri = [Uri]$Record.url
    $allowed = if ($Kind -eq 'Binary') { $uri.Host -eq 'repo.msys2.org' -and $uri.AbsolutePath.StartsWith('/mingw/ucrt64/') }
        else { ($uri.Host -eq 'repo.msys2.org' -and $uri.AbsolutePath.StartsWith('/mingw/sources/')) -or ($uri.Host -eq 'static.crates.io' -and $uri.AbsolutePath.StartsWith('/crates/')) }
    if ($uri.Scheme -ne 'https' -or -not $allowed -or $uri.Query -or $uri.Fragment -or
        $Record.filename -notmatch '^[A-Za-z0-9][A-Za-z0-9._+~-]+\.(pkg\.tar\.zst|src\.tar\.zst|crate)$' -or
        [Uri]::UnescapeDataString([IO.Path]::GetFileName($uri.AbsolutePath)) -ne $Record.filename -or
        $Record.sha256 -notmatch '^[a-f0-9]{64}$' -or $Record.bytes -le 0 -or $Record.bytes -gt 2GB) { throw '原生归档 lock 格式无效。' }
    New-Item -ItemType Directory -Path $CacheRoot -Force | Out-Null
    $path = Join-Path $CacheRoot $Record.filename
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        $partial = $path + '.partial'
        for ($attempt = 1; $attempt -le 3; $attempt++) {
            try {
                if ($Record.bytes -gt 64MB) {
                    # 固定范围请求避免大源码包被代理整包缓冲；最终校验完整归档的 SHA-256。
                    $stream = [IO.File]::Open($partial, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::Write, [IO.FileShare]::None)
                    try {
                        $start = [long]([Math]::Floor($stream.Length / 16MB) * 16MB)
                        $stream.SetLength($start); $stream.Position = $start
                        while ($start -lt $Record.bytes) {
                            $end = [Math]::Min($start + 16MB, [long]$Record.bytes) - 1
                            $part = $partial + '.part'
                            $response = Invoke-WebRequest -Uri $uri -Headers @{ Range = "bytes=$start-$end" } -OutFile $part -PassThru -TimeoutSec 120
                            if ($response.StatusCode -ne 206 -or [string]$response.Headers['Content-Range'] -ne "bytes $start-$end/$($Record.bytes)" -or
                                (Get-Item -LiteralPath $part).Length -ne $end - $start + 1) { throw '原生源码范围下载不完整。' }
                            $chunkStream = [IO.File]::OpenRead($part)
                            try { $chunkStream.CopyTo($stream) } finally { $chunkStream.Dispose() }
                            $start = $end + 1
                        }
                    } finally { $stream.Dispose() }
                } else { Invoke-WebRequest -Uri $uri -OutFile $partial -TimeoutSec 120 }
                if ((Get-Item -LiteralPath $partial).Length -ne $Record.bytes -or
                    (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Record.sha256) { throw '原生归档下载校验失败。' }
                Move-Item -LiteralPath $partial -Destination $path
                break
            } catch { if ($attempt -eq 3) { throw } }
        }
    }
    if ((Get-Item -LiteralPath $path).Length -ne $Record.bytes -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Record.sha256) { throw "原生归档与 lock 不匹配：$($Record.filename)" }
    return $path
}
