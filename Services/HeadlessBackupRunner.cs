using System;
using System.Collections.Generic;
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
    // Unlike ProcessRunner's 5-minute CaptureTimeout (sized for quick
    // capture-and-return calls), a real backup can legitimately run for hours
    // on a large first backup over slow USB or network media; this only needs
    // to catch a run that is actually stuck (e.g. robocopy hung on a flaky
    // network share) rather than one that is merely slow.
    private static readonly TimeSpan RunTimeout = TimeSpan.FromHours(12);

    public static int Run(string mode)
    {
        mode = (mode ?? "").Trim().ToLowerInvariant();
        if (mode is not ("auto" or "onconnect")) return 64;
        bool scheduled = mode == "auto";
        var prefs = AppPrefsStore.Load();

        if (!File.Exists(GuardPaths.ScriptPath))
        {
            DebugLog.Log("scheduled-run", "guard-backup.cmd not found; nothing to run");
            // Unlike "destination unreachable" (normal life for on-connect, see
            // the switch below), a missing script means Save Settings was never
            // run or the file was deleted/quarantined - a setup failure, not a
            // quiet skip, so it must surface on either trigger or an on-connect-
            // only user never learns their backups have silently stopped.
            if (prefs.NotifyFailure)
                ToastNotifier.Show("GUARD backup",
                    "The scheduled backup could not run: the backup script was not found. Open GUARD and click Save Settings.");
            return 2;
        }

        // Measured BEFORE the run, reported after it.
        //
        // This has to happen first because Mirror mode makes the backup match
        // the source: a run whose source has gone empty PURGES the destination,
        // so a check made afterwards finds both sides empty and can never fire.
        // That killed this warning in precisely the configuration where it
        // matters most - the nightly run that silently deleted the only copy.
        //
        // Cost is small enough to pay on the every-15-minutes on-connect fire
        // too: each source walk stops at its first file, and an unreachable
        // destination makes CheckSources return immediately, which is the same
        // one probe the script's own gate already does.
        var preRun = CheckBeforeRun(prefs);

        int code;
        try
        {
            var psi = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = GuardPaths.DataDir,
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(GuardPaths.ScriptPath);
            psi.ArgumentList.Add(mode);
            using var p = Process.Start(psi)!;
            try
            {
                ProcessRunner.WaitOrKill(p, "The scheduled backup", RunTimeout);
                code = p.ExitCode;
            }
            catch (TimeoutException ex)
            {
                // A hung run (stuck retrying a flaky network share, say) would
                // otherwise block every later scheduled/on-connect fire with no
                // toast telling the user backups have stalled; killed and
                // reported the same as any other setup failure (2), since
                // nothing in the 0/1/3 outcomes fits "we had to kill it".
                DebugLog.Log("scheduled-run", ex.Message);
                code = 2;
            }
        }
        catch (Exception ex)
        {
            DebugLog.Log("scheduled-run", "could not launch the backup script", ex);
            code = 2;
        }

        // A clean run whose sources have gone empty is the one outcome that
        // looks healthy and is not: robocopy copies an empty folder without
        // complaint, so the log says FINISHED OK and nothing else would ever
        // mention it. Reported under the FAILURE preference rather than the
        // success one - success toasts are off by default, which is exactly how
        // a backup holding none of the user's documents would stay invisible.
        //
        // Only when the DESTINATION still holds files from those folders (see
        // SaveValidation.CheckSources): a folder that was always empty, which on
        // a stock Windows profile means Contacts and often Music, must never
        // produce a nightly notification.
        string? vanishedNote = code == 0 ? DescribeVanished(preRun) : null;
        if (vanishedNote != null && prefs.NotifyFailure)
            ToastNotifier.Show("GUARD backup", vanishedNote);

        switch (code)
        {
            // Suppressed when the warning above already fired: two toasts for
            // one run is noise, and "finished successfully" directly after
            // "files have gone missing" reads as a contradiction. Not an early
            // return, which would also have swallowed the success toast for
            // anyone who wanted successes but not failures.
            case 0 when prefs.NotifySuccess && vanishedNote == null:
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

    // What the sources looked like before the run. MirrorPurges records whether
    // this run was going to delete the backup's copies to match an empty source,
    // which is only true for Mirror WITHOUT versioning - a versioned run mirrors
    // into a fresh dated folder, so the previous days' copies survive until the
    // prune and the alarming wording would be false.
    private sealed record PreRunHealth(List<SaveValidation.VanishedSource> Vanished, bool MirrorPurges);

    // Re-reads the saved configuration rather than taking it from the run: the
    // script is standalone and reports no per-folder counts, so the sources are
    // probed directly. Null when there is nothing to say. Best effort - a
    // failure here must never turn a finished backup into a reported problem.
    private static PreRunHealth? CheckBeforeRun(AppPrefs prefs)
    {
        try
        {
            var cfg = SettingsStore.Load();
            // Reachability first. Without a destination there is no history to
            // compare against, so the result is provably empty - and walking
            // every source tree to establish that would cost a full sweep on
            // each of the on-connect task's 96 fires a day, with the backup
            // drive unplugged, on battery.
            if (!SaveValidation.DestinationReachable(cfg)) return null;
            var health = SaveValidation.CheckSources(cfg, SaveValidation.SourceCheckCap);
            // Honours the same acknowledgements the window does, or a folder the
            // user has already said is deliberately empty would still toast
            // every night - the exact nagging the acknowledgement exists to end.
            var report = SaveValidation.Unacknowledged(health.Vanished, prefs.AcknowledgedEmpty);
            if (report.Count == 0) return null;
            return new PreRunHealth(report, cfg.Mode == "Mirror" && !cfg.Versioned);
        }
        catch (Exception ex)
        {
            DebugLog.Log("scheduled-run", "source health check failed", ex);
            return null;
        }
    }

    private static string? DescribeVanished(PreRunHealth? pre)
    {
        if (pre is null || pre.Vanished.Count == 0) return null;
        string what = pre.Vanished.Count == 1
            ? "the folder " + Environment.ExpandEnvironmentVariables(pre.Vanished[0].Source)
            : pre.Vanished.Count + " of the folders being backed up";
        // Past tense for the purge: by the time this is spoken the run has
        // already matched the backup to the empty source.
        return pre.MirrorPurges
            ? "The backup finished, but " + what + " had nothing to copy, so mirroring has now removed"
              + " its backup copies as well. Open GUARD to check."
            : "The backup finished, but " + what + " had nothing to copy while the backup still holds"
              + " files from it. Open GUARD to check.";
    }
}
