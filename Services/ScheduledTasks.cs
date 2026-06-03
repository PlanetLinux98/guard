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
            "$T = New-ScheduledTaskTrigger -Daily -At " + cfg.ScheduleTime + ";" +
            "$S = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries;" +
            "Register-ScheduledTask -TaskName '" + GuardPaths.FileTaskName + "' -Action $A -Trigger $T -Settings $S -Force | Out-Null";
        return ProcessRunner.RunPowerShell(ps);
    }

    public static void RemoveTask(string name)
    {
        string ps = "Unregister-ScheduledTask -TaskName '" + name + "' -Confirm:$false -ErrorAction SilentlyContinue";
        ProcessRunner.RunPowerShell(ps);
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
