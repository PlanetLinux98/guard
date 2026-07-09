using System;
using System.Diagnostics;
using System.IO;
using GuardWui3.Models;

namespace GuardWui3.Services;

// Runs guard-backup.cmd for the scheduled tasks with no console window (the
// task action is GUARD.exe --run-backup <mode>; a GUI-subsystem exe never
// opens a console). Maps the script's exit code to an outcome toast per the
// notification preferences, then exits with the same code so Task Scheduler's
// history stays meaningful.
//
// Exit-code contract with the generated script (see BackupScript):
//   0 = backup ran and finished cleanly
//   1 = backup ran but some folders reported errors
//   2 = backup could not run (destination unreachable, or setup failure)
//   3 = nothing to do (on-connect: destination absent or already done today;
//       or another backup already holds the run lock)
public static class HeadlessBackupRunner
{
    public static int Run(string mode)
    {
        mode = (mode ?? "").Trim().ToLowerInvariant();
        if (mode is not ("auto" or "onconnect")) return 64;
        bool scheduled = mode == "auto";
        var prefs = AppPrefsStore.Load();

        if (!File.Exists(GuardPaths.ScriptPath))
        {
            DebugLog.Log("scheduled-run", "guard-backup.cmd not found; nothing to run");
            if (scheduled && prefs.NotifyFailure)
                ToastNotifier.Show("GUARD backup",
                    "The scheduled backup could not run: the backup script was not found. Open GUARD and click Save Settings.");
            return 2;
        }

        int code;
        try
        {
            var psi = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = GuardPaths.BaseDir,
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(GuardPaths.ScriptPath);
            psi.ArgumentList.Add(mode);
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            code = p.ExitCode;
        }
        catch (Exception ex)
        {
            DebugLog.Log("scheduled-run", "could not launch the backup script", ex);
            code = 2;
        }

        switch (code)
        {
            case 0 when prefs.NotifySuccess:
                ToastNotifier.Show("GUARD backup", scheduled
                    ? "The scheduled backup finished successfully."
                    : "Today's backup finished successfully (destination connected).");
                break;
            case 1 when prefs.NotifyFailure:
                ToastNotifier.Show("GUARD backup",
                    "The backup finished, but some folders reported errors. Open GUARD and check the last log.");
                break;
            // On-connect exiting 2 is unexpected (its gate exits 3 when the
            // destination is absent), so only the scheduled run toasts here;
            // an unplugged drive is normal life for on-connect.
            case 2 when scheduled && prefs.NotifyFailure:
                ToastNotifier.Show("GUARD backup",
                    "The scheduled backup could not run - the destination was not reachable. It will be retried at the next scheduled time.");
                break;
        }
        return code;
    }
}
