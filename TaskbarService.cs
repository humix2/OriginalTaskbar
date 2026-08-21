using System.Text;

namespace OriginalTaskbar;

// Controls visibility of the standard Windows taskbar (primary + one per additional monitor).
internal static class TaskbarService
{
    private static List<IntPtr> FindTaskbarWindows()
    {
        var handles = new List<IntPtr>();
        var primary = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (primary != IntPtr.Zero) handles.Add(primary);

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            var sb = new StringBuilder(256);
            NativeMethods.GetClassName(hWnd, sb, sb.Capacity);
            if (sb.ToString() == "Shell_SecondaryTrayWnd") handles.Add(hWnd);
            return true;
        }, IntPtr.Zero);

        return handles;
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
