using System.Windows.Media;
using System.Windows.Media.Imaging;
using HuntReplay.Core.Storage;

namespace HuntReplay.App.ViewModels;

internal sealed class ClipViewModel
{
    public LibraryClip Clip { get; }
    public string FileName => Clip.Metadata.FileName;
    public string EventLabel => Clip.Metadata.EventTag.StartsWith("MULTI_KILL", StringComparison.Ordinal) ?
        "МУЛЬТИ-КИЛЛ " + Clip.Metadata.EventTag.Split('_').FirstOrDefault(s => s.StartsWith('x')) :
        Clip.Metadata.EventTag switch { "HUNTER_KILLED" => "ОХОТНИК УБИТ", "PLAYER_DOWNED" => "ОХОТНИК ПОВЕРЖЕН", "MANUAL" => "РУЧНОЙ ПОВТОР", _ => Clip.Metadata.EventTag.Replace('_', ' ') };
    public string Duration => TimeSpan.FromSeconds(Math.Max(0, Clip.Metadata.DurationSeconds)).ToString(@"mm\:ss");
    public string Size => $"{Clip.Metadata.Bytes / 1024d / 1024:F1} МБ";
    public string Date => Clip.Metadata.CreatedAt.ToLocalTime().ToString("dd MMM · HH:mm");
    public string Detail => $"{Date}   /   {Size}";
    public ImageSource? Thumbnail { get; }
    public ClipViewModel(LibraryClip clip)
    {
        Clip = clip;
        try
        {
            if (!File.Exists(clip.ThumbnailPath)) return;
            var image = new BitmapImage();
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 640; image.UriSource = new Uri(clip.ThumbnailPath); image.EndInit(); image.Freeze();
            Thumbnail = image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or System.Runtime.InteropServices.COMException) { }
    }
}
