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
    private string? currentPath;    private int videoWidth, videoHeight;
    private double currentVolume = 1.0;
    private double lastNonZeroVolume = 1.0;

    // Trimming Mode State
    private bool isTrimMode;
    private bool isPreviewingFragment;
    private bool isExporting;
    private double trimStart;
    private double trimEnd;
    private const double MinTrimDuration = 0.5;
    private const double MinPlayerWidth = 560; // The trim toolbar wraps its end controls below this.
    private const double SplitterWidth = 16;
    private const double LibraryPanelWidth = 420; // A list card with its badge, metadata, star and menu on one line each.
    private const double SideBySideMinWidth = MinPlayerWidth + SplitterWidth + LibraryPanelWidth;
    private const double MinTileWidth = 180;
    private bool? sideBySide;
    // Side by side the split follows the window (see FitSideColumns) until the splitter is dragged; a dragged split is kept across stacked spells.
    private bool userSplit;
    private GridLength playerShare = new(1, GridUnitType.Star), libraryShare = new(LibraryPanelWidth);
    private bool gridView;
    private bool openedLatest; // The newest clip is put in the player on the first visit only.
    private Window? fullscreen;
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
        SizeChanged += (_, e) =>
        {
            ApplyLayout(e.NewSize.Width >= SideBySideMinWidth);
            // Neither the splitter nor a narrower window may squeeze the player below what the trim toolbar needs.
            LibraryColumn.MaxWidth = Math.Max(LibraryPanelWidth, e.NewSize.Width - MinPlayerWidth - SplitterWidth);
            SelectionHints.Visibility = e.NewSize.Width >= 1120 ? Visibility.Visible : Visibility.Collapsed;
        };
        BodyGrid.SizeChanged += (_, _) => FitPlayerScreen();
        PlayerCard.SizeChanged += (_, _) => FitPlayerScreen();
        Unloaded += (_, _) => { Close(); fullscreen?.Close(); };
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
        if (e.OldValue is MainViewModel old) { old.ClosePlayer -= Close; old.TrimRequested -= TrimClip; old.PropertyChanged -= ModelPropertyChanged; }
        if (e.NewValue is MainViewModel model) { model.ClosePlayer += Close; model.TrimRequested += TrimClip; model.PropertyChanged += ModelPropertyChanged; }
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
        BackButton.IsEnabled = ForwardButton.IsEnabled = FullscreenButton.IsEnabled = false;
        VideoFormatRun.Text = "";
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
        if (sender is not ListBoxItem item || item.DataContext is not ClipViewModel clip) return;
        // WPF's Shift-click replaces the selection with anchor..clip; here it fills every gap between the
        // outermost selected clips and the clicked one, so 1 and 4 selected plus Shift-click on 6 gives 1-6.
        if (Keyboard.Modifiers == ModifierKeys.Shift && ClipList.SelectedItems.Count > 0)
        {
            var indices = ClipList.SelectedItems.Cast<object>().Select(ClipList.Items.IndexOf).Append(ClipList.Items.IndexOf(clip)).ToArray();
            for (var i = indices.Min(); i <= indices.Max(); i++)
                if (!ClipList.SelectedItems.Contains(ClipList.Items[i])) ClipList.SelectedItems.Add(ClipList.Items[i]);
            item.Focus();
            e.Handled = true;
            return;
        }
        // Ctrl/Shift clicks build a selection for bulk actions; SelectionChanged decides what the player shows.
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0) return;
        // The card checkbox does the same as a Ctrl-click; the star and menu buttons handle their own clicks.
        for (var d = e.OriginalSource as DependencyObject; d is Visual && d != item; d = VisualTreeHelper.GetParent(d))
            if (d is ButtonBase) return;
        if (DataContext is not MainViewModel model) return;
        model.PreviewClip = clip;
        ShowPreview(); // Also reopens a clip whose player was closed, when PreviewClip did not change.
        // A click only opens the clip; the selection for bulk actions changes through the box, Ctrl and Shift.
        item.Focus();
        e.Handled = true;
    }

    // A right-click on a clip outside the selection leaves the selection alone; the menu then acts on that clip.
    private void OnClipItemRightClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { IsSelected: false } item) { item.Focus(); e.Handled = true; }
    }

    private void ClipListContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var item = (e.OriginalSource as DependencyObject) is { } source ? ItemsControl.ContainerFromElement(ClipList, source) as ListBoxItem : null;
        item ??= Keyboard.FocusedElement as ListBoxItem; // Shift+F10 and the menu key.
        ClipMenu.Tag = item is { IsSelected: false, DataContext: ClipViewModel clip } ? clip : null;
    }

    private void ModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.PreviewClip)) ShowPreview();
        else if (e.PropertyName == nameof(MainViewModel.HasClips)) OpenLatestOnce(); // Clips can arrive after the page is shown.
    }

    // Opens MainViewModel.PreviewClip in the player unless it is already there.
    private void ShowPreview()
    {
        var clip = (DataContext as MainViewModel)?.PreviewClip;
        if (clip is not null && playerHandle != IntPtr.Zero && currentPath == clip.Clip.FullPath) return;
        Close();
        if (!IsVisible || clip is null) return;
        currentPath = clip.Clip.FullPath;
        LoadVideo(currentPath);
    }

    // The first visit opens the newest clip (paused) instead of an empty player.
    private void OpenLatestOnce()
    {
        if (openedLatest || !IsVisible || DataContext is not MainViewModel model || !model.HasClips) return;
        openedLatest = true;
        model.PreviewClip ??= ClipList.Items.OfType<ClipViewModel>().FirstOrDefault() ?? model.Clips[0];
    }

    private void ClearSelectionClick(object sender, RoutedEventArgs e) => ClipList.UnselectAll();

    private void CardMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not ClipViewModel clip) return;
        // Like a right-click: on a clip in the selection the menu acts on all of it, on any other clip on that clip alone.
        var menu = ClipMenu;
        menu.Tag = ClipList.SelectedItems.Contains(clip) ? null : clip;
        menu.DataContext = DataContext; // Opened from the card, it would otherwise inherit the clip as its DataContext.
        // Placement stays at the mouse point, the default a later right-click relies on.
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    private void TrimClip(ClipViewModel clip)
    {
        if (DataContext is MainViewModel model) model.PreviewClip = clip;
        ShowPreview();
        EnterTrimMode();
        PlayerImage.BringIntoView();
    }

    // Esc clears the selection; Enter previews the focused card, or plays and pauses it once it is in the player.
    // Delete, F2 and Shift+F10 come from the list's bindings and menu.
    private void ClipListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ClipList.SelectedItems.Count > 0) { ClipList.UnselectAll(); e.Handled = true; return; }
        if (DataContext is not MainViewModel model) return;
        // Plain arrows open the next clip without touching the selection; Shift+arrows still extend it.
        if (Keyboard.Modifiers == ModifierKeys.None && e.Key is Key.Up or Key.Down or Key.Left or Key.Right)
        {
            var columns = gridView ? Math.Max(1, FindVisualChild<UniformGrid>(ClipList)?.Columns ?? 1) : 1;
            var step = e.Key switch { Key.Up => -columns, Key.Down => columns, Key.Left => gridView ? -1 : 0, _ => gridView ? 1 : 0 };
            var from = (Keyboard.FocusedElement as ListBoxItem)?.DataContext ?? model.PreviewClip;
            var index = from is null ? 0 : ClipList.Items.IndexOf(from) + step;
            if (step != 0 && index >= 0 && index < ClipList.Items.Count && ClipList.Items[index] is ClipViewModel next)
            {
                ClipList.ScrollIntoView(next);
                model.PreviewClip = next;
                (ClipList.ItemContainerGenerator.ContainerFromItem(next) as ListBoxItem)?.Focus();
            }
            e.Handled = step != 0;
            return;
        }
        if (e.Key != Key.Enter) return;
        if ((Keyboard.FocusedElement as ListBoxItem)?.DataContext is ClipViewModel focused && focused != model.PreviewClip)
        {
            model.PreviewClip = focused;
            ShowPreview();
        }
        else PlayPause(ClipList, e);
        e.Handled = true;
    }

    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel model) return;
        // The selection is for bulk actions only; the player keeps whatever it shows.
        model.SelectedClips = ClipList.SelectedItems.Cast<ClipViewModel>().ToArray();
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
            BackButton.IsEnabled = ForwardButton.IsEnabled = FullscreenButton.IsEnabled = true;
            VideoFormatRun.Text = $"  •  {w}×{h}  •  {Path.GetExtension(path).TrimStart('.').ToUpperInvariant()}";
            FitPlayerScreen();
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
            fullscreen?.Close();
            Close();
        }
        else if (e.NewValue is true)
        {
            OpenLatestOnce();
            if (playerHandle == IntPtr.Zero) ShowPreview();
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
    // Side by side, the list scrolls itself.
    private void ClipListMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sideBySide == true) return;
        e.Handled = true;
        PageScroll.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = sender });
    }

    // Wide enough for the player beside a library column: the clips scroll on their own so the player stays in view.
    // Narrower, the page stacks the player above the clips and scrolls as a whole.
    private void ApplyLayout(bool side)
    {
        if (sideBySide == side) return;
        if (!side && sideBySide == true) (playerShare, libraryShare) = (PlayerColumn.Width, LibraryColumn.Width);
        sideBySide = side;
        LibraryColumn.MinWidth = side ? LibraryPanelWidth : 0;
        PlayerColumn.MinWidth = side ? MinPlayerWidth : 0;
        PlayerColumn.Width = side ? playerShare : new GridLength(1, GridUnitType.Star);
        LibraryColumn.Width = side ? libraryShare : new GridLength(0);
        SplitterColumn.Width = new GridLength(side ? SplitterWidth : 0);
        LibrarySplitter.Visibility = side ? Visibility.Visible : Visibility.Collapsed;
        BodyTopRow.Height = side ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        Grid.SetColumnSpan(PlayerCard, side ? 1 : 3);
        Grid.SetRow(LibraryPanel, side ? 0 : 1);
        Grid.SetColumn(LibraryPanel, side ? 2 : 0);
        Grid.SetColumnSpan(LibraryPanel, side ? 1 : 3);
        PlayerCard.Margin = side ? new Thickness(0) : new Thickness(0, 0, 0, 20);
        // Side by side the card runs the full column height and the screen takes what the bars leave; stacked it is sized by width.
        PlayerCard.VerticalAlignment = side ? VerticalAlignment.Stretch : VerticalAlignment.Top;
        PlayerScreenRow.Height = side ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        if (side) PlayerScreen.Height = double.NaN;
        // The narrower player has no room for both trim boundaries on one line, so the end controls drop below the start ones, still on the right like the end handle.
        Grid.SetRow(TrimEndControls, side ? 1 : 0);
        Grid.SetColumn(TrimEndControls, side ? 0 : 2);
        Grid.SetColumnSpan(TrimEndControls, side ? 3 : 1);
        TrimEndControls.HorizontalAlignment = side ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        TrimEndControls.Margin = side ? new Thickness(0, 8, 0, 0) : new Thickness(0);
        PageScroll.VerticalScrollBarVisibility = side ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        BodyGrid.Margin = new Thickness(0, 0, side ? 0 : 8, 0); // The gutter only makes room for the page scrollbar.
        ScrollViewer.SetVerticalScrollBarVisibility(ClipList, side ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);
        // Stacked, the list has no viewport of its own; a virtualizing panel would then measure to nothing.
        ScrollViewer.SetCanContentScroll(ClipList, side);
        FitPlayerScreen();
    }

    private double VideoAspect => videoWidth > 0 && videoHeight > 0 ? (double)videoHeight / videoWidth : 9.0 / 16;

    // Stacked, the screen takes the video's shape at the page width. Side by side, FitSideColumns sizes it.
    private void FitPlayerScreen()
    {
        if (sideBySide == true) { FitSideColumns(); return; }
        if (sideBySide is null || PlayerScreen.ActualWidth <= 0) return;
        double height = Math.Max(160, Math.Floor(Math.Min(PlayerScreen.ActualWidth * VideoAspect, 540)));
        if (double.IsNaN(PlayerScreen.Height) || Math.Abs(PlayerScreen.Height - height) >= 1) PlayerScreen.Height = height;
    }

    // Gives the player the width at which the video fills the column height, so no empty band is left under it.
    // The clip list keeps the rest, never less than its minimum or more than 45 % of the row; past those limits the video letterboxes.
    private void FitSideColumns()
    {
        if (userSplit || BodyGrid.ActualWidth <= 0 || PlayerScreen.ActualHeight <= 0) return;
        double total = BodyGrid.ActualWidth - SplitterWidth;
        double screenHeight = BodyGrid.ActualHeight - (PlayerCard.ActualHeight - PlayerScreen.ActualHeight);
        double player = Math.Max(screenHeight / VideoAspect, total * 0.55);
        double library = Math.Floor(Math.Max(LibraryPanelWidth, total - Math.Max(player, MinPlayerWidth)));
        if (LibraryColumn.Width.IsAbsolute && Math.Abs(LibraryColumn.Width.Value - library) < 1 && PlayerColumn.Width.IsStar) return;
        PlayerColumn.Width = new GridLength(1, GridUnitType.Star);
        LibraryColumn.Width = new GridLength(library);
    }

    private void SplitterDragCompleted(object sender, DragCompletedEventArgs e) => userSplit = true;

    private void ViewModeChecked(object sender, RoutedEventArgs e)
    {
        if (ClipList is null) return; // Raised while the view is still being built.
        gridView = GridViewToggle.IsChecked == true;
        ClipList.ItemTemplate = (DataTemplate)Resources[gridView ? "GridTile" : "ListCard"];
        ClipList.ItemsPanel = (ItemsPanelTemplate)Resources[gridView ? "GridPanel" : "ListPanel"];
        // Tiles sit apart on the grid; list cards stack with a hairline gap.
        ClipList.ItemContainerStyle = new Style(typeof(ListBoxItem), ClipList.ItemContainerStyle.BasedOn ?? ClipList.ItemContainerStyle);
        if (gridView) ClipList.ItemContainerStyle.Setters.Add(new Setter(PaddingProperty, new Thickness(0)));
        if (gridView) ClipList.ItemContainerStyle.Setters.Add(new Setter(MarginProperty, new Thickness(3)));
        Dispatcher.BeginInvoke(UpdateTileColumns, DispatcherPriority.Loaded);
    }

    private void PlayerMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        PlayerMenu.DataContext = DataContext; // The button sits in the clip's details, so it would otherwise get the clip.
        PlayerMenu.PlacementTarget = button;
        PlayerMenu.Placement = PlacementMode.Bottom;
        PlayerMenu.IsOpen = true;
    }

    private void PlaySelectedClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model || model.SelectedClips.Count == 0) return;
        model.PreviewClip = model.SelectedClips[0];
        ShowPreview();
        if (!playing && playerHandle != IntPtr.Zero) PlayPause(this, e);
    }

    // Explorer opens on the folder with the first selected clip highlighted.
    private void RevealSelectedClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel model && model.SelectedClips.Count > 0) model.RevealCommand.Execute(model.SelectedClips[0]);
    }

    private void BackClick(object sender, RoutedEventArgs e) => SkipBy(-10);
    private void ForwardClick(object sender, RoutedEventArgs e) => SkipBy(10);

    private void SkipBy(double seconds)
    {
        if (playerHandle == IntPtr.Zero || naturalDuration <= 0 || isTrimMode) return;
        double target = Math.Clamp(Seek.Value + seconds, 0, naturalDuration);
        Seek.Value = target; // SeekChanged schedules the seek.
        PositionText.Text = $"{TimeSpan.FromSeconds(target):mm\\:ss} / {TimeSpan.FromSeconds(naturalDuration):mm\\:ss}";
    }

    private void FullscreenClick(object sender, RoutedEventArgs e) => ToggleFullscreen();

    // A borderless window over the whole monitor showing the same live frame; Esc, F or a double-click leave it.
    private void ToggleFullscreen()
    {
        if (fullscreen is not null) { fullscreen.Close(); return; }
        if (playerHandle == IntPtr.Zero) return;
        var image = new Image { Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Linear);
        image.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding(nameof(Image.Source)) { Source = PlayerImage });
        var owner = Window.GetWindow(this);
        var window = new Window
        {
            Owner = owner, Title = owner?.Title ?? "", Content = image, Background = Brushes.Black, Cursor = Cursors.Hand,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Width = 640, Height = 360
        };
        window.Loaded += (_, _) => window.WindowState = WindowState.Maximized; // Maximized after placement, so on the owner's monitor.
        window.MouseLeftButtonUp += (_, _) => PlayPause(this, new RoutedEventArgs());
        window.MouseDoubleClick += (_, _) => window.Close();
        window.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Escape or Key.F: window.Close(); break;
                case Key.Space: PlayPause(this, new RoutedEventArgs()); break;
                case Key.Left: SkipBy(-10); break;
                case Key.Right: SkipBy(10); break;
            }
        };
        window.Closed += (_, _) => { fullscreen = null; owner?.Activate(); };
        fullscreen = window;
        window.Show();
    }

    private void OnClipListSizeChanged(object sender, SizeChangedEventArgs e) => UpdateTileColumns();

    private void UpdateTileColumns()
    {
        double availableWidth = ClipList.ActualWidth - 12; // Less the list scrollbar.
        if (availableWidth <= 0 || !gridView) return;
        const double gap = 8.0;
        int cols = Math.Max(1, (int)Math.Floor((availableWidth + gap) / (MinTileWidth + gap)));

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
        // Close() cancels and forgets this export; once another export owns exportCts, this one must not touch the UI.
        var cts = exportCts = new CancellationTokenSource();
        var token = cts.Token;

        SetTrimControlsEnabled(false);
        TrimProgressPanel.Visibility = Visibility.Visible;
        TrimProgressBar.Value = 0;
        TrimProgressPercent.Text = "0%";
        CancelExportButton.IsEnabled = true;
        CancelExportButton.Content = Loc["Common.Cancel"];
        TrimProgressStatus.Text = Loc.Format("Gallery.SavingProgress", 0);

        var progress = new Progress<double>(p =>
        {
            if (exportCts != cts) return;
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

            // After Close the player may show another clip: registering and leaving trim mode would act on that one.
            if (exportCts == cts) await HandleExportSuccess(exportedFile, start, end);
            else UpdateStatusMessage(Loc.Format("Gallery.FragmentSaved", Path.GetFileName(exportedFile)));
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
            cts.Dispose();
            // Null: Close or Escape already cancelled this export and no newer one runs, so the UI is still ours.
            if (exportCts == cts || exportCts is null)
            {
                isExporting = false;
                TrimProgressPanel.Visibility = Visibility.Collapsed;
                SetTrimControlsEnabled(true);
                exportCts = null;
            }
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
                    mainVm.PreviewClip = mainVm.Clips.FirstOrDefault(c => c.Clip.Metadata.Id == registeredClip.Metadata.Id) ?? mainVm.PreviewClip;
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
        if (DataContext is MainViewModel mainVm) mainVm.Message = message;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (SearchBox.IsKeyboardFocused) return;
        if (!isTrimMode)
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.None && playerHandle != IntPtr.Zero) { ToggleFullscreen(); e.Handled = true; }
            return;
        }

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

