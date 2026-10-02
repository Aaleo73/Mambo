using System.Diagnostics;
using System.Runtime.InteropServices;
using Mambo.App.Debug;

namespace Mambo.Diagnostics;

// 独立于 WinUI 和 libmpv，定位 DXGI composition 原生资源生命周期。
internal static partial class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Any(arg => arg is not ("--warp" or "--device-only")))
        {
            Console.WriteLine("用法：dotnet run --project scripts/diagnostics/Mambo.CompositionProbe -- [--warp] [--device-only]");
            return 2;
        }
        var warp = args.Contains("--warp", StringComparer.Ordinal);
        var composition = !args.Contains("--device-only", StringComparer.Ordinal);
        try
        {
            CreateAndRelease(warp, composition);
            await Task.Delay(500);
            var before = HandleDiagnostics.Capture();
            for (var i = 0; i < 20; i++) CreateAndRelease(warp, composition);
            await Task.Delay(5000);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var after = HandleDiagnostics.Capture();
            Console.WriteLine($"渲染设备：{(warp ? "WARP" : "硬件")}；composition：{composition}；创建 / 销毁 20 次，静置 5 秒");
            Console.WriteLine("类型,开始,结束,增量");
            foreach (var type in new[] { "Section", "Mutant" })
                Console.WriteLine($"{type},{before.GetValueOrDefault(type)},{after.GetValueOrDefault(type)},{after.GetValueOrDefault(type) - before.GetValueOrDefault(type)}");
            using var process = Process.GetCurrentProcess();
            Console.WriteLine($"结束总句柄：{process.HandleCount}；Private Bytes：{process.PrivateMemorySize64}");
            if (!before.ContainsKey("Section") || !after.ContainsKey("Section"))
            {
                Console.WriteLine("句柄类型采样不可用，无法判断稳定性。");
                return 2;
            }
            return after.GetValueOrDefault("Section") > before.GetValueOrDefault("Section") ||
                   after.GetValueOrDefault("Mutant") > before.GetValueOrDefault("Mutant") ? 1 : 0;
        }
        catch (COMException ex)
        {
            Console.WriteLine($"原生 composition 对照失败，HRESULT：0x{ex.HResult:X8}");
            return 2;
        }
    }

    private static unsafe void CreateAndRelease(bool warp, bool composition)
    {
        nint device = 0, context = 0, dxgiDevice = 0, adapter = 0, factory = 0, swapChain = 0;
        try
        {
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(0, warp ? 5u : 1u, 0, 0, 0, 0, 7,
                out device, out _, out context));
            var deviceId = new Guid("77db970f-6276-48ba-ba28-070143b4392c");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, in deviceId, out dxgiDevice));
            var setLatency = (delegate* unmanaged[MemberFunction]<nint, uint, int>)(*(nint**)dxgiDevice)[12];
            Marshal.ThrowExceptionForHR(setLatency(dxgiDevice, 3));
            if (!composition) return;

            var adapterId = new Guid("29038f61-3839-4626-91fd-086879011a05");
            var factoryId = new Guid("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
            var getAdapter = (delegate* unmanaged[MemberFunction]<nint, Guid*, nint*, int>)(*(nint**)dxgiDevice)[6];
            Marshal.ThrowExceptionForHR(getAdapter(dxgiDevice, &adapterId, &adapter));
            var getFactory = (delegate* unmanaged[MemberFunction]<nint, Guid*, nint*, int>)(*(nint**)adapter)[6];
            Marshal.ThrowExceptionForHR(getFactory(adapter, &factoryId, &factory));
            // 与固定 mpv 修订一致的 rgb10_a2 / flip sequential / 四缓冲配置。
            var desc = new Description
            {
                Width = 640, Height = 360, Format = 24, SampleCount = 1,
                Usage = 0x20 | 0x10 | 0x400, BufferCount = 4, SwapEffect = 3,
            };
            var createSwap = (delegate* unmanaged[MemberFunction]<nint, nint, Description*, nint, nint*, int>)(*(nint**)factory)[24];
            Marshal.ThrowExceptionForHR(createSwap(factory, device, &desc, 0, &swapChain));
        }
        finally
        {
            Release(swapChain);
            Release(factory);
            Release(adapter);
            Release(dxgiDevice);
            Release(context);
            Release(device);
        }
    }

    private static unsafe void Release(nint address)
    {
        if (address != 0)
            ((delegate* unmanaged[MemberFunction]<nint, uint>)(*(nint**)address)[2])(address);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Description
    {
        public uint Width, Height, Format;
        public int Stereo;
        public uint SampleCount, SampleQuality, Usage, BufferCount, Scaling, SwapEffect, AlphaMode, Flags;
    }

    [LibraryImport("d3d11")]
    private static partial int D3D11CreateDevice(nint adapter, uint type, nint software, uint flags,
        nint levels, uint levelCount, uint version, out nint device, out uint selected, out nint context);
}
