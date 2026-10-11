using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;

namespace HuntReplay.E2ETests;

public static class E2EInfrastructure
{
    private static bool nativeLoaded;
    private static string? cachedRepoRoot;
    private static Assembly? cachedAppAssembly;

    public static string RepoRoot
    {
        get
        {
            if (cachedRepoRoot != null) return cachedRepoRoot;
            string current = AppContext.BaseDirectory;
            var dir = new DirectoryInfo(current);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "HuntReplayStudio.sln")))
                {
                    cachedRepoRoot = dir.FullName;
                    return cachedRepoRoot;
                }
                dir = dir.Parent;
            }
            cachedRepoRoot = Path.GetFullPath(Directory.GetCurrentDirectory());
            return cachedRepoRoot;
        }
    }

    public static string ResolveBundledTool(string toolName)
    {
        string tool = Path.Combine(RepoRoot, "native", "vendor", "ffmpeg", "bin", toolName);
        if (File.Exists(tool)) return tool;
        throw new FileNotFoundException($"Bundled FFmpeg tool '{toolName}' not found at: {tool}");
    }

    public static string ResolveNativeDll()
    {
        string[] candidates = [
            Path.Combine(RepoRoot, "artifacts", "runtime", "HuntReplay.Media.dll"),
            Path.Combine(RepoRoot, "artifacts", "native", "Release", "HuntReplay.Media.dll"),
            Path.Combine(RepoRoot, "artifacts", "native", "Debug", "HuntReplay.Media.dll")
        ];
        string? found = candidates.FirstOrDefault(File.Exists);
        if (found != null) return found;
        throw new FileNotFoundException("HuntReplay.Media.dll not found in artifacts/runtime or artifacts/native.");
    }

    public static Assembly GetAppAssembly()
    {
        if (cachedAppAssembly != null) return cachedAppAssembly;
        string[] candidates = [
            Path.Combine(RepoRoot, "src", "HuntReplay.App", "bin", "x64", "Debug", "net8.0-windows10.0.19041.0", "win-x64", "HuntReplayStudio.dll"),
            Path.Combine(RepoRoot, "src", "HuntReplay.App", "bin", "x64", "Release", "net8.0-windows10.0.19041.0", "win-x64", "HuntReplayStudio.dll"),
            Path.Combine(RepoRoot, "src", "HuntReplay.App", "bin", "Debug", "net8.0-windows10.0.19041.0", "win-x64", "HuntReplayStudio.dll"),
            Path.Combine(RepoRoot, "src", "HuntReplay.App", "bin", "Release", "net8.0-windows10.0.19041.0", "win-x64", "HuntReplayStudio.dll")
        ];
        string? found = candidates.FirstOrDefault(File.Exists);
        if (found == null)
        {
            var files = Directory.GetFiles(Path.Combine(RepoRoot, "src", "HuntReplay.App", "bin"), "HuntReplayStudio.dll", SearchOption.AllDirectories);
            found = files.FirstOrDefault();
        }
        if (found == null) throw new FileNotFoundException("HuntReplayStudio.dll could not be located in src/HuntReplay.App/bin.");
        cachedAppAssembly = Assembly.LoadFrom(found);
        return cachedAppAssembly;
    }

    public static void EnsureNativeLoaded()
    {
        if (nativeLoaded) return;
        string dllPath = ResolveNativeDll();
        IntPtr handle = NativeLibrary.Load(dllPath);
        if (handle == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Failed to load native library {dllPath}");

        NativeLibrary.SetDllImportResolver(typeof(E2EInfrastructure).Assembly, (name, _, _) =>
        {
            if (name == "HuntReplay.Media.dll") return handle;
            return IntPtr.Zero;
        });
        nativeLoaded = true;
    }

    public static class NativeTrimInterop
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int ProgressCallback(IntPtr user, double progress);

        [DllImport("HuntReplay.Media.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        public static extern int hr_trim(
            string source_path,
            string target_path,
            double start_seconds,
            double end_seconds,
            int accurate,
            ProgressCallback? progress_cb,
            IntPtr user);

        [DllImport("HuntReplay.Media.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr hr_last_error();

        public static string? GetLastError() => Marshal.PtrToStringUni(hr_last_error());

        [DllImport("HuntReplay.Media.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        public static extern int hr_player_open(string path, out IntPtr player, out int width, out int height, out double duration);

        [DllImport("HuntReplay.Media.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void hr_player_close(IntPtr player);

        [DllImport("HuntReplay.Media.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int hr_player_seek(IntPtr player, double seconds);

        [DllImport("HuntReplay.Media.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        public static extern int hr_thumbnail(string path, int maxWidth, out IntPtr pixels, out int width, out int height, out int stride);

        [DllImport("HuntReplay.Media.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void hr_free(IntPtr memory);
    }

    public static void RunProcess(string exePath, string arguments, int timeoutMs = 30000)
    {
        var psi = new ProcessStartInfo(exePath, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {exePath}");
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(timeoutMs);
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"{Path.GetFileName(exePath)} failed with exit code {proc.ExitCode}: {stderr}");
        }
    }

    public static string GenerateSampleClip(string dir, string fileName, double duration = 6.0, bool hasAudio = true)
    {
        string ffmpeg = ResolveBundledTool("ffmpeg.exe");
        string outPath = Path.Combine(dir, fileName);
        string durStr = duration.ToString("0.0", CultureInfo.InvariantCulture);
        string args = hasAudio
            ? $"-f lavfi -i testsrc=duration={durStr}:size=640x360:rate=30 -f lavfi -i sine=frequency=1000:duration={durStr} -c:v libx264 -g 30 -bf 0 -c:a aac -pix_fmt yuv420p -y \"{outPath}\""
            : $"-f lavfi -i testsrc=duration={durStr}:size=640x360:rate=30 -an -c:v libx264 -g 30 -bf 0 -pix_fmt yuv420p -y \"{outPath}\"";
        RunProcess(ffmpeg, args);
        return outPath;
    }

    public static string GenerateMultiAudioClip(string dir, string fileName, double duration = 12.0)
    {
        string ffmpeg = ResolveBundledTool("ffmpeg.exe");
        string outPath = Path.Combine(dir, fileName);
        string durStr = duration.ToString("0.0", CultureInfo.InvariantCulture);
        string args = $"-f lavfi -i testsrc=duration={durStr}:size=1280x720:rate=30 " +
                      $"-f lavfi -i sine=frequency=440:sample_rate=48000:duration={durStr} " +
                      $"-f lavfi -i sine=frequency=880:sample_rate=44100:duration={durStr} " +
                      "-map 0:v -map 1:a -map 2:a " +
                      "-c:v libx264 -g 30 -bf 0 -pix_fmt yuv420p " +
                      "-c:a:0 aac -ac:a:0 2 -ar:a:0 48000 " +
                      "-c:a:1 aac -ac:a:1 1 -ar:a:1 44100 " +
                      $"-y \"{outPath}\"";
        RunProcess(ffmpeg, args);
        return outPath;
    }

    public static string GenerateThreeAudioClip(string dir, string fileName, double duration = 10.0)
    {
        string ffmpeg = ResolveBundledTool("ffmpeg.exe");
        string outPath = Path.Combine(dir, fileName);
        string durStr = duration.ToString("0.0", CultureInfo.InvariantCulture);
        string args = $"-f lavfi -i testsrc=duration={durStr}:size=640x360:rate=30 " +
                      $"-f lavfi -i sine=frequency=440:sample_rate=48000:duration={durStr} " +
                      $"-f lavfi -i sine=frequency=880:sample_rate=44100:duration={durStr} " +
                      $"-f lavfi -i sine=frequency=1200:sample_rate=32000:duration={durStr} " +
                      "-map 0:v -map 1:a -map 2:a -map 3:a " +
                      "-c:v libx264 -g 30 -bf 0 -pix_fmt yuv420p " +
                      "-c:a:0 aac -ac:a:0 2 -ar:a:0 48000 " +
                      "-c:a:1 aac -ac:a:1 1 -ar:a:1 44100 " +
                      "-c:a:2 aac -ac:a:2 2 -ar:a:2 32000 " +
                      $"-y \"{outPath}\"";
        RunProcess(ffmpeg, args);
        return outPath;
    }

    public static string GenerateLongClip(string dir, string fileName, double duration = 60.0)
    {
        string ffmpeg = ResolveBundledTool("ffmpeg.exe");
        string outPath = Path.Combine(dir, fileName);
        string durStr = duration.ToString("0.0", CultureInfo.InvariantCulture);
        string args = $"-f lavfi -i testsrc=duration={durStr}:size=640x360:rate=30 " +
                      $"-f lavfi -i sine=frequency=440:sample_rate=48000:duration={durStr} " +
                      $"-f lavfi -i sine=frequency=880:sample_rate=44100:duration={durStr} " +
                      "-map 0:v -map 1:a -map 2:a " +
                      "-c:v libx264 -g 30 -bf 0 -pix_fmt yuv420p " +
                      "-c:a:0 aac -ac:a:0 2 -ar:a:0 48000 " +
                      "-c:a:1 aac -ac:a:1 1 -ar:a:1 44100 " +
                      $"-y \"{outPath}\"";
        RunProcess(ffmpeg, args, 60000);
        return outPath;
    }

    public record StreamInfo(int Index, string CodecType, string CodecName, int SampleRate, int Channels, string TimeBase);
    public record MediaProbeResult(double Duration, List<StreamInfo> Streams);

    public static MediaProbeResult ProbeMedia(string filePath)
    {
        string ffprobe = ResolveBundledTool("ffprobe.exe");
        var psi = new ProcessStartInfo(ffprobe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-show_entries"); psi.ArgumentList.Add("format=duration");
        psi.ArgumentList.Add("-show_entries"); psi.ArgumentList.Add("stream=index,codec_name,codec_type,sample_rate,channels,time_base");
        psi.ArgumentList.Add("-of"); psi.ArgumentList.Add("json");
        psi.ArgumentList.Add(filePath);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffprobe");
        string json = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(10000);
        if (proc.ExitCode != 0) throw new InvalidOperationException($"ffprobe failed: {stderr}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        double duration = 0.0;
        if (root.TryGetProperty("format", out var formatElem) && formatElem.TryGetProperty("duration", out var durElem))
        {
            duration = double.Parse(durElem.GetString()!, CultureInfo.InvariantCulture);
        }

        var streams = new List<StreamInfo>();
        if (root.TryGetProperty("streams", out var streamsElem))
        {
            foreach (var stream in streamsElem.EnumerateArray())
            {
                int idx = stream.GetProperty("index").GetInt32();
                string type = stream.GetProperty("codec_type").GetString()!;
                string codec = stream.GetProperty("codec_name").GetString()!;
                int rate = stream.TryGetProperty("sample_rate", out var r) && r.GetString() != null ? int.Parse(r.GetString()!) : 0;
                int channels = stream.TryGetProperty("channels", out var c) ? c.GetInt32() : 0;
                string tb = stream.TryGetProperty("time_base", out var t) && t.GetString() != null ? t.GetString()! : "";
                streams.Add(new StreamInfo(idx, type, codec, rate, channels, tb));
            }
        }
        return new MediaProbeResult(duration, streams);
    }

    public record PacketInfo(double PtsTime, double DtsTime, double DurationTime);

    public static List<PacketInfo> ReadPackets(string filePath, string streamSpec)
    {
        string ffprobe = ResolveBundledTool("ffprobe.exe");
        var psi = new ProcessStartInfo(ffprobe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-select_streams"); psi.ArgumentList.Add(streamSpec);
        psi.ArgumentList.Add("-show_packets");
        psi.ArgumentList.Add("-show_entries"); psi.ArgumentList.Add("packet=pts_time,dts_time,duration_time");
        psi.ArgumentList.Add("-of"); psi.ArgumentList.Add("json");
        psi.ArgumentList.Add(filePath);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to run ffprobe packets");
        string json = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(20000);

        using var doc = JsonDocument.Parse(json);
        var packets = new List<PacketInfo>();
        if (doc.RootElement.TryGetProperty("packets", out var pktList))
        {
            foreach (var p in pktList.EnumerateArray())
            {
                if (p.TryGetProperty("pts_time", out var ptsElem) &&
                    double.TryParse(ptsElem.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double pts))
                {
                    double dts = pts;
                    if (p.TryGetProperty("dts_time", out var dtsElem) &&
                        double.TryParse(dtsElem.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedDts))
                    {
                        dts = parsedDts;
                    }
                    double dur = 0;
                    if (p.TryGetProperty("duration_time", out var durElem) &&
                        double.TryParse(durElem.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedDur))
                    {
                        dur = parsedDur;
                    }
                    packets.Add(new PacketInfo(pts, dts, dur));
                }
            }
        }
        return packets;
    }

    public static string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static void InTemp(Action<string> test)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "HR_E2E_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            test(tempDir);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    public static async Task InTempAsync(Func<string, Task> test)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "HR_E2E_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            await test(tempDir);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    public static ResourceDictionary LoadProductionTheme()
    {
        string themePath = Path.Combine(RepoRoot, "src", "HuntReplay.App", "Themes", "Dark.xaml");
        string xaml = File.ReadAllText(themePath).Replace(
            "clr-namespace:HuntReplay.App.Views\"",
            "clr-namespace:HuntReplay.App.Views;assembly=HuntReplay.E2ETests\"",
            StringComparison.Ordinal);
        return (ResourceDictionary)XamlReader.Parse(xaml);
    }

    public static void WithThemedWindow(Action<Window, StackPanel> test)
    {
        var host = new StackPanel { Resources = LoadProductionTheme(), Width = 640 };
        var window = new Window { Content = host, Width = 680, Height = 480, ShowInTaskbar = false, ShowActivated = false };
        window.Show();
        try
        {
            Pump();
            test(window, host);
        }
        finally
        {
            window.Close();
            Pump();
        }
    }

    public static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    public static void True(bool condition, string message = "Expected true")
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void False(bool condition, string message = "Expected false") => True(!condition, message);

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(message ?? $"Expected '{expected}', but got '{actual}'");
    }

    public static void InRange(double actual, double min, double max, string? message = null)
    {
        if (actual < min || actual > max)
            throw new InvalidOperationException(message ?? $"Expected value in range [{min}, {max}], but got {actual}");
    }

    public static void Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Expected exception of type {typeof(T).Name}, but caught {ex.GetType().Name}: {ex.Message}");
        }
        throw new InvalidOperationException($"Expected exception of type {typeof(T).Name}, but no exception was thrown.");
    }

    public static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            return;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Expected exception of type {typeof(T).Name}, but caught {ex.GetType().Name}: {ex.Message}");
        }
        throw new InvalidOperationException($"Expected exception of type {typeof(T).Name}, but no exception was thrown.");
    }
}
