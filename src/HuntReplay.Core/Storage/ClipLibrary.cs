using System.Text.Json;
using HuntReplay.Core.Configuration;
using HuntReplay.Core.Localization;

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
    /// <summary>Starred in the gallery. Lives in the record, so it follows a rename and goes with a delete.</summary>
    public bool Favorite { get; init; }
}

/// <summary>
/// A managed clip. For a clip stored by this version, <see cref="RecordPath"/> and <see cref="ThumbnailPath"/> point
/// into the application library, never into the clips folder. A 0.1.2 clip that Maintain has not adopted yet
/// points at its own sidecar files instead.
/// </summary>
public sealed record LibraryClip(string FullPath, ClipMetadata Metadata, string RecordPath, string ThumbnailPath);

/// <summary>What <see cref="ClipLibrary.Maintain"/> changed. Used for logging only.</summary>
public sealed record LibraryMaintenance(int Adopted, int Relocated, int Pruned);

/// <summary>On-disk library record: the clips folder that owns the clip, plus the clip metadata.</summary>
internal sealed record ClipRecord
{
    public required string Folder { get; init; }
    public required ClipMetadata Clip { get; init; }
}

/// <summary>A 0.1.2 sidecar (<c>name.mp4.hunt.json</c>) found in the clips folder.</summary>
internal sealed record LegacyClip(string Mp4Path, string SidecarPath, ClipMetadata Data);

/// <summary>
/// Clip library for one clips folder. The folder holds only MP4 files. Ownership and metadata are stored in
/// <c>{storeRoot}\records\{Id}.json</c>, thumbnails in <c>{storeRoot}\thumbs\{Id}.png</c>.
/// Sidecars written by 0.1.2 and earlier (<c>*.mp4.hunt.json</c>, <c>*.mp4.thumb.png</c>) are still read,
/// and <see cref="Maintain"/> moves them into the store.
/// </summary>
public sealed class ClipLibrary(string directory, string storeRoot)
{
    private const long MaxRecordBytes = 128 * 1024;
    private static readonly TimeSpan StaleTemporaryAge = TimeSpan.FromMinutes(10);
    // Mutations are serialized in-process so a rename, a retention pass and a maintenance pass cannot interleave.
    private static readonly object Gate = new();

    private readonly string recordsDirectory = Path.Combine(Path.GetFullPath(storeRoot), "records");
    private readonly string thumbnailsDirectory = Path.Combine(Path.GetFullPath(storeRoot), "thumbs");

