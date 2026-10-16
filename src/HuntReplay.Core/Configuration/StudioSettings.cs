using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using HuntReplay.Core.Localization;

namespace HuntReplay.Core.Configuration;

public enum CaptureSource { HuntWindow, Monitor }
public enum VideoEncoder { Auto, Amf, Nvenc, Qsv, X264 }
public enum VideoCodec { H264, Hevc, Av1 }
// Each preset is an output height; the width follows the source's aspect ratio. FullHd, Qhd and Uhd keep their
// names from older configs (1080p, 1440p, 2160p); the other presets were added after them.
public enum VideoResolution { Native, FullHd, Qhd, Uhd, P720, P900, P1200, P1600 }
public enum AudioMode { Mix, SeparateTracks }
public enum OverlayCorner { TopLeft, TopRight, BottomLeft, BottomRight }

public static class VideoResolutions
{
    /// <summary>Every preset other than Native, from the smallest output to the largest.</summary>
    public static IReadOnlyList<VideoResolution> Presets { get; } = [
        VideoResolution.P720, VideoResolution.P900, VideoResolution.FullHd, VideoResolution.P1200,
        VideoResolution.Qhd, VideoResolution.P1600, VideoResolution.Uhd];

    /// <summary>The output height of a preset; 0 for Native.</summary>
    public static int Height(this VideoResolution resolution) => resolution switch
    {
        VideoResolution.P720 => 720, VideoResolution.P900 => 900, VideoResolution.FullHd => 1080, VideoResolution.P1200 => 1200,
        VideoResolution.Qhd => 1440, VideoResolution.P1600 => 1600, VideoResolution.Uhd => 2160, _ => 0
    };

    /// <summary>
    /// The size recorded from a source of the given size. Mirrors hr::fit_output in native/src/capture.cpp:
    /// the source's aspect ratio is kept, dimensions are even, and a preset never scales the source up.
    /// </summary>
    public static (int Width, int Height) OutputSize(this VideoResolution resolution, int sourceWidth, int sourceHeight)
    {
        int height = resolution.Height();
        if (height == 0 || height >= sourceHeight) return (sourceWidth / 2 * 2, sourceHeight / 2 * 2);
        int width = (int)Math.Round((double)sourceWidth * height / sourceHeight, MidpointRounding.AwayFromZero);
        return (Math.Max(2, width / 2 * 2), height / 2 * 2);
    }
}

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
        if (SchemaVersion != 1) throw new InvalidDataException(Text.Get("Settings.UnsupportedSchema"));
        if (Video is null || Audio is null || Replay is null || Storage is null || System is null)
            throw new InvalidDataException(Text.Get("Settings.MissingSection"));
        Require(Enum.IsDefined(Video.Source) && Enum.IsDefined(Video.Encoder) &&
                Enum.IsDefined(Video.Codec) && Enum.IsDefined(Video.Resolution), "Settings.UnknownVideoMode");
        Require(Video.Fps is 30 or 60 or 120, "Settings.FpsRange");
        Require(Video.BitrateMbps is >= 10 and <= 80, "Settings.BitrateRange");
        Require(Video.GopSeconds is 1 or 2, "Settings.GopRange");
        Require(Video.Encoder != VideoEncoder.X264 || Video.Codec == VideoCodec.H264,
            "Settings.X264H264Only");
        Require(Video.MonitorDevice is not null, "Settings.MonitorMissing");
        Require(Video.GpuDevice is not null, "Settings.GpuMissing");
        Require(Enum.IsDefined(Audio.Mode), "Settings.UnknownAudioMode");
        Require(Audio.MicrophoneVolume is >= 0 and <= 100, "Settings.MicVolumeRange");
        Require(Audio.OutputDeviceId is not null && Audio.MicrophoneDeviceId is not null, "Settings.AudioDevicesMissing");
        Require(Replay.BufferSeconds is >= 15 and <= 300, "Settings.BufferRange");
        Require(double.IsFinite(Replay.PostEventSeconds) && Replay.PostEventSeconds is >= 0 and <= 30,
            "Settings.PostRollRange");
        Require(Replay.CoalesceGapSeconds is >= 1 and <= 30, "Settings.CoalesceRange");
        Require(Replay.MaxSequenceSeconds is >= 15 and <= 120, "Settings.SequenceRange");
        Require(Replay.PreEventSeconds is >= 1 and <= 300, "Settings.PreRollRange");
        Require(!string.IsNullOrWhiteSpace(Storage.Directory) && Path.IsPathFullyQualified(Storage.Directory),
            "Settings.StorageAbsolute");
        Require(Storage.LimitGb is 0 or 15 or 30 or 60, "Settings.DiskLimitRange");
        ClipFileName.ValidateTemplate(Storage.FileNameTemplate);
        Require(System.HotkeyVirtualKey is >= 0x70 and <= 0x7B, "Settings.HotkeyRange");
        Require(Enum.IsDefined(System.OverlayCorner) && System.OverlaySeconds is 2 or 3 or 5, "Settings.OverlayRange");
    }

    // Validation messages are catalog keys, resolved in the current UI language when a rule fails.
    private static void Require(bool condition, string messageKey)
    {
        if (!condition) throw new InvalidDataException(Text.Get(messageKey));
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
    // Hunt is often very dark; lower rates band and smear shadows. A saved config keeps its own rate.
    public int BitrateMbps { get; set; } = 40;
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
    // A small click-through notification over the game, never recorded into clips. Off unless the user enables it.
    public bool OverlayEnabled { get; set; }
    public OverlayCorner OverlayCorner { get; set; } = OverlayCorner.TopRight;
    public int OverlaySeconds { get; set; } = 3;
    public bool StartWithWindows { get; set; }
    public bool AutoBufferWithGame { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool FirstRunCompleted { get; set; }
    // A saved config keeps its language; only a new one starts in the Windows display language.
    public string Language { get; set; } = Text.WindowsDefault;
    public string Theme { get; set; } = "dark";
}
