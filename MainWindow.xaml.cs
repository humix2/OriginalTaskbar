using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WindowsDesktop;

namespace OriginalTaskbar;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly PerformanceCounter _cpuCounter = new("Processor", "% Processor Time", "_Total");

    private readonly Dictionary<string, (PerformanceCounter Recv, PerformanceCounter Sent)> _netCounters = new();
    private DateTime _lastNetRefresh = DateTime.MinValue;

    private readonly LowLevelKeyboardHook _keyboardHook = new();
    private readonly DispatcherTimer _reHideTaskbarTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private bool _winKeyIsDown;
    private bool _otherKeyPressedDuringWin;
    private readonly TaskbarWatcher _taskbarWatcher = new();

    // Belt-and-suspenders: whatever shows the taskbar back (a virtual-desktop-switch animation,
    // an Explorer-restart, a Windows 11 transition window we've never seen before, ...), this
    // guarantees it gets hidden again within one interval, without needing to reverse-engineer
    // exactly how Explorer did it this time.
    private readonly DispatcherTimer _taskbarEnforceTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };

    private readonly DispatcherTimer _fullscreenCheckTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private bool _hiddenForFullscreen;

    private readonly RunningAppsService _runningApps = new();
    private readonly DispatcherTimer _appsRefreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _isRefreshingApps;

    private readonly TrayIconMirrorService _trayIconMirror = new();
    private readonly DispatcherTimer _trayIconRefreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    private readonly AppBarService _appBar = new();
    private readonly DispatcherTimer _pinRetryTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private int _pinAttempts;

    private readonly AppSettings _settings = AppSettings.Load();
    private bool _batteryPresent;

    public MainWindow()
    {
        InitializeComponent();
        LogStartupDiagnostics();
        ApplyVisibility();
        UpdateBattery();

        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;

        // First sample of a PerformanceCounter is always 0; warm it up before the timer starts.
        _cpuCounter.NextValue();
        RefreshNetworkCounters();

        _timer.Tick += Timer_Tick;
        _timer.Start();

        // EnumWindows + per-window virtual-desktop/icon lookups are too slow to run on the UI
        // thread every tick (was stalling the clock and, worse, making the window unresponsive
        // enough that Task Manager's "End Task" escalated straight to a hard kill, skipping our
        // taskbar-restore cleanup). Runs on its own slower timer, off the UI thread.
        _appsRefreshTimer.Tick += async (_, _) => await RefreshRunningAppsAsync();
        _appsRefreshTimer.Start();

        _trayIconRefreshTimer.Tick += (_, _) => RefreshTrayIcons();
        _trayIconRefreshTimer.Start();

        TaskbarService.HideAll();
        _reHideTaskbarTimer.Tick += (_, _) =>
        {
            _reHideTaskbarTimer.Stop();
            TaskbarService.HideAll();
        };
        _keyboardHook.KeyDown += OnGlobalKeyDown;
        _keyboardHook.KeyUp += OnGlobalKeyUp;
        _taskbarWatcher.SuppressRehide = () => _reHideTaskbarTimer.IsEnabled;

        _fullscreenCheckTimer.Tick += (_, _) => UpdateFullscreenSuppression();
        _fullscreenCheckTimer.Start();

        _taskbarEnforceTimer.Tick += (_, _) =>
        {
            if (!_reHideTaskbarTimer.IsEnabled) TaskbarService.HideAll();
        };
        _taskbarEnforceTimer.Start();

        _pinRetryTimer.Tick += (_, _) => TryPinToAllDesktops();

        SourceInitialized += (_, _) =>
        {
            // ShowInTaskbar="False" alone keeps us off the taskbar button strip, but Alt+Tab
            // uses a separate rule (WS_EX_TOOLWINDOW, no WS_EX_APPWINDOW) - without it, this
            // always-running bar shows up as an entry that does nothing useful to switch to.
            HideFromAltTab();

            // The library's first call spins up an internal message-only window that requires
            // an STA thread. Force that init to happen here, on the UI thread, before the apps
            // list refresh (which runs on a background ThreadPool/MTA thread) can ever trigger
            // it there and crash.
            WarmUpVirtualDesktopApi();
            TryPinToAllDesktops();
            TryRegisterAppBar();
        };
        VirtualDesktop.CurrentChanged += OnVirtualDesktopChanged;
    }

    private void RefreshTrayIcons()
    {
        try
        {
            TrayIconList.ItemsSource = _trayIconMirror.GetNotifyIcons();
        }
        catch (Exception ex)
        {
            Log.Write($"Failed to refresh tray icons: {ex.Message}");
        }
    }

    private void TrayIcon_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TrayIconInfo info }) return;
        TrayIconMirrorService.Invoke(info.Element);
    }

    private static void LogStartupDiagnostics()
    {
        var isElevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        Log.Write("OS build: " + Environment.OSVersion.Version);
        Log.Write("Running elevated (admin): " + isElevated);
        Log.Write("VirtualDesktop.IsSupported: " + VirtualDesktop.IsSupported);
        if (isElevated)
        {
            Log.Write("WARNING: running elevated while Explorer normally is not - " +
                      "virtual-desktop COM calls to the shell commonly fail across this privilege boundary.");
        }
    }

    private void WarmUpVirtualDesktopApi()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            VirtualDesktop.IsCurrentVirtualDesktop(hwnd);
            Log.Write("VirtualDesktop API warm-up succeeded.");
        }
        catch (Exception ex)
        {
            Log.Write($"VirtualDesktop API warm-up failed:\n{ex}");
        }
    }

    private void TryPinToAllDesktops()
    {
        // The virtual-desktop pin API is undocumented and version-sensitive; a failure here
        // (elevation mismatch, unsupported Windows build, etc.) must never take the bar down.
        //
        // Right after the window is created, Explorer often hasn't indexed it as an
        // "ApplicationView" yet, so the very first pin attempt commonly fails with
        // TYPE_E_ELEMENTNOTFOUND. Retry a few times with a short delay instead of giving up.
        try
        {
            var pinned = this.Pin();
            if (pinned)
            {
                Log.Write($"Pin to all desktops: OK (attempt {_pinAttempts + 1})");
                _pinRetryTimer.Stop();
                return;
            }
            Log.Write($"Pin to all desktops: returned false (attempt {_pinAttempts + 1})");
        }
        catch (Exception ex)
        {
            Log.Write($"Pin attempt {_pinAttempts + 1} failed:\n{ex.Message}");
        }

        _pinAttempts++;
        if (_pinAttempts >= 10)
        {
            Log.Write("Giving up pinning to all desktops after 10 attempts.");
            _pinRetryTimer.Stop();
            return;
        }

        if (!_pinRetryTimer.IsEnabled) _pinRetryTimer.Start();
    }

    private void HideFromAltTab()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var exStyle = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        exStyle = (exStyle | NativeMethods.WS_EX_TOOLWINDOW) & ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, (IntPtr)exStyle);
    }

    private void TryRegisterAppBar()
    {
        try
        {
            _appBar.Register(this, Height);
            Log.Write("AppBar registered.");
        }
        catch (Exception ex)
        {
            Log.Write($"Failed to register AppBar:\n{ex}");
        }
    }

    private void UpdateFullscreenSuppression()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var isFullscreen = FullscreenDetector.IsForegroundWindowFullscreen(hwnd);
        if (isFullscreen == _hiddenForFullscreen) return;

        _hiddenForFullscreen = isFullscreen;
        Visibility = isFullscreen ? Visibility.Hidden : Visibility.Visible;
    }

    private void OnVirtualDesktopChanged(object? sender, VirtualDesktopChangedEventArgs e)
    {
        // Raised from the VirtualDesktop library's own internal listener thread, not our UI
        // thread - touching the Window (even just its handle) from here throws. Hop to the
        // Dispatcher first.
        Dispatcher.BeginInvoke(() =>
        {
            // Explorer's own virtual-desktop-switch animation re-shows the taskbar on its own;
            // react immediately here rather than waiting for _taskbarEnforceTimer's next tick.
            if (!_reHideTaskbarTimer.IsEnabled) TaskbarService.HideAll();

            _ = RefreshRunningAppsAsync();
        });
    }

    private async Task RefreshRunningAppsAsync()
    {
        if (_isRefreshingApps || !_settings.ShowRunningApps) return;
        _isRefreshingApps = true;

        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var apps = await Task.Run(() => _runningApps.GetCurrentDesktopApps(hwnd));
            Log.Write($"Running apps found: {apps.Count}");
            RunningAppsList.ItemsSource = apps;
        }
        catch (Exception ex)
        {
            Log.Write($"Failed to refresh running apps list:\n{ex}");
        }
        finally
        {
            _isRefreshingApps = false;
        }
    }

    private void RunningApp_Click(object sender, RoutedEventArgs e)
    {
        var hwnd = (IntPtr)((FrameworkElement)sender).Tag;
        if (NativeMethods.IsIconic(hwnd)) NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        NativeMethods.SetForegroundWindow(hwnd);
    }

    private void OnGlobalKeyDown(int vkCode)
    {
        if (vkCode == NativeMethods.VK_LWIN || vkCode == NativeMethods.VK_RWIN)
        {
            _winKeyIsDown = true;
            _otherKeyPressedDuringWin = false;
            return;
        }

        // Any other key going down while Win is held marks this as a combo (Win+1, Win+D, a
        // virtual-desktop-switch hotkey, ...) rather than a bare tap of the Windows key - those
        // don't need our taskbar revealed, and revealing it was exactly what made every Win+1
        // desktop switch flash the taskbar for the full grace period.
        if (_winKeyIsDown) _otherKeyPressedDuringWin = true;
    }

    private void OnGlobalKeyUp(int vkCode)
    {
        if (vkCode != NativeMethods.VK_LWIN && vkCode != NativeMethods.VK_RWIN) return;

        _winKeyIsDown = false;
        if (_otherKeyPressedDuringWin) return;

        // The Windows key was tapped alone: reveal the taskbar so Start menu behaves normally,
        // then hide it again after a short grace period.
        TaskbarService.ShowAll();
        _reHideTaskbarTimer.Stop();
        _reHideTaskbarTimer.Start();
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleVisibility_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string key }) return;

        switch (key)
        {
            case "Clock": _settings.ShowClock = !_settings.ShowClock; break;
            case "ClockSeconds": _settings.ShowClockSeconds = !_settings.ShowClockSeconds; break;
            case "Cpu": _settings.ShowCpu = !_settings.ShowCpu; break;
            case "Memory": _settings.ShowMemory = !_settings.ShowMemory; break;
            case "Network": _settings.ShowNetwork = !_settings.ShowNetwork; break;
            case "Battery": _settings.ShowBattery = !_settings.ShowBattery; break;
            case "DesktopLabel": _settings.ShowDesktopLabel = !_settings.ShowDesktopLabel; break;
            case "RunningApps": _settings.ShowRunningApps = !_settings.ShowRunningApps; break;
        }

        _settings.Save();
        ApplyVisibility();
        UpdateBattery();
    }

    private void ApplyVisibility()
    {
        ClockLabel.Visibility = ToVisibility(_settings.ShowClock);
        CpuSeparator.Visibility = ToVisibility(_settings.ShowCpu);
        CpuGroup.Visibility = ToVisibility(_settings.ShowCpu);
        MemorySeparator.Visibility = ToVisibility(_settings.ShowMemory);
        MemoryGroup.Visibility = ToVisibility(_settings.ShowMemory);
        NetworkSeparator.Visibility = ToVisibility(_settings.ShowNetwork);
        NetworkLabel.Visibility = ToVisibility(_settings.ShowNetwork);
        DesktopLabel.Visibility = ToVisibility(_settings.ShowDesktopLabel);
        RunningAppsList.Visibility = ToVisibility(_settings.ShowRunningApps);

        ShowClockMenuItem.IsChecked = _settings.ShowClock;
        ShowClockSecondsMenuItem.IsChecked = _settings.ShowClockSeconds;
        ShowCpuMenuItem.IsChecked = _settings.ShowCpu;
        ShowMemoryMenuItem.IsChecked = _settings.ShowMemory;
        ShowNetworkMenuItem.IsChecked = _settings.ShowNetwork;
        ShowBatteryMenuItem.IsChecked = _settings.ShowBattery;
        ShowDesktopLabelMenuItem.IsChecked = _settings.ShowDesktopLabel;
        ShowRunningAppsMenuItem.IsChecked = _settings.ShowRunningApps;
    }

    private static Visibility ToVisibility(bool show) => show ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateBattery()
    {
        if (!NativeMethods.GetSystemPowerStatus(out var status))
        {
            _batteryPresent = false;
            BatteryGroup.Visibility = Visibility.Collapsed;
            BatterySeparator.Visibility = Visibility.Collapsed;
            return;
        }

        _batteryPresent = status.BatteryFlag != NativeMethods.BATTERY_FLAG_NO_SYSTEM_BATTERY &&
                          status.BatteryLifePercent != NativeMethods.BATTERY_PERCENTAGE_UNKNOWN;

        var visible = _batteryPresent && _settings.ShowBattery;
        BatteryGroup.Visibility = ToVisibility(visible);
        BatterySeparator.Visibility = ToVisibility(visible);
        if (!visible) return;

        var isCharging = status.ACLineStatus == NativeMethods.AC_LINE_ONLINE;
        BatteryIcon.Text = isCharging ? "🔌" : "🔋";
        BatteryLabel.Text = $"{status.BatteryLifePercent}%";
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        ClockLabel.Text = DateTime.Now.ToString(_settings.ShowClockSeconds ? "HH:mm:ss" : "HH:mm");
        CpuLabel.Text = $"{_cpuCounter.NextValue():F0}%";

        var (_, _, usedPercent) = GetMemoryInfo();
        MemoryLabel.Text = $"{usedPercent:F0}%";

        var (recvPerSec, sentPerSec) = GetNetworkThroughput();
        NetworkLabel.Text = $"↓{FormatSpeed(recvPerSec)}    ↑{FormatSpeed(sentPerSec)}";

        UpdateDesktopLabel();
        UpdateBattery();
    }

    private void UpdateDesktopLabel()
    {
        try
        {
            var desktops = VirtualDesktop.GetDesktops();
            var current = VirtualDesktop.Current;
            var index = Array.FindIndex(desktops, d => d.Id == current.Id);
            DesktopLabel.Text = index >= 0 ? $"Desktop{index + 1}" : string.Empty;
        }
        catch (Exception ex)
        {
            Log.Write($"Failed to update desktop label: {ex.Message}");
        }
    }

    private static (double UsedGb, double TotalGb, double UsedPercent) GetMemoryInfo()
    {
        var status = new MEMORYSTATUSEX();
        GlobalMemoryStatusEx(status);

        const double bytesPerGb = 1024d * 1024 * 1024;
        double totalGb = status.ullTotalPhys / bytesPerGb;
        double freeGb = status.ullAvailPhys / bytesPerGb;
        double usedGb = totalGb - freeGb;

        return (usedGb, totalGb, status.dwMemoryLoad);
    }

    private (double RecvBytesPerSec, double SentBytesPerSec) GetNetworkThroughput()
    {
        if (DateTime.Now - _lastNetRefresh > TimeSpan.FromSeconds(30))
        {
            RefreshNetworkCounters();
        }

        double recv = 0, sent = 0;
        foreach (var (recvCounter, sentCounter) in _netCounters.Values)
        {
            recv += recvCounter.NextValue();
            sent += sentCounter.NextValue();
        }

        return (recv, sent);
    }

    private void RefreshNetworkCounters()
    {
        _lastNetRefresh = DateTime.Now;

        var category = new PerformanceCounterCategory("Network Interface");
        var currentInstances = category.GetInstanceNames();

        foreach (var stale in _netCounters.Keys.Except(currentInstances).ToList())
        {
            _netCounters[stale].Recv.Dispose();
            _netCounters[stale].Sent.Dispose();
            _netCounters.Remove(stale);
        }

        foreach (var instance in currentInstances)
        {
            if (_netCounters.ContainsKey(instance)) continue;

            var recvCounter = new PerformanceCounter("Network Interface", "Bytes Received/sec", instance);
            var sentCounter = new PerformanceCounter("Network Interface", "Bytes Sent/sec", instance);
            recvCounter.NextValue();
            sentCounter.NextValue();
            _netCounters[instance] = (recvCounter, sentCounter);
        }
    }

    private static string FormatSpeed(double bytesPerSec)
    {
        double kbPerSec = bytesPerSec / 1024;
        return kbPerSec >= 1024
            ? $"{kbPerSec / 1024:F1}MB/s"
            : $"{kbPerSec:F0}KB/s";
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _appsRefreshTimer.Stop();
        _trayIconRefreshTimer.Stop();
        _pinRetryTimer.Stop();
        _reHideTaskbarTimer.Stop();
        _fullscreenCheckTimer.Stop();
        _taskbarEnforceTimer.Stop();
        _keyboardHook.Dispose();
        _taskbarWatcher.Dispose();
        VirtualDesktop.CurrentChanged -= OnVirtualDesktopChanged;
        _appBar.Unregister();
        TaskbarService.ShowAll();

        _cpuCounter.Dispose();
        foreach (var (recv, sent) in _netCounters.Values)
        {
            recv.Dispose();
            sent.Dispose();
        }
        base.OnClosed(e);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private class MEMORYSTATUSEX
    {
        public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}
