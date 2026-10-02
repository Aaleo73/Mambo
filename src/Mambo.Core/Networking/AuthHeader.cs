using System.Net.Http.Headers;
using Mambo.Core.Contracts;
using Mambo.Core.Session;

namespace Mambo.Core.Networking;

public static class AuthHeader
{
    public static void Apply(HttpRequestMessage request, Guid deviceId, string version, AccountSession? session = null)
    {
        var user = session is null ? "" : $"UserId=\"{Safe(session.Secret.UserId)}\", ";
        request.Headers.TryAddWithoutValidation("Authorization", $"Emby {user}Client=\"Mambo\", Device=\"Windows\", DeviceId=\"{deviceId:D}\", Version=\"{Safe(version)}\"");
        if (session is not null)
        {
            var token = session.Secret.AccessToken;
            if (token.Any(char.IsControl)) throw new AppException(new(AppErrorKind.Contract, ErrorCodes.InvalidResponse, "服务器返回的认证信息无效。", false));
            request.Headers.Add("X-Emby-Token", token);
        }
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }
    private static string Safe(string value)
    {
        if (value.Length > 256 || value.Any(c => char.IsControl(c) || c is '"' or '\\'))
            throw new AppException(new(AppErrorKind.Contract, ErrorCodes.InvalidArgument, "认证字段格式无效。", false));
        return value;
    }
}
