using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace HuntReplay.App.Interop;
internal static class WindowAppearance
{
    public static void DarkCaption(Window window)
    {
        int enabled = 1;
        // Best effort: system title bar remains functional on builds ignoring this attribute.
        _ = DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 20, ref enabled, sizeof(int));
    }
    [DllImport("dwmapi")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
