using System.Diagnostics;
using System.Text;

namespace OriginalTaskbar;

// Controls visibility of the standard Windows taskbar (primary + one per additional monitor).
internal static class TaskbarService
{
    private static List<IntPtr> FindTaskbarWindows()
    {
        var handles = new List<IntPtr>();

        // Match by class name alone (Shell_TrayWnd / Shell_SecondaryTrayWnd) is spoofable: any
        // same-user process can create a window with that class name. Only act on windows that
        // are actually owned by a running explorer.exe process.
        var explorerPids = GetExplorerProcessIds();
        if (explorerPids.Count == 0) return handles;

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            var sb = new StringBuilder(256);
            NativeMethods.GetClassName(hWnd, sb, sb.Capacity);
            var className = sb.ToString();
            if (className is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd")) return true;

            NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
            if (explorerPids.Contains(pid)) handles.Add(hWnd);
            return true;
        }, IntPtr.Zero);

        return handles;
    }

    private static HashSet<uint> GetExplorerProcessIds()
    {
        var pids = new HashSet<uint>();
        foreach (var process in Process.GetProcessesByName("explorer"))
        {
            using (process) pids.Add((uint)process.Id);
        }
        return pids;
    }

    public static void HideAll()
    {
        foreach (var hWnd in FindTaskbarWindows())
            NativeMethods.ShowWindow(hWnd, NativeMethods.SW_HIDE);
    }

    public static void ShowAll()
    {
        foreach (var hWnd in FindTaskbarWindows())
            NativeMethods.ShowWindow(hWnd, NativeMethods.SW_SHOW);
    }
}
