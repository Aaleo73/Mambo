using Mambo.Core.Contracts;

namespace Mambo.Core.Playback;

/// <summary>显式用户选择时执行版本校验；每次启动前只读复验已批准文件。</summary>
public interface IExternalPlayerValidator
{
    Task<ExternalMpvApproval> ValidateAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>不执行文件；指纹不匹配、路径无效或无法读取时返回 false。</summary>
    Task<bool> VerifyAsync(ExternalMpvApproval approval, CancellationToken cancellationToken = default);
}
