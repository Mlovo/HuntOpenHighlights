using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HuntReplay.Core.Updates;

namespace HuntReplay.Tests;

public static partial class Program
{
    private const string AssetUrl = "https://github.com/Mlovo/HuntOpenHighlights/releases/download/v0.2.0/HuntReplayStudio.exe";
    private static readonly string Digest = new('a', 64);

    private static ReleaseInfo Release(string assetUrl = AssetUrl, string digest = "sha256:", string name = "HuntReplayStudio.exe", string page = "https://github.com/Mlovo/HuntOpenHighlights/releases/tag/v0.2.0")
    {
        var json = JsonSerializer.Serialize(new
        {
            tag_name = "v0.2.0", html_url = page, body = "Notes",
            assets = new object[]
            {
                new { name = "SHA256SUMS.txt", browser_download_url = "https://github.com/Mlovo/HuntOpenHighlights/releases/download/v0.2.0/SHA256SUMS.txt", digest = "sha256:" + new string('b', 64) },
                new { name, browser_download_url = assetUrl, digest = digest == "sha256:" ? digest + Digest : digest },
            },
        });
        using var doc = JsonDocument.Parse(json);
        return AppUpdate.ParseRelease(doc.RootElement);
    }

    private static void UpdateTests()
    {
        Test("update release parsing picks the executable asset and its digest", () =>
        {
            var r = Release();
            Equal("0.2.0", r.Version); Equal(AssetUrl, r.AssetUrl); Equal(Digest, r.Sha256); Equal("Notes", r.Notes);
            Equal("https://github.com/Mlovo/HuntOpenHighlights/releases/tag/v0.2.0", r.PageUrl);
        });
        Test("update release without the executable has nothing to install", () =>
        {
            var r = Release(name: "Other.exe");
            True(r.AssetUrl is null); True(r.Sha256 is null);
        });
        Test("update download URLs outside this repository's releases are dropped", () =>
        {
            True(Release(assetUrl: "http://github.com/Mlovo/HuntOpenHighlights/releases/download/v0.2.0/HuntReplayStudio.exe").AssetUrl is null);
            True(Release(assetUrl: "https://evil.example/Mlovo/HuntOpenHighlights/releases/download/v0.2.0/HuntReplayStudio.exe").AssetUrl is null);
            True(Release(assetUrl: "https://github.com/Someone/HuntOpenHighlights/releases/download/v0.2.0/HuntReplayStudio.exe").AssetUrl is null);
            True(Release(assetUrl: "https://github.com/Mlovo/HuntOpenHighlights/raw/main/HuntReplayStudio.exe").AssetUrl is null);
            True(Release(assetUrl: "https://github.com/Mlovo/HuntOpenHighlights/releases/download/v0.2.0/payload.exe").AssetUrl is null);
            True(Release(page: "https://example.com/release").PageUrl is null);
        });
        Test("update digest must be a full SHA-256", () =>
        {
            Equal(Digest, AppUpdate.ParseSha256Digest("SHA256:" + Digest.ToUpperInvariant()));
            True(AppUpdate.ParseSha256Digest(null) is null);
            True(AppUpdate.ParseSha256Digest("sha1:" + new string('a', 40)) is null);
            True(AppUpdate.ParseSha256Digest("sha256:" + new string('a', 63)) is null);
            True(AppUpdate.ParseSha256Digest("sha256:" + new string('g', 64)) is null);
            True(Release(digest: "").Sha256 is null);
        });
        Test("update notes drop links that cannot be clicked in a message box", () =>
        {
            True(AppUpdate.PlainNotes("**Full Changelog**: https://github.com/Mlovo/HuntOpenHighlights/compare/v0.1.8...v0.1.9") is null);
            True(AppUpdate.PlainNotes(null) is null);
            Equal("What's Changed\n• Faster export by @Mlovo\n• See the guide\n\nFixed crash",
                AppUpdate.PlainNotes("## What's Changed\r\n* Faster export by @Mlovo in https://github.com/Mlovo/HuntOpenHighlights/pull/7\r\n" +
                    "- See [the guide](https://example.com/guide)\r\n\r\n\r\n**Fixed** crash\r\n\r\n**Full Changelog**: https://github.com/x/y/compare/a...b"));
        });
        Test("update version comparison", () =>
        {
            True(AppUpdate.IsNewer("0.2.0", "0.1.9")); True(AppUpdate.IsNewer("0.1.10", "0.1.9"));
            False(AppUpdate.IsNewer("0.1.9", "0.1.9")); False(AppUpdate.IsNewer("0.1.8", "0.1.9")); False(AppUpdate.IsNewer("", "0.1.9"));
        });
        Test("update file hash is checked against the digest", () => InTemp(path =>
        {
            var file = Path.Combine(path, "new.exe");
            var content = Encoding.ASCII.GetBytes("new build");
            File.WriteAllBytes(file, content);
            var hex = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
            True(AppUpdate.MatchesSha256Async(file, hex).GetAwaiter().GetResult());
            False(AppUpdate.MatchesSha256Async(file, Digest).GetAwaiter().GetResult());
        }));
        Test("update replaces the executable and keeps the old one aside", () => InTemp(path =>
        {
            var exe = Path.Combine(path, "HuntReplayStudio.exe");
            File.WriteAllText(exe, "old"); File.WriteAllText(AppUpdate.BackupPath(exe), "stale");
            File.WriteAllText(AppUpdate.StagingPath(exe), "new");
            AppUpdate.Replace(exe, AppUpdate.StagingPath(exe));
            Equal("new", File.ReadAllText(exe)); Equal("old", File.ReadAllText(AppUpdate.BackupPath(exe)));
            False(File.Exists(AppUpdate.StagingPath(exe)));
            True(AppUpdate.CleanUp(exe));
            False(File.Exists(AppUpdate.BackupPath(exe))); True(File.Exists(exe));
        }));
        Test("failed update swap puts the current executable back", () => InTemp(path =>
        {
            var exe = Path.Combine(path, "HuntReplayStudio.exe");
            File.WriteAllText(exe, "old");
            Throws<IOException>(() => AppUpdate.Replace(exe, Path.Combine(path, "missing.exe")));
            Equal("old", File.ReadAllText(exe)); False(File.Exists(AppUpdate.BackupPath(exe)));
        }));
    }
}
