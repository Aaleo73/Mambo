using Mambo.Core.Contracts;

namespace Mambo.Core.Networking;

public static class ErrorText
{
    public static AppError Network(string stage) => new(AppErrorKind.Network, ErrorCodes.NetworkUnavailable, stage + "失败：无法连接服务器，请检查网络或服务器地址。", true, stage);
    public static AppError Http(int status, string stage) => status is 401 or 403
        ? new(AppErrorKind.Auth, ErrorCodes.SessionExpired, stage == "登录" ? "用户名或密码错误" : "登录已失效，请重新连接。", false, stage, status)
        : new(AppErrorKind.Server, "http." + status, stage + "失败：服务器暂时无法完成请求。", status is 408 or 425 or 429 || status >= 500, stage, status);
    public static AppError InvalidResponse(string stage) => new(AppErrorKind.Contract, ErrorCodes.InvalidResponse, stage + "失败：服务器返回的数据格式无效。", false, stage);
}
