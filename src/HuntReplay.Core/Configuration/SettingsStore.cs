using System.Text.Json;
using HuntReplay.Core.Localization;

namespace HuntReplay.Core.Configuration;

public sealed class SettingsStore(string path)
{
    public string FilePath { get; } = Path.GetFullPath(path);
    public string? LoadWarning { get; private set; }

    public StudioSettings Load()
    {
        if (!File.Exists(FilePath)) return new();
        try
        {
            var info = new FileInfo(FilePath);
            if (info.Length > 1024 * 1024) throw new InvalidDataException(Text.Get("Settings.FileTooLarge"));
            var value = JsonSerializer.Deserialize<StudioSettings>(File.ReadAllText(FilePath), StudioSettings.JsonOptions)
                ?? throw new InvalidDataException(Text.Get("Settings.FileEmpty"));
            value.Validate();
            return value;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // Preserve corrupt/future configs. The user can inspect them before saving again.
            LoadWarning = Text.Format("Settings.LoadFailed", ex.Message);
            return new();
        }
    }

    public void Save(StudioSettings value)
    {
        value.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, value, StudioSettings.JsonOptions);
                stream.Flush(true);
            }
            if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".bak", true);
            File.Move(temp, FilePath, true);
            LoadWarning = null;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
