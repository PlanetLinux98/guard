using System;
using System.IO;
using System.Runtime.InteropServices;

namespace GuardWui3.Services;

// Second-launch handling for the GUI app. The mutex name is keyed to the
// install folder (InstallId), so two different portable copies may each run,
// but a second launch of the SAME copy - which would race backup-settings.ini,
// guard-prefs.ini and the staged updater - activates the first window instead.
public static class SingleInstance
{
    // Local\ (per session), not Global\: another user's session running the
    // same folder is isolated anyway (its own prefs writes are the same risk,
    // but blocking across sessions would confuse fast user switching).
    public static string MutexName => @"Local\GUARD-" + GuardPaths.InstallId;

    // Find the existing instance's main window and bring it to the front.
    // Matched by process image path, not window title: "GUARD" is too generic
    // a title to trust, and another install's window must not be activated.
    public static unsafe void ActivateExisting()
    {
        try
        {
            _foundHwnd = 0;
            delegate* unmanaged[Stdcall]<nint, nint, int> cb = &EnumProc;
            EnumWindows((nint)cb, 0);
            if (_foundHwnd == 0) return;
            ShowWindow(_foundHwnd, SW_RESTORE);
            SetForegroundWindow(_foundHwnd);
        }
        catch { }
    }

    private static nint _foundHwnd;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int EnumProc(nint hwnd, nint lParam)
    {
        if (!IsWindowVisible(hwnd)) return 1;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || pid == Environment.ProcessId) return 1;
        if (!IsSameExe(pid)) return 1;
        _foundHwnd = hwnd;
        return 0; // stop enumerating
    }

    private static bool IsSameExe(uint pid)
    {
        nint h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == 0) return false;
        try
        {
            Span<char> buf = stackalloc char[1024];
            uint len = (uint)buf.Length;
            unsafe
            {
                fixed (char* p = buf)
                {
                    if (!QueryFullProcessImageNameW(h, 0, p, ref len)) return false;
                }
            }
            string path = new string(buf[..(int)len]);
            // Compare against the RESOLVED exe, not Environment.ProcessPath:
            // launched through winget's portable symlink, ProcessPath is the alias
            // in %LOCALAPPDATA%\Microsoft\WinGet\Links, so a same-exe match would
            // silently fail and the existing window would never get activated.
            // QueryFullProcessImageName returns the resolved path (verified), so
            // the two are directly comparable.
            return string.Equals(path, GuardPaths.ExePath, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
        finally { CloseHandle(h); }
    }

    private const int SW_RESTORE = 9;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("user32.dll")] private static extern int EnumWindows(nint proc, nint lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern unsafe bool QueryFullProcessImageNameW(nint process, uint flags, char* buffer, ref uint size);
}
