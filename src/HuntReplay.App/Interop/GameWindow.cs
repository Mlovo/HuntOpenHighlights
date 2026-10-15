using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace HuntReplay.App.Interop;

internal static class GameWindow
{
    private static readonly string[] PrimaryGameProcessNames = { "HuntGame", "HuntGameSteam" };
    private static readonly string[] LauncherProcessNames = { "Hunt" };

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
        foreach (var name in PrimaryGameProcessNames)
        {
            var procs = Process.GetProcessesByName(name);
            if (procs.Length > 0)
            {
                foreach (var p in procs) p.Dispose();
                return true;
            }
        }
        foreach (var name in LauncherProcessNames)
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
        var candidatePids = new HashSet<int>();
        if (additionalPid > 0) candidatePids.Add(additionalPid);
        foreach (var name in PrimaryGameProcessNames)
            foreach (var process in Process.GetProcessesByName(name))
                using (process) candidatePids.Add(process.Id);
        if (candidatePids.Count == 0) return IntPtr.Zero;

        IntPtr bestWindow = IntPtr.Zero;
        int bestScore = 0;

        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var pid);
            int windowPid = (int)pid;
            if (!candidatePids.Contains(windowPid)) return true;

            // Reject child or owned windows
            if (GetWindow(window, GW_OWNER) != IntPtr.Zero) return true;

            bool isIconic = IsIconic(window);
            bool isVisible = IsWindowVisible(window) || isIconic;
            if (!isVisible) return true;

            string title = GetWindowTitle(window);
            string className = GetWindowClassName(window);

            // Strictly filter out Easy Anti-Cheat splash, launcher, and loading dialogs
            if (IsSplashOrEac(title, className)) return true;

            Rect bounds = default;
            if (!isIconic)
            {
                if (!GetClientRect(window, out bounds) || bounds.Right < 640 || bounds.Bottom < 480)
                    return true;
            }

            int score = 100;
            if (additionalPid > 0 && windowPid == additionalPid) score += 10000;
            if (string.Equals(className, "CryENGINE", StringComparison.OrdinalIgnoreCase)) score += 3000;
            if (title.IndexOf("Hunt", StringComparison.OrdinalIgnoreCase) >= 0) score += 1000;
            if (!isIconic)
            {
                score += 500;
                score += (bounds.Right * bounds.Bottom) / 1000;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestWindow = window;
            }

            return true;
        }, IntPtr.Zero);

        return bestWindow;
    }

    private static bool IsSplashOrEac(string title, string className)
    {
        if (title.IndexOf("Easy Anti-Cheat", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (title.IndexOf("EasyAntiCheat", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (title.IndexOf("Waiting for game", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (title.IndexOf("Splash", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (className.IndexOf("EasyAntiCheat", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (className.IndexOf("Splash", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    private static string GetWindowTitle(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        return GetWindowTextNative(hWnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
    }

    private static string GetWindowClassName(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        return GetClassNameNative(hWnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
    }

    private const uint GW_OWNER = 4;
    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32")] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32")] private static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextNative(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameNative(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
}
