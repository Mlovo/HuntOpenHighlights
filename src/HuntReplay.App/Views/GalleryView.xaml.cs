using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HuntReplay.App.Controls;
using HuntReplay.App.Interop;
using HuntReplay.App.Services;
using HuntReplay.App.ViewModels;
using HuntReplay.Core.Media;
using HuntReplay.Core.Storage;

namespace HuntReplay.App.Views;

public partial class GalleryView : UserControl
{
    // Frames are picked up once per WPF render pass, in step with the display, not on a 20 ms timer that beats against 60 FPS.
    private readonly RenderTicker timer = new();
    // Written on the UI thread, read by the background thumbnail preload.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, BitmapSource> previewCache = new();
    private readonly object playerLock = new();
    private CancellationTokenSource? preloadCts;
    private CancellationTokenSource? previewCts;
    private IntPtr playerHandle = IntPtr.Zero;
    private WriteableBitmap? bitmap;
    private bool playing, dragging, updating;
    private double naturalDuration;
    private string? currentPath;
    private int videoWidth, videoHeight;
    private double currentVolume = 1.0;
    private double lastNonZeroVolume = 1.0;

    // Trimming Mode State
    private bool isTrimMode;
    private bool isPreviewingFragment;
    private bool isExporting;
    private double trimStart;
    private double trimEnd;
    private const double MinTrimDuration = 0.5;
    private CancellationTokenSource? exportCts;

    // Async Seek Infrastructure for Smooth Scrubbing
    private readonly object asyncSeekLock = new();
    private double? pendingLiveSeekTime;
    private bool isAsyncSeeking;
    private bool isLiveSeeking;

    public GalleryView()
    {
        InitializeComponent();
        timer.Tick += OnTimerTick;
        ClipList.SizeChanged += OnClipListSizeChanged;
        Unloaded += (_, _) => Close();
        LocalizationService.Instance.PropertyChanged += (_, _) => UpdateLocalizedLabels();
        UpdateLocalizedLabels();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (playerHandle == IntPtr.Zero || bitmap is null) return;
        NativeMethods.hr_player_get_state(playerHandle, out int isPlaying, out int isEnded, out double currentPts);
        if (isEnded == 1)
        {
            ShowPaused();
            timer.Stop();
        }
        if (isPlaying == 1)
        {
            CopyLatestFrame(playerHandle);

            if (isTrimMode)
            {
                if (isPreviewingFragment && currentPts >= trimEnd)
                {
                    ShowPaused();
                    NativeMethods.hr_player_pause(playerHandle);
                    timer.Stop();
                    isPreviewingFragment = false;

                    NativeMethods.hr_player_seek(playerHandle, trimEnd);
                    RefreshCurrentFrame();
                    TrimSlider.Playhead = trimEnd;
                    PositionText.Text = $"{VideoTrimmingFormatters.FormatTime(trimEnd)} / {VideoTrimmingFormatters.FormatTime(naturalDuration)}";
                }
                else
                {
                    // While a fragment preview starts, the player may still report the position it is seeking away from;
                    // the indicator never leaves the selected fragment.
                    double shown = isPreviewingFragment ? Math.Clamp(currentPts, trimStart, trimEnd) : currentPts;
                    TrimSlider.Playhead = shown;
                    PositionText.Text = $"{VideoTrimmingFormatters.FormatTime(shown)} / {VideoTrimmingFormatters.FormatTime(naturalDuration)}";
                }
            }
            else if (!dragging)
            {
                updating = true;
                Seek.Value = currentPts;
                PositionText.Text = $"{TimeSpan.FromSeconds(currentPts):mm\\:ss} / {TimeSpan.FromSeconds(naturalDuration):mm\\:ss}";
                updating = false;
            }
        }
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
        previewCache.Clear();

        preloadCts?.Cancel();
        preloadCts?.Dispose();
        preloadCts = null;

        previewCts?.Cancel();
        previewCts?.Dispose();
        previewCts = null;

        PreviewCard.Visibility = Visibility.Collapsed;

        lock (asyncSeekLock)
        {
            pendingLiveSeekTime = null;
        }

        if (isTrimMode)
        {
            if (exportCts is not null)
            {
                exportCts.Cancel();
                exportCts.Dispose();
                exportCts = null;
            }
            isTrimMode = false;
            isPreviewingFragment = false;
            isExporting = false;
            TrimToolbar.Visibility = Visibility.Collapsed;
            TrimProgressPanel.Visibility = Visibility.Collapsed;
            TrimSlider.Visibility = Visibility.Collapsed;
            Seek.Visibility = Visibility.Visible;
        }

        TrimButton.IsEnabled = false;

        IntPtr handleToClose = Interlocked.Exchange(ref playerHandle, IntPtr.Zero);
        if (handleToClose != IntPtr.Zero)
        {
            // Preview/seek workers hold playerLock for a whole decode; the UI thread must not wait for them.
            // Pause is lock-free in the native player and silences audio before the deferred close.
            NativeMethods.hr_player_pause(handleToClose);
            Task.Run(() => { lock (playerLock) NativeMethods.hr_player_close(handleToClose); });
        }

        bitmap = null;
        PlayerImage.Source = null;
        Seek.IsEnabled = false;
        PlayButton.IsEnabled = false;
        PlayButton.Content = "▶";
        updating = true;
        Seek.Value = 0;
        Seek.Maximum = 1;
        updating = false;
        PositionText.Text = "00:00 / 00:00";
        PlayerHint.Visibility = Visibility.Visible;
        PlayerHintText.Text = LocalizationService.Instance["Gallery.SelectClipHint"];
    }

