using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

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
        return version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "0.1.6";
    }

    public static async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var currentVersionStr = GetCurrentVersionString();
        try
        {
            using var response = await HttpClient.GetAsync(ApiUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(
                    false,
                    currentVersionStr,
                    null,
                    null,
                    null,
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.TryGetProperty("tag_name", out var tagElem) ? tagElem.GetString() ?? "" : "";
            var htmlUrl = root.TryGetProperty("html_url", out var urlElem) ? urlElem.GetString() ?? "" : "";
            var body = root.TryGetProperty("body", out var bodyElem) ? bodyElem.GetString() ?? "" : "";

            var cleanTag = tagName.TrimStart('v', 'V').Trim();
            if (Version.TryParse(cleanTag, out var latestVer) &&
                Version.TryParse(currentVersionStr, out var currentVer))
            {
                bool hasUpdate = latestVer > currentVer;
                return new UpdateCheckResult(
                    hasUpdate,
                    currentVersionStr,
                    cleanTag,
                    string.IsNullOrWhiteSpace(htmlUrl) ? null : htmlUrl,
                    body,
                    null);
            }

            return new UpdateCheckResult(
                false,
                currentVersionStr,
                string.IsNullOrWhiteSpace(cleanTag) ? null : cleanTag,
                htmlUrl,
                body,
                null);
        }
        catch (OperationCanceledException)
        {
            return new UpdateCheckResult(false, currentVersionStr, null, null, null, "Проверка отменена");
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(false, currentVersionStr, null, null, null, ex.Message);
        }
    }
}
