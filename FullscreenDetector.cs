using System.Runtime.InteropServices;
using System.Text;

namespace OriginalTaskbar;

// Detects an exclusive-fullscreen foreground window (e.g. a fullscreened video or game) on the
// monitor our bar occupies. A merely maximized window stops at the work area we reserve via
// AppBarService, but a fullscreen window deliberately covers the whole monitor rect, ignoring
// the work area - that's the signal real auto-hide taskbars use to get out of the way.
internal static class FullscreenDetector
{
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint MONITORINFOF_PRIMARY = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    public static bool IsForegroundWindowFullscreen(IntPtr ownHwnd)
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || hwnd == ownHwnd) return false;
        if (NativeMethods.IsIconic(hwnd)) return false;

        var sb = new StringBuilder(256);
        NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
        var className = sb.ToString();
        // The desktop itself covers the whole monitor too, but isn't a "fullscreen app".
        if (className is "Progman" or "WorkerW") return false;

        if (!GetWindowRect(hwnd, out var windowRect)) return false;

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return false;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;

        // Our bar only occupies the primary monitor; a fullscreen app on another monitor
        // doesn't conflict with it.
        if ((info.dwFlags & MONITORINFOF_PRIMARY) == 0) return false;

        return windowRect.Left <= info.rcMonitor.Left &&
               windowRect.Top <= info.rcMonitor.Top &&
               windowRect.Right >= info.rcMonitor.Right &&
               windowRect.Bottom >= info.rcMonitor.Bottom;
    }
}
