using System.Runtime.InteropServices;
using System.Text;

namespace OriginalTaskbar;

// TaskbarService.HideAll() only hides the taskbar at the moment it's called; Explorer is free
// to show it again on its own afterwards (shortly after this process starts, when switching
// virtual desktops, or - if the user has Windows' own "auto-hide the taskbar" setting on - when
// the mouse touches the screen edge). Polling for that is either too slow (visible flicker) or
// wasteful. Instead, this listens for the EVENT_OBJECT_SHOW accessibility event on Explorer's
// process and re-hides the taskbar the instant it is shown, unless told to stand down.
internal sealed class TaskbarWatcher : IDisposable
{
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const int OBJID_WINDOW = 0;

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    // Kept alive for the lifetime of the hook - otherwise the GC can collect the delegate out
    // from under the unmanaged callback, the same hazard as LowLevelKeyboardHook's _proc field.
    private readonly WinEventDelegate _proc;
    private IntPtr _hookHandle = IntPtr.Zero;

    // While this returns true, an Explorer-initiated show is left alone (e.g. we just showed
    // the taskbar ourselves for the Windows-key grace period).
    public Func<bool>? SuppressRehide { get; set; }

    public TaskbarWatcher()
    {
        // Narrow the hook to Explorer's process so it isn't woken up for every window-show
        // event system-wide.
        var explorerPid = 0u;
        var primary = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (primary != IntPtr.Zero)
        {
            NativeMethods.GetWindowThreadProcessId(primary, out explorerPid);
        }

        _proc = OnWinEvent;
        _hookHandle = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _proc,
            explorerPid, 0, WINEVENT_OUTOFCONTEXT);
        Log.Write($"TaskbarWatcher installed (explorerPid={explorerPid}, hook={_hookHandle != IntPtr.Zero}).");
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild,
        uint dwEventThread, uint dwmsEventTime)
    {
        if (idObject != OBJID_WINDOW || hwnd == IntPtr.Zero) return;
        if (SuppressRehide?.Invoke() == true) return;

        var sb = new StringBuilder(256);
        NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
        var className = sb.ToString();
        if (className is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
        }
    }

    public void Dispose()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWinEvent(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }
}
