using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using HuntReplay.Core.Localization;
using HuntReplay.Core.Updates;

namespace HuntReplay.App.Services;

public sealed record UpdateCheckResult(
    bool HasUpdate,
    string CurrentVersion,
    ReleaseInfo? Release,
    string? ErrorMessage)
{
    public string? LatestVersion => Release is { Version.Length: > 0 } r ? r.Version : null;
}

public static class UpdateCheckerService
{
    private const string ApiUrl = "https://api.github.com/repos/" + AppUpdate.Repository + "/releases/latest";
    private static readonly HttpClient HttpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HuntReplayStudio", GetCurrentVersionString()));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));
        return client;
    }

    public static string GetCurrentVersionString()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "0.0.0"; // Assembly version comes from <Version> in the csproj.
    }

    public static async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var current = GetCurrentVersionString();
        UpdateCheckResult Fail(string error) => new(false, current, null, error);
        try
        {
            using var response = await HttpClient.GetAsync(ApiUrl, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return Fail($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
            var release = AppUpdate.ParseRelease(doc.RootElement);
            return new(AppUpdate.IsNewer(release.Version, current), current, release, null);
        }
        catch (OperationCanceledException) { return Fail(Text.Get("Update.Cancelled")); }
        catch (Exception ex) { return Fail(ex.Message); }
    }
}
