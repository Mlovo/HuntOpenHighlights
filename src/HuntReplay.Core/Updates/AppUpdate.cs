using System.Security.Cryptography;
using System.Text.Json;

namespace HuntReplay.Core.Updates;

/// <summary>The parts of a GitHub release the updater needs. Every URL is already checked against this repository.</summary>
public sealed record ReleaseInfo(string Version, string? PageUrl, string? Notes, string? AssetUrl, string? Sha256);

/// <summary>Release parsing, download verification and the in-place executable swap; no network or UI.</summary>
public static class AppUpdate
{
    public const string Repository = "Mlovo/HuntOpenHighlights";
    public const string ReleasesPage = "https://github.com/" + Repository + "/releases";
    public const string AssetName = "HuntReplayStudio.exe";

    /// <summary>Reads a GitHub "latest release" response. Untrusted URLs and malformed digests become null.</summary>
    public static ReleaseInfo ParseRelease(JsonElement root)
    {
        static string Prop(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        string? assetUrl = null, sha256 = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var asset in assets.EnumerateArray())
            {
                if (!string.Equals(Prop(asset, "name"), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
                var url = Prop(asset, "browser_download_url");
                assetUrl = IsTrustedAssetUrl(url) ? url : null;
                sha256 = ParseSha256Digest(Prop(asset, "digest"));
                break;
            }
        var page = Prop(root, "html_url");
        var notes = Prop(root, "body");
        return new(Prop(root, "tag_name").TrimStart('v', 'V').Trim(), IsTrustedPageUrl(page) ? page : null,
            notes.Length == 0 ? null : notes, assetUrl, sha256);
    }

    public static bool IsNewer(string latest, string current) =>
        Version.TryParse(latest, out var l) && Version.TryParse(current, out var c) && l > c;

    /// <summary>Only this repository's pages on github.com, over HTTPS.</summary>
    public static bool IsTrustedPageUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.Host == "github.com" &&
        u.AbsolutePath.StartsWith("/" + Repository + "/", StringComparison.Ordinal);

    /// <summary>Only this repository's release downloads; GitHub redirects them to its own storage over HTTPS.</summary>
    public static bool IsTrustedAssetUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.Host == "github.com" &&
        u.AbsolutePath.StartsWith("/" + Repository + "/releases/download/", StringComparison.Ordinal) &&
        u.AbsolutePath.EndsWith("/" + AssetName, StringComparison.OrdinalIgnoreCase);

    /// <summary>GitHub's asset digest is "sha256:&lt;64 hex&gt;"; returns the lower-case hex, or null for anything else.</summary>
    public static string? ParseSha256Digest(string? digest)
    {
        const string prefix = "sha256:";
        if (digest is null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var hex = digest[prefix.Length..].Trim().ToLowerInvariant();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex : null;
    }

    public static async Task<bool> MatchesSha256Async(string path, string expectedHex, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return string.Equals(Convert.ToHexString(hash), expectedHex, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Where the new executable is downloaded: beside the current one, so the final move stays on one volume.</summary>
    public static string StagingPath(string executable) => executable + ".download";
    /// <summary>Where the replaced executable waits until the next start deletes it.</summary>
    public static string BackupPath(string executable) => executable + ".old";

    /// <summary>
    /// Puts <paramref name="staged"/> in place of <paramref name="executable"/>. Windows allows renaming a running
    /// executable, so the current one moves aside first; if the new one cannot be moved in, the old one is put back.
    /// </summary>
    public static void Replace(string executable, string staged)
    {
        var backup = BackupPath(executable);
        if (File.Exists(backup)) File.Delete(backup);
        File.Move(executable, backup);
        try { File.Move(staged, executable); }
        catch
        {
            File.Move(backup, executable);
            throw;
        }
    }

    /// <summary>Best effort: removes what a previous update left behind. Returns false when a file is still locked.</summary>
    public static bool CleanUp(string executable)
    {
        bool clean = true;
        foreach (var path in new[] { BackupPath(executable), StagingPath(executable) })
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { clean = false; }
        }
        return clean;
    }
}
