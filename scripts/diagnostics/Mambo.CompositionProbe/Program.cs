using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Mambo.App.Debug;

namespace Mambo.Diagnostics;

// 独立于 WinUI 和 libmpv，定位 DXGI composition 原生资源生命周期。
internal static partial class Program
{
    private static async Task<int> Main(string[] args)
    {
        var cycles = 20;
        var warp = false;
        var composition = true;
        var sharedDevice = false;
        var flush = false;
        var names = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--warp": warp = true; break;
                case "--device-only": composition = false; break;
                case "--shared-device": sharedDevice = true; break;
                case "--flush": flush = true; break;
                case "--names": names = true; break;
                case "--cycles" when i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.None,
                    CultureInfo.InvariantCulture, out var value) && value is >= 1 and <= 100000:
                    cycles = value;
                    i++;
                    break;
                default:
                    Console.WriteLine("用法：dotnet run --project scripts/diagnostics/Mambo.CompositionProbe -- [--warp] [--device-only] [--shared-device] [--flush] [--names] [--cycles N]");
                    return 2;
            }
        }
        if (sharedDevice && !composition)
        {
            Console.WriteLine("--shared-device 只对 composition 交换链有意义，不能与 --device-only 同用。");
            return 2;
        }
        try
        {
            // 第一次创建会加载驱动与 DXGI 的一次性资源，不计入增量。
            Run(1, warp, composition, sharedDevice, flush);
            await Task.Delay(500);
            using var process = Process.GetCurrentProcess();
            var before = HandleDiagnostics.Capture();
            process.Refresh();
            var privateBefore = process.PrivateMemorySize64;
            var handlesBefore = process.HandleCount;
            var namesBefore = names ? CaptureNames() : [];
            Run(cycles, warp, composition, sharedDevice, flush);
            await Task.Delay(5000);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var after = HandleDiagnostics.Capture();
            process.Refresh();
            var privateAfter = process.PrivateMemorySize64;
            Console.WriteLine($"渲染设备：{(warp ? "WARP" : "硬件")}；composition：{composition}；共用设备：{sharedDevice}；释放前 ClearState + Flush：{flush}；创建 / 销毁 {cycles} 次，静置 5 秒");
            Console.WriteLine("类型,开始,结束,增量");
            foreach (var type in new[] { "Section", "Mutant" })
                Console.WriteLine($"{type},{before.GetValueOrDefault(type)},{after.GetValueOrDefault(type)},{after.GetValueOrDefault(type) - before.GetValueOrDefault(type)}");
            Console.WriteLine($"总句柄,{handlesBefore},{process.HandleCount},{process.HandleCount - handlesBefore}");
            Console.WriteLine($"Private Bytes,{privateBefore},{privateAfter},{privateAfter - privateBefore}");
            Console.WriteLine($"每次增量：Private Bytes {(privateAfter - privateBefore) / (double)cycles:F0} 字节");
            if (names)
            {
                // 只看本进程自己的 Section / Mutant；名称里的数字归一后按增量列出，用来判断是谁创建的。
                Console.WriteLine("新增对象（类型,名称,增量）");
                var distinct = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                var namesAfter = CaptureNames(distinct);
                foreach (var (key, count) in namesAfter.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    if (count - namesBefore.GetValueOrDefault(key) is var delta and not 0)
                        Console.WriteLine($"{key},{delta}（结束时共 {distinct[key].Count} 个不同名称，例：{distinct[key].First()}）");
            }
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

    private static unsafe void Run(int cycles, bool warp, bool composition, bool sharedDevice, bool flush)
    {
        if (!sharedDevice)
        {
            for (var i = 0; i < cycles; i++)
            {
                nint device = 0, context = 0;
                try
                {
                    CreateDevice(warp, out device, out context);
                    CreateAndReleaseSwapChain(device, context, composition, flush);
                }
                finally
                {
                    Release(context);
                    Release(device);
                }
            }
            return;
        }
        nint shared = 0, sharedContext = 0;
        try
        {
            CreateDevice(warp, out shared, out sharedContext);
            for (var i = 0; i < cycles; i++) CreateAndReleaseSwapChain(shared, sharedContext, composition, flush);
        }
        finally
        {
            Release(sharedContext);
            Release(shared);
        }
    }

    private static void CreateDevice(bool warp, out nint device, out nint context) =>
        Marshal.ThrowExceptionForHR(D3D11CreateDevice(0, warp ? 5u : 1u, 0, 0, 0, 0, 7, out device, out _, out context));

    private static unsafe void CreateAndReleaseSwapChain(nint device, nint context, bool composition, bool flush)
    {
        nint dxgiDevice = 0, adapter = 0, factory = 0, swapChain = 0;
        try
        {
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
            if (flush)
            {
                // flip 模型交换链的销毁是延迟的；按微软的说明在释放后清状态并 Flush，排除“只是尚未销毁”。
                ((delegate* unmanaged[MemberFunction]<nint, void>)(*(nint**)context)[110])(context);
                ((delegate* unmanaged[MemberFunction]<nint, void>)(*(nint**)context)[111])(context);
            }
            Release(factory);
            Release(adapter);
            Release(dxgiDevice);
        }
    }

    private static unsafe Dictionary<string, int> CaptureNames(Dictionary<string, HashSet<string>>? distinct = null)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        const int capacity = 2048;
        var buffer = Marshal.AllocHGlobal(capacity);
        try
        {
            // 句柄值是 4 的倍数；逐个询问本进程的句柄表，无效值直接失败，不需要枚举全系统。
            for (nint handle = 4; handle < 0x40000; handle += 4)
            {
                if (NtQueryObject(handle, 2, buffer, capacity, out _) < 0) continue;
                var type = Text(buffer);
                if (type is not ("Section" or "Mutant")) continue;
                var name = NtQueryObject(handle, 1, buffer, capacity, out _) >= 0 ? Text(buffer) : "";
                var key = $"{type},{(name.Length == 0 ? "(未命名)" : Digits().Replace(name, "#"))}";
                counts[key] = counts.GetValueOrDefault(key) + 1;
                if (distinct is null) continue;
                if (!distinct.TryGetValue(key, out var seen)) distinct[key] = seen = new(StringComparer.Ordinal);
                seen.Add(name);
            }
            return counts;
        }
        finally { Marshal.FreeHGlobal(buffer); }

        static string Text(nint address)
        {
            var text = *(UnicodeString*)address;
            return text.Buffer == 0 ? "" : Marshal.PtrToStringUni(text.Buffer, text.Length / 2) ?? "";
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex("[0-9A-Fa-f]{4,}|[0-9]+")]
    private static partial System.Text.RegularExpressions.Regex Digits();

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { public ushort Length, MaximumLength; public nint Buffer; }

    [LibraryImport("ntdll")]
    private static partial int NtQueryObject(nint handle, int informationClass, nint buffer, uint length, out uint needed);

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
