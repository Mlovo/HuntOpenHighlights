using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HuntReplay.App.Interop;

/// <summary>
/// One system-wide process enumeration. Process.GetProcessesByName enumerates every process on each call,
/// so a probe that asks several questions takes a single snapshot and queries it.
/// </summary>
internal sealed class ProcessSnapshot
{
    private readonly ILookup<string, int> idsByName;
    private readonly HashSet<int> ids;

    private ProcessSnapshot(ILookup<string, int> idsByName)
    {
        this.idsByName = idsByName;
        ids = idsByName.SelectMany(group => group).ToHashSet();
    }

    public static ProcessSnapshot Take()
    {
        var processes = Process.GetProcesses();
        try { return new(processes.ToLookup(p => p.ProcessName, p => p.Id, StringComparer.OrdinalIgnoreCase)); }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public bool Has(string name) => idsByName.Contains(name);
    public IEnumerable<int> Ids(string name) => idsByName[name];
    public bool IsAlive(int pid) => ids.Contains(pid);
}

internal static partial class GameWindow
{
    private static readonly string[] PrimaryGameProcessNames = ["HuntGame", "HuntGameSteam"];
    private static readonly string[] LauncherProcessNames = ["Hunt"];

    public static bool HasGameProcess(ProcessSnapshot processes, int additionalPid = 0) =>
        (additionalPid > 0 && processes.IsAlive(additionalPid)) ||
        PrimaryGameProcessNames.Concat(LauncherProcessNames).Any(processes.Has);

    public static IntPtr Find(int additionalPid = 0) => Find(ProcessSnapshot.Take(), additionalPid);

    public static IntPtr Find(ProcessSnapshot processes, int additionalPid = 0)
    {
        var candidatePids = PrimaryGameProcessNames.SelectMany(processes.Ids).ToHashSet();
        if (additionalPid > 0) candidatePids.Add(additionalPid);
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
            if (title.Contains("Hunt", StringComparison.OrdinalIgnoreCase)) score += 1000;
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

    private static readonly string[] SplashTitles = ["Easy Anti-Cheat", "EasyAntiCheat", "Waiting for game", "Splash"];
    private static readonly string[] SplashClasses = ["EasyAntiCheat", "Splash"];

    private static bool IsSplashOrEac(string title, string className) =>
        SplashTitles.Any(s => title.Contains(s, StringComparison.OrdinalIgnoreCase)) ||
        SplashClasses.Any(s => className.Contains(s, StringComparison.OrdinalIgnoreCase));

    private static unsafe string GetWindowTitle(IntPtr hWnd)
    {
        char* buffer = stackalloc char[256];
        int length = GetWindowTextNative(hWnd, buffer, 256);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    private static unsafe string GetWindowClassName(IntPtr hWnd)
    {
        char* buffer = stackalloc char[256];
        int length = GetClassNameNative(hWnd, buffer, 256);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    private const uint GW_OWNER = 4;
    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    // Delegate callbacks are not supported by source-generated LibraryImport; EnumWindows keeps DllImport.
    [DllImport("user32")] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
    [LibraryImport("user32")] private static partial uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [LibraryImport("user32")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsWindowVisible(IntPtr window);
    [LibraryImport("user32")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsIconic(IntPtr window);
    [LibraryImport("user32")] private static partial IntPtr GetWindow(IntPtr window, uint command);
    [LibraryImport("user32")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetClientRect(IntPtr window, out Rect rect);
    [LibraryImport("user32", EntryPoint = "GetWindowTextW")] private static unsafe partial int GetWindowTextNative(IntPtr hWnd, char* text, int maxCount);
    [LibraryImport("user32", EntryPoint = "GetClassNameW")] private static unsafe partial int GetClassNameNative(IntPtr hWnd, char* className, int maxCount);
}
