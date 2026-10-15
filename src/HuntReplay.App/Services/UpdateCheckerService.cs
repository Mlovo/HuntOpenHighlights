using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using HuntReplay.Core.Localization;

namespace HuntReplay.App.Services;

public sealed record UpdateCheckResult(
    bool HasUpdate,
    string CurrentVersion,
    string? LatestVersion,
    string? ReleaseUrl,
    string? ReleaseNotes,
    string? ErrorMessage);

public static class UpdateCheckerService
{
    private const string ApiUrl = "https://api.github.com/repos/Mlovo/HuntOpenHighlights/releases/latest";
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
        UpdateCheckResult Fail(string error) => new(false, current, null, null, null, error);
        try
        {
            using var response = await HttpClient.GetAsync(ApiUrl, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return Fail($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
            string Prop(string name) => doc.RootElement.TryGetProperty(name, out var e) ? e.GetString() ?? "" : "";
            var tag = Prop("tag_name").TrimStart('v', 'V').Trim();
            var url = Prop("html_url");
            bool newer = Version.TryParse(tag, out var latest) && Version.TryParse(current, out var mine) && latest > mine;
            return new(newer, current, tag.Length == 0 ? null : tag, url.Length == 0 ? null : url, Prop("body"), null);
        }
        catch (OperationCanceledException) { return Fail(Text.Get("Update.Cancelled")); }
        catch (Exception ex) { return Fail(ex.Message); }
    }
}
