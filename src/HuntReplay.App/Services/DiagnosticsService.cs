using System.Text;
using HuntReplay.App.Interop;
using HuntReplay.Core.Configuration;

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
                throw new PlatformNotSupportedException("Нужны Windows build 19041+ и x64.");
            return Environment.OSVersion.VersionString;
        });
        Check("MessageBus.dll / Authenticode", () => { SelfInstaller.CheckDll(SelfInstaller.DllPath); return "NVIDIA Corporation / SHA-256 OK"; });
        Check("Реестр HKLM / 64-bit + 32-bit", () =>
        {
            var status = SelfInstaller.Check();
            if (!status.Registry64 || !status.Registry32) throw new IOException($"Registry64={status.Registry64}; Registry32={status.Registry32}");
            return "InstallPath, LogPath, LogLevel, ModuleMap зарегистрированы";
        });
        Check("Named pipe", () => pipe.IsListening ? @"\\.\pipe\NvMessageBusBroadcast / CurrentUserOnly"
            : throw new IOException(pipe.Failure ?? "Сервер не слушает"));
        Check("Папка клипов / запись / свободное место", () =>
        {
            Directory.CreateDirectory(settings.Storage.Directory);
            var temp = Path.Combine(settings.Storage.Directory, "hrs-diagnostic-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            var drive = new DriveInfo(Path.GetPathRoot(settings.Storage.Directory)!);
            return drive.IsReady ? $"{drive.AvailableFreeSpace / 1_000_000_000d:F1} ГБ свободно" : "Запись доступна";
        });
        IReadOnlyList<DeviceOption> devices = [];
        Check("Встроенный FFmpeg / DXGI / WASAPI", () =>
        {
            devices = NativeCaptureEngine.EnumerateDevices();
            return string.Join("; ", devices.Where(d => d.Kind == 3).Select(d => d.Name));
        });
        foreach (var device in devices.Where(d => d.Kind is 0 or 1 or 2))
            report.AppendLine($"      {(device.Kind == 0 ? "DISPLAY" : device.Kind == 1 ? "OUTPUT" : "MIC")} · {device.Name}{(device.IsDefault ? " [default]" : "")}");
        Check("Видеоэнкодер + WASAPI/AAC / реальный тест", () =>
        {
            var status = recorder.Status;
            if (status.Running && status.Buffer.Seconds > 0) return status.Encoder + " / пакеты поступают";
            if (devices.Count == 0) throw new IOException("Список устройств недоступен");
            NativeCaptureEngine.Probe(settings, devices);
            return $"{settings.Video.Encoder} / {settings.Video.Codec}: GPU-кадры закодированы, аудиоустройства открыты, AAC получен";
        });
        report.AppendLine();
        report.AppendLine("CPU, 60/120 FPS и задержка зависят от GPU, драйвера и нагрузки игры.");
        report.AppendLine("WGC не внедряет код в игру; сертификат совместимости/гарантия EAC не предоставляется.");
        report.AppendLine("WASAPI loopback пишет весь звук выбранного endpoint, не только Hunt.");
        report.AppendLine("При HDR включите SDR: HDR tone mapping / 10-bit в этой версии не реализованы.");
        report.AppendLine("Наличие AMF/NVENC/QSV в сборке не означает поддержку всех кодеков вашим GPU.");
        report.AppendLine("Отчёт локальный. Вы можете сохранить его и изучить пути/названия устройств перед отправкой.");
        return report.ToString();

        void Check(string name, Func<string> operation)
        {
            try { report.AppendLine($"[ OK ] {name}\n       {operation()}"); }
            catch (Exception ex) { report.AppendLine($"[FAIL] {name}\n       {ex.Message}"); }
        }
    }
}
