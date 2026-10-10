using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HuntReplay.App.Interop;
using HuntReplay.App.ViewModels;

namespace HuntReplay.App.Views;

public partial class GalleryView : UserControl
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(20) };
    private readonly Dictionary<int, BitmapSource> previewCache = new();
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

    public GalleryView()
    {
        InitializeComponent();
        timer.Tick += OnTimerTick;
        Unloaded += (_, _) => Close();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (playerHandle == IntPtr.Zero || bitmap is null) return;
        NativeMethods.hr_player_get_state(playerHandle, out int isPlaying, out int isEnded, out double currentPts);
        if (isEnded == 1)
        {
            playing = false;
            PlayButton.Content = "▶";
            timer.Stop();
        }
        if (isPlaying == 1)
        {
            bitmap.Lock();
            try
            {
                NativeMethods.hr_player_read_frame(playerHandle, bitmap.BackBuffer, bitmap.BackBufferStride, videoWidth, videoHeight, out int updated, out _);
                if (updated == 1)
                {
                    bitmap.AddDirtyRect(new Int32Rect(0, 0, videoWidth, videoHeight));
                }
            }
            finally
            {
                bitmap.Unlock();
            }

            if (!dragging)
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

        IntPtr handleToClose = IntPtr.Zero;
        lock (playerLock)
        {
            handleToClose = playerHandle;
            playerHandle = IntPtr.Zero;
        }

        if (handleToClose != IntPtr.Zero)
        {
            NativeMethods.hr_player_close(handleToClose);
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
        PlayerHint.Text = "Выберите клип, чтобы посмотреть";
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
                PlayerHint.Text = "Не удалось открыть видеоклип.";
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

            bitmap.Lock();
            try
            {
                NativeMethods.hr_player_read_frame(opened, bitmap.BackBuffer, bitmap.BackBufferStride, videoWidth, videoHeight, out int updated, out _);
                if (updated == 1)
                {
                    bitmap.AddDirtyRect(new Int32Rect(0, 0, videoWidth, videoHeight));
                }
            }
            finally
            {
                bitmap.Unlock();
            }

            PlayerHint.Visibility = Visibility.Collapsed;
            PlayButton.IsEnabled = true;
            Seek.IsEnabled = naturalDuration > 0;
            if (naturalDuration > 0) Seek.Maximum = naturalDuration;
            PositionText.Text = $"00:00 / {TimeSpan.FromSeconds(naturalDuration):mm\\:ss}";
            NativeMethods.hr_player_set_volume(opened, (float)currentVolume);

            PreloadThumbnails(opened, naturalDuration);
        }
        catch (Exception ex)
        {
            PlayerHint.Visibility = Visibility.Visible;
            PlayerHint.Text = "Ошибка плеера: " + ex.Message;
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

                IntPtr pixels = IntPtr.Zero;
                int w = 0, h = 0, stride = 0;
                int res = -1;

                lock (playerLock)
                {
                    if (token.IsCancellationRequested || playerHandle != handle || handle == IntPtr.Zero) break;
                    res = NativeMethods.hr_player_preview(handle, s, 192, out pixels, out w, out h, out stride);
                }

                if (res == 0 && pixels != IntPtr.Zero)
                {
                    try
                    {
                        var bytes = new byte[checked(stride * h)];
                        Marshal.Copy(pixels, bytes, 0, bytes.Length);
                        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bytes, stride);
                        bmp.Freeze();
                        Dispatcher.BeginInvoke(() =>
                        {
                            if (!token.IsCancellationRequested && playerHandle == handle)
                            {
                                previewCache[secKey] = bmp;
                            }
                        });
                    }
                    finally
                    {
                        NativeMethods.hr_free(pixels);
                    }
                }
            }
        }, token);
    }

    private void PlayPause(object sender, RoutedEventArgs e)
    {
        if (playerHandle == IntPtr.Zero) return;
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
            if (token.IsCancellationRequested) return;
            try
            {
                IntPtr pixels = IntPtr.Zero;
                int w = 0, h = 0, stride = 0;
                int res = -1;

                lock (playerLock)
                {
                    if (token.IsCancellationRequested || handle != playerHandle || handle == IntPtr.Zero) return;
                    res = NativeMethods.hr_player_preview(handle, targetSec, 192, out pixels, out w, out h, out stride);
                }

                if (res == 0 && pixels != IntPtr.Zero)
                {
                    try
                    {
                        var bytes = new byte[checked(stride * h)];
                        Marshal.Copy(pixels, bytes, 0, bytes.Length);
                        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bytes, stride);
                        bmp.Freeze();
                        Dispatcher.BeginInvoke(() =>
                        {
                            if (token.IsCancellationRequested || handle != playerHandle) return;
                            previewCache[secondKey] = bmp;
                            if (PreviewCard.Visibility == Visibility.Visible)
                            {
                                var curPos = Mouse.GetPosition(Seek);
                                double curRatio = Math.Clamp(curPos.X / Math.Max(1, Seek.ActualWidth), 0.0, 1.0);
                                if (Math.Abs((int)Math.Round(curRatio * naturalDuration) - secondKey) <= 1)
                                {
                                    PreviewThumb.Source = bmp;
                                }
                            }
                        });
                    }
                    finally
                    {
                        NativeMethods.hr_free(pixels);
                    }
                }
            }
            catch { }
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
        NativeMethods.hr_player_seek(playerHandle, target);
        RefreshCurrentFrame();
    }

    private void SeekChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!updating && !dragging && Seek.IsEnabled && playerHandle != IntPtr.Zero)
        {
            NativeMethods.hr_player_seek(playerHandle, Seek.Value);
            RefreshCurrentFrame();
        }
    }

    private void RefreshCurrentFrame()
    {
        if (bitmap is null || playerHandle == IntPtr.Zero) return;
        bitmap.Lock();
        try
        {
            NativeMethods.hr_player_read_frame(playerHandle, bitmap.BackBuffer, bitmap.BackBufferStride, videoWidth, videoHeight, out int updated, out _);
            if (updated == 1)
            {
                bitmap.AddDirtyRect(new Int32Rect(0, 0, videoWidth, videoHeight));
            }
        }
        finally
        {
            bitmap.Unlock();
        }
        PositionText.Text = $"{TimeSpan.FromSeconds(Seek.Value):mm\\:ss} / {TimeSpan.FromSeconds(naturalDuration):mm\\:ss}";
    }

    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false) Close();
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
            VolumeSlider.ToolTip = $"Громкость: {percent}%";
        }

        if (VolumeIcon is not null && VolumeButton is not null)
        {
            if (currentVolume <= 0.001)
            {
                VolumeIcon.Text = "\uE74F";
                VolumeButton.ToolTip = "Включить звук";
            }
            else if (currentVolume < 0.5)
            {
                VolumeIcon.Text = "\uE993";
                VolumeButton.ToolTip = $"Выключить звук ({percent}%)";
            }
            else
            {
                VolumeIcon.Text = "\uE767";
                VolumeButton.ToolTip = $"Выключить звук ({percent}%)";
            }
        }
    }

    private void VolumeMouseWheel(object sender, MouseWheelEventArgs e)
    {
        double step = e.Delta > 0 ? 0.05 : -0.05;
        VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + step, 0.0, 1.0);
        e.Handled = true;
    }
}

