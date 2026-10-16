using System.Runtime.InteropServices;
using HuntReplay.App.Views;
using HuntReplay.Core.Configuration;
using HuntReplay.Core.Localization;

namespace HuntReplay.App.Services;

/// <summary>
/// Decides when the over-the-game notification appears. The window is created on first use, so nothing exists
/// while the setting is off. Must be called on the UI thread.
/// </summary>
internal sealed class OverlayService(StudioLog log) : IDisposable
{
    private OverlayWindow? window;
    private bool skippingFullscreen;

    /// <summary>
    /// Shows a notification when it is enabled, Studio is not the active window (Studio shows its own status then)
    /// and no exclusive fullscreen game is running. A preview shows with the given, possibly unsaved, settings regardless.
    /// </summary>
    public void Notify(SystemSettings settings, OverlayKind kind, string title, string detail = "", bool preview = false)
    {
        if (!preview && (!settings.OverlayEnabled || StudioIsActive() || ExclusiveFullscreen())) return;
        try { (window ??= new OverlayWindow()).Notify(kind, title, detail, settings.OverlayCorner, settings.OverlaySeconds); }
        catch (Exception) when (!preview) { } // Best effort: a notification must never interrupt status updates or saving.
    }

    private static bool StudioIsActive()
    {
        _ = GetWindowThreadProcessId(GetForegroundWindow(), out uint process);
        return process == (uint)Environment.ProcessId;
    }

    // A window over an exclusive fullscreen Direct3D game occludes its output: the game may stop presenting and
    // freeze until the window is gone. Windows reports that state itself; notifications wait it out silently.
    private bool ExclusiveFullscreen()
    {
        bool exclusive = SHQueryUserNotificationState(out int state) == 0 && state == QunsRunningD3DFullScreen;
        if (exclusive && !skippingFullscreen) log.Write(Text.Get("Overlay.SkippedFullscreen"));
        skippingFullscreen = exclusive;
        return exclusive;
    }

    public void Dispose()
    {
        window?.Close();
        window = null;
    }

    private const int QunsRunningD3DFullScreen = 3;
    [DllImport("user32")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("shell32")] private static extern int SHQueryUserNotificationState(out int state);
}
