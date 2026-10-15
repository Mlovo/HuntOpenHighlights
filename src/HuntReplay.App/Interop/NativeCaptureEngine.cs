using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HuntReplay.Core.Configuration;
using HuntReplay.Core.Localization;
using HuntReplay.Core.Media;

namespace HuntReplay.App.Interop;

internal sealed class NativeCaptureEngine : IDisposable
{
    private IntPtr session;
    private readonly NativeMethods.PacketCallback packetCallback;
    private readonly NativeMethods.LogCallback logCallback;
    private Exception? callbackError;
    public string Description { get; private set; } = Text.Get("Capture.NotRunning");
    public event Action<EncodedPacket>? Packet;
    public event Action<int, string>? Log;

    public NativeCaptureEngine()
    {
        packetCallback = OnPacket;
        logCallback = (_, level, text) => { try { Log?.Invoke(level, Marshal.PtrToStringUTF8(text) ?? "Native event"); } catch { } };
    }

    public void Start(StudioSettings settings, IntPtr window, IReadOnlyList<DeviceOption> devices)
    {
        if (session != IntPtr.Zero) throw new InvalidOperationException(Text.Get("Capture.AlreadyRunning"));
        RuntimeAssets.EnsureLoaded();
        var config = BuildConfig(settings, window, devices);
        NativeMethods.Check(NativeMethods.hr_start(in config, packetCallback, logCallback, IntPtr.Zero, out session));
        Description = Marshal.PtrToStringUni(NativeMethods.hr_description(session)) ?? "GPU encoder";
    }

    public NativeStats Stats
    {
        get
        {
            if (session == IntPtr.Zero) return default;
            NativeMethods.Check(NativeMethods.hr_get_stats(session, out var stats));
            if (callbackError is not null) stats.Running = 0;
            return stats;
        }
    }

    public Exception? CallbackError => callbackError;
    public void Stop() { if (session != IntPtr.Zero) NativeMethods.hr_stop(session); }

    // Caller must retain this session until all exports have finished (RecorderService enforces it).
    public unsafe void Export(ReplaySnapshot snapshot, string path)
    {
        NativeMethods.Check(NativeMethods.hr_mux_open(session, path, out var mux));
        try
        {
            foreach (var packet in snapshot.Packets)
            {
                fixed (byte* data = packet.Payload)
                {
                    var native = new NativePacket
                    {
                        Stream = packet.StreamIndex, Keyframe = packet.IsKeyFrame ? 1 : 0,
                        PtsUs = packet.PtsUs, DtsUs = packet.DtsUs, DurationUs = packet.DurationUs,
                        Data = (IntPtr)data, Size = packet.Payload.Length
                    };
                    NativeMethods.Check(NativeMethods.hr_mux_write(mux, in native, snapshot.StartUs));
                }
            }
            var owned = mux;
            mux = IntPtr.Zero; // Close consumes its handle even when trailer/flush fails.
            NativeMethods.Check(NativeMethods.hr_mux_close(owned));
        }
        finally { if (mux != IntPtr.Zero) NativeMethods.hr_mux_abort(mux); }
    }

    public static IReadOnlyList<DeviceOption> EnumerateDevices()
    {
        RuntimeAssets.EnsureLoaded();
        var devices = new List<DeviceOption>();
        Exception? error = null;
        NativeMethods.DeviceCallback callback = (IntPtr _, in NativeDevice device) =>
        {
            try
            {
                devices.Add(new(device.Kind, Marshal.PtrToStringUni(device.Id) ?? "", Marshal.PtrToStringUni(device.Name) ?? "",
                    device.Handle, device.IsDefault != 0, device.Vendor));
            }
            catch (Exception ex) { error = ex; }
        };
        NativeMethods.Check(NativeMethods.hr_devices(callback, IntPtr.Zero));
        GC.KeepAlive(callback);
        if (error is not null) throw error;
        return LocalizeMonitorNames(devices);
    }

