using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HuntReplay.Core.Configuration;

public enum CaptureSource { HuntWindow, Monitor }
public enum VideoEncoder { Auto, Amf, Nvenc, Qsv, X264 }
public enum VideoCodec { H264, Hevc, Av1 }
public enum VideoResolution { Native, FullHd, Qhd, Uhd }
public enum AudioMode { Mix, SeparateTracks }

public sealed class StudioSettings
{
    public int SchemaVersion { get; set; } = 1;
    public VideoSettings Video { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public ReplaySettings Replay { get; set; } = new();
    public StorageSettings Storage { get; set; } = new();
    public SystemSettings System { get; set; } = new();

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public StudioSettings Clone() => JsonSerializer.Deserialize<StudioSettings>(
        JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!;

    public bool IsEquivalent(StudioSettings? other)
    {
        if (other is null) return false;
        string a = JsonSerializer.Serialize(this, JsonOptions);
        string b = JsonSerializer.Serialize(other, JsonOptions);
        return string.Equals(a, b, StringComparison.Ordinal);
    }

    public void Validate()
    {
        if (SchemaVersion != 1) throw new InvalidDataException("Неподдерживаемая версия config.json.");
        if (Video is null || Audio is null || Replay is null || Storage is null || System is null)
            throw new InvalidDataException("В config.json отсутствует раздел настроек.");
        Require(Enum.IsDefined(Video.Source) && Enum.IsDefined(Video.Encoder) &&
                Enum.IsDefined(Video.Codec) && Enum.IsDefined(Video.Resolution), "Неизвестный видеорежим.");
        Require(Video.Fps is 30 or 60 or 120, "FPS: 30, 60 или 120.");
        Require(Video.BitrateMbps is >= 10 and <= 80, "Битрейт: 10–80 Мбит/с.");
        Require(Video.GopSeconds is 1 or 2, "GOP: 1 или 2 секунды.");
        Require(Video.Encoder != VideoEncoder.X264 || Video.Codec == VideoCodec.H264,
            "CPU x264 поддерживает только H.264. Программный fallback никогда не включается скрыто.");
        Require(Video.MonitorDevice is not null, "Не указан монитор.");
        Require(Video.GpuDevice is not null, "Не указана видеокарта.");
        Require(Enum.IsDefined(Audio.Mode), "Неизвестный аудиорежим.");
        Require(Audio.MicrophoneVolume is >= 0 and <= 100, "Громкость микрофона: 0–100%.");
        Require(Audio.OutputDeviceId is not null && Audio.MicrophoneDeviceId is not null, "Не указаны аудиоустройства.");
        Require(Replay.BufferSeconds is >= 15 and <= 300, "Буфер: 15–300 секунд.");
        Require(double.IsFinite(Replay.PostEventSeconds) && Replay.PostEventSeconds is >= 0 and <= 30,
            "Постзапись: 0–30 секунд.");
        Require(Replay.CoalesceGapSeconds is >= 1 and <= 30, "Окно объединения: 1–30 секунд.");
        Require(Replay.MaxSequenceSeconds is >= 15 and <= 120, "Максимальная серия: 15–120 секунд.");
        Require(Replay.PreEventSeconds is >= 1 and <= 300, "Предзапись: 1–300 секунд.");
        Require(!string.IsNullOrWhiteSpace(Storage.Directory) && Path.IsPathFullyQualified(Storage.Directory),
            "Для клипов нужен абсолютный путь.");
        Require(Storage.LimitGb is 0 or 15 or 30 or 60, "Лимит диска: 15, 30, 60 ГБ или без ограничений.");
        ClipFileName.ValidateTemplate(Storage.FileNameTemplate);
        Require(System.HotkeyVirtualKey is >= 0x70 and <= 0x7B, "Для ручного сохранения доступны F1–F12.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

public sealed class VideoSettings
{
    public CaptureSource Source { get; set; } = CaptureSource.HuntWindow;
    // Persist the Windows display device name, never a transient HMONITOR.
    private string monitorDevice = "";
    // Old UI versions could serialize null during ComboBox refresh. Recover as Windows default.
    [AllowNull]
    public string MonitorDevice { get => monitorDevice; set => monitorDevice = value ?? ""; }
    private string gpuDevice = "";
    [AllowNull]
    public string GpuDevice { get => gpuDevice; set => gpuDevice = value ?? ""; }
    public VideoResolution Resolution { get; set; } = VideoResolution.Native;
    public int Fps { get; set; } = 60;
    public VideoEncoder Encoder { get; set; } = VideoEncoder.Auto;
    public VideoCodec Codec { get; set; } = VideoCodec.H264;
    public int BitrateMbps { get; set; } = 25;
    public int GopSeconds { get; set; } = 1;
}

public sealed class AudioSettings
{
    private string outputDeviceId = "", microphoneDeviceId = "";
    [AllowNull]
    public string OutputDeviceId { get => outputDeviceId; set => outputDeviceId = value ?? ""; }
    public bool MicrophoneEnabled { get; set; }
    [AllowNull]
    public string MicrophoneDeviceId { get => microphoneDeviceId; set => microphoneDeviceId = value ?? ""; }
    public int MicrophoneVolume { get; set; } = 80;
    public AudioMode Mode { get; set; } = AudioMode.Mix;
}

public sealed class ReplaySettings
{
    public int BufferSeconds { get; set; } = 60;
    public double PostEventSeconds { get; set; } = 7;
    public bool Coalesce { get; set; } = true;
    public int CoalesceGapSeconds { get; set; } = 15;
    public int MaxSequenceSeconds { get; set; } = 60;
    public bool SmartTrim { get; set; } = true;
    public int PreEventSeconds { get; set; } = 20;
    public bool HunterKilled { get; set; } = true;
    public bool PlayerDowned { get; set; } = true;
    // Other Highlights games can opt in by editing this array. Never execute it as code.
    public string[] AdditionalEvents { get; set; } = [];

    public bool IsEnabled(string id) => id switch
    {
        "HUNTER_KILLED" => HunterKilled,
        "PLAYER_DOWNED" => PlayerDowned,
        _ => AdditionalEvents?.Contains(id, StringComparer.Ordinal) == true
    };
}

public sealed class StorageSettings
{
    public string Directory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "HuntHighlights");
    public string FileNameTemplate { get; set; } = "Hunt_{event}_{date}_{time}.mp4";
    public bool AutoCleanup { get; set; }
    public int LimitGb { get; set; } = 30;
}

public sealed class SystemSettings
{
    public int HotkeyVirtualKey { get; set; } = 0x78; // RegisterHotKey F9, NOT SendInput.
    public bool SoundOnSave { get; set; } = true;
    public bool ToastOnSave { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool AutoBufferWithGame { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool FirstRunCompleted { get; set; }
    public string Language { get; set; } = "ru";
    public string Theme { get; set; } = "dark";
}
