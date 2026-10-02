using System.Reflection;
using System.Runtime.InteropServices;

namespace Mambo.Player.LibMpv;

public sealed record MpvProbeResult(bool Available, ulong ApiVersion, string Message);

public static class MpvRuntime
{
    private static readonly object Gate = new();
    private static bool resolverRegistered;
    private static nint library;

    private static void RegisterResolver()
    {
        lock (Gate)
        {
            if (resolverRegistered) return;
            NativeLibrary.SetDllImportResolver(typeof(LibMpvNative).Assembly, Resolve);
            resolverRegistered = true;
        }
    }

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != LibMpvNative.Library) return 0;
        lock (Gate)
        {
            if (library != 0) return library;
            foreach (var relative in new[] { Path.Combine("mpv", "libmpv-2.dll"), "libmpv-2.dll" })
            {
                if (NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, relative), out library))
                    return library;
            }
            throw new DllNotFoundException("内置播放器组件缺失（libmpv-2.dll），请重新安装或切换到外部播放器。");
        }
    }

    public static MpvProbeResult Probe()
    {
        RegisterResolver();
        try
        {
            var api = LibMpvNative.mpv_client_api_version();
            if (api < 0x00020005)
                return new(false, api, "内置播放器组件版本过旧，需要 mpv 0.41 或更高版本。");
            using var handle = new MpvHandle();
            if (handle.IsInvalid || LibMpvNative.mpv_set_option_string(handle, "d3d11-output-mode", "composition") < 0)
                return new(false, api, "内置播放器不支持 D3D11 合成输出，请更换 libmpv 组件。");
            return new(true, api, "内置播放器组件可用。");
        }
        catch (DllNotFoundException) { return new(false, 0, "内置播放器组件缺失（libmpv-2.dll），请重新安装或切换到外部播放器。"); }
        catch (BadImageFormatException) { return new(false, 0, "内置播放器组件格式错误，请安装 x64 版本。"); }
        catch (EntryPointNotFoundException) { return new(false, 0, "内置播放器组件接口不完整，请重新安装。"); }
    }
}
