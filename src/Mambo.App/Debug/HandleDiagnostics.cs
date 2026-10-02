using System.Runtime.InteropServices;

namespace Mambo.App.Debug;

/// <summary>仅用于 Video Lab；只输出当前进程各类句柄的数量，不读取对象名或其它进程的数据。</summary>
internal static unsafe partial class HandleDiagnostics
{
    public static Dictionary<string, int> Capture()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var typeNames = new Dictionary<ushort, string>();
        var capacity = 1 << 20;
        nint buffer = 0;
        nint typeBuffer = Marshal.AllocHGlobal(4096);
        try
        {
            int result;
            do
            {
                if (buffer != 0) Marshal.FreeHGlobal(buffer);
                buffer = Marshal.AllocHGlobal(capacity);
                result = NtQuerySystemInformation(64, buffer, (uint)capacity, out var needed);
                if (result == unchecked((int)0xC0000004)) capacity = checked((int)needed + 65536);
            } while (result == unchecked((int)0xC0000004));
            if (result < 0) return counts;
            var count = *(nuint*)buffer;
            var entries = (HandleEntry*)(buffer + 2 * sizeof(nuint));
            for (nuint i = 0; i < count; i++)
            {
                var entry = entries[i];
                if (entry.ProcessId != (nuint)Environment.ProcessId) continue;
                if (!typeNames.TryGetValue(entry.TypeIndex, out var name))
                {
                    name = $"类型 {entry.TypeIndex}";
                    if (NtQueryObject((nint)entry.Handle, 2, typeBuffer, 4096, out _) >= 0)
                    {
                        var text = *(UnicodeString*)typeBuffer;
                        name = Marshal.PtrToStringUni(text.Buffer, text.Length / 2) ?? name;
                    }
                    typeNames[entry.TypeIndex] = name;
                }
                counts[name] = counts.GetValueOrDefault(name) + 1;
            }
            return counts;
        }
        finally
        {
            if (buffer != 0) Marshal.FreeHGlobal(buffer);
            Marshal.FreeHGlobal(typeBuffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HandleEntry
    {
        public nint Object;
        public nuint ProcessId, Handle;
        public uint Access;
        public ushort BacktraceIndex, TypeIndex;
        public uint Attributes, Reserved;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { public ushort Length, MaximumLength; public nint Buffer; }
    [LibraryImport("ntdll")]
    private static partial int NtQuerySystemInformation(int informationClass, nint buffer, uint length, out uint needed);
    [LibraryImport("ntdll")]
    private static partial int NtQueryObject(nint handle, int informationClass, nint buffer, uint length, out uint needed);
}
