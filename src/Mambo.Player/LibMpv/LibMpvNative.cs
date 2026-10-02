using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Mambo.Player.LibMpv;

internal static unsafe partial class LibMpvNative
{
    internal const string Library = "libmpv-2";
    [LibraryImport(Library)] internal static partial ulong mpv_client_api_version();
    [LibraryImport(Library)] internal static partial nint mpv_create();
    [LibraryImport(Library)] internal static partial int mpv_initialize(MpvHandle handle);
    [LibraryImport(Library)] internal static partial void mpv_terminate_destroy(nint handle);
    [LibraryImport(Library)] internal static partial nint mpv_error_string(int error);
    [LibraryImport(Library)] internal static partial void mpv_free(nint pointer);
    [LibraryImport(Library)] internal static partial void mpv_free_node_contents(ref MpvNode node);
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int mpv_set_option_string(MpvHandle handle, string name, string value);
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int mpv_set_property_string(MpvHandle handle, string name, string value);
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int mpv_set_property(MpvHandle handle, string name, MpvFormat format, void* value);
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int mpv_set_property_async(MpvHandle handle, ulong id, string name, MpvFormat format, void* value);
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int mpv_get_property(MpvHandle handle, string name, MpvFormat format, void* value);
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint mpv_get_property_string(MpvHandle handle, string name);
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int mpv_observe_property(MpvHandle handle, ulong id, string name, MpvFormat format);
    [LibraryImport(Library)] internal static partial int mpv_unobserve_property(MpvHandle handle, ulong id);
    [LibraryImport(Library)] internal static partial int mpv_command(MpvHandle handle, byte** args);
    [LibraryImport(Library)] internal static partial int mpv_command_node(MpvHandle handle, ref MpvNode args, out MpvNode result);
    [LibraryImport(Library)] internal static partial int mpv_command_async(MpvHandle handle, ulong id, byte** args);
    [LibraryImport(Library)] internal static partial int mpv_command_node_async(MpvHandle handle, ulong id, ref MpvNode args);
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int mpv_request_log_messages(MpvHandle handle, string level);
    [LibraryImport(Library)] internal static partial int mpv_request_event(MpvHandle handle, MpvEventId id, int enabled);
    [LibraryImport(Library)] internal static partial nint mpv_wait_event(MpvHandle handle, double timeout);
    [LibraryImport(Library)] internal static partial void mpv_wakeup(MpvHandle handle);
}

internal sealed class MpvHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public MpvHandle() : base(true) { SetHandle(LibMpvNative.mpv_create()); }
    protected override bool ReleaseHandle()
    {
        LibMpvNative.mpv_terminate_destroy(handle);
        return true;
    }
}
