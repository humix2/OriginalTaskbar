using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace OriginalTaskbar;

internal sealed class TrayIconInfo
{
    public required string Name { get; init; }
    public required AutomationElement Element { get; init; }
    public ImageSource? Icon { get; init; }
}

// Windows 11's notification-area icons live inside a single XAML/Composition surface with no
// separate native window per icon (see the now-removed SystemTrayEmbedder / TrayCaptureProbe
// experiments - reparenting and PrintWindow both dead-end here). This reads the real icon list
// via UI Automation (reliable, and works even while the real taskbar is hidden), then resolves
// an image for each entry from:
//   1. Explorer's own per-icon records under HKCU\Control Panel\NotifyIconSettings, which hold
//      the owning .exe path, the initial tooltip and a PNG snapshot of the actual tray icon.
//   2. Running processes, matched by process name or by the .exe's FileDescription/ProductName
//      (covers tooltips like "マウスふるふる" whose process is "mousefr").
// The tooltip text from UIA is the only key we have, so both sources are matched against it.
internal sealed class TrayIconMirrorService
{
    [DllImport("user32.dll")]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFullName(IntPtr hProcess, ref uint packageFullNameLength, StringBuilder? packageFullName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    private const uint ProcessQueryLimitedInformation = 0x1000;

    // A name that couldn't be resolved is retried after this long, so an icon whose app or
    // registry record shows up a little after the tray entry itself still gets picked up.
    private static readonly TimeSpan UnresolvedRetryInterval = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, ImageSource> _iconCache = new();
    private readonly Dictionary<string, DateTime> _unresolved = new();

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
            var name = FirstLine(rawName);
            if (name.Length == 0) continue;

            results.Add(new TrayIconInfo { Name = name, Element = element, Icon = ResolveIcon(name) });
        }

