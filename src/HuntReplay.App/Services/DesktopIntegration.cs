using System.ComponentModel;
using System.Diagnostics;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using HuntReplay.Core.Configuration;
using HuntReplay.Core.Storage;
using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.Win32;

namespace HuntReplay.App.Services;

internal sealed class HotkeyService : IDisposable
{
    private readonly HwndSource source;
    private readonly Action save;
    private int activeId;
    public int CurrentKey { get; private set; }
    public HotkeyService(Window window, Action save)
    {
        this.save = save;
        source = HwndSource.FromHwnd(new WindowInteropHelper(window).EnsureHandle())!;
        source.AddHook(WndProc);
    }
    public void Register(int virtualKey)
    {
        if (virtualKey == CurrentKey) return;
        int id = activeId == 0x4811 ? 0x4812 : 0x4811;
        if (!RegisterHotKey(source.Handle, id, 0x4000 /* MOD_NOREPEAT */, (uint)virtualKey))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Горячая клавиша уже занята. Выберите другую F-клавишу.");
        if (activeId != 0) UnregisterHotKey(source.Handle, activeId);
        activeId = id; CurrentKey = virtualKey;
    }
    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam, ref bool handled)
    {
        if (message == 0x0312 && wparam.ToInt32() == activeId) { handled = true; save(); }
        return IntPtr.Zero;
    }
    public void Dispose()
    {
        if (activeId != 0) UnregisterHotKey(source.Handle, activeId);
        source.RemoveHook(WndProc);
    }
    [DllImport("user32", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32")] private static extern bool UnregisterHotKey(IntPtr window, int id);
}

internal static class DesktopIntegration
{
    public static void ConfigureStartup(bool enabled, bool portable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        if (enabled)
        {
            var executable = Environment.ProcessPath ?? throw new IOException("Не найден путь .exe.");
            key.SetValue("HuntReplayStudio", $"\"{executable}\" --tray" + (portable ? " --portable" : ""));
        }
        else key.DeleteValue("HuntReplayStudio", false);
    }
    public static void NotifySaved(LibraryClip clip, StudioSettings settings, StudioLog log)
    {
        if (settings.System.SoundOnSave) SystemSounds.Asterisk.Play();
        if (!settings.System.ToastOnSave) return;
        try
        {
            var toast = new ToastContentBuilder().AddArgument("action", "gallery")
                .AddArgument("clip", clip.Metadata.Id.ToString())
                .AddText("Хайлайт сохранён")
                .AddText($"{clip.Metadata.EventTag.Replace('_', ' ')} · {clip.Metadata.DurationSeconds:F0} сек");
            if (File.Exists(clip.ThumbnailPath)) toast.AddInlineImage(new Uri(clip.ThumbnailPath));
            toast.Show();
        }
        catch (Exception ex) { log.Write("Windows не показала toast: " + ex.Message, "WARN"); }
    }
    public static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { Path.GetFullPath(folder) }, UseShellExecute = true });
    }
    public static void RevealFile(string file)
    {
        Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = "/select,\"" + Path.GetFullPath(file) + "\"", UseShellExecute = true });
    }
    // Opens the already saved MP4 with whatever Windows associates with .mp4. Nothing is re-encoded or copied.
    public static void OpenWithDefaultPlayer(string file)
    {
        var full = Path.GetFullPath(file);
        if (!File.Exists(full)) throw new FileNotFoundException("Клип не найден.", full);
        Process.Start(new ProcessStartInfo(full) { UseShellExecute = true });
    }
}
