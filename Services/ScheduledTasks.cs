using System;
using System.Collections.Generic;
using System.Linq;
using GuardWui3.Models;

namespace GuardWui3.Services;

public static class ScheduledTasks
{
    // Returns a problem message, or null on success.
    public static string? UpdateFileTask(Settings cfg)
    {
        string arg = "/c \"" + GuardPaths.ScriptPath + "\" auto";
        string ps =
            "$A = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument '" + PsQuote(arg) + "';" +
            "$T = New-ScheduledTaskTrigger " + TriggerArgs(cfg) + ";" +
            "$S = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries;" +
            "Register-ScheduledTask -TaskName '" + GuardPaths.FileTaskName + "' -Action $A -Trigger $T -Settings $S -Force | Out-Null";
        var err = ProcessRunner.RunPowerShell(ps);
        // Drop the pre-0.3 task name so an upgrader isn't left with two triggers.
        RemoveTask(GuardPaths.LegacyFileTaskName);
        return err;
    }

    // All seven days (or an empty list, defensively) -> a plain daily trigger;
    // any subset -> a weekly trigger limited to those days. The empty fallback
    // matters because the manual "Create/Update Task" button can reach here with
    // no days selected (e.g. the schedule is off); a -DaysOfWeek with no days is
    // invalid, so we never emit one.
    private static string TriggerArgs(Settings cfg)
    {
        var days = cfg.ScheduleDays.Distinct().ToList();
        if (days.Count == 0 || days.Count >= 7)
            return "-Daily -At " + cfg.ScheduleTime;
        return "-Weekly -DaysOfWeek " + string.Join(",", days) + " -At " + cfg.ScheduleTime;
    }

    public static void RemoveTask(string name)
    {
        string ps = "Unregister-ScheduledTask -TaskName '" + name + "' -Confirm:$false -ErrorAction SilentlyContinue";
        ProcessRunner.RunPowerShell(ps);
    }

    // Removes both the current and the legacy task names, so disabling the
    // schedule or hitting Remove Task reliably clears any GUARD backup task,
    // including one registered under the old name by a pre-0.3 build.
    public static void RemoveAllTasks()
    {
        RemoveTask(GuardPaths.FileTaskName);
        RemoveTask(GuardPaths.LegacyFileTaskName);
    }

    public static string? QueryNextRun(string name)
    {
        try
        {
            string ps = "try { (Get-ScheduledTaskInfo -TaskName '" + name +
                "' -ErrorAction Stop).NextRunTime.ToString('yyyy-MM-dd HH:mm') } catch { '' }";
            string outp = ProcessRunner.RunPowerShellCapture(ps).Trim();
            return string.IsNullOrEmpty(outp) ? null : outp;
        }
        catch { return null; }
    }

    private static string PsQuote(string s) => s.Replace("'", "''");
}
