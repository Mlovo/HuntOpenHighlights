using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using HuntReplay.Core.Configuration;

namespace HuntReplay.App.Views;

internal enum OverlayKind { Info, Problem }

/// <summary>
/// A small notification drawn over the game without touching the game process: a separate topmost window that never
/// takes focus, lets every click through and is excluded from screen capture. Nothing is drawn while it is hidden,
/// and while it is shown it is a static layered window, so the cost is one composition of a few hundred pixels.
/// </summary>
internal sealed class OverlayWindow : Window
{
    private readonly DispatcherTimer hideTimer = new();
    private readonly Border card;
    private readonly Border strip;
    private readonly TextBlock title = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock detail = new() { FontSize = 12, Margin = new(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private IntPtr handle, monitor;
    private OverlayCorner corner;

    public OverlayWindow()
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true; Focusable = false; IsHitTestVisible = false;
        WindowStartupLocation = WindowStartupLocation.Manual; Left = -32000; Top = -32000; SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = new FontFamily("Segoe UI"); UseLayoutRounding = true;
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        detail.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        strip = new Border { Width = 4, CornerRadius = new(8, 0, 0, 8) };
        var text = new StackPanel { Margin = new(14, 10, 18, 11), Children = { title, detail } };
        var layout = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(strip, Dock.Left);
        layout.Children.Add(strip); layout.Children.Add(text);
        card = new Border
        {
            CornerRadius = new(8), BorderThickness = new(1), MinWidth = 240, MaxWidth = 400,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Child = layout
        };
        card.SetResourceReference(Border.BackgroundProperty, "PopupBackground");
        card.SetResourceReference(Border.BorderBrushProperty, "Border");
        Content = card;
        hideTimer.Tick += (_, _) => { hideTimer.Stop(); Hide(); };
        // New text and a monitor with another scale both resize the window after it was placed: keep its corner where it was.
        SizeChanged += (_, _) => { if (IsVisible) Place(); };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        handle = new WindowInteropHelper(this).Handle;
        // Clicks fall through to the game, and showing the window never activates it, so the game keeps focus and input.
        SetWindowLong(handle, GwlExStyle, GetWindowLong(handle, GwlExStyle) | WsExTransparent | WsExLayered | WsExNoActivate | WsExToolWindow);
        // Keeps the notification out of monitor capture (Windows 10 2004+). Window capture records only the game anyway.
        _ = SetWindowDisplayAffinity(handle, WdaExcludeFromCapture);
    }

    /// <summary>Shows the notification on the monitor the user is looking at, replacing one that is still visible.</summary>
    public void Notify(OverlayKind kind, string heading, string text, OverlayCorner where, int seconds)
    {
        title.Text = heading;
        detail.Text = text;
        detail.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        strip.SetResourceReference(Border.BackgroundProperty, kind == OverlayKind.Problem ? "Warning" : "Accent");
        corner = where;
        monitor = MonitorFromWindow(GetForegroundWindow(), MonitorDefaultToPrimary);
        if (handle == IntPtr.Zero) { Show(); Hide(); } // Lets WPF finish its first placement off-screen.
        Place(); // First guess from the measured card, so a hidden window does not appear at its old place.
        if (!IsVisible) Show();
        UpdateLayout(); // Sizes the window to the new text now instead of on the next layout pass.
        Place(); // From the real window size; also raises it above a game that became topmost after the last notification.
        hideTimer.Stop();
        hideTimer.Interval = TimeSpan.FromSeconds(seconds);
        hideTimer.Start();
    }

    private void Place()
    {
        if (handle == IntPtr.Zero || monitor == IntPtr.Zero) return;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        double scale = GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1;
        int width, height;
        if (IsVisible && GetWindowRect(handle, out var bounds))
        {
            // The size WPF actually gave the window. Positioning the first notification from the Measure estimate
            // alone left it further right than the later ones.
            width = bounds.Right - bounds.Left; height = bounds.Bottom - bounds.Top;
        }
        else
        {
            card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            width = (int)Math.Ceiling(card.DesiredSize.Width * scale); height = (int)Math.Ceiling(card.DesiredSize.Height * scale);
        }
        int margin = (int)Math.Round(24 * scale);
        // The whole monitor, not the work area: a fullscreen game covers the taskbar.
        var area = info.Monitor;
        int x = corner is OverlayCorner.TopLeft or OverlayCorner.BottomLeft ? area.Left + margin : area.Right - margin - width;
        int y = corner is OverlayCorner.TopLeft or OverlayCorner.TopRight ? area.Top + margin : area.Bottom - margin - height;
        SetWindowPos(handle, HwndTopmost, x, y, 0, 0, SwpNoActivate | SwpNoSize); // WPF sizes the window to the card itself.
    }

    private const int GwlExStyle = -20, WsExTransparent = 0x20, WsExLayered = 0x80000, WsExNoActivate = 0x8000000, WsExToolWindow = 0x80;
    private const uint WdaExcludeFromCapture = 0x11, MonitorDefaultToPrimary = 1, SwpNoActivate = 0x10, SwpNoSize = 0x1;
    private static readonly IntPtr HwndTopmost = new(-1);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32")] private static extern int SetWindowLong(IntPtr window, int index, int value);
    [DllImport("user32")] private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
    [DllImport("user32")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32", EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("shcore")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
}
