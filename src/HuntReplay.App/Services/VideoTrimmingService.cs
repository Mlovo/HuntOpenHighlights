using HuntReplay.App.Interop;
using HuntReplay.Core.Localization;

namespace HuntReplay.App.Services;

/// <summary>
/// Trims a clip between two timestamps with the HuntReplay.Media.dll engine, without modifying the source.
/// accurate re-encodes the cut boundaries; otherwise packets are stream-copied. Progress reports values in [0, 1].
/// </summary>
public sealed class VideoTrimmingService
{
    public static VideoTrimmingService Instance { get; } = new();

    public async Task<string> TrimVideoAsync(
        string sourcePath,
        string targetPath,
        double startSeconds,
        double endSeconds,
        bool accurate = false,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException(Text.Get("Trim.SourceEmpty"), nameof(sourcePath));
        if (string.IsNullOrWhiteSpace(targetPath))
            throw new ArgumentException(Text.Get("Trim.TargetEmpty"), nameof(targetPath));

        if (!double.IsFinite(startSeconds))
            throw new ArgumentException(Text.Get("Trim.StartInvalid"), nameof(startSeconds));
        if (!double.IsFinite(endSeconds))
            throw new ArgumentException(Text.Get("Trim.EndInvalid"), nameof(endSeconds));

        string fullSource = Path.GetFullPath(sourcePath);
        string fullTarget = Path.GetFullPath(targetPath);

        if (!File.Exists(fullSource))
            throw new FileNotFoundException(Text.Get("Trim.SourceMissing"), fullSource);

        if (string.Equals(fullSource, fullTarget, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Text.Get("Trim.TargetIsSource"));

        if (startSeconds < 0.0)
            throw new ArgumentOutOfRangeException(nameof(startSeconds), Text.Get("Trim.StartNegative"));

        if (endSeconds <= startSeconds)
            throw new ArgumentException(Text.Get("Trim.EndBeforeStart"), nameof(endSeconds));

        if (endSeconds - startSeconds < 0.5)
            throw new InvalidOperationException(Text.Get("Trim.TooShort"));

        string? targetDir = Path.GetDirectoryName(fullTarget);
        if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        cancellationToken.ThrowIfCancellationRequested();

        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            RuntimeAssets.EnsureLoaded();

            NativeMethods.hr_trim_progress_callback progressCallback = (IntPtr _, double progressValue) =>
            {
                try
                {
                    progress?.Report(Math.Clamp(progressValue, 0.0, 1.0));
                }
                catch
                {
                }
                return cancellationToken.IsCancellationRequested ? 1 : 0;
            };

            // The engine deletes its output on any failure, so it never writes to the chosen path directly:
            // a file the user agreed to overwrite is replaced only by a finished fragment.
            string temp = fullTarget + "." + Guid.NewGuid().ToString("N")[..8] + ".partial.mp4";
            int result;
            try
            {
                result = NativeMethods.hr_trim(
                    fullSource,
                    temp,
                    startSeconds,
                    endSeconds,
                    accurate ? 1 : 0,
                    progressCallback,
                    IntPtr.Zero);
            }
            finally
            {
                GC.KeepAlive(progressCallback);
            }

            try
            {
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);

                if (result < 0)
                {
                    string? nativeError = NativeMethods.GetLastError();
                    string message = !string.IsNullOrWhiteSpace(nativeError)
                        ? nativeError
                        : Text.Get("Trim.EngineError");
                    throw new InvalidOperationException(message);
                }

                if (!File.Exists(temp))
                {
                    throw new IOException(Text.Get("Trim.OutputMissing"));
                }

                File.Move(temp, fullTarget, overwrite: true);
            }
            finally
            {
                TryCleanupFile(temp);
            }

            progress?.Report(1.0);
            return fullTarget;
        }, cancellationToken);
    }

    /// <summary>
    /// Generates a non-colliding default target path formatted as `<Source>_trimmed.mp4`.
    /// </summary>
    public static string GenerateDefaultTargetPath(string sourcePath)
    {
        string dir = Path.GetDirectoryName(sourcePath) ?? "";
        string nameWithoutExt = Path.GetFileNameWithoutExtension(sourcePath);
        string ext = Path.GetExtension(sourcePath);
        if (string.IsNullOrEmpty(ext)) ext = ".mp4";

        string candidate = Path.Combine(dir, $"{nameWithoutExt}_trimmed{ext}");
        if (!File.Exists(candidate)) return candidate;

        for (int i = 1; i < 1000; i++)
        {
            candidate = Path.Combine(dir, $"{nameWithoutExt}_trimmed_{i}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }

        return Path.Combine(dir, $"{nameWithoutExt}_trimmed_{Guid.NewGuid().ToString("N")[..8]}{ext}");
    }

    private static void TryCleanupFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }
}

/// <summary>
/// Helper utilities for formatting timestamps and calculating fine-tuning steps in player trimming controls.
/// </summary>
public static class VideoTrimmingFormatters
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
