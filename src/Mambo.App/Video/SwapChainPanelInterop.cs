using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;

namespace Mambo.App.Video;

internal static unsafe class SwapChainPanelInterop
{
    private static readonly Guid PanelInterface = new("63aad0b8-7c24-40ff-85a8-640d944cc325");
    private static readonly Guid SwapChainInterface = new("a8be2ac4-199f-4946-b331-79599fb98de7");

    public static void Bind(SwapChainPanel panel, nint swapChain)
    {
        var unknown = ((WinRT.IWinRTObject)panel).NativeObject.ThisPtr;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in PanelInterface, out var native));
        try
        {
            var table = *(nint**)native;
            var call = (delegate* unmanaged[MemberFunction]<nint, nint, int>)table[3];
            Marshal.ThrowExceptionForHR(call(native, swapChain));
        }
        finally { Marshal.Release(native); }
    }

    public static void SetScale(nint swapChain, double scale)
    {
        if (swapChain == 0) return;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(swapChain, in SwapChainInterface, out var native));
        try
        {
            var matrix = new Matrix { M11 = (float)(1 / scale), M22 = (float)(1 / scale) };
            var call = (delegate* unmanaged[MemberFunction]<nint, Matrix*, int>)(*(nint**)native)[34];
            Marshal.ThrowExceptionForHR(call(native, &matrix));
        }
        finally { Marshal.Release(native); }
    }

    public static (int Width, int Height) BufferSize(nint swapChain)
    {
        if (swapChain == 0) return (0, 0);
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(swapChain, in SwapChainInterface, out var native));
        try
        {
            Description description = default;
            var call = (delegate* unmanaged[MemberFunction]<nint, Description*, int>)(*(nint**)native)[18];
            Marshal.ThrowExceptionForHR(call(native, &description));
            return ((int)description.Width, (int)description.Height);
        }
        finally { Marshal.Release(native); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Matrix { public float M11, M12, M21, M22, M31, M32; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Description
    {
        public uint Width, Height, Format;
        public int Stereo;
        public uint SampleCount, SampleQuality, Usage, BufferCount, Scaling, SwapEffect, AlphaMode, Flags;
    }
}