    public string Root { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

    /// <summary>Managed clips in this folder, newest first. Read-only.</summary>
    public IReadOnlyList<LibraryClip> List()
    {
        if (!Directory.Exists(Root)) return [];
        var byPath = new Dictionary<string, LibraryClip>(StringComparer.OrdinalIgnoreCase);
        foreach (var (filePath, record) in ReadRecords().OrderBy(r => r.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            if (!SameFolder(record.Folder)) continue;
            var path = Path.Combine(Root, record.Clip.FileName);
            var metadata = Measure(path, record.Clip);
            if (metadata is not null) byPath.TryAdd(path, new(path, metadata, filePath, ThumbnailPathFor(record.Clip.Id)));
        }
        // 0.1.2 clips stay visible even before Maintain has moved them into the store.
        foreach (var legacy in ReadLegacy())
        {
            if (byPath.ContainsKey(legacy.Mp4Path)) continue;
            var metadata = Measure(legacy.Mp4Path, legacy.Data);
            // Until Maintain adopts it, a 0.1.2 clip is addressed through its own sidecar files. An Id shared with
            // another clip's record therefore can never make Delete or Rename touch that record or its thumbnail.
            if (metadata is not null)
                byPath.Add(legacy.Mp4Path, new(legacy.Mp4Path, metadata, legacy.SidecarPath, legacy.Mp4Path + ".thumb.png"));
        }
        return byPath.Values.OrderByDescending(c => c.Metadata.CreatedAt).ToArray();
    }

    public string NewPath(string template, string eventTag, DateTimeOffset time)
    {
        Directory.CreateDirectory(Root);
        var name = ClipFileName.Create(template, eventTag, time);
        var path = Resolve(name);
        if (File.Exists(path) || File.Exists(path + ".partial.mp4") || File.Exists(path + ".hunt.json") || RecordedFileNames().Contains(name))
            path = Resolve(Path.GetFileNameWithoutExtension(name) + "_" + Guid.NewGuid().ToString("N")[..8] + ".mp4");
        return path;
    }

    public LibraryClip Register(string path, ClipMetadata metadata)
    {
        lock (Gate)
        {
            EnsureChild(path);
            if (!File.Exists(path) || IsLink(path)) throw new IOException(Text.Get("Library.ClipMissingOrLink"));
            return WriteRecord(path, metadata with { FileName = Path.GetFileName(path), Bytes = new FileInfo(path).Length });
        }
    }

    public LibraryClip Rename(LibraryClip clip, string newName)
    {
        lock (Gate)
        {
            EnsureOwned(clip);
            if (!ClipFileName.IsSafeName(newName)) throw new InvalidDataException(Text.Get("Library.InvalidMp4Name"));
            var target = Resolve(newName);
            if (string.Equals(clip.FullPath, target, StringComparison.OrdinalIgnoreCase)) return clip;
            if (File.Exists(target) || File.Exists(target + ".hunt.json")) throw new IOException(Text.Get("Library.NameTaken"));
            File.Move(clip.FullPath, target);
            LibraryClip renamed;
            try { renamed = WriteRecord(target, clip.Metadata with { FileName = newName, Id = FreeId(clip) }); }
            catch { File.Move(target, clip.FullPath); throw; } // The record did not change, so the MP4 goes back.
            // The record is authoritative now; 0.1.2 leftovers beside the old name are retired best-effort.
            RetireLegacy(clip.FullPath, renamed.Metadata.Id);
            return renamed;
        }
    }

    /// <summary>Stars or unstars a clip. The MP4 is never touched; a 0.1.2 clip is adopted into the store on the way.</summary>
    public LibraryClip SetFavorite(LibraryClip clip, bool favorite)
    {
        lock (Gate)
        {
            EnsureOwned(clip);
            var updated = WriteRecord(clip.FullPath, clip.Metadata with { Favorite = favorite, Id = FreeId(clip) });
            if (!string.Equals(clip.RecordPath, updated.RecordPath, StringComparison.OrdinalIgnoreCase)) RetireLegacy(clip.FullPath, updated.Metadata.Id);
            return updated;
        }
    }

    /// <summary>
    /// The Id a clip keeps when its record is rewritten. A 0.1.2 clip whose Id is already used by another record
    /// or thumbnail (a copied clips folder) gets a fresh Id, so the other clip's files are never overwritten.
    /// </summary>
    private Guid FreeId(LibraryClip clip)
    {
        var id = clip.Metadata.Id;
        var isOwnRecord = string.Equals(clip.RecordPath, RecordPathFor(id), StringComparison.OrdinalIgnoreCase);
        return isOwnRecord || (!File.Exists(RecordPathFor(id)) && !File.Exists(ThumbnailPathFor(id))) ? id : Guid.NewGuid();
    }

    public void Delete(LibraryClip clip)
    {
        lock (Gate)
        {
            EnsureOwned(clip);
            DeleteListed(clip);
        }
    }

    /// <summary>
    /// Deletes each clip on its own and returns the ones that failed: a clip whose MP4 is locked stays listed,
    /// the rest are still deleted. One listing proves ownership for all of them.
    /// </summary>
    public IReadOnlyList<(LibraryClip Clip, Exception Error)> DeleteEach(IEnumerable<LibraryClip> clips)
    {
        var failed = new List<(LibraryClip, Exception)>();
        lock (Gate)
        {
            var listed = List().ToDictionary(c => c.FullPath, StringComparer.OrdinalIgnoreCase);
            foreach (var clip in clips)
            {
                try
                {
                    if (!listed.TryGetValue(clip.FullPath, out var actual) || actual.Metadata.Id != clip.Metadata.Id)
                        throw new IOException(Text.Get("Library.NotOwned"));
                    DeleteListed(actual);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed.Add((clip, ex)); }
            }
        }
        return failed;
    }

    /// <summary>
    /// Deletes a clip whose ownership is already proven: the caller holds <see cref="Gate"/> and took the clip
    /// from <see cref="List"/> in the same critical section, so the full record re-scan of EnsureOwned is skipped.
    /// </summary>
    private void DeleteListed(LibraryClip clip)
    {
        EnsureNoLinks(clip);
        File.Delete(clip.FullPath); // If locked by a player, metadata remains intact.
        // Metadata goes last: if the MP4 survives, the clip stays listed and can be retried.
        DeleteIfExists(clip.ThumbnailPath);
        DeleteIfExists(clip.RecordPath);
        RetireLegacy(clip.FullPath, null);
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
        lock (Gate)
        {
            // One listing for the whole pass: per-clip Delete would re-read every record for each removal.
            var clips = List();
            var bytes = clips.Sum(c => c.Metadata.Bytes);
            var removed = 0;
            foreach (var clip in clips.OrderBy(c => c.Metadata.CreatedAt))
            {
                if (bytes <= limit) break;
                if (protectedIds?.Contains(clip.Metadata.Id) == true) continue;
                try { DeleteListed(clip); bytes -= clip.Metadata.Bytes; removed++; }
                catch (IOException) { /* Busy files are not force-deleted. */ }
                catch (UnauthorizedAccessException) { }
            }
            return removed;
        }
    }

    /// <summary>
    /// Brings the store in line with the clips folder: moves 0.1.2 sidecars into the store, follows a renamed
    /// clips folder, and removes records and thumbnails whose MP4 was deleted outside Studio.
    /// Files Studio did not write are never touched, and broken records are left in place.
    /// </summary>
    public LibraryMaintenance Maintain()
    {
        lock (Gate)
        {
            Directory.CreateDirectory(recordsDirectory);
            Directory.CreateDirectory(thumbnailsDirectory);
            var adopted = AdoptLegacy();
            var relocated = FollowFolderMove();
            var pruned = PruneOrphans();
            RemoveStaleTemporaryFiles();
            return new(adopted, relocated, pruned);
        }
    }

    public static void RequireDiskSpace(string path, long estimatedBytes)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path))!;
        DriveInfo drive;
        try { drive = new DriveInfo(root); }
        catch (ArgumentException) { return; } // UNC shares cannot be queried; a full share still fails the write itself.
        if (drive.IsReady && drive.AvailableFreeSpace < estimatedBytes + 128L * 1024 * 1024)
            throw new IOException(Text.Get("Library.NotEnoughSpace"));
    }

    private LibraryClip WriteRecord(string path, ClipMetadata clip)
    {
        Directory.CreateDirectory(recordsDirectory);
        Directory.CreateDirectory(thumbnailsDirectory);
        var recordPath = RecordPathFor(clip.Id);
        WriteJson(recordPath, new ClipRecord { Folder = Root, Clip = clip });
        return new(path, clip, recordPath, ThumbnailPathFor(clip.Id));
    }

    private int AdoptLegacy()
    {
        var adopted = 0;
        var records = ReadRecords();
        foreach (var legacy in ReadLegacy())
        {
            try
            {
                var fileName = Path.GetFileName(legacy.Mp4Path);
                var owner = records.FirstOrDefault(r => SameFolder(r.Record.Folder) &&
                    string.Equals(r.Record.Clip.FileName, fileName, StringComparison.OrdinalIgnoreCase));
                if (owner.Record is not null)
                {
                    // The store already owns this file. The same Id means an earlier run stopped before the sidecar was removed.
                    if (owner.Record.Clip.Id == legacy.Data.Id) RetireLegacy(legacy.Mp4Path, legacy.Data.Id);
                    continue;
                }
                // Keep the Id unless a record or thumbnail with it already exists (for example, a copied clips folder).
                var id = File.Exists(RecordPathFor(legacy.Data.Id)) || File.Exists(ThumbnailPathFor(legacy.Data.Id))
                    ? Guid.NewGuid() : legacy.Data.Id;
                var clip = legacy.Data with { Id = id, Bytes = new FileInfo(legacy.Mp4Path).Length };
                WriteJson(RecordPathFor(id), new ClipRecord { Folder = Root, Clip = clip });
                RetireLegacy(legacy.Mp4Path, id);
                adopted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { /* The sidecar stays; the clip remains visible through the legacy path in List(). */ }
        }
        return adopted;
    }

    private int FollowFolderMove()
    {
        var moved = 0;
        var records = ReadRecords();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (filePath, record) in records)
        {
            try
            {
                if (SameFolder(record.Folder) || !Directory.Exists(Root)) continue;
                var name = record.Clip.FileName;
                // The old MP4 is gone and the same name exists here: the clips folder was moved or renamed.
                if (File.Exists(Path.Combine(record.Folder, name)) || !IsManagedMp4(Path.Combine(Root, name)) || !claimed.Add(name)) continue;
                if (records.Any(other => SameFolder(other.Record.Folder) &&
                    string.Equals(other.Record.Clip.FileName, name, StringComparison.OrdinalIgnoreCase))) continue;
                WriteJson(filePath, record with { Folder = Root });
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return moved;
    }

    private int PruneOrphans()
    {
        var pruned = 0;
        // Records are read before the folder listing: a clip registered after this point is never pruned.
        var records = ReadRecords();
        if (Directory.Exists(Root) && PresentFileNames() is { } present)
        {
            foreach (var (filePath, record) in records)
            {
                if (!SameFolder(record.Folder) || present.Contains(record.Clip.FileName)) continue;
                try
                {
                    DeleteIfExists(filePath);
                    DeleteIfExists(ThumbnailPathFor(record.Clip.Id));
                    pruned++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            pruned += PruneLegacyOrphans(present);
        }
        foreach (var thumbnail in FilesMatching(thumbnailsDirectory, "*.png"))
        {
            try
            {
                if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(thumbnail), "D", out var id) && !File.Exists(RecordPathFor(id)))
                {
                    File.Delete(thumbnail);
                    pruned++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return pruned;
    }

    /// <summary>
    /// 0.1.2 leftovers whose MP4 was deleted outside Studio. Only valid sidecars and their thumbnails are removed;
    /// a broken sidecar is kept, as is anything whose MP4 still exists.
    /// </summary>
    private int PruneLegacyOrphans(HashSet<string> present)
    {
        var pruned = 0;
        foreach (var sidecar in FilesMatching(Root, "*.mp4.hunt.json"))
        {
            try
            {
                if (IsLink(sidecar) || new FileInfo(sidecar).Length > MaxRecordBytes) continue;
                var data = JsonSerializer.Deserialize<ClipMetadata>(File.ReadAllText(sidecar), StudioSettings.JsonOptions);
                if (data is null || !IsValid(data) || present.Contains(data.FileName)) continue;
                var mp4 = Path.Combine(Root, data.FileName);
                if (!string.Equals(mp4 + ".hunt.json", sidecar, StringComparison.OrdinalIgnoreCase)) continue;
                RetireLegacy(mp4, null);
                pruned++;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
            { /* Broken sidecars are never deleted automatically. */ }
        }
        foreach (var thumbnail in FilesMatching(Root, "*.mp4.thumb.png"))
        {
            try
            {
                var mp4Name = Path.GetFileName(thumbnail)[..^".thumb.png".Length];
                if (present.Contains(mp4Name) || IsLink(thumbnail)) continue;
                File.Delete(thumbnail);
                pruned++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return pruned;
    }

    private void RemoveStaleTemporaryFiles()
    {
        // *.partial.mp4 in the clips folder is an export or trim that a crash or power loss interrupted.
        foreach (var (directory, pattern) in new[] { (recordsDirectory, "*.tmp"), (thumbnailsDirectory, "*.tmp"), (Root, "*.partial.mp4") })
        {
            foreach (var temp in FilesMatching(directory, pattern))
            {
                try { if (!IsLink(temp) && DateTime.UtcNow - File.GetLastWriteTimeUtc(temp) > StaleTemporaryAge) File.Delete(temp); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private HashSet<string>? PresentFileNames()
    {
        try
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(Root)) names.Add(Path.GetFileName(file));
            return names;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private HashSet<string> RecordedFileNames() =>
        ReadRecords().Where(r => SameFolder(r.Record.Folder)).Select(r => r.Record.Clip.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Removes 0.1.2 sidecars beside an MP4. With an Id, its legacy thumbnail moves into the store.</summary>
    private void RetireLegacy(string mp4Path, Guid? keepThumbnailFor)
    {
        try
        {
            var legacyThumbnail = mp4Path + ".thumb.png";
            if (File.Exists(legacyThumbnail) && keepThumbnailFor is { } id && !File.Exists(ThumbnailPathFor(id)))
                File.Move(legacyThumbnail, ThumbnailPathFor(id));
            else
                DeleteIfExists(legacyThumbnail);
            DeleteIfExists(mp4Path + ".hunt.json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { /* Best effort: the store record is authoritative, and List() never shows a duplicate. */ }
    }

    /// <summary>Valid records in the store. Broken files are skipped, never deleted.</summary>
    private List<(string FilePath, ClipRecord Record)> ReadRecords()
    {
        var result = new List<(string FilePath, ClipRecord Record)>();
        foreach (var file in FilesMatching(recordsDirectory, "*.json"))
        {
            try
            {
                if (IsLink(file) || new FileInfo(file).Length > MaxRecordBytes) continue;
                var record = JsonSerializer.Deserialize<ClipRecord>(File.ReadAllText(file), StudioSettings.JsonOptions);
                if (record is not null && IsValidRecord(file, record)) result.Add((file, record));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
            { /* One broken record does not hide healthy clips. */ }
        }
        return result;
    }

    /// <summary>0.1.2 sidecars in the clips folder whose MP4 exists. Broken sidecars are skipped, never deleted.</summary>
    private List<LegacyClip> ReadLegacy()
    {
        var result = new List<LegacyClip>();
        foreach (var sidecar in FilesMatching(Root, "*.mp4.hunt.json"))
        {
            try
            {
                if (IsLink(sidecar) || new FileInfo(sidecar).Length > MaxRecordBytes) continue;
                var data = JsonSerializer.Deserialize<ClipMetadata>(File.ReadAllText(sidecar), StudioSettings.JsonOptions);
                if (data is null || !IsValid(data)) continue;
                var mp4 = Path.Combine(Root, data.FileName);
                if (!string.Equals(mp4 + ".hunt.json", sidecar, StringComparison.OrdinalIgnoreCase) || !IsManagedMp4(mp4)) continue;
                result.Add(new(mp4, sidecar, data));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
            { /* One broken sidecar does not hide all healthy clips. */ }
        }
        return result;
    }

    private static string[] FilesMatching(string directory, string pattern)
    {
        if (!Directory.Exists(directory)) return [];
        try
        {
            return Directory.EnumerateFiles(directory, pattern, new EnumerationOptions
            {
                RecurseSubdirectories = false, MatchCasing = MatchCasing.CaseInsensitive, IgnoreInaccessible = true
            }).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static ClipMetadata? Measure(string mp4Path, ClipMetadata data)
    {
        try { return IsManagedMp4(mp4Path) ? data with { Bytes = new FileInfo(mp4Path).Length } : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool IsValid(ClipMetadata data) =>
        data.Owner == "HuntReplayStudio/1" && data.Id != Guid.Empty && ClipFileName.IsSafeName(data.FileName) &&
        !string.IsNullOrWhiteSpace(data.EventTag) && data.EventTag.Length <= 512 &&
        double.IsFinite(data.DurationSeconds) && data.DurationSeconds >= 0 && data.DurationSeconds <= 86400;

    private static bool IsValidRecord(string filePath, ClipRecord record) =>
        Path.IsPathFullyQualified(record.Folder) && IsValid(record.Clip) &&
        string.Equals(Path.GetFileNameWithoutExtension(filePath), record.Clip.Id.ToString("D"), StringComparison.OrdinalIgnoreCase);

    private bool SameFolder(string folder) => string.Equals(folder, Root, StringComparison.OrdinalIgnoreCase);

    private static bool IsManagedMp4(string path) => File.Exists(path) && !IsLink(path);

    private string RecordPathFor(Guid id) => Path.Combine(recordsDirectory, id.ToString("D") + ".json");

    private string ThumbnailPathFor(Guid id) => Path.Combine(thumbnailsDirectory, id.ToString("D") + ".png");

    private string Resolve(string name)
    {
        if (!ClipFileName.IsSafeName(name)) throw new InvalidDataException(Text.Get("Library.InvalidClipName"));
        var path = Path.Combine(Root, name);
        EnsureChild(path);
        return path;
    }

    private void EnsureChild(string path)
    {
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(Text.Get("Library.OutsideLibrary"));
        if (Directory.Exists(Root) && IsLink(Root)) throw new IOException(Text.Get("Library.RootIsLink"));
    }

    private void EnsureNoLinks(LibraryClip clip)
    {
        EnsureChild(clip.FullPath);
        if (IsLink(clip.FullPath) || IsExistingLink(clip.RecordPath) || IsExistingLink(clip.ThumbnailPath) ||
            IsExistingLink(clip.FullPath + ".hunt.json") || IsExistingLink(clip.FullPath + ".thumb.png"))
            throw new IOException(Text.Get("Library.LinksForbidden"));
    }

    private void EnsureOwned(LibraryClip clip)
    {
        EnsureNoLinks(clip);
        var actual = List().FirstOrDefault(c => c.FullPath == clip.FullPath);
        if (actual?.Metadata.Id != clip.Metadata.Id) throw new IOException(Text.Get("Library.NotOwned"));
    }

    private static bool IsExistingLink(string path) => File.Exists(path) && IsLink(path);

    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static void WriteJson<T>(string path, T value)
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
