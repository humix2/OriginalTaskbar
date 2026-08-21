using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WindowsDesktop;

namespace OriginalTaskbar;

internal sealed class AppWindowInfo
{
    public required IntPtr Handle { get; init; }
    public required string Title { get; init; }
    public ImageSource? Icon { get; init; }
}

// Enumerates alt-tab-style top-level windows that belong to the currently active
// virtual desktop, for display as a running-apps list on the bar.
internal sealed class RunningAppsService
{
    private readonly Dictionary<IntPtr, ImageSource?> _iconCache = new();

    // EnumWindows returns windows in Z-order, which reshuffles every time focus changes.
    // Remember the order each window was first seen in so the displayed list stays stable.
    private readonly Dictionary<IntPtr, int> _firstSeenOrder = new();
    private int _nextOrderIndex;

    public List<AppWindowInfo> GetCurrentDesktopApps(IntPtr excludeHwnd)
    {
        var results = new List<AppWindowInfo>();
        int enumerated = 0, altTabCandidates = 0, onCurrentDesktop = 0;
        Exception? firstDesktopCheckError = null;

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            enumerated++;
            try
            {
                if (hWnd == excludeHwnd || !IsAltTabWindow(hWnd)) return true;
                altTabCandidates++;

                bool isCurrent;
                try
                {
                    isCurrent = VirtualDesktop.IsCurrentVirtualDesktop(hWnd);
                }
                catch (Exception ex)
                {
                    firstDesktopCheckError ??= ex;
                    return true;
                }
                if (!isCurrent) return true;
                onCurrentDesktop++;

                var title = GetWindowTitle(hWnd);
                if (string.IsNullOrWhiteSpace(title)) return true;

                results.Add(new AppWindowInfo { Handle = hWnd, Title = title, Icon = GetIcon(hWnd) });
            }
            catch (Exception ex)
            {
                // One misbehaving window (COM/version quirks, races on close) must not
                // abort enumeration of the rest.
                Log.Write($"Skipped window {hWnd}: {ex.Message}");
            }
            return true;
        }, IntPtr.Zero);

        Log.Write($"enumerated={enumerated} altTabCandidates={altTabCandidates} " +
                  $"onCurrentDesktop={onCurrentDesktop} shown={results.Count}");
        if (firstDesktopCheckError is not null)
            Log.Write($"IsCurrentVirtualDesktop failed at least once:\n{firstDesktopCheckError}");

        // Assign order in a first pass (in EnumWindows' original sequence) before sorting,
        // so multiple windows seen for the first time in the same pass get a deterministic order.
        var orderKeys = results.ToDictionary(a => a.Handle, a => GetFirstSeenOrder(a.Handle));
        results.Sort((a, b) => orderKeys[a.Handle].CompareTo(orderKeys[b.Handle]));

        PruneCaches(results);
        return results;
    }

    private int GetFirstSeenOrder(IntPtr hWnd)
    {
        if (_firstSeenOrder.TryGetValue(hWnd, out var index)) return index;

        index = _nextOrderIndex++;
        _firstSeenOrder[hWnd] = index;
        return index;
    }

    private static bool IsAltTabWindow(IntPtr hWnd)
    {
        if (!NativeMethods.IsWindowVisible(hWnd)) return false;

        var exStyle = (long)NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GWL_EXSTYLE);
        if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0 && (exStyle & NativeMethods.WS_EX_APPWINDOW) == 0)
            return false;

        if (NativeMethods.GetWindow(hWnd, NativeMethods.GW_OWNER) != IntPtr.Zero &&
            (exStyle & NativeMethods.WS_EX_APPWINDOW) == 0)
            return false;

        if (NativeMethods.GetWindowTextLength(hWnd) == 0) return false;

        // UWP/packaged windows on other virtual desktops stay alive but DWM-cloaked.
        var hr = NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_CLOAKED, out var cloaked, sizeof(int));
        if (hr == 0 && cloaked != 0) return false;

        return true;
    }

    private static string GetWindowTitle(IntPtr hWnd)
    {
        var length = NativeMethods.GetWindowTextLength(hWnd);
        if (length == 0) return string.Empty;

        var sb = new StringBuilder(length + 1);
        NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private ImageSource? GetIcon(IntPtr hWnd)
    {
        if (_iconCache.TryGetValue(hWnd, out var cached)) return cached;

        var icon = TryGetWindowIcon(hWnd) ?? TryGetProcessIcon(hWnd);
        _iconCache[hWnd] = icon;
        return icon;
    }

    private static ImageSource? TryGetWindowIcon(IntPtr hWnd)
    {
        foreach (var iconType in new[] { NativeMethods.ICON_BIG, NativeMethods.ICON_SMALL2, NativeMethods.ICON_SMALL })
        {
            NativeMethods.SendMessageTimeout(hWnd, NativeMethods.WM_GETICON, iconType, IntPtr.Zero,
                NativeMethods.SMTO_ABORTIFHUNG, 100, out var result);
            if (result != IntPtr.Zero) return ToImageSource(result);
        }

        var classIcon = NativeMethods.GetClassLongPtr(hWnd, NativeMethods.GCLP_HICON);
        if (classIcon != IntPtr.Zero) return ToImageSource(classIcon);

        classIcon = NativeMethods.GetClassLongPtr(hWnd, NativeMethods.GCLP_HICONSM);
        return classIcon != IntPtr.Zero ? ToImageSource(classIcon) : null;
    }

    private static ImageSource? TryGetProcessIcon(IntPtr hWnd)
    {
        try
        {
            NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
            using var process = Process.GetProcessById((int)pid);
            var exePath = process.MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath)) return null;

            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            return icon is null ? null : ToImageSource(icon.Handle);
        }
        catch
        {
            return null;
        }
    }

    // hIcon ownership stays with the window/class (WM_GETICON, GetClassLongPtr) or with the
    // `using`-disposed System.Drawing.Icon above - never destroyed here.
    private static ImageSource? ToImageSource(IntPtr hIcon)
    {
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
    }

    private void PruneCaches(List<AppWindowInfo> current)
    {
        var liveHandles = current.Select(a => a.Handle).ToHashSet();
        foreach (var stale in _iconCache.Keys.Except(liveHandles).ToList())
            _iconCache.Remove(stale);
        foreach (var stale in _firstSeenOrder.Keys.Except(liveHandles).ToList())
            _firstSeenOrder.Remove(stale);
    }
}
