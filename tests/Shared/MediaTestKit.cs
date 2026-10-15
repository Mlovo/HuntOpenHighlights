using System.Diagnostics;
using System.IO;

namespace HuntReplay.TestShared;

/// <summary>
/// Process and file helpers shared by the core and E2E media suites (linked into both projects).
/// Child output is always drained concurrently: a full stdout or stderr pipe can never deadlock a test.
/// </summary>
internal static class MediaTestKit
{
    /// <summary>A file under the repository (e.g. native/vendor/ffmpeg/bin/ffmpeg.exe), searched upward from the test binaries.</summary>
    public static string? TryFindRepoFile(params string[] relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine([dir.FullName, .. relative]);
            if (File.Exists(candidate)) return candidate;
        }
        var fromWorkingDirectory = Path.Combine([Directory.GetCurrentDirectory(), .. relative]);
        return File.Exists(fromWorkingDirectory) ? fromWorkingDirectory : null;
    }

    public static string? TryResolveBundledTool(string toolName) =>
        TryFindRepoFile("native", "vendor", "ffmpeg", "bin", toolName);

    public static string ResolveBundledTool(string toolName) =>
        TryResolveBundledTool(toolName) ?? throw new FileNotFoundException($"Bundled tool {toolName} not found.");

    public static void RunProcess(string exePath, string arguments, int timeoutMs = 30000) =>
        Run(new ProcessStartInfo(exePath, arguments), timeoutMs);

    /// <summary>Runs a tool with discrete arguments and returns its standard output.</summary>
    public static string RunForOutput(string exePath, IEnumerable<string> arguments, int timeoutMs = 30000)
    {
        var psi = new ProcessStartInfo(exePath);
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        return Run(psi, timeoutMs);
    }

    private static string Run(ProcessStartInfo psi, int timeoutMs)
    {
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {psi.FileName}");
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        var name = Path.GetFileName(psi.FileName);
        if (!proc.WaitForExit(timeoutMs))
        {
            proc.Kill(entireProcessTree: true);
            throw new TimeoutException($"{name} exceeded {timeoutMs} ms");
        }
        if (proc.ExitCode != 0) throw new InvalidOperationException($"{name} failed with exit code {proc.ExitCode}: {stderr.Result}");
        return stdout.Result;
    }
}