    // The native layer reports monitors with a language-neutral fallback label; captions shown to people are built here.
    private static List<DeviceOption> LocalizeMonitorNames(List<DeviceOption> devices)
    {
        int ordinal = 0;
        for (int i = 0; i < devices.Count; ++i)
        {
            var device = devices[i];
            if (device.Kind != 0) continue;
            ++ordinal;
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo((IntPtr)(long)device.Handle, ref info)) continue;
            var key = device.IsDefault ? "Device.MonitorPrimary" : "Device.Monitor";
            devices[i] = device with
            {
                Name = Text.Format(key, ordinal, info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top)
            };
        }
        return devices;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32", EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    public static void Probe(StudioSettings settings, IReadOnlyList<DeviceOption> devices)
    {
        RuntimeAssets.EnsureLoaded();
        var config = BuildConfig(settings, settings.Video.Source == CaptureSource.HuntWindow ? GameWindow.Find() : IntPtr.Zero, devices);
        NativeMethods.Check(NativeMethods.hr_probe(in config));
    }

    public static void SaveThumbnail(string video, string target)
    {
        RuntimeAssets.EnsureLoaded();
        NativeMethods.Check(NativeMethods.hr_thumbnail(video, 640, out var pixels, out var width, out var height, out var stride));
        try
        {
            var bytes = new byte[checked(stride * height)];
            Marshal.Copy(pixels, bytes, 0, bytes.Length);
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bytes, stride);
            bitmap.Freeze();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            encoder.Save(file);
        }
        finally { NativeMethods.hr_free(pixels); }
    }

    private static NativeConfig BuildConfig(StudioSettings settings, IntPtr window, IReadOnlyList<DeviceOption> devices)
    {
        settings.Validate();
        bool explicitMonitor = settings.Video.Source == CaptureSource.Monitor && !string.IsNullOrEmpty(settings.Video.MonitorDevice);
        var selected = explicitMonitor
            ? devices.FirstOrDefault(d => d.Kind == 0 && d.Id == settings.Video.MonitorDevice)
            : devices.FirstOrDefault(d => d.Kind == 0 && d.IsDefault);
        if (selected is null) throw new InvalidOperationException(Text.Get("Capture.MonitorDisconnected"));
        var (width, height) = settings.Video.Resolution switch
        {
            VideoResolution.FullHd => (1920, 1080), VideoResolution.Qhd => (2560, 1440),
            VideoResolution.Uhd => (3840, 2160), _ => (0, 0)
        };
        return new NativeConfig
        {
            Size = (uint)Marshal.SizeOf<NativeConfig>(), Encoder = (int)settings.Video.Encoder, Codec = (int)settings.Video.Codec,
            Fps = settings.Video.Fps, Width = width, Height = height, Bitrate = settings.Video.BitrateMbps * 1_000_000,
            GopSeconds = settings.Video.GopSeconds, Window = (ulong)window.ToInt64(), Monitor = selected.Handle,
            OutputDevice = settings.Audio.OutputDeviceId, MicrophoneDevice = settings.Audio.MicrophoneDeviceId,
            MicrophoneEnabled = settings.Audio.MicrophoneEnabled ? 1 : 0, MicrophoneGain = settings.Audio.MicrophoneVolume / 100f,
            SeparateMicrophone = settings.Audio.Mode == AudioMode.SeparateTracks ? 1 : 0,
            GpuDevice = settings.Video.GpuDevice
        };
    }

    private void OnPacket(IntPtr user, in NativePacket packet)
    {
        try
        {
            if (callbackError is not null) return;
            if (packet.Size <= 0 || packet.Size > 64 * 1024 * 1024) throw new InvalidDataException(Text.Get("Capture.InvalidPacketSize"));
            var payload = GC.AllocateUninitializedArray<byte>(packet.Size);
            Marshal.Copy(packet.Data, payload, 0, payload.Length);
            Packet?.Invoke(new(packet.Stream, payload, packet.PtsUs, packet.DtsUs, packet.DurationUs, packet.Keyframe != 0));
        }
        catch (Exception ex)
        {
            if (Interlocked.CompareExchange(ref callbackError, ex, null) is null)
            {
                try { Log?.Invoke(2, Text.Format("Capture.PacketBufferStopped", ex.Message)); } catch { }
            }
        }
    }

    public void Dispose()
    {
        if (session == IntPtr.Zero) return;
        NativeMethods.hr_destroy(session); // Joins callbacks before delegates may be collected.
        session = IntPtr.Zero;
        GC.KeepAlive(packetCallback); GC.KeepAlive(logCallback);
    }
}
