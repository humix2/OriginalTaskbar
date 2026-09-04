using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OriginalTaskbar;

internal sealed class TrayIconInfo
{
    public required string Name { get; init; }
    public required AutomationElement Element { get; init; }
    public ImageSource? Icon { get; init; }
}

// Windows 11's notification-area icons live inside a single XAML/Composition surface with no
// separate native window per icon (see the now-removed SystemTrayEmbedder / TrayCaptureProbe
// experiments - reparenting and PrintWindow both dead-end here). Rather than reproduce Explorer's
// exact rendered pixels, this reads the real icon list via UI Automation (reliable, and works
// even while the real taskbar is hidden) and substitutes each entry's own .exe icon as a stand-in
// - the same extraction RunningAppsService already uses for the running-apps list.
internal sealed class TrayIconMirrorService
{
    [DllImport("user32.dll")]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    private readonly Dictionary<string, ImageSource?> _iconCache = new();

    public List<TrayIconInfo> GetNotifyIcons()
    {
        var results = new List<TrayIconInfo>();

        var shellTray = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (shellTray == IntPtr.Zero) return results;

        var bridge = FindWindowEx(shellTray, IntPtr.Zero, "Windows.UI.Composition.DesktopWindowContentBridge", null);
        if (bridge == IntPtr.Zero) return results;

        AutomationElement root;
        try
        {
            root = AutomationElement.FromHandle(bridge);
        }
        catch
        {
            return results;
        }

        AutomationElementCollection matches;
        try
        {
            var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, "NotifyItemIcon");
            matches = root.FindAll(TreeScope.Descendants, condition);
        }
        catch (Exception ex)
        {
            Log.Write($"TrayIconMirrorService: FindAll failed: {ex.Message}");
            return results;
        }

        foreach (AutomationElement element in matches)
        {
            string rawName;
            try
            {
                rawName = element.Current.Name;
            }
            catch
            {
                continue;
            }

            // Multi-line tooltips (e.g. "QuiteRSS\n新着ニュース: 0\n...") carry the app name on
            // the first line only.
            var name = rawName.Split('\n')[0].Trim();
            if (name.Length == 0) continue;

            results.Add(new TrayIconInfo { Name = name, Element = element, Icon = ResolveIcon(name) });
        }

        return results;
    }

    private ImageSource? ResolveIcon(string appName)
    {
        if (_iconCache.TryGetValue(appName, out var cached)) return cached;

        var icon = ExtractIconForProcessName(appName);
        _iconCache[appName] = icon;
        return icon;
    }

    // Some tray tooltips append a status suffix to the app name (e.g. Steam shows
    // "Steam - 作業中" while online/in-game), so an exact match against the process name
    // would silently fail and leave the icon blank. Accept a prefix match as long as the
    // character right after the process name isn't alphanumeric, to avoid e.g. matching
    // "steam" against "SteamService".
    private static bool NameMatchesProcess(string appName, string processName)
    {
        if (string.Equals(appName, processName, StringComparison.OrdinalIgnoreCase)) return true;

        return appName.Length > processName.Length
            && appName.StartsWith(processName, StringComparison.OrdinalIgnoreCase)
            && !char.IsLetterOrDigit(appName[processName.Length]);
    }

    private static ImageSource? ExtractIconForProcessName(string appName)
    {
        string? exePath = null;
        var processes = Process.GetProcesses();
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (!NameMatchesProcess(appName, process.ProcessName)) continue;
                    exePath = process.MainModule?.FileName;
                    if (exePath != null) break;
                }
                catch
                {
                    // Inaccessible process (different integrity level, protected, ...) - skip it.
                }
            }
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }

        if (exePath == null) return null;

        try
        {
            using var extracted = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            if (extracted == null) return null;

            var source = Imaging.CreateBitmapSourceFromHIcon(
                extracted.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception ex)
        {
            Log.Write($"TrayIconMirrorService: icon extraction failed for '{exePath}': {ex.Message}");
            return null;
        }
    }

    public static void Invoke(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern) && pattern is InvokePattern invoke)
            {
                invoke.Invoke();
            }
        }
        catch (Exception ex)
        {
            Log.Write($"TrayIconMirrorService: Invoke failed: {ex.Message}");
        }
    }
}
