using System.Globalization;
using System.Text.RegularExpressions;
using HuntReplay.Core.Localization;

namespace HuntReplay.Core.Configuration;

public static class ClipFileName
{
    private static readonly char[] WindowsInvalid = "<>:\"/\\|?*".ToCharArray();

    public static void ValidateTemplate(string template)
    {
        if (string.IsNullOrWhiteSpace(template) || template.Length > 150 ||
            template.IndexOfAny(WindowsInvalid) >= 0 || template.Any(char.IsControl) ||
            !template.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(Text.Get("FileName.TemplateInvalid"));
        var remaining = template.Replace("{event}", "e").Replace("{date}", "d").Replace("{time}", "t");
        if (remaining.Contains('{') || remaining.Contains('}') || IsReserved(remaining))
            throw new InvalidDataException(Text.Get("FileName.TemplatePlaceholders"));
    }

    public static string Create(string template, string eventTag, DateTimeOffset time)
    {
        ValidateTemplate(template);
        var safeTag = SafeTagRegex.Replace(eventTag, "_");
        if (safeTag.Length > 80) safeTag = safeTag[..80];
        var name = template.Replace("{event}", safeTag)
            .Replace("{date}", time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Replace("{time}", time.ToString("HH-mm-ss-fff", CultureInfo.InvariantCulture));
        if (IsReserved(name)) throw new InvalidDataException(Text.Get("FileName.Reserved"));
        return name;
    }

    public static bool IsSafeName(string name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 220 &&
        name.IndexOfAny(WindowsInvalid) < 0 && !name.Any(char.IsControl) &&
        name == name.TrimEnd('.', ' ') && !IsReserved(name) &&
        name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);

    private static bool IsReserved(string name)
    {
        var stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" ||
               (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
                 stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '0' and <= '9');
    }

    private static readonly Regex SafeTagRegex = new("[^a-zA-Z0-9_-]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
