using System.Text;
using Mambo.Core.Contracts;

namespace Mambo.Core.Networking;

public sealed class ServerAddress
{
    private ServerAddress(Uri uri) { Uri = uri; }
    public Uri Uri { get; }
    public static ServerAddress Normalize(string? input)
    {
        input = input?.Trim();
        if (string.IsNullOrEmpty(input)) throw Invalid("请输入服务器地址");
        if (Encoding.UTF8.GetByteCount(input) > 2048) throw Invalid("服务器地址过长");
        if (!input.Contains("://", StringComparison.Ordinal)) input = "https://" + input;
        if (!System.Uri.TryCreate(input, UriKind.Absolute, out var uri)) throw Invalid("服务器地址格式无效");
        if (uri.Scheme is not ("http" or "https")) throw Invalid("服务器地址仅支持 HTTP 或 HTTPS");
        if (string.IsNullOrEmpty(uri.Host)) throw Invalid("服务器地址缺少主机名");
        if (!string.IsNullOrEmpty(uri.UserInfo)) throw Invalid("服务器地址不能包含用户名或密码");
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) throw Invalid("服务器地址不能包含查询参数或片段");
        return new ServerAddress(new Uri(uri.AbsoluteUri.TrimEnd('/'), UriKind.Absolute));
    }
    public Uri Endpoint(string relative) => new(Uri.AbsoluteUri.TrimEnd('/') + "/" + relative.TrimStart('/'));
    public override string ToString() => "ServerAddress { <redacted> }";
    private static AppException Invalid(string text) => new(new(AppErrorKind.Contract, ErrorCodes.InvalidArgument, text, false));
}
