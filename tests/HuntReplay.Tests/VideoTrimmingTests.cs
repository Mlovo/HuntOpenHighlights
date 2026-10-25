using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using static HuntReplay.TestShared.MediaTestKit;

namespace HuntReplay.Tests;

public static partial class Program
{
    private static bool nativeLoaded;

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string fileName, IntPtr reserved, uint flags);

    private static class NativeTrimInterop
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

    private static bool TryEnsureNativeLoaded()
    {
        if (nativeLoaded) return true;
        string dir = AppContext.BaseDirectory;
        string? dllPath = null;
        for (int i = 0; i < 6; i++)
        {
            string candidate = Path.Combine(dir, "artifacts", "runtime", "HuntReplay.Media.dll");
            if (File.Exists(candidate)) { dllPath = candidate; break; }
            string? parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (parent == null || parent == dir) break;
            dir = parent;
        }
        if (dllPath == null)
        {
            string candidate = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "runtime", "HuntReplay.Media.dll");
            if (File.Exists(candidate)) dllPath = candidate;
        }
        if (dllPath == null) return false;

        string? ffmpeg = TryResolveBundledTool("ffmpeg.exe");
        if (ffmpeg == null) return false;

        try
        {
            IntPtr handle = LoadLibraryEx(dllPath, IntPtr.Zero, 0x100 | 0x1000);
            if (handle == IntPtr.Zero) return false;
            NativeLibrary.SetDllImportResolver(typeof(Program).Assembly, (name, _, _) =>
            {
                if (name == "HuntReplay.Media.dll") return handle;
                return IntPtr.Zero;
            });
            nativeLoaded = true;
            return true;
        }
        catch
        {
            return false;
        }
    }


    private static string GenerateSampleClip(string dir, string fileName, double duration = 6.0, bool hasAudio = true)
    {
        string ffmpeg = ResolveBundledTool("ffmpeg.exe");
        string outPath = Path.Combine(dir, fileName);
        string durStr = duration.ToString("0.0", CultureInfo.InvariantCulture);
        string args = hasAudio
            ? $"-f lavfi -i testsrc=duration={durStr}:size=640x360:rate=30 -f lavfi -i sine=frequency=1000:duration={durStr} -c:v libx264 -g 30 -c:a aac -pix_fmt yuv420p -y \"{outPath}\""
            : $"-f lavfi -i testsrc=duration={durStr}:size=640x360:rate=30 -an -c:v libx264 -g 30 -pix_fmt yuv420p -y \"{outPath}\"";
        RunProcess(ffmpeg, args);
        return outPath;
    }

    private static string GenerateDualAudioClip(string dir, string fileName, double duration = 6.0)
    {
        string ffmpeg = ResolveBundledTool("ffmpeg.exe");
        string outPath = Path.Combine(dir, fileName);
        string durStr = duration.ToString("0.0", CultureInfo.InvariantCulture);
        string args = $"-f lavfi -i testsrc=duration={durStr}:size=640x360:rate=30 -f lavfi -i sine=frequency=440:duration={durStr} -f lavfi -i sine=frequency=880:duration={durStr} -map 0:v -map 1:a -map 2:a -c:v libx264 -g 30 -c:a aac -pix_fmt yuv420p -y \"{outPath}\"";
        RunProcess(ffmpeg, args);
        return outPath;
    }

    private record MediaProbeResult(double Duration, List<string> VideoStreams, List<string> AudioStreams);

    private static MediaProbeResult ProbeMedia(string filePath)
    {
        string json = RunForOutput(ResolveBundledTool("ffprobe.exe"),
            ["-v", "error", "-show_entries", "format=duration", "-show_entries", "stream=index,codec_name,codec_type", "-of", "json", filePath]);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        double duration = 0.0;
        if (root.TryGetProperty("format", out var formatElem) && formatElem.TryGetProperty("duration", out var durElem))
        {
            duration = double.Parse(durElem.GetString()!, CultureInfo.InvariantCulture);
        }

        var videoStreams = new List<string>();
        var audioStreams = new List<string>();
        if (root.TryGetProperty("streams", out var streamsElem))
        {
            foreach (var stream in streamsElem.EnumerateArray())
            {
                string type = stream.GetProperty("codec_type").GetString()!;
                string codec = stream.GetProperty("codec_name").GetString()!;
                if (type == "video") videoStreams.Add(codec);
                else if (type == "audio") audioStreams.Add(codec);
            }
        }
        return new MediaProbeResult(duration, videoStreams, audioStreams);
    }

    private static string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void ValidateTrimmingParameters(string? sourcePath, string? targetPath, double startSeconds, double endSeconds)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("Исходный путь к видеофайлу не может быть пустым.", nameof(sourcePath));
        if (string.IsNullOrWhiteSpace(targetPath))
            throw new ArgumentException("Целевой путь для сохранения не может быть пустым.", nameof(targetPath));

        if (double.IsNaN(startSeconds) || double.IsInfinity(startSeconds))
            throw new ArgumentException("Время начала содержит некорректное значение.", nameof(startSeconds));
        if (double.IsNaN(endSeconds) || double.IsInfinity(endSeconds))
            throw new ArgumentException("Время окончания содержит некорректное значение.", nameof(endSeconds));

        string fullSource = Path.GetFullPath(sourcePath);
        string fullTarget = Path.GetFullPath(targetPath);

        if (!File.Exists(fullSource))
            throw new FileNotFoundException("Исходный видеофайл не найден на диске.", fullSource);

        if (string.Equals(fullSource, fullTarget, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Целевой путь сохранения не может совпадать с исходным файлом.");

        if (startSeconds < 0.0)
            throw new ArgumentOutOfRangeException(nameof(startSeconds), "Время начала фрагмента должно быть неотрицательным.");

        if (endSeconds <= startSeconds)
            throw new ArgumentException("Время окончания фрагмента должно строго превышать время начала.", nameof(endSeconds));

        if (endSeconds - startSeconds < 0.5)
            throw new InvalidOperationException("Длительность фрагмента должна составлять не менее 0.5 с.");
    }

    private static class LocalFormatters
    {
        public static string FormatTime(double seconds)
        {
            if (double.IsNaN(seconds) || seconds < 0.0) seconds = 0.0;
            var ts = TimeSpan.FromSeconds(seconds);
            if (ts.TotalHours >= 1.0)
            {
                return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
            }
            return $"{ts.Minutes:D2}:{ts.Seconds:D2}";
        }

        public static double StepTime(double current, double delta, double min, double max)
        {
            double stepped = Math.Round(current + delta, 3);
            return Math.Clamp(stepped, min, max);
        }
    }

    private static async Task VideoTrimmingTestsAsync()
    {
        // Parameter validation tests
        Test("trim validation rejects empty source path", () =>
        {
            Throws<ArgumentException>(() => ValidateTrimmingParameters("", "target.mp4", 0, 5));
            Throws<ArgumentException>(() => ValidateTrimmingParameters("   ", "target.mp4", 0, 5));
        });

        Test("trim validation rejects empty target path", () => InTemp(dir =>
        {
            string source = Path.Combine(dir, "source.mp4");
            File.WriteAllBytes(source, [1, 2, 3]);
            Throws<ArgumentException>(() => ValidateTrimmingParameters(source, "", 0, 5));
            Throws<ArgumentException>(() => ValidateTrimmingParameters(source, "   ", 0, 5));
        }));

        Test("trim validation requires existing source file", () =>
        {
            Throws<FileNotFoundException>(() => ValidateTrimmingParameters("nonexistent_file.mp4", "out.mp4", 0, 5));
        });

        Test("trim validation rejects identical source and target paths", () => InTemp(dir =>
        {
            string source = Path.Combine(dir, "clip.mp4");
            File.WriteAllBytes(source, [1, 2, 3]);
            Throws<InvalidOperationException>(() => ValidateTrimmingParameters(source, source, 0, 5));
            Throws<InvalidOperationException>(() => ValidateTrimmingParameters(source, Path.Combine(dir, "CLIP.mp4"), 0, 5));
        }));

        Test("trim validation rejects negative start timestamp", () => InTemp(dir =>
        {
            string source = Path.Combine(dir, "source.mp4");
            File.WriteAllBytes(source, [1, 2, 3]);
            Throws<ArgumentOutOfRangeException>(() => ValidateTrimmingParameters(source, Path.Combine(dir, "out.mp4"), -1.0, 5.0));
        }));

        Test("trim validation rejects end time less than or equal to start", () => InTemp(dir =>
        {
            string source = Path.Combine(dir, "source.mp4");
            File.WriteAllBytes(source, [1, 2, 3]);
            Throws<ArgumentException>(() => ValidateTrimmingParameters(source, Path.Combine(dir, "out.mp4"), 5.0, 5.0));
            Throws<ArgumentException>(() => ValidateTrimmingParameters(source, Path.Combine(dir, "out.mp4"), 5.0, 4.0));
        }));

        Test("trim validation enforces minimum duration", () => InTemp(dir =>
        {
            string source = Path.Combine(dir, "source.mp4");
            File.WriteAllBytes(source, [1, 2, 3]);
            Throws<InvalidOperationException>(() => ValidateTrimmingParameters(source, Path.Combine(dir, "out.mp4"), 2.0, 2.3));
        }));

        Test("trim validation rejects NaN or infinite timestamps", () => InTemp(dir =>
        {
            string source = Path.Combine(dir, "source.mp4");
            File.WriteAllBytes(source, [1, 2, 3]);
            Throws<ArgumentException>(() => ValidateTrimmingParameters(source, Path.Combine(dir, "out.mp4"), double.NaN, 5.0));
            Throws<ArgumentException>(() => ValidateTrimmingParameters(source, Path.Combine(dir, "out.mp4"), 0.0, double.PositiveInfinity));
        }));

        // Formatters tests
        Test("timestamp formatting converts sub-hour seconds to MM:SS", () =>
        {
            Equal("00:00", LocalFormatters.FormatTime(0.0));
            Equal("00:05", LocalFormatters.FormatTime(5.0));
            Equal("01:23", LocalFormatters.FormatTime(83.0));
            Equal("59:59", LocalFormatters.FormatTime(3599.0));
        });

        Test("timestamp formatting converts hour-plus seconds to HH:MM:SS", () =>
        {
            Equal("01:00:00", LocalFormatters.FormatTime(3600.0));
            Equal("01:05:30", LocalFormatters.FormatTime(3930.0));
            Equal("10:00:00", LocalFormatters.FormatTime(36000.0));
        });

        Test("fine-tuning step increments clamp within valid range", () =>
        {
            Equal(5.1, LocalFormatters.StepTime(5.0, +0.1, 0.0, 10.0));
            Equal(4.9, LocalFormatters.StepTime(5.0, -0.1, 0.0, 10.0));
            Equal(0.0, LocalFormatters.StepTime(0.05, -0.1, 0.0, 10.0));
            Equal(10.0, LocalFormatters.StepTime(9.95, +0.1, 0.0, 10.0));
        });

        // Windows native engine integration tests
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (!TryEnsureNativeLoaded())
            {
                Console.WriteLine("SKIP: Native media engine (HuntReplay.Media.dll / ffmpeg.exe) not built yet.");
                return;
            }

            Test("native trim rejects invalid input and sets hr_last_error", () =>
            {
                int res = NativeTrimInterop.hr_trim("nonexistent.mp4", "out.mp4", 0, 5, 0, null, IntPtr.Zero);
                True(res < 0);
                string? err = NativeTrimInterop.GetLastError();
                True(!string.IsNullOrWhiteSpace(err));
            });

            Test("native trim standard clip preserves video and audio", () => InTemp(dir =>
            {
                string source = GenerateSampleClip(dir, "source.mp4", 6.0, hasAudio: true);
                string preHash = ComputeFileSha256(source);
                string target = Path.Combine(dir, "target.mp4");

                int res = NativeTrimInterop.hr_trim(source, target, 1.0, 4.0, 0, null, IntPtr.Zero);
                Equal(0, res);
                True(File.Exists(target));

                var probe = ProbeMedia(target);
                True(probe.Duration >= 2.5 && probe.Duration <= 3.5);
                True(probe.VideoStreams.Contains("h264"));
                True(probe.AudioStreams.Contains("aac"));

                string postHash = ComputeFileSha256(source);
                Equal(preHash, postHash);
            }));

            Test("native trim source file is immutable across trims", () => InTemp(dir =>
            {
                string source = GenerateSampleClip(dir, "immutable.mp4", 5.0, hasAudio: true);
                string initialHash = ComputeFileSha256(source);
                DateTime initialTime = File.GetLastWriteTimeUtc(source);

                // Successful trim
                string target1 = Path.Combine(dir, "t1.mp4");
                Equal(0, NativeTrimInterop.hr_trim(source, target1, 1.0, 3.0, 0, null, IntPtr.Zero));
                Equal(initialHash, ComputeFileSha256(source));
                Equal(initialTime, File.GetLastWriteTimeUtc(source));

                // Cancelled trim
                string target2 = Path.Combine(dir, "t2.mp4");
                NativeTrimInterop.ProgressCallback cancelCb = (_, _) => 1;
                int cancelRes = NativeTrimInterop.hr_trim(source, target2, 1.0, 4.0, 0, cancelCb, IntPtr.Zero);
                True(cancelRes < 0);
                False(File.Exists(target2));
                Equal(initialHash, ComputeFileSha256(source));
                Equal(initialTime, File.GetLastWriteTimeUtc(source));

                // Failed trim
                int failRes = NativeTrimInterop.hr_trim(source, source, 1.0, 3.0, 0, null, IntPtr.Zero);
                True(failRes < 0);
                Equal(initialHash, ComputeFileSha256(source));
                Equal(initialTime, File.GetLastWriteTimeUtc(source));
            }));

            Test("native trim cancellation deletes target file", () => InTemp(dir =>
            {
                string source = GenerateSampleClip(dir, "source_cancel.mp4", 8.0, hasAudio: true);
                string target = Path.Combine(dir, "cancelled_target.mp4");

                NativeTrimInterop.ProgressCallback cancelCb = (_, prog) => prog >= 0.1 ? 1 : 0;
                int res = NativeTrimInterop.hr_trim(source, target, 1.0, 7.0, 0, cancelCb, IntPtr.Zero);
                True(res < 0);
                False(File.Exists(target));
            }));

            Test("native trim video without audio", () => InTemp(dir =>
            {
                string source = GenerateSampleClip(dir, "no_audio.mp4", 6.0, hasAudio: false);
                string target = Path.Combine(dir, "target_no_audio.mp4");

                int res = NativeTrimInterop.hr_trim(source, target, 1.0, 4.0, 0, null, IntPtr.Zero);
                Equal(0, res);
                True(File.Exists(target));

                var probe = ProbeMedia(target);
                True(probe.Duration >= 2.5 && probe.Duration <= 3.5);
                True(probe.VideoStreams.Contains("h264"));
                Equal(0, probe.AudioStreams.Count);
            }));

            Test("native trim multi-track audio preservation", () => InTemp(dir =>
            {
                string source = GenerateDualAudioClip(dir, "dual_audio.mp4", 6.0);
                string target = Path.Combine(dir, "target_dual.mp4");

                int res = NativeTrimInterop.hr_trim(source, target, 1.0, 4.0, 0, null, IntPtr.Zero);
                Equal(0, res);
                True(File.Exists(target));

                var probe = ProbeMedia(target);
                True(probe.Duration >= 2.5 && probe.Duration <= 3.5);
                True(probe.VideoStreams.Contains("h264"));
                Equal(2, probe.AudioStreams.Count);
                True(probe.AudioStreams.TrueForAll(a => a == "aac"));
            }));

            Test("trimmed output is playable in native player engine", () => InTemp(dir =>
            {
                string source = GenerateSampleClip(dir, "source_play.mp4", 6.0, hasAudio: true);
                string target = Path.Combine(dir, "target_play.mp4");

                int res = NativeTrimInterop.hr_trim(source, target, 1.0, 4.0, 0, null, IntPtr.Zero);
                Equal(0, res);

                int openRes = NativeTrimInterop.hr_player_open(target, out IntPtr player, out int w, out int h, out double dur);
                Equal(0, openRes);
                try
                {
                    True(w > 0 && h > 0);
                    True(dur >= 2.5 && dur <= 3.5);

                    int seekRes = NativeTrimInterop.hr_player_seek(player, 1.0);
                    Equal(0, seekRes);

                    int thumbRes = NativeTrimInterop.hr_thumbnail(target, 320, out IntPtr pixels, out int tw, out int th, out int stride);
                    Equal(0, thumbRes);
                    try
                    {
                        True(pixels != IntPtr.Zero && tw > 0 && th > 0 && stride > 0);
                    }
                    finally
                    {
                        NativeTrimInterop.hr_free(pixels);
                    }
                }
                finally
                {
                    NativeTrimInterop.hr_player_close(player);
                }
            }));

            Test("native trim non-keyframe start produces exact duration without keyframe bleed", () => InTemp(dir =>
            {
                string source = GenerateSampleClip(dir, "source_non_keyframe.mp4", 6.0, hasAudio: true);
                string target = Path.Combine(dir, "target_exact_1_5s.mp4");

                // Mid-GOP cut: keyframes at 0.0s, 1.0s, 2.0s, 3.0s, 4.0s; cut [2.5s -> 4.0s] requested duration is 1.5s
                int res = NativeTrimInterop.hr_trim(source, target, 2.5, 4.0, 1, null, IntPtr.Zero);
                Equal(0, res);
                True(File.Exists(target));

                var probe = ProbeMedia(target);
                True(Math.Abs(probe.Duration - 1.5) < 0.05);
                True(probe.VideoStreams.Contains("h264"));
                True(probe.AudioStreams.Contains("aac"));

                int openRes = NativeTrimInterop.hr_player_open(target, out IntPtr player, out int w, out int h, out double dur);
                Equal(0, openRes);
                try
                {
                    True(w > 0 && h > 0);
                    True(Math.Abs(dur - 1.5) < 0.05);
                }
                finally
                {
                    NativeTrimInterop.hr_player_close(player);
                }
            }));

            Test("native trim non-keyframe start with accurate: 0 automatically re-encodes to eliminate bleed", () => InTemp(dir =>
            {
                string source = GenerateSampleClip(dir, "source_auto_reencode.mp4", 6.0, hasAudio: true);
                string target = Path.Combine(dir, "target_auto_1_5s.mp4");

                // Cutting at non-keyframe timestamp 2.5s must not bleed from 2.0s even when accurate == 0
                int res = NativeTrimInterop.hr_trim(source, target, 2.5, 4.0, 0, null, IntPtr.Zero);
                Equal(0, res);
                True(File.Exists(target));

                var probe = ProbeMedia(target);
                True(Math.Abs(probe.Duration - 1.5) < 0.05);
            }));

            await TestAsync("service trim executes end-to-end via VideoTrimmingService", async () =>
            {
                if (LoadTrimmingService() is not { } loaded) return;
                var (service, trimMethod) = loaded;

                string tempDir = Path.Combine(Path.GetTempPath(), "HuntReplay-svc-test-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                try
                {
                    string source = GenerateSampleClip(tempDir, "svc_source.mp4", 6.0, hasAudio: true);
                    string target = Path.Combine(tempDir, "svc_target.mp4");

                    var progressList = new List<double>();
                    var progressLock = new object();
                    var progress = new Progress<double>(p => { lock (progressLock) progressList.Add(p); });

                    var task = (Task<string>)trimMethod.Invoke(service, [source, target, 1.0, 4.0, false, progress, CancellationToken.None])!;
                    string resultPath = await task;

                    Equal(target, resultPath);
                    True(File.Exists(resultPath));
                    True(progressList.Count > 0);
                    Equal(1.0, progressList[^1]);
                }
                finally
                {
                    if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                }
            });

            await TestAsync("service trim never deletes an existing target it was allowed to overwrite", async () =>
            {
                if (LoadTrimmingService() is not { } loaded) return;
                var (service, trimMethod) = loaded;
                string tempDir = Path.Combine(Path.GetTempPath(), "HuntReplay-svc-test-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                try
                {
                    string source = GenerateSampleClip(tempDir, "svc_source.mp4", 6.0, hasAudio: true);
                    string target = Path.Combine(tempDir, "other_clip.mp4");
                    byte[] original = [1, 2, 3, 4];
                    File.WriteAllBytes(target, original);

                    // A range past the end of the clip fails inside the engine, after it opened its output.
                    await ThrowsAsync<InvalidOperationException>(() =>
                        (Task<string>)trimMethod.Invoke(service, [source, target, 100.0, 101.0, false, null, CancellationToken.None])!);
                    True(File.ReadAllBytes(target).AsSpan().SequenceEqual(original));

                    // Cancels from inside the engine's first progress report, so the engine itself aborts mid-write.
                    using var cancelled = new CancellationTokenSource();
                    var cancelling = new SyncProgress(_ => cancelled.Cancel());
                    await ThrowsAsync<OperationCanceledException>(() =>
                        (Task<string>)trimMethod.Invoke(service, [source, target, 1.0, 5.0, false, cancelling, cancelled.Token])!);
                    True(File.ReadAllBytes(target).AsSpan().SequenceEqual(original));

                    // A finished fragment replaces the file and leaves no temporary output behind.
                    await (Task<string>)trimMethod.Invoke(service, [source, target, 1.0, 4.0, false, null, CancellationToken.None])!;
                    True(ProbeMedia(target).VideoStreams.Contains("h264"));
                    Equal(2, Directory.GetFiles(tempDir).Length);
                }
                finally
                {
                    if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                }
            });
        }
    }

    // Progress<T> posts to the thread pool; this reports on the engine's own callback thread.
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    // A built HuntReplayStudio.dll, so tests can drive production App types; null when the app has not been built.
    private static Assembly? AppAssembly()
    {
        string?[] candidates = [
            TryFindRepoFile("src", "HuntReplay.App", "bin", "x64", "Release", "net8.0-windows10.0.19041.0", "win-x64", "HuntReplayStudio.dll"),
            TryFindRepoFile("src", "HuntReplay.App", "bin", "x64", "Debug", "net8.0-windows10.0.19041.0", "win-x64", "HuntReplayStudio.dll"),
            TryFindRepoFile("src", "HuntReplay.App", "bin", "Release", "net8.0-windows10.0.19041.0", "win-x64", "HuntReplayStudio.dll"),
            TryFindRepoFile("src", "HuntReplay.App", "bin", "Debug", "net8.0-windows10.0.19041.0", "win-x64", "HuntReplayStudio.dll")
        ];
        // Newest build wins: a stale output folder must not test old code.
        string? appDll = candidates.OfType<string>().OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if (appDll == null) Console.WriteLine("SKIP: HuntReplayStudio.dll not built; App-level tests do nothing.");
        return appDll == null ? null : Assembly.LoadFrom(appDll);
    }

    private static (object Service, MethodInfo Trim)? LoadTrimmingService()
    {
        if (AppAssembly() is not { } app) return null;
        var serviceType = app.GetType("HuntReplay.App.Services.VideoTrimmingService")!;
        return (serviceType.GetProperty("Instance")!.GetValue(null)!, serviceType.GetMethod("TrimVideoAsync")!);
    }
}
