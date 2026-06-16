using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GuardWui3.Models;

namespace GuardWui3.Services;

public static class ScheduledTasks
{
    public sealed record ApplyResult(string? Error, string? NextRun);

    // Applies the complete scheduled-task state for a save in ONE PowerShell
    // invocation: register-or-remove the timed task, drop the legacy pre-0.3
    // name, register-or-remove the on-connect task, and query the next run.
    // Batching matters: each powershell.exe start pays a multi-second
    // ScheduledTasks-module import, and the per-operation methods below cost
    // that 3-4 times per save. Errors are routed to stdout behind ERRFILE/
    // ERRCONN markers (stderr would mix PowerShell's own noise in), the next
    // run behind NEXT.
    public static ApplyResult ApplyAll(Settings cfg)
    {
        var sb = new StringBuilder();
        if (cfg.ScheduleEnabled)
        {
            string arg = "/c \"" + GuardPaths.ScriptPath + "\" auto";
            sb.Append("try {")
              .Append("$A = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument '" + PsQuote(arg) + "';")
              .Append("$T = New-ScheduledTaskTrigger " + TriggerArgs(cfg) + ";")
              .Append("$S = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries;")
              .Append("Register-ScheduledTask -TaskName '" + GuardPaths.FileTaskName + "' -Action $A -Trigger $T -Settings $S -Force -ErrorAction Stop | Out-Null")
              .Append("} catch { Write-Output ('ERRFILE ' + $_.Exception.Message) };");
        }
        else
        {
            sb.Append("Unregister-ScheduledTask -TaskName '" + GuardPaths.FileTaskName + "' -Confirm:$false -ErrorAction SilentlyContinue;");
        }
        sb.Append("Unregister-ScheduledTask -TaskName '" + GuardPaths.LegacyFileTaskName + "' -Confirm:$false -ErrorAction SilentlyContinue;");
        if (cfg.TriggerOnConnect)
        {
            string arg = "/c \"" + GuardPaths.ScriptPath + "\" onconnect";
            sb.Append("try {")
              .Append("$A2 = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument '" + PsQuote(arg) + "';")
              .Append("$T2 = New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Minutes 15) -RepetitionDuration (New-TimeSpan -Days 3650);")
              .Append("$L2 = New-ScheduledTaskTrigger -AtLogOn -User ([Security.Principal.WindowsIdentity]::GetCurrent().Name);")
              .Append("$S2 = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries;")
              .Append("Register-ScheduledTask -TaskName '" + GuardPaths.OnConnectTaskName + "' -Action $A2 -Trigger $T2,$L2 -Settings $S2 -Force -ErrorAction Stop | Out-Null")
              .Append("} catch { Write-Output ('ERRCONN ' + $_.Exception.Message) };");
        }
        else
        {
            sb.Append("Unregister-ScheduledTask -TaskName '" + GuardPaths.OnConnectTaskName + "' -Confirm:$false -ErrorAction SilentlyContinue;");
        }
        sb.Append("try { Write-Output ('NEXT ' + (Get-ScheduledTaskInfo -TaskName '" + GuardPaths.FileTaskName +
                  "' -ErrorAction Stop).NextRunTime.ToString('yyyy-MM-dd HH:mm')) } catch { }");

        string output;
        try { output = ProcessRunner.RunPowerShellCapture(sb.ToString()); }
        catch (Exception ex) { return new ApplyResult(ex.Message, null); }

        string? fileErr = null, connErr = null, next = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("ERRFILE ")) fileErr = "Scheduled backup task: " + line.Substring(8);
            else if (line.StartsWith("ERRCONN ")) connErr = "On-connect task: " + line.Substring(8);
            else if (line.StartsWith("NEXT ")) next = line.Substring(5);
        }
        string? err = fileErr != null && connErr != null ? fileErr + "\n\n" + connErr : fileErr ?? connErr;
        return new ApplyResult(err, next);
    }

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

    // "Run when the destination becomes available" detection. Honest options:
    //
    //  1. Event-triggered task on device arrival (DriverFrameworks-UserMode
    //     event 2003, or WPD/PnP events). Rejected: the operational log that
    //     carries those events is disabled by default, the event IDs and
    //     payloads shift between Windows versions, the subscription keys off
    //     device instance IDs rather than the drive letter the user typed, and
    //     a network share becoming reachable raises no device event at all.
    //
    //  2. A resident watcher (service/tray app). Rejected: GUARD is a portable
    //     generator; backups must work with the app closed and nothing installed.
    //
    //  3. (chosen) A second scheduled task that runs the script with an
    //     "onconnect" argument every 15 minutes and at logon. The check is a
    //     single `if exist "%DEST%\"` plus a date-stamp comparison, so when the
    //     destination is absent or today's backup already succeeded it exits in
    //     milliseconds with no log churn. This is gentle Task-Scheduler-level
    //     polling, covers external drives and network shares identically, and
    //     keys off the same destination path the rest of GUARD uses.
    //
    // The 15-minute repetition rides on a -Once trigger, the standard Windows
    // PowerShell 5.1 idiom (5.1 has no -RepetitionInterval on -Daily, and
    // requires an explicit -RepetitionDuration; 10 years is effectively
    // indefinite). The -AtLogOn trigger catches the common "drive was already
    // plugged in when the PC started" case without waiting out the interval.
    // It is scoped to the current user (-User ...GetCurrent().Name): a bare
    // -AtLogOn is an "any user" logon trigger, which Task Scheduler only lets
    // an administrator register, so saving without elevation failed with
    // "Access is denied". Per-user registers fine non-elevated.
    public static string? UpdateOnConnectTask()
    {
        string arg = "/c \"" + GuardPaths.ScriptPath + "\" onconnect";
        string ps =
            "$A = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument '" + PsQuote(arg) + "';" +
            "$T = New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Minutes 15) -RepetitionDuration (New-TimeSpan -Days 3650);" +
            "$L = New-ScheduledTaskTrigger -AtLogOn -User ([Security.Principal.WindowsIdentity]::GetCurrent().Name);" +
            "$S = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries;" +
            "Register-ScheduledTask -TaskName '" + GuardPaths.OnConnectTaskName + "' -Action $A -Trigger $T,$L -Settings $S -Force | Out-Null";
        return ProcessRunner.RunPowerShell(ps);
    }

    public static void RemoveTask(string name)
    {
        string ps = "Unregister-ScheduledTask -TaskName '" + name + "' -Confirm:$false -ErrorAction SilentlyContinue";
        ProcessRunner.RunPowerShell(ps);
    }

    // Removes the current and legacy schedule task names, so disabling the
    // schedule reliably clears the timed backup task, including one registered
    // under the old name by a pre-0.3 build. Deliberately leaves the on-connect
    // task alone: it is an independent trigger with its own setting.
    public static void RemoveScheduleTasks()
    {
        RemoveTask(GuardPaths.FileTaskName);
        RemoveTask(GuardPaths.LegacyFileTaskName);
    }

    public static void RemoveAllTasks()
    {
        RemoveScheduleTasks();
        RemoveTask(GuardPaths.OnConnectTaskName);
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
