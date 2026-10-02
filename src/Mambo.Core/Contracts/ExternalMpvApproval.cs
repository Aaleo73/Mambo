namespace Mambo.Core.Contracts;

/// <summary>用户选择并通过版本校验的外部 mpv 文件；路径和指纹仅用于本地设置。</summary>
public sealed record ExternalMpvApproval(
    string Path,
    string Sha256,
    long Size,
    long LastWriteTimeUtcTicks,
    string Version)
{
    // 不把本机路径或未经信任的版本输出带入日志、调试输出与异常。
    public override string ToString() => "ExternalMpvApproval { Fingerprint = <redacted> }";
}
