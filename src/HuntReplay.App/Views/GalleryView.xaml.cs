using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using HuntReplay.App.ViewModels;
using HuntReplay.Core.Media;

namespace HuntReplay.App.Views;
public partial class GalleryView : UserControl
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private bool playing, dragging, updating;
    public GalleryView()
    {
        InitializeComponent();
        timer.Tick += (_, _) =>
        {
            if (dragging || !Player.NaturalDuration.HasTimeSpan) return;
            updating = true;
            Seek.Value = Player.Position.TotalSeconds;
            PositionText.Text = $"{Player.Position:mm\\:ss} / {Player.NaturalDuration.TimeSpan:mm\\:ss}";
            updating = false;
        };
    }
    private void ContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel old) old.ClosePlayer -= Close;
        if (e.NewValue is MainViewModel model) model.ClosePlayer += Close;
    }
    public void Close()
    {
        timer.Stop(); Player.Stop(); Player.Close(); Player.Source = null; playing = false;
        Seek.IsEnabled = false; PlayButton.IsEnabled = false; PlayButton.Content = "▶";
        updating = true; Seek.Value = 0; Seek.Maximum = 1; updating = false;
        PositionText.Text = "00:00 / 00:00";
        PlayerHint.Visibility = Visibility.Visible; PlayerHint.Text = "Выберите клип, чтобы посмотреть";
    }
    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Close();
        if (ClipList.SelectedItem is not ClipViewModel clip) return;
        Player.Source = new Uri(clip.Clip.FullPath);
        Player.Play(); playing = true; PlayButton.Content = "Ⅱ";
    }
    private void MediaOpened(object sender, RoutedEventArgs e)
    {
        PlayerHint.Visibility = Visibility.Collapsed;
        PlayButton.IsEnabled = true; Seek.IsEnabled = Player.NaturalDuration.HasTimeSpan;
        if (Player.NaturalDuration.HasTimeSpan) Seek.Maximum = Player.NaturalDuration.TimeSpan.TotalSeconds;
        timer.Start();
    }
    private void PlayPause(object sender, RoutedEventArgs e)
    {
        if (playing) Player.Pause(); else Player.Play();
        playing = !playing; PlayButton.Content = playing ? "Ⅱ" : "▶";
    }
    private void MediaEnded(object sender, RoutedEventArgs e)
    { Player.Pause(); playing = false; PlayButton.Content = "▶"; Player.Position = TimeSpan.Zero; }
    private void MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        // Keep the HRESULT (not a guess at its cause) and the saved file untouched; only the preview fails.
        var hresult = e.ErrorException?.HResult ?? 0;
        var fileName = (ClipList.SelectedItem as ClipViewModel)?.FileName ?? "клип";
        Close();
        PlayerHint.Text = PlaybackDiagnostics.Hint(hresult);
        PlayerHint.TextAlignment = TextAlignment.Center;
        PlayerHint.FontSize = 11;
        if (DataContext is MainViewModel model) model.PlaybackFailed(hresult, fileName);
    }
    private void SeekStarted(object sender, MouseButtonEventArgs e) => dragging = true;
    private void SeekEnded(object sender, MouseButtonEventArgs e) { dragging = false; Player.Position = TimeSpan.FromSeconds(Seek.Value); }
    private void SeekChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (!updating && !dragging && Seek.IsEnabled) Player.Position = TimeSpan.FromSeconds(Seek.Value); }
    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false && Player is not null) { Player.Pause(); playing = false; PlayButton.Content = "▶"; timer.Stop(); }
        else if (Player?.Source is not null) timer.Start();
    }
}