    private void OnClipItemClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item && item.DataContext is ClipViewModel clip)
        {
            if (playerHandle == IntPtr.Zero || currentPath != clip.Clip.FullPath)
            {
                Close();
                currentPath = clip.Clip.FullPath;
                LoadVideo(currentPath);
            }
        }
    }

    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Close();
        if (!IsVisible || ClipList.SelectedItem is not ClipViewModel clip) return;
        currentPath = clip.Clip.FullPath;
        LoadVideo(currentPath);
    }

    private void LoadVideo(string path)
    {
        try
        {
            int w = 0, h = 0;
            double dur = 0;
            IntPtr opened = IntPtr.Zero;
            int res = NativeMethods.hr_player_open(path, out opened, out w, out h, out dur);
            if (res < 0 || opened == IntPtr.Zero)
            {
                PlayerHint.Visibility = Visibility.Visible;
                PlayerHintText.Text = LocalizationService.Instance["Gallery.OpenFailed"];
                return;
            }

            lock (playerLock)
            {
                playerHandle = opened;
            }

            videoWidth = w;
            videoHeight = h;
            naturalDuration = dur;
            bitmap = new WriteableBitmap(videoWidth, videoHeight, 96, 96, PixelFormats.Bgra32, null);
            PlayerImage.Source = bitmap;

            CopyLatestFrame(opened);

            PlayerHint.Visibility = Visibility.Collapsed;
            PlayButton.IsEnabled = true;
            Seek.IsEnabled = naturalDuration > 0;
            if (naturalDuration > 0) Seek.Maximum = naturalDuration;
            PositionText.Text = $"00:00 / {TimeSpan.FromSeconds(naturalDuration):mm\\:ss}";
            NativeMethods.hr_player_set_volume(opened, (float)currentVolume);

            bool canTrim = naturalDuration >= MinTrimDuration;
            TrimButton.IsEnabled = canTrim;

            PreloadThumbnails(opened, naturalDuration);
        }
        catch (Exception ex)
        {
            PlayerHint.Visibility = Visibility.Visible;
            PlayerHintText.Text = Loc.Format("Gallery.PlayerError", ex.Message);
        }
    }

    private void PreloadThumbnails(IntPtr handle, double duration)
    {
        preloadCts?.Cancel();
        preloadCts?.Dispose();
        var cts = new CancellationTokenSource();
        preloadCts = cts;
        var token = cts.Token;

        Task.Run(() =>
        {
            double step = duration > 40 ? 3.0 : 2.0;
            for (double s = 0; s <= duration; s += step)
            {
                if (token.IsCancellationRequested) break;
                int secKey = (int)Math.Round(s);
                if (previewCache.ContainsKey(secKey)) continue;
                if (playerHandle != handle) break; // Another clip took over; RenderPreview re-checks under the lock.
                if (RenderPreview(handle, s, token) is not { } bmp) continue;
                Dispatcher.BeginInvoke(() =>
                {
                    if (!token.IsCancellationRequested && playerHandle == handle) previewCache[secKey] = bmp;
                });
            }
        }, token);
    }

    private void PlayPause(object sender, RoutedEventArgs e)
    {
        if (playerHandle == IntPtr.Zero) return;
        if (isTrimMode)
        {
            PreviewRangeClick(sender, e);
            return;
        }
        playing = !playing;
        PlayButton.Content = playing ? "Ⅱ" : "▶";
        if (playing)
        {
            NativeMethods.hr_player_play(playerHandle);
            timer.Start();
        }
        else
        {
            NativeMethods.hr_player_pause(playerHandle);
            timer.Stop();
        }
    }

    private void ScreenClicked(object sender, MouseButtonEventArgs e)
    {
        if (playerHandle != IntPtr.Zero)
        {
            PlayPause(this, new RoutedEventArgs());
        }
    }

    private void SeekMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!Seek.IsEnabled || naturalDuration <= 0) return;
        dragging = true;
        Seek.CaptureMouse();
        ApplySeekFromMouse(e);
    }

    private void SeekMouseMove(object sender, MouseEventArgs e)
    {
        if (dragging && Seek.IsEnabled && naturalDuration > 0 && e.LeftButton == MouseButtonState.Pressed)
        {
            ApplySeekFromMouse(e);
        }
    }

    private void SeekMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (dragging)
        {
            dragging = false;
            Seek.ReleaseMouseCapture();
            ApplySeekFromMouse(e);
            if (!Seek.IsMouseOver) PreviewCard.Visibility = Visibility.Collapsed;
        }
    }

    private void SeekHover(object sender, MouseEventArgs e)
    {
        if (playerHandle == IntPtr.Zero || naturalDuration <= 0 || !Seek.IsEnabled)
        {
            PreviewCard.Visibility = Visibility.Collapsed;
            return;
        }

        var pos = e.GetPosition(Seek);
        double ratio = Math.Clamp(pos.X / Math.Max(1, Seek.ActualWidth), 0.0, 1.0);
        double targetSec = ratio * naturalDuration;
        int secondKey = (int)Math.Round(targetSec);

        Point mouseInCanvas = e.GetPosition(PreviewCanvas);
        double cardWidth = PreviewCard.ActualWidth > 0 ? PreviewCard.ActualWidth : 176;
        double canvasWidth = PreviewCanvas.ActualWidth > 0 ? PreviewCanvas.ActualWidth : ActualWidth;
        double cardX = Math.Clamp(mouseInCanvas.X - (cardWidth / 2), 4, Math.Max(4, canvasWidth - cardWidth - 4));
        Canvas.SetLeft(PreviewCard, cardX);
        PreviewTime.Text = TimeSpan.FromSeconds(targetSec).ToString(@"mm\:ss");
        PreviewCard.Visibility = Visibility.Visible;

        if (previewCache.TryGetValue(secondKey, out var cachedBmp))
        {
            PreviewThumb.Source = cachedBmp;
            return;
        }

        int closestKey = -1;
        int minDiff = int.MaxValue;
        foreach (var key in previewCache.Keys)
        {
            int diff = Math.Abs(key - secondKey);
            if (diff < minDiff && diff <= 2)
            {
                minDiff = diff;
                closestKey = key;
            }
        }
        if (closestKey >= 0)
        {
            PreviewThumb.Source = previewCache[closestKey];
        }
        else
        {
            PreviewThumb.Source = null;
        }

        previewCts?.Cancel();
        previewCts?.Dispose();
        var cts = new CancellationTokenSource();
        previewCts = cts;
        var token = cts.Token;
        var handle = playerHandle;

        Task.Run(() =>
        {
            if (RenderPreview(handle, targetSec, token) is not { } bmp) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (token.IsCancellationRequested || handle != playerHandle) return;
                previewCache[secondKey] = bmp;
                if (PreviewCard.Visibility != Visibility.Visible) return;
                var curPos = Mouse.GetPosition(Seek);
                double curRatio = Math.Clamp(curPos.X / Math.Max(1, Seek.ActualWidth), 0.0, 1.0);
                if (Math.Abs((int)Math.Round(curRatio * naturalDuration) - secondKey) <= 1) PreviewThumb.Source = bmp;
            });
        }, token);
    }

    private void SeekLeave(object sender, MouseEventArgs e)
    {
        if (!dragging)
        {
            PreviewCard.Visibility = Visibility.Collapsed;
            previewCts?.Cancel();
            previewCts?.Dispose();
            previewCts = null;
        }
    }

    private void ApplySeekFromMouse(MouseEventArgs e)
    {
        if (playerHandle == IntPtr.Zero || naturalDuration <= 0) return;
        var pos = e.GetPosition(Seek);
        double ratio = Math.Clamp(pos.X / Math.Max(1, Seek.ActualWidth), 0.0, 1.0);
        double target = ratio * naturalDuration;
        updating = true;
        Seek.Value = target;
        PositionText.Text = $"{TimeSpan.FromSeconds(target):mm\\:ss} / {TimeSpan.FromSeconds(naturalDuration):mm\\:ss}";
        updating = false;
        ScheduleAsyncSeek(target);
    }

    private void SeekChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!updating && !dragging && Seek.IsEnabled && playerHandle != IntPtr.Zero)
        {
            ScheduleAsyncSeek(Seek.Value);
        }
    }

    private void RefreshCurrentFrame()
    {
        if (bitmap is null || playerHandle == IntPtr.Zero) return;
        CopyLatestFrame(playerHandle);

        if (!isTrimMode)
        {
            PositionText.Text = $"{TimeSpan.FromSeconds(Seek.Value):mm\\:ss} / {TimeSpan.FromSeconds(naturalDuration):mm\\:ss}";
        }
        else
        {
            PositionText.Text = $"{VideoTrimmingFormatters.FormatTime(TrimSlider.Playhead)} / {VideoTrimmingFormatters.FormatTime(naturalDuration)}";
        }
    }

    private void ScheduleAsyncSeek(double timestamp)
    {
        if (playerHandle == IntPtr.Zero || naturalDuration <= 0) return;

        lock (asyncSeekLock)
        {
            pendingLiveSeekTime = timestamp;
            if (isAsyncSeeking) return;
            isAsyncSeeking = true;
        }

        Task.Run(() =>
        {
            while (true)
            {
                double target;
                lock (asyncSeekLock)
                {
                    if (!pendingLiveSeekTime.HasValue)
                    {
                        isAsyncSeeking = false;
                        break;
                    }
                    target = pendingLiveSeekTime.Value;
                    pendingLiveSeekTime = null;
                }

                lock (playerLock)
                {
                    if (playerHandle != IntPtr.Zero)
                    {
                        NativeMethods.hr_player_seek(playerHandle, target);
                    }
                    else
                    {
                        break;
                    }
                }

                Dispatcher.BeginInvoke(new Action(RefreshCurrentFrame), DispatcherPriority.Render);
            }
        });
    }

    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false)
        {
            Close();
        }
        else if (e.NewValue is true)
        {
            if (playerHandle == IntPtr.Zero && ClipList?.SelectedItem is ClipViewModel clip)
            {
                currentPath = clip.Clip.FullPath;
                LoadVideo(currentPath);
            }
        }
    }

    private void VolumeButtonClick(object sender, RoutedEventArgs e)
    {
        if (VolumeSlider.Value > 0.001)
        {
            lastNonZeroVolume = VolumeSlider.Value;
            VolumeSlider.Value = 0;
        }
        else
        {
            VolumeSlider.Value = lastNonZeroVolume > 0.001 ? lastNonZeroVolume : 1.0;
        }
    }

    private void VolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        currentVolume = Math.Clamp(VolumeSlider.Value, 0.0, 1.0);
        if (playerHandle != IntPtr.Zero)
        {
            NativeMethods.hr_player_set_volume(playerHandle, (float)currentVolume);
        }

        int percent = (int)Math.Round(currentVolume * 100);
        if (VolumeSlider is not null)
        {
            VolumeSlider.ToolTip = Loc.Format("Gallery.VolumeTooltip", percent);
        }

        if (VolumeIcon is not null && VolumeButton is not null)
        {
            if (currentVolume <= 0.001)
            {
                VolumeIcon.Text = "\uE74F";
                VolumeButton.ToolTip = Loc["Gallery.Unmute"];
            }
            else if (currentVolume < 0.5)
            {
                VolumeIcon.Text = "\uE993";
                VolumeButton.ToolTip = Loc.Format("Gallery.MuteTooltip", percent);
            }
            else
            {
                VolumeIcon.Text = "\uE767";
                VolumeButton.ToolTip = Loc.Format("Gallery.MuteTooltip", percent);
            }
        }
    }

    private void VolumeMouseWheel(object sender, MouseWheelEventArgs e)
    {
        double step = e.Delta > 0 ? 0.05 : -0.05;
        VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + step, 0.0, 1.0);
        e.Handled = true;
    }

    // The clip ListBox's own ScrollViewer swallows the wheel even with scrolling disabled; hand it to the page.
    private void ClipListMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        PageScroll.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = sender });
    }

    private void OnClipListSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double availableWidth = e.NewSize.Width;
        if (availableWidth <= 0) return;

        // Visual balance: target min tile width ~240px with 14px horizontal spacing
        const double gap = 14.0;
        const double minTileWidth = 240.0;

        int cols = Math.Max(1, (int)Math.Floor((availableWidth + gap) / (minTileWidth + gap)));

        var uniformGrid = FindVisualChild<UniformGrid>(ClipList);
        if (uniformGrid is not null && uniformGrid.Columns != cols)
        {
            uniformGrid.Columns = cols;
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) return typed;
            var nested = FindVisualChild<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    #region Video Trimming Feature

    private void TrimButtonClick(object sender, RoutedEventArgs e)
    {
        if (playerHandle == IntPtr.Zero || naturalDuration < MinTrimDuration) return;
        EnterTrimMode();
    }

    public void EnterTrimMode()
    {
        if (playerHandle == IntPtr.Zero || naturalDuration < MinTrimDuration) return;
        if (isTrimMode) return;

        if (playing)
        {
            playing = false;
            PlayButton.Content = "▶";
            NativeMethods.hr_player_pause(playerHandle);
            timer.Stop();
        }

        isTrimMode = true;
        isPreviewingFragment = false;

        PreviewCard.Visibility = Visibility.Collapsed;
        previewCts?.Cancel();
        previewCts?.Dispose();
        previewCts = null;

        trimStart = 0.0;
        trimEnd = naturalDuration;

        TrimSlider.Minimum = 0.0;
        TrimSlider.Maximum = naturalDuration;
        TrimSlider.MinRange = MinTrimDuration;
        TrimSlider.RangeStart = trimStart;
        TrimSlider.RangeEnd = trimEnd;
        TrimSlider.Playhead = Seek.Value;

        Seek.Visibility = Visibility.Collapsed;
        TrimSlider.Visibility = Visibility.Visible;
        TrimToolbar.Visibility = Visibility.Visible;
        TrimProgressPanel.Visibility = Visibility.Collapsed;

        TrimButton.IsEnabled = false;
 
        UpdateTrimTimeDisplays();
        Focus();
    }

    private void CancelTrimClick(object sender, RoutedEventArgs e)
    {
        ExitTrimMode();
    }

    public void ExitTrimMode()
    {
        if (!isTrimMode) return;

        if (playing)
        {
            ShowPaused();
            NativeMethods.hr_player_pause(playerHandle);
            timer.Stop();
        }

        if (exportCts is not null)
        {
            exportCts.Cancel();
            exportCts.Dispose();
            exportCts = null;
        }

        isTrimMode = false;
        isPreviewingFragment = false;
        isExporting = false;

        TrimToolbar.Visibility = Visibility.Collapsed;
        TrimProgressPanel.Visibility = Visibility.Collapsed;
        TrimSlider.Visibility = Visibility.Collapsed;
        Seek.Visibility = Visibility.Visible;

        bool canTrim = playerHandle != IntPtr.Zero && naturalDuration >= MinTrimDuration;
        TrimButton.IsEnabled = canTrim;

        if (playerHandle != IntPtr.Zero && naturalDuration > 0)
        {
            NativeMethods.hr_player_get_state(playerHandle, out _, out _, out double currentPts);
            updating = true;
            Seek.Value = Math.Clamp(currentPts, 0.0, naturalDuration);
            PositionText.Text = $"{TimeSpan.FromSeconds(Seek.Value):mm\\:ss} / {TimeSpan.FromSeconds(naturalDuration):mm\\:ss}";
            updating = false;
        }
    }

    private void OnTrimSliderLiveSeek(object sender, RangeSeekEventArgs e)
    {
        if (playerHandle == IntPtr.Zero || naturalDuration <= 0) return;
        isLiveSeeking = true;
        try
        {
            TrimSlider.Playhead = e.Timestamp;
            PositionText.Text = $"{VideoTrimmingFormatters.FormatTime(e.Timestamp)} / {VideoTrimmingFormatters.FormatTime(naturalDuration)}";
            UpdateTrimTimeDisplays();
            ScheduleAsyncSeek(e.Timestamp);
        }
        finally
        {
            isLiveSeeking = false;
        }
    }

    private void OnTrimDragStarted(object sender, RoutedEventArgs e)
    {
        if (playing)
        {
            PauseTrimPlayback();
        }
    }

    private void OnTrimDragCompleted(object sender, RoutedEventArgs e)
    {
        if (playerHandle == IntPtr.Zero || naturalDuration <= 0) return;
        ScheduleAsyncSeek(TrimSlider.Playhead);
    }

    private void OnTrimSelectionStartChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!isTrimMode || playerHandle == IntPtr.Zero) return;
        trimStart = e.NewValue;
        UpdateTrimTimeDisplays();
    }

    private void OnTrimSelectionEndChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!isTrimMode || playerHandle == IntPtr.Zero) return;
        trimEnd = e.NewValue;
        UpdateTrimTimeDisplays();
    }

    private void OnTrimPlayheadChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!isTrimMode || playerHandle == IntPtr.Zero || playing || isLiveSeeking || TrimSlider.IsDragging) return;
        ScheduleAsyncSeek(e.NewValue);
        PositionText.Text = $"{VideoTrimmingFormatters.FormatTime(e.NewValue)} / {VideoTrimmingFormatters.FormatTime(naturalDuration)}";
    }

    private void UpdateTrimTimeDisplays()
    {
        if (TrimStartText is not null)
            TrimStartText.Text = VideoTrimmingFormatters.FormatTime(trimStart);
        if (TrimEndText is not null)
            TrimEndText.Text = VideoTrimmingFormatters.FormatTime(trimEnd);
        if (TrimDurationText is not null)
            TrimDurationText.Text = VideoTrimmingFormatters.FormatTime(Math.Max(0.0, trimEnd - trimStart));
    }

    private void StepStartMinusClick(object sender, RoutedEventArgs e)
    {
        if (!isTrimMode || playerHandle == IntPtr.Zero) return;
        SetTrimStart(VideoTrimmingFormatters.StepTime(trimStart, -0.1, 0.0, trimEnd - MinTrimDuration));
    }

    private void StepStartPlusClick(object sender, RoutedEventArgs e)
    {
        if (!isTrimMode || playerHandle == IntPtr.Zero) return;
        SetTrimStart(VideoTrimmingFormatters.StepTime(trimStart, +0.1, 0.0, trimEnd - MinTrimDuration));
    }

    private void StepEndMinusClick(object sender, RoutedEventArgs e)
    {
        if (!isTrimMode || playerHandle == IntPtr.Zero) return;
        SetTrimEnd(VideoTrimmingFormatters.StepTime(trimEnd, -0.1, trimStart + MinTrimDuration, naturalDuration));
    }

    private void StepEndPlusClick(object sender, RoutedEventArgs e)
    {
        if (!isTrimMode || playerHandle == IntPtr.Zero) return;
        SetTrimEnd(VideoTrimmingFormatters.StepTime(trimEnd, +0.1, trimStart + MinTrimDuration, naturalDuration));
    }

    private void SetTrimStart(double newStart)
    {
        trimStart = Math.Clamp(newStart, 0.0, trimEnd - MinTrimDuration);
        TrimSlider.RangeStart = trimStart;
        TrimSlider.Playhead = trimStart;
        UpdateTrimTimeDisplays();
        PositionText.Text = $"{VideoTrimmingFormatters.FormatTime(trimStart)} / {VideoTrimmingFormatters.FormatTime(naturalDuration)}";
        ScheduleAsyncSeek(trimStart);
    }

    private void SetTrimEnd(double newEnd)
    {
        trimEnd = Math.Clamp(newEnd, trimStart + MinTrimDuration, naturalDuration);
        TrimSlider.RangeEnd = trimEnd;
        TrimSlider.Playhead = trimEnd;
        UpdateTrimTimeDisplays();
        PositionText.Text = $"{VideoTrimmingFormatters.FormatTime(trimEnd)} / {VideoTrimmingFormatters.FormatTime(naturalDuration)}";
        ScheduleAsyncSeek(trimEnd);
    }

    private void JumpStartClick(object sender, RoutedEventArgs e)
    {
        if (!isTrimMode || playerHandle == IntPtr.Zero) return;
        if (playing) PauseTrimPlayback();
        TrimSlider.Playhead = trimStart;
        PositionText.Text = $"{VideoTrimmingFormatters.FormatTime(trimStart)} / {VideoTrimmingFormatters.FormatTime(naturalDuration)}";
        ScheduleAsyncSeek(trimStart);
    }

    private void JumpEndClick(object sender, RoutedEventArgs e)
    {
        if (!isTrimMode || playerHandle == IntPtr.Zero) return;
        if (playing) PauseTrimPlayback();
        TrimSlider.Playhead = trimEnd;
        PositionText.Text = $"{VideoTrimmingFormatters.FormatTime(trimEnd)} / {VideoTrimmingFormatters.FormatTime(naturalDuration)}";
        ScheduleAsyncSeek(trimEnd);
    }

    private void MarkInClick(object sender, RoutedEventArgs e)
    {
        if (!isTrimMode || playerHandle == IntPtr.Zero) return;
        NativeMethods.hr_player_get_state(playerHandle, out _, out _, out double currentPts);
        SetTrimStart(currentPts);
    }

    private void MarkOutClick(object sender, RoutedEventArgs e)
    {
        if (!isTrimMode || playerHandle == IntPtr.Zero) return;
        NativeMethods.hr_player_get_state(playerHandle, out _, out _, out double currentPts);
        SetTrimEnd(currentPts);
    }

    private void PauseTrimPlayback()
    {
        ShowPaused();
        NativeMethods.hr_player_pause(playerHandle);
        timer.Stop();
        isPreviewingFragment = false;
    }

    private void PreviewRangeClick(object sender, RoutedEventArgs e)
    {
        if (!isTrimMode || playerHandle == IntPtr.Zero) return;

        if (playing)
        {
            PauseTrimPlayback();
        }
        else
        {
            NativeMethods.hr_player_get_state(playerHandle, out _, out _, out double currentPts);
            if (currentPts < trimStart || currentPts >= trimEnd - 0.1)
            {
                NativeMethods.hr_player_seek(playerHandle, trimStart);
                RefreshCurrentFrame();
                TrimSlider.Playhead = trimStart;
            }

            isPreviewingFragment = true;
            playing = true;
            PlayButton.Content = "Ⅱ";
            if (PreviewRangeIcon is not null) PreviewRangeIcon.Text = "\uE769";
            if (PreviewRangeText is not null) PreviewRangeText.Text = Loc["Gallery.Pause"];
            NativeMethods.hr_player_play(playerHandle);
            timer.Start();
        }
    }

    private async void SaveFragmentClick(object sender, RoutedEventArgs e)
    {
        if (playerHandle == IntPtr.Zero || string.IsNullOrEmpty(currentPath) || !File.Exists(currentPath)) return;
        if (isExporting) return;

        double start = TrimSlider.RangeStart;
        double end = TrimSlider.RangeEnd;

        if (double.IsNaN(start) || double.IsNaN(end) || start < 0.0 || end > naturalDuration)
        {
            UpdateStatusMessage(Loc["Gallery.InvalidBoundaries"]);
            return;
        }

        if (end <= start || end - start < MinTrimDuration)
        {
            UpdateStatusMessage(Loc.Format("Gallery.FragmentTooShort", MinTrimDuration));
            return;
        }

        if (playing) PauseTrimPlayback();

        string defaultCandidate = VideoTrimmingService.GenerateDefaultTargetPath(currentPath);
        string initialDir = Path.GetDirectoryName(defaultCandidate) ?? "";
        string defaultName = Path.GetFileName(defaultCandidate);

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc["Gallery.SaveDialogTitle"],
            InitialDirectory = initialDir,
            FileName = defaultName,
            Filter = Loc["Gallery.Mp4Filter"],
            DefaultExt = ".mp4",
            AddExtension = true,
            OverwritePrompt = true
        };

        var window = Window.GetWindow(this);
        if (dialog.ShowDialog(window) != true) return;

        string targetPath = dialog.FileName;

        if (string.Equals(Path.GetFullPath(targetPath), Path.GetFullPath(currentPath), StringComparison.OrdinalIgnoreCase))
        {
            string errorMsg = Loc["Gallery.CannotOverwriteSource"];
            MessageBox.Show(window, errorMsg,
                Loc["Gallery.ExportErrorTitle"],
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        isExporting = true;
        exportCts?.Dispose();
        exportCts = new CancellationTokenSource();
        var token = exportCts.Token;

        SetTrimControlsEnabled(false);
        TrimProgressPanel.Visibility = Visibility.Visible;
        TrimProgressBar.Value = 0;
        TrimProgressPercent.Text = "0%";
        CancelExportButton.IsEnabled = true;
        CancelExportButton.Content = Loc["Common.Cancel"];
        TrimProgressStatus.Text = Loc.Format("Gallery.SavingProgress", 0);

        var progress = new Progress<double>(p =>
        {
            if (!isExporting) return;
            int percentage = (int)Math.Clamp(Math.Round(p * 100.0), 0, 100);
            TrimProgressBar.Value = percentage;
            TrimProgressPercent.Text = $"{percentage}%";
            TrimProgressStatus.Text = Loc.Format("Gallery.SavingProgress", percentage);
        });

        try
        {
            string exportedFile = await VideoTrimmingService.Instance.TrimVideoAsync(
                currentPath, targetPath, start, end, accurate: false, progress: progress, cancellationToken: token);

            if (!File.Exists(exportedFile))
            {
                throw new IOException(Loc["Gallery.FragmentMissing"]);
            }

            await HandleExportSuccess(exportedFile, start, end);
        }
        catch (OperationCanceledException)
        {
            UpdateStatusMessage(Loc["Gallery.ExportCancelled"]);
        }
        catch (Exception ex)
        {
            // The service never leaves a partial file at targetPath, and a file already there is the user's.
            UpdateStatusMessage(Loc.Format("Gallery.ExportError", ex.Message));
        }
        finally
        {
            isExporting = false;
            TrimProgressPanel.Visibility = Visibility.Collapsed;
            SetTrimControlsEnabled(true);
            exportCts?.Dispose();
            exportCts = null;
        }
    }

    private void CancelExportClick(object sender, RoutedEventArgs e)
    {
        if (exportCts is not null && !exportCts.IsCancellationRequested)
        {
            CancelExportButton.IsEnabled = false;
            CancelExportButton.Content = Loc["Common.Cancelling"];
            exportCts.Cancel();
        }
    }

    private async Task HandleExportSuccess(string exportedFile, double start, double end)
    {
        var mainVm = DataContext as MainViewModel;
        string? libraryRoot = mainVm?.LibraryRoot;
        string clipsDir = Path.GetDirectoryName(currentPath!) ?? "";

        bool isInsideClipsDir = libraryRoot is not null && !string.IsNullOrEmpty(clipsDir) &&
            string.Equals(Path.GetFullPath(Path.GetDirectoryName(exportedFile)!), Path.GetFullPath(clipsDir), StringComparison.OrdinalIgnoreCase);

        if (isInsideClipsDir)
        {
            try
            {
                var library = new ClipLibrary(clipsDir, libraryRoot!);
                var metadata = new ClipMetadata
                {
                    FileName = Path.GetFileName(exportedFile),
                    EventTag = "TRIMMED",
                    Events = ["TRIMMED"],
                    GroupId = "",
                    CreatedAt = DateTimeOffset.Now,
                    DurationSeconds = Math.Max(0.1, end - start),
                    Encoder = "stream copy",
                    TruncatedStart = false,
                    TruncatedEnd = false
                };
                var registeredClip = library.Register(exportedFile, metadata);
                try
                {
                    NativeCaptureEngine.SaveThumbnail(exportedFile, registeredClip.ThumbnailPath);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
                { /* A missing preview image never fails the export; the gallery shows a placeholder. */ }

                if (mainVm is not null)
                {
                    await mainVm.RefreshClipsAsync();
                    mainVm.SelectedClip = mainVm.Clips.FirstOrDefault(c => c.Clip.Metadata.Id == registeredClip.Metadata.Id);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // The MP4 is on disk; say why it is missing from the gallery instead of reporting a clean success.
                UpdateStatusMessage(Loc.Format("Gallery.SavedNotRegistered", ex.Message));
                return;
            }
        }

        try
        {
            System.Media.SystemSounds.Asterisk.Play();
        }
        catch { }

        string saveNotice = Loc.Format("Gallery.FragmentSaved", Path.GetFileName(exportedFile));
        UpdateStatusMessage(saveNotice);

        ExitTrimMode();
    }

    private void SetTrimControlsEnabled(bool enabled)
    {
        TrimSlider.IsEnabled = enabled;
        SaveFragmentButton.IsEnabled = enabled;
        CancelTrimButton.IsEnabled = enabled;
        PreviewRangeButton.IsEnabled = enabled;
        PlayButton.IsEnabled = enabled;
        StepStartMinusButton.IsEnabled = enabled;
        StepStartPlusButton.IsEnabled = enabled;
        StepEndMinusButton.IsEnabled = enabled;
        StepEndPlusButton.IsEnabled = enabled;
        MarkInButton.IsEnabled = enabled;
        MarkOutButton.IsEnabled = enabled;
        JumpStartButton.IsEnabled = enabled;
        JumpEndButton.IsEnabled = enabled;
    }

    private void UpdateStatusMessage(string message)
    {
        if (DataContext is MainViewModel mainVm)
        {
            try
            {
                var prop = typeof(MainViewModel).GetProperty("Message",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                prop?.SetValue(mainVm, message);
            }
            catch
            {
            }
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (!isTrimMode) return;
        if (SearchBox.IsKeyboardFocused) return;

        switch (e.Key)
        {
            case Key.I:
                MarkInClick(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.O:
                MarkOutClick(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.Space:
                PreviewRangeClick(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.Escape:
                ExitTrimMode();
                e.Handled = true;
                break;
            case Key.Left:
                if (Keyboard.Modifiers == ModifierKeys.Shift)
                    SetTrimStart(trimStart - 1.0);
                else
                    SetTrimStart(trimStart - 0.1);
                e.Handled = true;
                break;
            case Key.Right:
                if (Keyboard.Modifiers == ModifierKeys.Shift)
                    SetTrimStart(trimStart + 1.0);
                else
                    SetTrimStart(trimStart + 0.1);
                e.Handled = true;
                break;
            case Key.Home:
                JumpStartClick(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.End:
                JumpEndClick(this, new RoutedEventArgs());
                e.Handled = true;
                break;
        }
    }

    /// <summary>Start/Stop/Tick like a DispatcherTimer, but raised by CompositionTarget.Rendering.</summary>
    private sealed class RenderTicker
    {
        private bool running;
        public event EventHandler? Tick;
        public void Start()
        {
            if (running) return;
            running = true;
            CompositionTarget.Rendering += OnRendering;
        }
        public void Stop()
        {
            if (!running) return;
            running = false;
            CompositionTarget.Rendering -= OnRendering;
        }
        private void OnRendering(object? sender, EventArgs e) => Tick?.Invoke(this, e);
    }

    private void ShowPaused()
    {
        playing = false;
        PlayButton.Content = "▶";
        if (PreviewRangeIcon is not null) PreviewRangeIcon.Text = "\uE768";
        if (PreviewRangeText is not null) PreviewRangeText.Text = Loc["Gallery.Preview"];
    }

    private void CopyLatestFrame(IntPtr handle)
    {
        if (bitmap is null) return;
        bitmap.Lock();
        try
        {
            NativeMethods.hr_player_read_frame(handle, bitmap.BackBuffer, bitmap.BackBufferStride, videoWidth, videoHeight, out int updated, out _);
            if (updated == 1) bitmap.AddDirtyRect(new Int32Rect(0, 0, videoWidth, videoHeight));
        }
        finally { bitmap.Unlock(); }
    }

    // Decodes one preview frame; null when the player changed or the decode failed. One copy: native buffer to bitmap.
    private BitmapSource? RenderPreview(IntPtr handle, double seconds, CancellationToken token)
    {
        IntPtr pixels;
        int w, h, stride, res;
        lock (playerLock)
        {
            if (token.IsCancellationRequested || handle != playerHandle || handle == IntPtr.Zero) return null;
            res = NativeMethods.hr_player_preview(handle, seconds, 192, out pixels, out w, out h, out stride);
        }
        if (res != 0 || pixels == IntPtr.Zero) return null;
        try
        {
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, checked(stride * h), stride);
            bmp.Freeze();
            return bmp;
        }
        finally { NativeMethods.hr_free(pixels); }
    }

    private static LocalizationService Loc => LocalizationService.Instance;

    // Static labels are XAML bindings to Loc[...]; only text that the code also changes at runtime is set here.
    private void UpdateLocalizedLabels()
    {
        if (CancelExportButton is not null && CancelExportButton.IsEnabled) CancelExportButton.Content = Loc["Common.Cancel"];
        if (PreviewRangeText is not null) PreviewRangeText.Text = Loc[isPreviewingFragment ? "Gallery.Pause" : "Gallery.Preview"];
        if (PlayerHintText is not null && (playerHandle == IntPtr.Zero || PlayerHint.Visibility == Visibility.Visible))
        {
            PlayerHintText.Text = LocalizationService.Instance["Gallery.SelectClipHint"];
        }
    }

    #endregion
}

