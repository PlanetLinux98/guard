using System;
using System.IO;

namespace GuardWui3.Services;

// Optional diagnostics for the failure paths the app deliberately swallows
// (network checks, task registration, toast raising). Off unless a debug.flag
// file sits next to the exe or GUARD_DEBUG=1 is set, so normal runs write
// nothing. Crash logging is the exception: an unhandled exception always
// writes Logs\crash_last.log, because that is exactly when no one had a
// chance to opt in.
public static class DebugLog
{
    private static readonly object Gate = new();
    private static readonly bool Enabled = ComputeEnabled();

    private static bool ComputeEnabled()
    {
        try
        {
            return File.Exists(Path.Combine(GuardPaths.BaseDir, "debug.flag"))
                || Environment.GetEnvironmentVariable("GUARD_DEBUG") == "1";
        }
        catch { return false; }
    }

    public static void Log(string area, string message)
    {
        if (!Enabled) return;
        try
        {
            string path = Path.Combine(GuardPaths.BaseDir, @"Logs\debug_last.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            lock (Gate)
                File.AppendAllText(path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  [" + area + "] " + message + Environment.NewLine);
        }
        catch { }
    }

    public static void Log(string area, string message, Exception ex)
        => Log(area, message + " - " + ex.GetType().Name + ": " + ex.Message);

    public static void Crash(Exception? ex, string source)
    {
        try
        {
            string path = Path.Combine(GuardPaths.BaseDir, @"Logs\crash_last.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            lock (Gate)
                File.WriteAllText(path,
                    "GUARD " + GuardPaths.AppVersion + " crashed " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    + " (" + source + ")" + Environment.NewLine + Environment.NewLine
                    + (ex?.ToString() ?? "(no exception object)") + Environment.NewLine);
        }
        catch { }
    }
}
