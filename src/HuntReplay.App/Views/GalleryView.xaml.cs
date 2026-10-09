using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using HuntReplay.App.ViewModels;
using HuntReplay.Core.Media;
using Microsoft.Web.WebView2.Core;

namespace HuntReplay.App.Views;
public partial class GalleryView : UserControl
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private bool playing, dragging, updating, webViewReady;
    private double currentPosition, naturalDuration;
    private string? currentPath;

    public GalleryView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        timer.Tick += async (_, _) =>
        {
            if (dragging || !webViewReady || !playing) return;
            try
            {
                var json = await Player.ExecuteScriptAsync("video ? JSON.stringify({ pos: video.currentTime, dur: video.duration, paused: video.paused, ended: video.ended }) : null");
                if (string.IsNullOrEmpty(json) || json == "null") return;
                var state = JsonDocument.Parse(json).RootElement;
                if (state.TryGetProperty("pos", out var p) && state.TryGetProperty("dur", out var d))
                {
                    currentPosition = p.GetDouble();
                    var dur = d.GetDouble();
                    if (!double.IsNaN(dur) && dur > 0)
                    {
                        naturalDuration = dur;
                        Seek.Maximum = naturalDuration;
                        Seek.IsEnabled = true;
                    }
                    updating = true;
                    Seek.Value = currentPosition;
                    PositionText.Text = $"{TimeSpan.FromSeconds(currentPosition):mm\\:ss} / {TimeSpan.FromSeconds(naturalDuration):mm\\:ss}";
                    updating = false;
                }
                if (state.TryGetProperty("ended", out var ended) && ended.GetBoolean())
                {
                    playing = false;
                    PlayButton.Content = "▶";
                    timer.Stop();
                }
            }
            catch { }
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (webViewReady) return;
        try
        {
            var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HuntReplayStudio", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: appData);
            await Player.EnsureCoreWebView2Async(env);
            Player.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Player.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Player.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Player.CoreWebView2.Settings.IsZoomControlEnabled = false;
            Player.CoreWebView2.SetVirtualHostNameToFolderMapping("local.huntreplay", Path.GetPathRoot(Environment.SystemDirectory)!, CoreWebView2HostResourceAccessKind.Allow);
            Player.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            webViewReady = true;
            if (currentPath is not null) LoadVideo(currentPath);
        }
        catch (Exception ex)
        {
            PlayerHint.Text = "WebView2 недоступен: " + ex.Message;
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var doc = JsonDocument.Parse(e.WebMessageAsJson);
            if (doc.RootElement.TryGetProperty("type", out var type))
            {
                var t = type.GetString();
                if (t == "loadedmetadata")
                {
                    naturalDuration = doc.RootElement.GetProperty("duration").GetDouble();
                    PlayerHint.Visibility = Visibility.Collapsed;
                    PlayButton.IsEnabled = true;
                    Seek.IsEnabled = naturalDuration > 0;
                    if (naturalDuration > 0) Seek.Maximum = naturalDuration;
                    PositionText.Text = $"00:00 / {TimeSpan.FromSeconds(naturalDuration):mm\\:ss}";
                }
                else if (t == "click")
                {
                    PlayPause(this, new RoutedEventArgs());
                }
                else if (t == "error")
                {
                    var fileName = (ClipList.SelectedItem as ClipViewModel)?.FileName ?? "клип";
                    PlayerHint.Visibility = Visibility.Visible;
                    PlayerHint.Text = PlaybackDiagnostics.Hint(unchecked((int)0xC00D11B1));
                    PlayerHint.TextAlignment = TextAlignment.Center;
                    PlayerHint.FontSize = 11;
                    if (DataContext is MainViewModel model) model.PlaybackFailed(unchecked((int)0xC00D11B1), fileName);
                }
            }
        }
        catch { }
    }

    private void ContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel old) old.ClosePlayer -= Close;
        if (e.NewValue is MainViewModel model) model.ClosePlayer += Close;
    }

    public void Close()
    {
        timer.Stop();
        playing = false;
        currentPath = null;
        Seek.IsEnabled = false; PlayButton.IsEnabled = false; PlayButton.Content = "▶";
        updating = true; Seek.Value = 0; Seek.Maximum = 1; updating = false;
        PositionText.Text = "00:00 / 00:00";
        PlayerHint.Visibility = Visibility.Visible; PlayerHint.Text = "Выберите клип, чтобы посмотреть";
        if (webViewReady)
        {
            try { Player.NavigateToString("<html><body style='background:#080C0E;margin:0;overflow:hidden;'></body></html>"); } catch { }
        }
    }

    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Close();
        if (!IsVisible || ClipList.SelectedItem is not ClipViewModel clip) return;
        currentPath = clip.Clip.FullPath;
        if (webViewReady) LoadVideo(currentPath);
    }

    private void LoadVideo(string path)
    {
        var fileUri = new Uri(path).AbsoluteUri;
        var html = $$"""
        <!DOCTYPE html>
        <html>
        <head>
          <style>
            * { margin: 0; padding: 0; box-sizing: border-box; }
            body, html { width: 100%; height: 100%; background: #080C0E; overflow: hidden; display: flex; align-items: center; justify-content: center; }
            video { width: 100%; height: 100%; object-fit: contain; cursor: pointer; }
          </style>
        </head>
        <body>
          <video id="v" src="{{fileUri}}" playsinline></video>
          <script>
            const video = document.getElementById('v');
            video.addEventListener('loadedmetadata', () => {
              window.chrome.webview.postMessage({ type: 'loadedmetadata', duration: video.duration });
            });
            video.addEventListener('error', () => {
              window.chrome.webview.postMessage({ type: 'error' });
            });
            video.addEventListener('click', () => {
              window.chrome.webview.postMessage({ type: 'click' });
            });
          </script>
        </body>
        </html>
        """;
        Player.NavigateToString(html);
        PlayerHint.Visibility = Visibility.Collapsed;
        PlayButton.IsEnabled = true;
    }

    private void PlayPause(object sender, RoutedEventArgs e)
    {
        if (!webViewReady || currentPath is null) return;
        playing = !playing;
        PlayButton.Content = playing ? "Ⅱ" : "▶";
        if (playing)
        {
            Player.ExecuteScriptAsync("video.play()");
            timer.Start();
        }
        else
        {
            Player.ExecuteScriptAsync("video.pause()");
            timer.Stop();
        }
    }

    private void SeekStarted(object sender, MouseButtonEventArgs e) => dragging = true;

    private void SeekEnded(object sender, MouseButtonEventArgs e)
    {
        dragging = false;
        if (webViewReady)
        {
            currentPosition = Seek.Value;
            Player.ExecuteScriptAsync($"video.currentTime = {currentPosition:F2}");
        }
    }

    private void SeekChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!updating && !dragging && Seek.IsEnabled && webViewReady)
        {
            currentPosition = Seek.Value;
            Player.ExecuteScriptAsync($"video.currentTime = {currentPosition:F2}");
        }
    }

    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false) Close();
    }
}