        return results;
    }

    private static string FirstLine(string text) => text.Split('\n')[0].Trim();

    private ImageSource? ResolveIcon(string appName)
    {
        if (_iconCache.TryGetValue(appName, out var cached)) return cached;
        if (_unresolved.TryGetValue(appName, out var failedAt) && DateTime.UtcNow - failedAt < UnresolvedRetryInterval)
        {
            return null;
        }

        ImageSource? icon = null;
        try
        {
            icon = FindIcon(appName);
        }
        catch (Exception ex)
        {
            Log.Write($"TrayIconMirrorService: icon lookup failed for '{appName}': {ex.Message}");
        }

        if (icon != null)
        {
            _iconCache[appName] = icon;
            _unresolved.Remove(appName);
        }
        else
        {
            _unresolved[appName] = DateTime.UtcNow;
            Log.Write($"TrayIconMirrorService: no icon found for '{appName}'");
        }
        return icon;
    }

    private sealed record NotifyIconRecord(string? ExePath, string Tooltip, byte[]? Snapshot);

    // PackageFullName is set for packaged (Store / MSIX) apps, whose tray tooltip usually starts
    // with the localized package display name rather than anything in the .exe itself
    // (e.g. "スマートフォン連携" for PhoneExperienceHost.exe).
    private sealed record RunningExe(string ProcessName, string ExePath, string? PackageFullName);

    private static ImageSource? FindIcon(string appName)
    {
        var records = LoadNotifyIconRecords();
        var running = GetRunningExes();
        var runningPaths = new HashSet<string>(running.Select(r => r.ExePath), StringComparer.OrdinalIgnoreCase);

        // 1. Explorer's own record whose tooltip is exactly this name.
        var icon = IconFromRecords(records.Where(r => string.Equals(r.Tooltip, appName, StringComparison.OrdinalIgnoreCase)), runningPaths);
        if (icon != null) return icon;

        // 2. A running process whose name or version-info description matches.
        var exePath = FindRunningExe(appName, running);
        if (exePath != null)
        {
            icon = IconFromRecords(records.Where(r => string.Equals(r.ExePath, exePath, StringComparison.OrdinalIgnoreCase)), runningPaths)
                ?? ExtractExeIcon(exePath);
            if (icon != null) return icon;
        }

        // 3. Looser tooltip match - tooltips often gain a status suffix after registration
        //    (e.g. "PowerToys Awake" -> "PowerToys Awake [無期限][オフ]").
        return IconFromRecords(
            records.Where(r => r.Tooltip.Length > 0
                && (NamesMatch(appName, r.Tooltip) || NamesMatch(r.Tooltip, appName))),
            runningPaths);
    }

    // Among candidate records, prefer one whose app is currently running (stale records from
    // old versions linger in the registry), then one with a snapshot image.
    private static ImageSource? IconFromRecords(IEnumerable<NotifyIconRecord> candidates, HashSet<string> runningPaths)
    {
        var ordered = candidates
            .OrderByDescending(r => r.ExePath != null && runningPaths.Contains(r.ExePath))
            .ThenByDescending(r => r.Snapshot is { Length: > 0 });

        foreach (var record in ordered)
        {
            var icon = DecodeSnapshot(record.Snapshot);
            if (icon == null && record.ExePath != null) icon = ExtractExeIcon(record.ExePath);
            if (icon != null) return icon;
        }
        return null;
    }

    private static List<NotifyIconRecord> LoadNotifyIconRecords()
    {
        var records = new List<NotifyIconRecord>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
            if (root == null) return records;

            foreach (var subName in root.GetSubKeyNames())
            {
                try
                {
                    using var key = root.OpenSubKey(subName);
                    if (key == null) continue;

                    var exePath = ExpandKnownFolderPath(key.GetValue("ExecutablePath") as string);
                    var tooltip = FirstLine(key.GetValue("InitialTooltip") as string ?? "");
                    var snapshot = key.GetValue("IconSnapshot") as byte[];
                    records.Add(new NotifyIconRecord(exePath, tooltip, snapshot));
                }
                catch
                {
                    // Unreadable record - skip it.
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"TrayIconMirrorService: reading NotifyIconSettings failed: {ex.Message}");
        }
        return records;
    }

    // ExecutablePath is stored as "{KNOWNFOLDERID}\rest\of\path" for paths under well-known
    // folders (Program Files, System32, Windows, ...).
    private static string? ExpandKnownFolderPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '{') return path;

        var close = path.IndexOf('}');
        if (close < 0 || !Guid.TryParse(path[..(close + 1)], out var folderId)) return path;

        if (SHGetKnownFolderPath(ref folderId, 0, IntPtr.Zero, out var ptr) != 0) return path;
        try
        {
            var folder = Marshal.PtrToStringUni(ptr);
            return folder == null ? path : folder + path[(close + 1)..];
        }
        finally
        {
            Marshal.FreeCoTaskMem(ptr);
        }
    }

    private static List<RunningExe> GetRunningExes()
    {
        var result = new List<RunningExe>();
        var processes = Process.GetProcesses();
        try
        {
            foreach (var process in processes)
            {
                var handle = OpenProcess(ProcessQueryLimitedInformation, false, process.Id);
                if (handle == IntPtr.Zero) continue;
                try
                {
                    var path = GetProcessPath(handle);
                    if (path != null) result.Add(new RunningExe(process.ProcessName, path, GetProcessPackageFullName(handle)));
                }
                finally
                {
                    CloseHandle(handle);
                }
            }
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
        return result;
    }

    // QueryFullProcessImageName with limited-query access works even for elevated processes,
    // where Process.MainModule throws.
    private static string? GetProcessPath(IntPtr handle)
    {
        var buffer = new StringBuilder(1024);
        uint size = (uint)buffer.Capacity;
        return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
    }

    private static string? GetProcessPackageFullName(IntPtr handle)
    {
        uint length = 0;
        const int ErrorInsufficientBuffer = 122;
        if (GetPackageFullName(handle, ref length, null) != ErrorInsufficientBuffer || length == 0) return null;

        var buffer = new StringBuilder((int)length);
        return GetPackageFullName(handle, ref length, buffer) == 0 ? buffer.ToString() : null;
    }

    private static string? GetPackageDisplayName(string packageFullName)
    {
        try
        {
            var package = new Windows.Management.Deployment.PackageManager()
                .FindPackageForUser(string.Empty, packageFullName);
            return package?.DisplayName;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindRunningExe(string appName, List<RunningExe> running)
    {
        foreach (var exe in running)
        {
            if (NamesMatch(appName, exe.ProcessName)) return exe.ExePath;
        }

        foreach (var exePath in running.Select(r => r.ExePath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(exePath);
                if (IsUsefulLabel(info.FileDescription) && NamesMatch(appName, info.FileDescription!)) return exePath;
                if (IsUsefulLabel(info.ProductName) && NamesMatch(appName, info.ProductName!)) return exePath;
            }
            catch
            {
                // No version info / access denied - skip it.
            }
        }

        foreach (var exe in running.Where(r => r.PackageFullName != null)
                     .DistinctBy(r => r.PackageFullName, StringComparer.OrdinalIgnoreCase))
        {
            var displayName = GetPackageDisplayName(exe.PackageFullName!);
            if (IsUsefulLabel(displayName) && NamesMatch(appName, displayName!)) return exe.ExePath;
        }
        return null;
    }

    // Generic product names like "Microsoft® Windows® Operating System" would match nothing
    // useful anyway, but very short labels could prefix-match unrelated tooltips.
    private static bool IsUsefulLabel(string? label) => label != null && label.Trim().Length >= 3;

    // Some tray tooltips append a status suffix to the app name (e.g. Steam shows
    // "Steam - 作業中" while online/in-game), so an exact match would silently fail and leave the
    // icon blank. Accept a prefix match as long as the character right after the candidate isn't
    // alphanumeric, to avoid e.g. matching "steam" against "SteamService".
    private static bool NamesMatch(string appName, string candidate)
    {
        candidate = candidate.Trim();
        if (candidate.Length == 0) return false;
        if (string.Equals(appName, candidate, StringComparison.OrdinalIgnoreCase)) return true;

        return appName.Length > candidate.Length
            && appName.StartsWith(candidate, StringComparison.OrdinalIgnoreCase)
            && !char.IsLetterOrDigit(appName[candidate.Length]);
    }

    private static ImageSource? DecodeSnapshot(byte[]? snapshot)
    {
        if (snapshot is not { Length: > 0 }) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(snapshot);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? ExtractExeIcon(string exePath)
    {
        if (!File.Exists(exePath)) return null;
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
