using System.Runtime.InteropServices;

namespace HuntReplay.App.Interop;

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct NativeConfig
{
    public uint Size;
    public int Encoder, Codec, Fps, Width, Height, Bitrate, GopSeconds;
    public ulong Window, Monitor;
    [MarshalAs(UnmanagedType.LPWStr)] public string OutputDevice;
    [MarshalAs(UnmanagedType.LPWStr)] public string MicrophoneDevice;
    public int MicrophoneEnabled;
    public float MicrophoneGain;
    public int SeparateMicrophone;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePacket
{
    public int Stream, Keyframe;
    public long PtsUs, DtsUs, DurationUs;
    public IntPtr Data;
    public int Size;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeStats
{
    public long Frames, DroppedFrames, EncodedBytes, ElapsedUs;
    public int Width, Height, Encoder, Codec, Running, SourceStale;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeDevice
{
    public int Kind, IsDefault, Vendor;
    public ulong Handle;
    public IntPtr Id, Name;
}

internal static class NativeMethods
{
    private const string Library = "HuntReplay.Media.dll";
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void PacketCallback(IntPtr user, in NativePacket packet);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void LogCallback(IntPtr user, int level, IntPtr message);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void DeviceCallback(IntPtr user, in NativeDevice device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int hr_trim_progress_callback(IntPtr user, double progress);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int hr_abi_version();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr hr_last_error();
    internal static string? GetLastError() => Marshal.PtrToStringUni(hr_last_error());
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int hr_devices(DeviceCallback callback, IntPtr user);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int hr_probe(in NativeConfig config);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int hr_start(in NativeConfig config,
        PacketCallback packets, LogCallback logs, IntPtr user, out IntPtr session);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void hr_stop(IntPtr session);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void hr_destroy(IntPtr session);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int hr_get_stats(IntPtr session, out NativeStats stats);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr hr_description(IntPtr session);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int hr_mux_open(IntPtr session, string path, out IntPtr mux);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int hr_mux_write(IntPtr mux, in NativePacket packet, long originUs);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int hr_mux_close(IntPtr mux);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void hr_mux_abort(IntPtr mux);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int hr_thumbnail(string path, int maxWidth, out IntPtr pixels, out int width, out int height, out int stride);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void hr_free(IntPtr memory);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int hr_player_open(string path, out IntPtr player, out int width, out int height, out double duration);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void hr_player_close(IntPtr player);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int hr_player_play(IntPtr player);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int hr_player_pause(IntPtr player);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int hr_player_seek(IntPtr player, double seconds);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int hr_player_get_state(IntPtr player, out int isPlaying, out int isEnded, out double currentPts);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int hr_player_read_frame(IntPtr player, IntPtr buffer, int stride, int width, int height, out int updated, out double pts);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int hr_player_preview(IntPtr player, double seconds, int maxWidth, out IntPtr pixels, out int width, out int height, out int stride);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int hr_player_set_volume(IntPtr player, float volume);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int hr_trim(
        string source_path,
        string target_path,
        double start_seconds,
        double end_seconds,
        int accurate,
        hr_trim_progress_callback? progress_cb,
        IntPtr user);

    internal static void Check(int result)
    {
        if (result < 0) throw new InvalidOperationException(Marshal.PtrToStringUni(hr_last_error()) ?? "Ошибка мультимедийного движка.");
    }
}
