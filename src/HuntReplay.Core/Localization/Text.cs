using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace HuntReplay.Core.Localization;

/// <summary>
/// The single source of user-visible text for every layer (Core, Protocol, App). Catalogs are embedded JSON files,
/// <c>{language}.json</c>, so the portable single-file build needs no satellite assemblies. Russian is the
/// reference catalog: a key missing from another language falls back to it, and an unknown key returns the key itself.
/// A unit test keeps the catalogs in step (same keys, same <c>{n}</c> placeholders) and checks every key used in source.
/// </summary>
public static class Text
{
    public const string Russian = "ru";
    public const string English = "en";
    public static IReadOnlyList<string> Languages { get; } = [Russian, English];

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Catalogs =
        Languages.ToDictionary(language => language, Load);
    // One reference swap per language change; readers on any thread see a complete catalog.
    private static volatile IReadOnlyDictionary<string, string> current = Catalogs[Russian];

    public static string Language { get; private set; } = Russian;

    /// <summary>Culture for language-dependent formatting such as month names. Numbers keep the OS culture.</summary>
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(Language == English ? "en-US" : "ru-RU");

    /// <summary>Raised after the language changes, on the thread that changed it.</summary>
    public static event Action? LanguageChanged;

    /// <summary>Selects a language by code ("en", "english", "ru"...). Anything unrecognized selects Russian.</summary>
    public static void SetLanguage(string? code)
    {
        var language = code?.Trim().ToLowerInvariant() switch
        {
            "en" or "english" => English,
            _ => Russian
        };
        if (language == Language) return;
        current = Catalogs[language];
        Language = language;
        LanguageChanged?.Invoke();
    }

    public static string Get(string key) =>
        current.TryGetValue(key, out var value) || Catalogs[Russian].TryGetValue(key, out value) ? value : key;

    public static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);

    /// <summary>The raw catalog of one language; used by the consistency tests.</summary>
    public static IReadOnlyDictionary<string, string> Catalog(string language) => Catalogs[language];

    private static IReadOnlyDictionary<string, string> Load(string language)
    {
        var name = $"HuntReplay.Core.Localization.{language}.json";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("Missing embedded localization catalog " + name);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidDataException("Empty localization catalog " + name);
    }
}
