using System.IO;

namespace OriginalTaskbar;

// WinExe apps have no console attached, so Console.WriteLine goes nowhere - write
// diagnostics to a file next to the executable instead.
internal static class Log
{
    // Flip to true when diagnosing an issue again; noisy enough that it's not worth
    // leaving on once things are working.
    private static readonly bool Enabled = false;

    private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "diagnostics.log");
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        if (!Enabled) return;

        try
        {
            lock (Gate)
            {
                File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never be the thing that crashes the app.
        }
    }
}
