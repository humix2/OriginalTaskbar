using System.Windows;
using System.Windows.Threading;

namespace OriginalTaskbar;

public partial class App : Application
{
    public App()
    {
        // The taskbar belongs to Explorer, a separate process - if this app dies
        // unexpectedly the hidden state would otherwise persist. Always restore it.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TaskbarService.ShowAll();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => TaskbarService.ShowAll();
        DispatcherUnhandledException += App_DispatcherUnhandledException;
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // This is an always-on utility bar: a stray exception (e.g. from the undocumented,
        // version-sensitive virtual-desktop APIs) should never be allowed to take it down.
        Log.Write($"Unhandled exception suppressed: {e.Exception}");
        TaskbarService.ShowAll();
        e.Handled = true;
    }
}
