using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace OriginalTaskbar;

// Registers the window as a Windows "AppBar" (the same mechanism the real taskbar uses) so it
// reserves a strip of the work area - maximized windows and the desktop stop before it instead
// of being covered by it.
internal sealed class AppBarService
{
    private const int ABM_NEW = 0x00000000;
    private const int ABM_REMOVE = 0x00000001;
    private const int ABM_QUERYPOS = 0x00000002;
    private const int ABM_SETPOS = 0x00000003;
    private const int ABE_TOP = 1;
    private const int ABN_POSCHANGED = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")]
    private static extern uint SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern uint RegisterWindowMessage(string lpString);

    private Window? _window;
    private IntPtr _hwnd;
    private HwndSource? _source;
    private int _callbackMessageId;
    private double _heightDip;

    public void Register(Window window, double heightDip)
    {
        _window = window;
        _heightDip = heightDip;
        _hwnd = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
        _callbackMessageId = (int)RegisterWindowMessage("OriginalTaskbar_AppBarNotify");

        var abd = new APPBARDATA
        {
            cbSize = Marshal.SizeOf<APPBARDATA>(),
            hWnd = _hwnd,
            uCallbackMessage = (uint)_callbackMessageId
        };
        SHAppBarMessage(ABM_NEW, ref abd);

        ApplyPosition();
    }

    public void Unregister()
    {
        if (_hwnd == IntPtr.Zero) return;

        var abd = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd };
        SHAppBarMessage(ABM_REMOVE, ref abd);

        _source?.RemoveHook(WndProc);
        _hwnd = IntPtr.Zero;
    }

    private void ApplyPosition()
    {
        if (_window is null || _hwnd == IntPtr.Zero) return;

        var dpiScale = (_source ?? HwndSource.FromHwnd(_hwnd))?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        var heightPx = (int)Math.Round(_heightDip * dpiScale);
        var screenWidthPx = (int)Math.Round(SystemParameters.PrimaryScreenWidth * dpiScale);

        var abd = new APPBARDATA
        {
            cbSize = Marshal.SizeOf<APPBARDATA>(),
            hWnd = _hwnd,
            uEdge = ABE_TOP,
            rc = new RECT { Left = 0, Top = 0, Right = screenWidthPx, Bottom = heightPx }
        };

        SHAppBarMessage(ABM_QUERYPOS, ref abd);
        abd.rc.Bottom = abd.rc.Top + heightPx; // keep our fixed height regardless of the suggestion

        SHAppBarMessage(ABM_SETPOS, ref abd);

        _window.Left = abd.rc.Left / dpiScale;
        _window.Top = abd.rc.Top / dpiScale;
        _window.Width = (abd.rc.Right - abd.rc.Left) / dpiScale;
        _window.Height = (abd.rc.Bottom - abd.rc.Top) / dpiScale;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _callbackMessageId && wParam.ToInt32() == ABN_POSCHANGED)
        {
            ApplyPosition();
        }
        return IntPtr.Zero;
    }
}
