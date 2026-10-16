using System.Text.RegularExpressions;
using HuntReplay.Core.Configuration;
using HuntReplay.Core.Localization;

namespace HuntReplay.Tests;

public static partial class Program
{
    private static readonly Regex Placeholder = new(@"\{(\d+)(?:[:,][^}]*)?\}", RegexOptions.CultureInvariant);

    // Keys referenced in source: Loc["X"], Loc.Format("X", ...), Text.Get("X"), Text.Format("X", ...),
    // Loc[cond ? "X" : "Y"], XAML {Binding Loc[X]} / {Binding [X], Source=...} and Require(..., "Settings.X").
    private static readonly Regex[] KeyUsages =
    [
        new(@"\b(?:Loc|loc|Text|LocalizationService\.Instance)(?:\.(?:Get|Format))?\s*[\[(]\s*""([A-Za-z]+\.[A-Za-z0-9.]+)""", RegexOptions.CultureInvariant),
        new(@"\?\s*""([A-Z][A-Za-z]+\.[A-Za-z0-9.]+)""\s*:\s*""([A-Z][A-Za-z]+\.[A-Za-z0-9.]+)""", RegexOptions.CultureInvariant),
        new(@"\bLoc\[([A-Za-z]+\.[A-Za-z0-9.]+)\]", RegexOptions.CultureInvariant),
        new(@"\{Binding \[([A-Za-z]+\.[A-Za-z0-9.]+)\]", RegexOptions.CultureInvariant),
        new(@"\bRequire\([^;]*""(Settings\.[A-Za-z0-9]+)""\)", RegexOptions.CultureInvariant | RegexOptions.Singleline),
    ];

    private static void LocalizationTests()
    {
        Test("localization catalogs have identical keys in every language", () =>
        {
            var reference = Text.Catalog(Text.Russian);
            foreach (var language in Text.Languages)
            {
                var catalog = Text.Catalog(language);
                var missing = reference.Keys.Except(catalog.Keys).ToArray();
                var extra = catalog.Keys.Except(reference.Keys).ToArray();
                if (missing.Length + extra.Length > 0)
                    throw new Exception($"{language}: missing [{string.Join(", ", missing)}], extra [{string.Join(", ", extra)}]");
            }
        });
        Test("localization catalogs use the same {n} placeholders in every language", () =>
        {
            var reference = Text.Catalog(Text.Russian);
            foreach (var language in Text.Languages)
                foreach (var (key, value) in Text.Catalog(language))
                {
                    var expected = Placeholders(reference[key]);
                    var actual = Placeholders(value);
                    if (!expected.SetEquals(actual))
                        throw new Exception($"{language}/{key}: placeholders {{{string.Join(",", actual)}}} differ from ru {{{string.Join(",", expected)}}}");
                }
        });
        Test("every localization key used in source exists in the catalog, and every catalog key is used", () =>
        {
            var root = RepositoryRoot();
            if (root is null) return; // Installed test runs without sources have nothing to scan.
            var catalog = Text.Catalog(Text.Russian);
            var unknown = new SortedSet<string>(StringComparer.Ordinal);
            var used = new HashSet<string>(StringComparer.Ordinal);
            var sources = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
                .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".xaml", StringComparison.Ordinal))
                .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                               !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));
            foreach (var path in sources)
            {
                var text = File.ReadAllText(path);
                foreach (var usage in KeyUsages)
                    foreach (Match match in usage.Matches(text))
                        foreach (Group group in match.Groups.Values.Skip(1))
                        {
                            if (!group.Success) continue;
                            used.Add(group.Value);
                            if (!catalog.ContainsKey(group.Value)) unknown.Add($"{Path.GetFileName(path)}: {group.Value}");
                        }
            }
            if (unknown.Count > 0) throw new Exception("Unknown localization keys: " + string.Join("; ", unknown));
            var unused = catalog.Keys.Where(key => !used.Contains(key)).OrderBy(key => key, StringComparer.Ordinal).ToArray();
            if (unused.Length > 0) throw new Exception("Unused localization keys (remove them): " + string.Join(", ", unused));
        });
        Test("language switch changes text and an unknown key falls back to the key itself", () =>
        {
            var original = Text.Language;
            try
            {
                Text.SetLanguage("en");
                Equal("Cancel", Text.Get("Common.Cancel"));
                Equal("Saved: clip.mp4", Text.Format("Status.ClipSaved", "clip.mp4"));
                Text.SetLanguage("something-else");
                Equal(Text.Russian, Text.Language);
                Equal("Отмена", Text.Get("Common.Cancel"));
                Equal("No.Such.Key", Text.Get("No.Such.Key"));
            }
            finally { Text.SetLanguage(original); }
        });
        Test("first start follows the Windows display language", () =>
        {
            var original = System.Globalization.CultureInfo.CurrentUICulture;
            try
            {
                System.Globalization.CultureInfo.CurrentUICulture = new("ru-RU");
                Equal(Text.Russian, new SystemSettings().Language);
                System.Globalization.CultureInfo.CurrentUICulture = new("en-US");
                Equal(Text.English, new SystemSettings().Language);
                System.Globalization.CultureInfo.CurrentUICulture = new("uk-UA");
                Equal(Text.English, new SystemSettings().Language);
            }
            finally { System.Globalization.CultureInfo.CurrentUICulture = original; }
        });
        Test("a saved language survives loading", () =>
            Equal(Text.English, System.Text.Json.JsonSerializer.Deserialize<StudioSettings>("""{"System":{"Language":"en"}}""", StudioSettings.JsonOptions)!.System.Language));
    }

    private static HashSet<int> Placeholders(string value) =>
        Placeholder.Matches(value).Select(m => int.Parse(m.Groups[1].Value)).ToHashSet();

    private static string? RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "HuntReplayStudio.sln"))) return dir.FullName;
        return null;
    }
}
