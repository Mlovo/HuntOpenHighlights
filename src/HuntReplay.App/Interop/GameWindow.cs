using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HuntReplay.App.Interop;

internal static class GameWindow
{
    private static readonly string[] ProcessNames = { "Hunt", "HuntGame", "HuntGameSteam" };

    public static bool HasGameProcess(int additionalPid = 0)
    {
        if (additionalPid > 0)
        {
            try
            {
                var p = Process.GetProcessById(additionalPid);
                using (p) if (!p.HasExited) return true;
            }
            catch { }
        }
        foreach (var name in ProcessNames)
        {
            var procs = Process.GetProcessesByName(name);
            if (procs.Length > 0)
            {
                foreach (var p in procs) p.Dispose();
                return true;
            }
        }
        return false;
    }

    public static IntPtr Find(int additionalPid = 0)
    {
        var candidates = new HashSet<int>();
        foreach (var name in ProcessNames)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process) candidates.Add(process.Id);
            }
        }
        if (additionalPid > 0) candidates.Add(additionalPid);
        IntPtr result = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var pid);
            if (!candidates.Contains((int)pid) || GetWindow(window, 4) != IntPtr.Zero) return true;
            
            bool visible = IsWindowVisible(window) || IsIconic(window);
            if (!visible) return true;

            // When minimized (iconic), GetClientRect returns 0,0, which is normal
            if (!IsIconic(window))
            {
                if (!GetClientRect(window, out var bounds) || bounds.Right < 320 || bounds.Bottom < 240) return true;
            }
            result = window;
            return false;
        }, IntPtr.Zero);
        return result;
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32")] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32")] private static extern bool GetClientRect(IntPtr window, out Rect rect);
}
