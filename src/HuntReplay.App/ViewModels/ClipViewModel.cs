using System.Windows.Media;
using System.Windows.Media.Imaging;
using HuntReplay.App.Services;
using HuntReplay.Core.Storage;

namespace HuntReplay.App.ViewModels;

internal sealed class ClipViewModel
{
    public LibraryClip Clip { get; }
    public string FileName => Clip.Metadata.FileName;
    public string EventLabel
    {
        get
        {
            var loc = LocalizationService.Instance;
            if (Clip.Metadata.EventTag.StartsWith("MULTI_KILL", StringComparison.Ordinal))
            {
                var suffix = Clip.Metadata.EventTag.Split('_').FirstOrDefault(s => s.StartsWith('x'));
                return loc.Format("Event.MultiKill", suffix);
            }
            return Clip.Metadata.EventTag switch
            {
                "HUNTER_KILLED" => loc["Event.HunterKilled"],
                "PLAYER_DOWNED" => loc["Event.PlayerDowned"],
                "MANUAL" => loc["Event.Manual"],
                _ => Clip.Metadata.EventTag.Replace('_', ' ')
            };
        }
    }
    public string Duration => TimeSpan.FromSeconds(Math.Max(0, Clip.Metadata.DurationSeconds)).ToString(@"mm\:ss");
    public string Size => LocalizationService.Instance.Format("Unit.MegabytesValue", Clip.Metadata.Bytes / 1024d / 1024);
    public string Date => Clip.Metadata.CreatedAt.ToLocalTime().ToString("dd MMM · HH:mm", HuntReplay.Core.Localization.Text.Culture);
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
