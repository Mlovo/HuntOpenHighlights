using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using HuntReplay.Core.Localization;
using HuntReplay.Core.Updates;

namespace HuntReplay.App.Services;

/// <summary>An update step failed; the message is localized and ready to show.</summary>
internal sealed class UpdateInstallException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Downloads a verified release executable, swaps it for the running one and starts it.</summary>
internal static class UpdateInstallerService
{
    public const string UpdatedFromArgument = "--updated-from";
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);
    // No overall timeout: the executable is ~160 MB. A stalled read is caught per read instead.
    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HuntReplayStudio", UpdateCheckerService.GetCurrentVersionString()));
        return client;
    }

    private static string ExecutablePath =>
        Environment.ProcessPath ?? throw new UpdateInstallException(Text.Format("Update.Error.Replace", "Environment.ProcessPath"));

    /// <summary>Downloads beside the running executable and checks GitHub's SHA-256. Returns the staged file.</summary>
    public static async Task<string> DownloadAsync(ReleaseInfo release, IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (release.AssetUrl is null) throw new UpdateInstallException(Text.Get("Update.Error.NoAsset"));
        if (release.Sha256 is null) throw new UpdateInstallException(Text.Get("Update.Error.NoDigest"));
        var staged = AppUpdate.StagingPath(ExecutablePath);
        try
        {
            FileStream output;
            try { output = new FileStream(staged, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true); }
            catch (UnauthorizedAccessException ex) { throw new UpdateInstallException(Text.Format("Update.Error.NotWritable", Path.GetDirectoryName(staged)), ex); }
            await using (output)
            {
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                try
                {
                    stall.CancelAfter(StallTimeout);
                    using var response = await Http.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        throw new UpdateInstallException(Text.Format("Update.Error.Download", $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}"));
                    long? total = response.Content.Headers.ContentLength;
                    await using var input = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
                    var buffer = new byte[1 << 16];
                    long received = 0;
                    int read;
                    stall.CancelAfter(StallTimeout);
                    while ((read = await input.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        received += read;
                        if (total > 0) progress.Report(Math.Min(1, received / (double)total.Value));
                        stall.CancelAfter(StallTimeout);
                    }
                    if (total > 0 && received != total)
                        throw new UpdateInstallException(Text.Format("Update.Error.Download", $"{received} / {total} B"));
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new UpdateInstallException(Text.Get("Update.Error.Stalled"), ex);
                }
            }
            if (!await AppUpdate.MatchesSha256Async(staged, release.Sha256, cancellationToken).ConfigureAwait(false))
                throw new UpdateInstallException(Text.Get("Update.Error.Digest"));
            return staged;
        }
        catch (Exception ex)
        {
            TryDelete(staged);
            if (ex is UpdateInstallException or OperationCanceledException) throw;
            throw new UpdateInstallException(Text.Format("Update.Error.Download", ex.Message), ex);
        }
    }

    /// <summary>Moves the staged executable into place; on failure the current one stays as it was.</summary>
    public static void Install(string staged)
    {
        try { AppUpdate.Replace(ExecutablePath, staged); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(staged);
            throw new UpdateInstallException(Text.Format("Update.Error.Replace", ex.Message), ex);
        }
    }

    /// <summary>Starts the new executable; it waits for this process to exit before it takes the single-instance lock.</summary>
    public static void Restart(bool portable)
    {
        var start = new ProcessStartInfo(ExecutablePath) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
        start.ArgumentList.Add(UpdatedFromArgument);
        start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        if (portable) start.ArgumentList.Add("--portable");
        using var _ = Process.Start(start);
    }

    /// <summary>Called by the new executable at startup: waits for the old process, then removes its leftovers.</summary>
    public static async Task FinishAsync(string[] args)
    {
        int index = Array.IndexOf(args, UpdatedFromArgument);
        if (index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
        {
            try
            {
                using var previous = Process.GetProcessById(pid);
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2)); // Saving pending clips can take a while.
                await previous.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OperationCanceledException) { }
        }
        if (Environment.ProcessPath is { } executable) AppUpdate.CleanUp(executable);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
