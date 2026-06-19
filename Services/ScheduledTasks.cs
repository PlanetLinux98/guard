using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GuardWui3.Models;

namespace GuardWui3.Services;

public static class ScheduledTasks
{
    public sealed record ApplyResult(string? Error, string? NextRun);

    // Applies the whole scheduled-task state for a save in ONE PowerShell call:
    // register-or-remove the timed task, drop the legacy pre-0.3 name,
    // register-or-remove the on-connect task, query the next run. Batching
    // matters: each powershell.exe start pays a multi-second ScheduledTasks-module
    // import, which the per-operation methods below cost 3-4x per save. Errors
    // route to stdout behind ERRFILE/ERRCONN markers (stderr would mix in
    // PowerShell's noise), the next run behind NEXT.
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

    // All seven days (or empty, defensively) -> daily trigger; any subset ->
    // weekly trigger on those days. The empty fallback matters: the manual
    // "Create/Update Task" button can reach here with no days selected (schedule
    // off), and -DaysOfWeek with no days is invalid, so we never emit one.
    private static string TriggerArgs(Settings cfg)
    {
        var days = cfg.ScheduleDays.Distinct().ToList();
        if (days.Count == 0 || days.Count >= 7)
            return "-Daily -At " + cfg.ScheduleTime;
        return "-Weekly -DaysOfWeek " + string.Join(",", days) + " -At " + cfg.ScheduleTime;
    }

    // "Run when the destination becomes available" detection. Options weighed:
    //
    //  1. Event-triggered task on device arrival (DriverFrameworks-UserMode event
    //     2003, or WPD/PnP events). Rejected: its operational log is disabled by
    //     default, event IDs/payloads shift between Windows versions, the
    //     subscription keys off device instance IDs not the drive letter the user
    //     typed, and a network share becoming reachable raises no device event.
    //
    //  2. A resident watcher (service/tray app). Rejected: GUARD is a portable
    //     generator; backups must work with the app closed and nothing installed.
    //
    //  3. (chosen) A second scheduled task running the script with "onconnect"
    //     every 15 minutes and at logon. The check is one `if exist "%DEST%\"`
    //     plus a date-stamp compare, so an absent destination or already-done
    //     backup exits in milliseconds with no log churn. Gentle Task-Scheduler
    //     polling, covers drives and shares identically, keys off GUARD's usual
    //     dest path.
    //
    // The 15-minute repetition rides a -Once trigger, the PowerShell 5.1 idiom
    // (5.1 has no -RepetitionInterval on -Daily and needs an explicit
    // -RepetitionDuration; 10 years is effectively indefinite). The -AtLogOn
    // trigger catches "drive was already plugged in at startup" without waiting
    // out the interval. Scoped to the current user (-User ...GetCurrent().Name):
    // a bare -AtLogOn is an "any user" trigger, which only an admin can register,
    // so saving non-elevated failed with "Access is denied". Per-user registers
    // fine non-elevated.
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
    // schedule reliably clears the timed task, including one a pre-0.3 build
    // registered under the old name. Leaves the on-connect task alone:
    // independent trigger with its own setting.
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
