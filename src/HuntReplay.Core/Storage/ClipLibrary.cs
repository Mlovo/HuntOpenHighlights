using System.Text.Json;
using HuntReplay.Core.Configuration;

namespace HuntReplay.Core.Storage;

public sealed record ClipMetadata
{
    public string Owner { get; init; } = "HuntReplayStudio/1";
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string FileName { get; init; }
    public required string EventTag { get; init; }
    public string[] Events { get; init; } = [];
    public string GroupId { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public double DurationSeconds { get; init; }
    public long Bytes { get; init; }
    public string Encoder { get; init; } = "";
    public bool TruncatedStart { get; init; }
    public bool TruncatedEnd { get; init; }
}

public sealed record LibraryClip(string FullPath, ClipMetadata Metadata)
{
    public string SidecarPath => FullPath + ".hunt.json";
    public string ThumbnailPath => FullPath + ".thumb.png";
}

/// <summary>Only managed MP4s with valid ownership sidecars may be renamed/deleted/retained.</summary>
public sealed class ClipLibrary(string directory)
{
    public string Root { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

    public IReadOnlyList<LibraryClip> List()
    {
        if (!Directory.Exists(Root)) return [];
        var clips = new List<LibraryClip>();
        foreach (var sidecar in Directory.EnumerateFiles(Root, "*.mp4.hunt.json", new EnumerationOptions { RecurseSubdirectories = false, MatchCasing = MatchCasing.CaseInsensitive }))
        {
            try
            {
                if (IsLink(sidecar) || new FileInfo(sidecar).Length > 128 * 1024) continue;
                var data = JsonSerializer.Deserialize<ClipMetadata>(File.ReadAllText(sidecar), StudioSettings.JsonOptions);
                if (data is null || data.Owner != "HuntReplayStudio/1" || data.Id == Guid.Empty ||
                    !ClipFileName.IsSafeName(data.FileName) || string.IsNullOrWhiteSpace(data.EventTag) ||
                    data.EventTag.Length > 512 || !double.IsFinite(data.DurationSeconds) ||
                    data.DurationSeconds < 0 || data.DurationSeconds > 86400) continue;
                var path = Resolve(data.FileName);
                if (!string.Equals(path + ".hunt.json", sidecar, StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(path) || IsLink(path)) continue;
                clips.Add(new(path, data with { Bytes = new FileInfo(path).Length }));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidDataException)
            { /* One broken sidecar does not hide all healthy clips. Never delete it automatically. */ }
        }
        return clips.OrderByDescending(c => c.Metadata.CreatedAt).ToArray();
    }

    public string NewPath(string template, string eventTag, DateTimeOffset time)
    {
        Directory.CreateDirectory(Root);
        var name = ClipFileName.Create(template, eventTag, time);
        var path = Resolve(name);
        if (File.Exists(path) || File.Exists(path + ".partial.mp4") || File.Exists(path + ".hunt.json"))
            path = Resolve(Path.GetFileNameWithoutExtension(name) + "_" + Guid.NewGuid().ToString("N")[..8] + ".mp4");
        return path;
    }

    public LibraryClip Register(string path, ClipMetadata metadata)
    {
        EnsureChild(path);
        if (!File.Exists(path) || IsLink(path)) throw new IOException("Клип отсутствует или является ссылкой.");
        var actual = metadata with { FileName = Path.GetFileName(path), Bytes = new FileInfo(path).Length };
        WriteSidecar(path + ".hunt.json", actual);
        return new(path, actual);
    }

    public LibraryClip Rename(LibraryClip clip, string newName)
    {
        EnsureOwned(clip);
        if (!ClipFileName.IsSafeName(newName)) throw new InvalidDataException("Недопустимое имя .mp4.");
        var target = Resolve(newName);
        if (string.Equals(clip.FullPath, target, StringComparison.OrdinalIgnoreCase)) return clip;
        if (File.Exists(target) || File.Exists(target + ".hunt.json") || File.Exists(target + ".thumb.png"))
            throw new IOException("Это имя уже занято.");
        File.Move(clip.FullPath, target);
        bool thumbnailMoved = false;
        try
        {
            var renamed = Register(target, clip.Metadata with { FileName = newName });
            if (File.Exists(clip.ThumbnailPath))
            {
                File.Move(clip.ThumbnailPath, renamed.ThumbnailPath);
                thumbnailMoved = true;
            }
            File.Delete(clip.SidecarPath);
            return renamed;
        }
        catch
        {
            // Roll back both media assets if metadata could not be updated.
            if (thumbnailMoved) File.Move(target + ".thumb.png", clip.ThumbnailPath);
            File.Move(target, clip.FullPath);
            if (File.Exists(target + ".hunt.json")) File.Delete(target + ".hunt.json");
            throw;
        }
    }

    public void Delete(LibraryClip clip)
    {
        EnsureOwned(clip);
        File.Delete(clip.FullPath); // If locked by a player, metadata remains intact.
        File.Delete(clip.ThumbnailPath);
        File.Delete(clip.SidecarPath);
    }

    public int EnforceLimit(StorageSettings settings, ISet<Guid>? protectedIds = null)
    {
        if (!settings.AutoCleanup || settings.LimitGb == 0) return 0;
        return EnforceByteLimit(settings.LimitGb * 1_000_000_000L, protectedIds);
    }

    public int EnforceByteLimit(long limit, ISet<Guid>? protectedIds = null)
    {
        if (limit < 0) throw new ArgumentOutOfRangeException(nameof(limit));
        if (limit == 0) return 0;
        var clips = List();
        var bytes = clips.Sum(c => c.Metadata.Bytes);
        var removed = 0;
        foreach (var clip in clips.OrderBy(c => c.Metadata.CreatedAt))
        {
            if (bytes <= limit) break;
            if (protectedIds?.Contains(clip.Metadata.Id) == true) continue;
            try { Delete(clip); bytes -= clip.Metadata.Bytes; removed++; }
            catch (IOException) { /* Busy files are not force-deleted. */ }
            catch (UnauthorizedAccessException) { }
        }
        return removed;
    }

    public static void RequireDiskSpace(string path, long estimatedBytes)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path))!;
        var drive = new DriveInfo(root);
        if (drive.IsReady && drive.AvailableFreeSpace < estimatedBytes + 128L * 1024 * 1024)
            throw new IOException("Недостаточно места: нужен размер клипа + 128 МБ резерва.");
    }

    private string Resolve(string name)
    {
        if (!ClipFileName.IsSafeName(name)) throw new InvalidDataException("Недопустимое имя клипа.");
        var path = Path.Combine(Root, name);
        EnsureChild(path);
        return path;
    }

    private void EnsureChild(string path)
    {
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Файл вне библиотеки клипов.");
        if (Directory.Exists(Root) && IsLink(Root)) throw new IOException("Библиотека не должна быть reparse point.");
    }

    private void EnsureOwned(LibraryClip clip)
    {
        EnsureChild(clip.FullPath);
        if (IsLink(clip.FullPath) || IsLink(clip.SidecarPath) ||
            (File.Exists(clip.ThumbnailPath) && IsLink(clip.ThumbnailPath)))
            throw new IOException("Операции со ссылками запрещены.");
        var actual = List().FirstOrDefault(c => c.FullPath == clip.FullPath);
        if (actual?.Metadata.Id != clip.Metadata.Id) throw new IOException("Файл не принадлежит HuntReplay Studio.");
    }

    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static void WriteSidecar(string path, ClipMetadata value)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value, StudioSettings.JsonOptions));
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
