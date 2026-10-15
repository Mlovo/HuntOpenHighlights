using System.Text;
using HuntReplay.App.Interop;
using HuntReplay.Core.Configuration;
using HuntReplay.Core.Localization;

namespace HuntReplay.App.Services;

internal static class DiagnosticsService
{
    public static string Run(StudioSettings settings, RecorderService recorder, HighlightsPipeServer pipe)
    {
        var report = new StringBuilder();
        report.AppendLine("HUNTREPLAY STUDIO / SYSTEM READINESS");
        report.AppendLine(DateTimeOffset.Now.ToString("u"));
        report.AppendLine("─────────────────────────────────────────────────");
        Check("Windows 10 2004+ x64", () =>
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) || !Environment.Is64BitProcess)
                throw new PlatformNotSupportedException(Text.Get("Diag.WindowsRequired"));
            return Environment.OSVersion.VersionString;
        });
        Check("MessageBus.dll / Authenticode", () => { SelfInstaller.CheckDll(SelfInstaller.DllPath); return "NVIDIA Corporation / SHA-256 OK"; });
        Check(Text.Get("Diag.RegistryCheck"), () =>
        {
            var status = SelfInstaller.Check();
            if (!status.Registry64 || !status.Registry32) throw new IOException($"Registry64={status.Registry64}; Registry32={status.Registry32}");
            return Text.Get("Diag.RegistryOk");
        });
        Check("Named pipe", () => pipe.IsListening ? @"\\.\pipe\NvMessageBusBroadcast / CurrentUserOnly"
            : throw new IOException(pipe.Failure ?? Text.Get("Diag.PipeNotListening")));
        Check(Text.Get("Diag.StorageCheck"), () =>
        {
            Directory.CreateDirectory(settings.Storage.Directory);
            var temp = Path.Combine(settings.Storage.Directory, "hrs-diagnostic-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            var drive = new DriveInfo(Path.GetPathRoot(settings.Storage.Directory)!);
            return drive.IsReady ? Text.Format("Diag.FreeSpace", drive.AvailableFreeSpace / 1_000_000_000d) : Text.Get("Diag.Writable");
        });
        IReadOnlyList<DeviceOption> devices = [];
        Check(Text.Get("Diag.RuntimeCheck"), () =>
        {
            devices = NativeCaptureEngine.EnumerateDevices();
            return string.Join("; ", devices.Where(d => d.Kind == 3).Select(d => d.Name));
        });
        foreach (var device in devices.Where(d => d.Kind is 0 or 1 or 2))
            report.AppendLine($"      {(device.Kind == 0 ? "DISPLAY" : device.Kind == 1 ? "OUTPUT" : "MIC")} · {device.Name}{(device.IsDefault ? " [default]" : "")}");
        Check(Text.Get("Diag.EncoderCheck"), () =>
        {
            var status = recorder.Status;
            if (status.Running && status.Buffer.Seconds > 0) return Text.Format("Diag.PacketsFlowing", status.Encoder);
            if (devices.Count == 0) throw new IOException(Text.Get("Diag.NoDevices"));
            NativeCaptureEngine.Probe(settings, devices);
            return Text.Format("Diag.ProbeOk", settings.Video.Encoder, settings.Video.Codec);
        });
        report.AppendLine();
        report.AppendLine(Text.Get("Diag.FooterPerformance"));
        report.AppendLine(Text.Get("Diag.FooterHdr"));
        report.AppendLine(Text.Get("Diag.FooterCodecs"));
        report.AppendLine(Text.Get("Diag.FooterPrivacy"));
        return report.ToString();

        void Check(string name, Func<string> operation)
        {
            try { report.AppendLine($"[ OK ] {name}\n       {operation()}"); }
            catch (Exception ex) { report.AppendLine($"[FAIL] {name}\n       {ex.Message}"); }
        }
    }
}
