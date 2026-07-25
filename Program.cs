using System;
using System.Runtime.InteropServices;
using GuardWui3.Services;

namespace GuardWui3;

// Replaces the XAML-generated Main (DISABLE_XAML_GENERATED_MAIN in the csproj).
// Two jobs before any XAML loads:
//  - "--run-backup auto|onconnect": the scheduled tasks launch GUARD.exe itself
//    instead of cmd.exe. A GUI-subsystem exe never opens a console, so scheduled
//    and on-connect runs are fully hidden (the old cmd.exe action flashed a
//    console every 15 minutes for the on-connect check). The helper runs the
//    generated script hidden, raises the outcome toast, and exits - no XAML,
//    no window, no WinUI init.
//  - Single instance per install folder: a second GUARD.exe from the same
//    folder would race the settings ini and the staged updater, so it just
//    activates the first window and exits. Keyed to BaseDir, so two separate
//    portable copies can still run side by side.
public static class Program
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectory(string lpPathName);

    [STAThread]
    static int Main(string[] args)
    {
        // Self-contained WindowsAppSDK loads Microsoft.WindowsAppRuntime.dll (and
        // friends) via a bare-name LoadLibrary that searches "the directory the
        // application loaded from" - which Windows resolves from the invoked path,
        // not the real one, when launched through a symlink (winget's portable
        // packages run through one in %LOCALAPPDATA%\Microsoft\WinGet\Links). That
        // crashes with DllNotFoundException before any managed handler below gets a
        // chance to run. SetDllDirectory overrides that search step outright, so it
        // must come first - GuardPaths.BaseDir already resolves the symlink chain.
        SetDllDirectory(GuardPaths.BaseDir);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            DebugLog.Crash(e.ExceptionObject as Exception, "AppDomain");

        // Before anything reads a setting or a script: under a winget install
        // the working files live outside the package folder (see
        // GuardPaths.DataDir), and both the window and the headless run below
        // must see them in the same place.
        GuardPaths.MigrateWorkingFiles();

        if (args.Length >= 2 && args[0] == "--run-backup")
            return HeadlessBackupRunner.Run(args[1]);

        // initiallyOwned avoids a create/acquire race between two launches. The
        // mutex must live for the whole run: Application.Start blocks here until
        // the window closes, so the using scope covers it.
        using var mutex = new System.Threading.Mutex(
            initiallyOwned: true, SingleInstance.MutexName, out bool createdNew);
        if (!createdNew)
        {
            SingleInstance.ActivateExisting();
            return 0;
        }

        global::WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(_ =>
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
        return 0;
    }
}
