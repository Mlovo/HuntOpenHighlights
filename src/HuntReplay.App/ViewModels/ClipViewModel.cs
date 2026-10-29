using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HuntReplay.App.Services;
using HuntReplay.Core.Storage;

namespace HuntReplay.App.ViewModels;

internal sealed class ClipViewModel : ObservableObject
{
    private bool isPreviewing;
    // Set by MainViewModel.PreviewClip so the card can mark the clip shown in the player.
    public bool IsPreviewing { get => isPreviewing; set => SetProperty(ref isPreviewing, value); }
    private LibraryClip clip;
    // Replaced in place when only the record changes (a star), so the list keeps its selection and scroll position.
    public LibraryClip Clip { get => clip; set { if (SetProperty(ref clip, value)) OnPropertyChanged(nameof(IsFavorite)); } }
    public bool IsFavorite => Clip.Metadata.Favorite;
    // Picks the badge colour: kills green, downs red, multi-kills blue, manual saves neutral.
    public string EventKind => Clip.Metadata.EventTag switch
    {
        var t when t.StartsWith("MULTI_KILL", StringComparison.Ordinal) => "Multi",
        "HUNTER_KILLED" => "Kill",
        "PLAYER_DOWNED" => "Down",
        _ => "Other"
    };
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
    public string When => Clip.Metadata.CreatedAt.ToLocalTime().ToString("dd MMM yyyy  •  HH:mm", HuntReplay.Core.Localization.Text.Culture);
    // Search text: what the player shows under the clip.
    public string Meta => $"{When}  •  {Duration}  •  {Size}";
    // Generated names carry the {time} stamp (HH-mm-ss-fff) and read better as their date; a renamed clip keeps its own name.
    private static readonly Regex GeneratedName = new(@"\d{2}-\d{2}-\d{2}-\d{3}", RegexOptions.Compiled);
    private bool HasGeneratedName => GeneratedName.IsMatch(FileName);
    public string Title => HasGeneratedName ? Date : Path.GetFileNameWithoutExtension(FileName);
    // Only a name the user gave the clip is worth a line on the list card; generated names repeat the date.
    public string? CustomName => HasGeneratedName ? null : Path.GetFileNameWithoutExtension(FileName);
    public ImageSource? Thumbnail { get; }
    // A thumbnail file is written once per clip Id, so one decoded by an earlier library refresh is reused as is.
    public ClipViewModel(LibraryClip clip, ImageSource? decoded = null)
    {
        this.clip = clip;
        Thumbnail = decoded;
        if (decoded is not null) return;
        try
        {
            if (!File.Exists(clip.ThumbnailPath)) return;
            var image = new BitmapImage();
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
            // Gallery tiles are 240-500 px wide; 640 px decodes kept ~0.9 MB per clip in memory.
            image.DecodePixelWidth = 400; image.UriSource = new Uri(clip.ThumbnailPath); image.EndInit(); image.Freeze();
            Thumbnail = image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or System.Runtime.InteropServices.COMException) { }
    }
}
