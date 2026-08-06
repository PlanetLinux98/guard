using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GuardWui3.Models;

namespace GuardWui3.Services;

public static class ScheduledTasks
{
    public sealed record ApplyResult(string? Error, string? NextRun);

    // A PowerShell helper that renders a DateTime as "yyyy-MM-dd HH:mm" without
    // a format string. The bug this fixes: .ToString('yyyy-MM-dd HH:mm') follows
    // the OS locale's CALENDAR, so a Thai or Saudi Windows reports a Buddhist or
    // Hijri year (verified: th-TH gives 2569-07-25, ar-SA gives 1448-02-11).
    // Every caller wraps the query in catch {}, so a bad value here surfaces as
    // a wrong date rather than an error.
    //
    // DateTime.Year/Month/Day are Gregorian whatever the culture, and
    // Int32.ToString('00') is only padding, so composing from the parts is
    // correct everywhere. Passing InvariantCulture would work equally well
    // (including under Constrained Language Mode, which restricts method
    // invocation on non-core types, not static property access); this spelling
    // is simply the one with nothing culture-shaped left in it.
    private const string PsFormatDate =
        "function GuardDate($d){ if ($null -eq $d) { return '' }; " +
        "$d.Year.ToString('0000')+'-'+$d.Month.ToString('00')+'-'+$d.Day.ToString('00')+' '+" +
        "$d.Hour.ToString('00')+':'+$d.Minute.ToString('00') };";

    // The next-run query for one task, as a PowerShell expression yielding the
    // formatted string (empty when the task is absent or has no next run).
    private static string PsNextRun(string taskName) =>
        "(GuardDate (Get-ScheduledTaskInfo -TaskName '" + taskName + "' -ErrorAction Stop).NextRunTime)";

    // Applies the whole scheduled-task state for a save in ONE PowerShell call:
    // register-or-remove the timed task, drop the legacy pre-0.3 name,
    // register-or-remove the on-connect task, query the next run. Batching
    // matters: each powershell.exe start pays a multi-second ScheduledTasks-module
    // import, so per-operation calls cost 3-4x per save. Errors route to stdout
    // behind ERRFILE/ERRCONN markers (stderr would mix in PowerShell's noise),
    // the next run behind NEXT.
    //
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
    public static ApplyResult ApplyAll(Settings cfg)
    {
        // Actions launch GUARD.exe itself, not cmd.exe: a GUI-subsystem exe
        // opens no console, so the nightly run and the every-15-minute
        // on-connect check are invisible (the cmd.exe action flashed a console
        // window each time while the user was signed in). The helper mode runs
        // the same script hidden and raises the outcome toast; see Program /
        // HeadlessBackupRunner. -MultipleInstances IgnoreNew plus the script's
        // own run lock keep overlapping fires from colliding on the log.
        // GuardPaths.ExePath, not Environment.ProcessPath: it already resolves
        // the winget-portable symlink to the real exe, so a task saved while
        // running through that alias still registers a launchable action.
        string exe = ProcessRunner.PsQuote(GuardPaths.ExePath);
        var sb = new StringBuilder();
        if (cfg.ScheduleEnabled)
        {
            sb.Append("try {")
              .Append("$A = New-ScheduledTaskAction -Execute '" + exe + "' -Argument '--run-backup auto';")
              .Append("$T = New-ScheduledTaskTrigger " + TriggerArgs(cfg) + ";")
              .Append("$S = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew;")
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
            sb.Append("try {")
              .Append("$A2 = New-ScheduledTaskAction -Execute '" + exe + "' -Argument '--run-backup onconnect';")
              .Append("$T2 = New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Minutes 15) -RepetitionDuration (New-TimeSpan -Days 3650);")
              .Append("$L2 = New-ScheduledTaskTrigger -AtLogOn -User ([Security.Principal.WindowsIdentity]::GetCurrent().Name);")
              .Append("$S2 = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew;")
              .Append("Register-ScheduledTask -TaskName '" + GuardPaths.OnConnectTaskName + "' -Action $A2 -Trigger $T2,$L2 -Settings $S2 -Force -ErrorAction Stop | Out-Null")
              .Append("} catch { Write-Output ('ERRCONN ' + $_.Exception.Message) };");
        }
        else
        {
            sb.Append("Unregister-ScheduledTask -TaskName '" + GuardPaths.OnConnectTaskName + "' -Confirm:$false -ErrorAction SilentlyContinue;");
        }
        sb.Append(PsFormatDate)
          .Append("try { Write-Output ('NEXT ' + " + PsNextRun(GuardPaths.FileTaskName) + ") } catch { }");

        string output;
        try { output = ProcessRunner.RunPowerShellCapture(sb.ToString()); }
        catch (Exception ex) { return new ApplyResult(ex.Message, null); }

        // A multi-line exception message (a CimException's message, say) prints
        // its later lines with no ERRFILE/ERRCONN/NEXT prefix of their own;
        // `current` tracks which error the last recognized line started, so
        // those continuation lines fold into it instead of being silently
        // dropped, until the next recognized prefix or the end of output.
        string? next = null;
        var fileSb = new StringBuilder();
        var connSb = new StringBuilder();
        StringBuilder? current = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("ERRFILE ")) { fileSb.Append(line.Substring(8)); current = fileSb; }
            else if (line.StartsWith("ERRCONN ")) { connSb.Append(line.Substring(8)); current = connSb; }
            else if (line.StartsWith("NEXT ")) { next = line.Substring(5); current = null; }
            else current?.Append('\n').Append(line);
        }
        string? fileErr = fileSb.Length > 0 ? "Scheduled backup task: " + fileSb : null;
        string? connErr = connSb.Length > 0 ? "On-connect task: " + connSb : null;
        string? err = fileErr != null && connErr != null ? fileErr + "\n\n" + connErr : fileErr ?? connErr;
        return new ApplyResult(err, next);
    }

    // Remaining names the backup tasks that survived the attempt, so the caller
    // can say which ones rather than claiming a clean sweep it did not verify.
    public sealed record RemoveResult(List<string> Remaining, bool ImageTaskRemains, string? Error);

    // Unregisters every task GUARD registers, whatever the current settings say.
    //
    // This exists because the tasks live in WINDOWS, not in GUARD's folder: a
    // portable app that is deleted (or a winget install that is uninstalled)
    // takes its exe and its scripts away but leaves the registrations behind,
    // and the on-connect task then wakes every 15 minutes forever to run
    // something that is no longer there. Turning the schedules off and saving
    // does the same job, but only while GUARD still exists to do it - so this
    // is the deliberate "I am about to remove GUARD" exit.
    //
    // The two backup tasks plus the pre-0.3 name come off non-elevated. The
    // system image task is a SYSTEM / highest-privileges registration, so
    // removing it needs Administrator; whether one is still registered is
    // reported back rather than assumed, so a user who never scheduled an image
    // is never shown a UAC prompt.
    public static RemoveResult RemoveAll()
    {
        string[] backupTasks =
            { GuardPaths.FileTaskName, GuardPaths.LegacyFileTaskName, GuardPaths.OnConnectTaskName };

        var sb = new StringBuilder();
        foreach (var name in backupTasks)
            sb.Append("Unregister-ScheduledTask -TaskName '" + name + "' -Confirm:$false -ErrorAction SilentlyContinue;");

        // Verified, not assumed. Every Unregister above is SilentlyContinue, and
        // RunPowerShellCapture returns stdout without inspecting the exit code,
        // so nothing so far can tell success from failure - and there are real
        // ways to fail: a task registered by another account, a disabled Task
        // Scheduler service, PowerShell blocked by policy, the capture timeout
        // killing the process. Telling the user their tasks are gone when they
        // are not is the exact failure this whole feature exists to prevent.
        sb.Append("foreach ($n in @(");
        for (int i = 0; i < backupTasks.Length; i++)
            sb.Append(i > 0 ? ",'" : "'").Append(backupTasks[i]).Append('\'');
        sb.Append(")) { try { Get-ScheduledTask -TaskName $n -ErrorAction Stop | Out-Null;")
          .Append(" Write-Output ('LEFT ' + $n) } catch { } };");
        // Positive control. A missing task and a broken task subsystem throw the
        // SAME exception from Get-ScheduledTask, so the loop above going quiet
        // proves nothing on its own: a stopped Task Scheduler service or an
        // unloadable ScheduledTasks module would look exactly like a clean
        // sweep, and the user would be told their tasks were removed while they
        // were still registered. Enumerating ANY task proves the query works.
        sb.Append("try { if (@(Get-ScheduledTask -ErrorAction Stop).Count -gt 0)")
          .Append(" { Write-Output 'QUERYOK' } } catch { };");
        sb.Append("try { Get-ScheduledTask -TaskName '" + GuardPaths.SystemImageTaskName +
                  "' -ErrorAction Stop | Out-Null; Write-Output 'IMAGE 1' } catch { Write-Output 'IMAGE 0' };");
        // Sentinel: "ran and found nothing left" and "never really ran" are both
        // silent otherwise, and they call for opposite responses.
        sb.Append("Write-Output 'DONE'");

        string output;
        try { output = ProcessRunner.RunPowerShellCapture(sb.ToString()); }
        catch (Exception ex) { return new RemoveResult(new List<string>(), false, ex.Message); }

        if (!output.Contains("DONE", StringComparison.Ordinal))
            return new RemoveResult(new List<string>(), false,
                "Windows did not confirm the change, so the tasks may still be registered.");
        if (!output.Contains("QUERYOK", StringComparison.Ordinal))
            return new RemoveResult(new List<string>(), false,
                "Windows would not report on its scheduled tasks, so GUARD cannot confirm they were"
                + " removed. Check Task Scheduler for tasks whose names begin with \"GUARD\".");

        var remaining = new List<string>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("LEFT ", StringComparison.Ordinal)) remaining.Add(line.Substring(5));
        }
        return new RemoveResult(remaining, output.Contains("IMAGE 1", StringComparison.Ordinal), null);
    }

    // The elevated half of RemoveAll; separate so the caller can confirm before
    // raising the UAC prompt. Returns null on success, else the reason.
    public static string? RemoveSystemImageTask()
    {
        // Same verify-don't-assume reasoning as RemoveAll: the Unregister is
        // SilentlyContinue, so without the read-back this could only ever report
        // success. Exit 3 means the task survived.
        string script = "Unregister-ScheduledTask -TaskName '" + GuardPaths.SystemImageTaskName +
                        "' -Confirm:$false -ErrorAction SilentlyContinue; " +
                        "try { Get-ScheduledTask -TaskName '" + GuardPaths.SystemImageTaskName +
                        "' -ErrorAction Stop | Out-Null; exit 3 } catch { exit 0 }";
        int code = ProcessRunner.RunPowerShellElevatedCode(script, out var err);
        if (code == 0) return null;
        if (code == 3) return "The system image task is still registered.";
        if (code == ProcessRunner.ElevationDeclined) return "The system image task " + err;
        return "The system image task could not be removed" + (err != null ? " - " + err : ".");
    }

    // Registers (or removes) the "GUARD System Image" task. Unlike the backup
    // tasks this MUST run elevated: the task runs as SYSTEM with highest
    // privileges so it needs no interactive UAC at fire time, but registering a
    // SYSTEM/Highest task (and later deleting it) is itself an admin operation.
    // So one UAC prompt here, and the caller should only invoke this when the
    // image-schedule state actually changed, to avoid prompting on every save.
    //
    // Registration goes through schtasks /Create /XML rather than the
    // New-ScheduledTask* cmdlets because PowerShell 5.1 has no monthly trigger,
    // and the XML cleanly carries the SYSTEM principal, the monthly/weekly/daily
    // calendar trigger, and a Command+Arguments action (no /TR quoting hazard).
    // The XML is shipped base64-encoded inside the elevated script so no quoting
    // or here-string fragility can corrupt it.
    public static ApplyResult ApplySystemImage(Settings cfg)
    {
        // Turning the schedule OFF reads the task back, the same verify-don't-assume
        // RemoveSystemImageTask does and for the same reason: the Unregister is
        // SilentlyContinue, so the bare "exit 0" this used to end on reported
        // success whatever actually happened. A SYSTEM/Highest wbadmin task that
        // survived then kept firing on schedule while the UI showed no schedule.
        // Exit 3 means the task is still there. (Registration needs no equivalent:
        // schtasks /Create propagates its own exit code.)
        string script = cfg.ImageScheduleEnabled
            ? BuildSystemImageRegisterScript(cfg)
            : "Unregister-ScheduledTask -TaskName '" + GuardPaths.SystemImageTaskName +
              "' -Confirm:$false -ErrorAction SilentlyContinue; " +
              "try { Get-ScheduledTask -TaskName '" + GuardPaths.SystemImageTaskName +
              "' -ErrorAction Stop | Out-Null; exit 3 } catch { exit 0 }";

        int code = ProcessRunner.RunPowerShellElevatedCode(script, out var err);
        if (code == ProcessRunner.ElevationDeclined || code == ProcessRunner.ElevationLaunchFailed)
            return new ApplyResult("System image schedule " + err, null);
        if (code == 3)
            return new ApplyResult("System image schedule could not be turned off: the task is still registered.", null);
        if (code != 0)
            return new ApplyResult("System image schedule did not complete (exit code " + code + ").", null);

        // Querying task info is read-only, so the next run reads back un-elevated.
        string? next = cfg.ImageScheduleEnabled ? QueryNextRun(GuardPaths.SystemImageTaskName) : null;
        return new ApplyResult(null, next);
    }

    private static string BuildSystemImageRegisterScript(Settings cfg)
    {
        string xml = BuildSystemImageTaskXml(cfg);
        string b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(xml));
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("$b64 = '" + b64 + "'");
        sb.AppendLine("$xml = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($b64))");
        // %SystemRoot%\Temp, not the user temp: schtasks reads this XML to
        // register a SYSTEM task, so it must not sit where a non-elevated
        // same-user process could swap it before schtasks reads it. Standard
        // users cannot write under %SystemRoot%\Temp.
        sb.AppendLine("$tmp = [IO.Path]::Combine($env:SystemRoot, 'Temp', 'guard_si_' + [Guid]::NewGuid().ToString('N') + '.xml')");
        sb.AppendLine("[IO.File]::WriteAllText($tmp, $xml, [Text.Encoding]::Unicode)");
        sb.AppendLine("schtasks /Create /TN '" + GuardPaths.SystemImageTaskName + "' /XML \"$tmp\" /F | Out-Null");
        sb.AppendLine("$code = $LASTEXITCODE");
        sb.AppendLine("Remove-Item $tmp -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("exit $code");
        return sb.ToString();
    }

    // Task Scheduler 1.2 XML. UserId S-1-5-18 is the locale-independent SYSTEM
    // SID; HighestAvailable = run with highest privileges. ExecutionTimeLimit
    // PT0S removes the default 3-day cap (a full image can be long but should not
    // be killed). The action splits Command from Arguments so the script path's
    // spaces need no shell quoting.
    private static string BuildSystemImageTaskXml(Settings cfg)
    {
        string time = NormalizeTime(cfg.ImageScheduleTime);
        // Invariant: StartBoundary must be a Gregorian date, but a bare
        // ToString follows the culture's default calendar, and on locales
        // where that is not Gregorian (Thai Buddhist, Umm al-Qura) yyyy
        // yields a year Task Scheduler rejects.
        string start = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
            + "T" + time + ":00";
        string trigger = cfg.ImageCadence switch
        {
            "Daily" => "<ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay>",
            "Monthly" => "<ScheduleByMonth><DaysOfMonth><Day>" +
                         Math.Clamp(cfg.ImageMonthlyDay, 1, 28) + "</Day></DaysOfMonth><Months>" +
                         AllMonthsXml + "</Months></ScheduleByMonth>",
            _ => "<ScheduleByWeek><DaysOfWeek><" + cfg.ImageWeeklyDay +
                 "/></DaysOfWeek><WeeksInterval>1</WeeksInterval></ScheduleByWeek>",
        };
        string args = XmlEscape("/c \"" + GuardPaths.SystemImageScriptPath + "\" auto");
        return
            "<?xml version=\"1.0\" encoding=\"UTF-16\"?>" +
            "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
              "<RegistrationInfo><Description>GUARD full system image (wbadmin -allCritical).</Description></RegistrationInfo>" +
              "<Triggers><CalendarTrigger><StartBoundary>" + start +
                "</StartBoundary><Enabled>true</Enabled>" + trigger + "</CalendarTrigger></Triggers>" +
              "<Principals><Principal id=\"Author\"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>" +
              "<Settings>" +
                "<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>" +
                "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
                "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>" +
                "<StartWhenAvailable>true</StartWhenAvailable>" +
                "<Enabled>true</Enabled>" +
                "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>" +
              "</Settings>" +
              "<Actions Context=\"Author\"><Exec><Command>cmd.exe</Command><Arguments>" + args +
                "</Arguments></Exec></Actions>" +
            "</Task>";
    }

    private const string AllMonthsXml =
        "<January/><February/><March/><April/><May/><June/>" +
        "<July/><August/><September/><October/><November/><December/>";

    // Public: SettingsStore normalizes both schedule times at load with this,
    // so a hand-edited ini value can never be spliced into a PowerShell command
    // (TriggerArgs) or the task XML.
    public static string NormalizeTime(string? t, string fallback = "03:00")
    {
        if (!string.IsNullOrWhiteSpace(t) && TimeSpan.TryParse(t.Trim(), out var ts)
            && ts >= TimeSpan.Zero && ts < TimeSpan.FromDays(1))
            return ts.ToString(@"hh\:mm");
        return fallback;
    }

    private static string XmlEscape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    // All seven days (or empty, defensively) -> daily trigger; any subset ->
    // weekly trigger on those days. The empty fallback matters: the manual
    // "Create/Update Task" button can reach here with no days selected (schedule
    // off), and -DaysOfWeek with no days is invalid, so we never emit one.
    private static string TriggerArgs(Settings cfg)
    {
        // Normalized again defensively: the time is interpolated into a
        // PowerShell command, so it must only ever be an HH:mm token.
        string at = NormalizeTime(cfg.ScheduleTime, "02:00");
        var days = cfg.ScheduleDays.Distinct().ToList();
        if (days.Count == 0 || days.Count >= 7)
            return "-Daily -At " + at;
        return "-Weekly -DaysOfWeek " + string.Join(",", days) + " -At " + at;
    }

    public sealed record TaskActionInfo(string Name, string Execute, string Arguments);
    // QueryOk distinguishes "Windows reported and the task is not there" from
    // "the query itself failed", which an empty Actions list alone cannot: the
    // caller treats a missing image task as a schedule that needs re-applying,
    // and must not raise that on a query that never ran. Same reasoning as
    // RemoveAll's QUERYOK marker.
    public sealed record StartupState(string? NextRun, string? ImageNextRun, List<TaskActionInfo> Actions, bool QueryOk);

    // One batched launch-time query: the backup and image tasks' next runs plus
    // each GUARD task's registered action. Both next runs, because only a
    // schedule-CHANGING save re-queries the image task, so without this the
    // System Image page sat on "Next run: (unknown)" for the whole session.
    // The actions matter because GUARD is portable: the tasks embed absolute
    // paths from save time, so a moved or renamed folder leaves them silently
    // firing into the old location while the UI still shows a healthy next-run
    // time. The caller compares these against the current install and
    // re-registers what it may (see MainWindow).
    public static StartupState QueryStartupState()
    {
        var sb = new StringBuilder();
        sb.Append(PsFormatDate);
        sb.Append("try { Write-Output ('NEXT ' + " + PsNextRun(GuardPaths.FileTaskName) + ") } catch { };");
        sb.Append("try { Write-Output ('NEXTIMG ' + " + PsNextRun(GuardPaths.SystemImageTaskName) + ") } catch { };");
        sb.Append("foreach ($n in @('" + GuardPaths.FileTaskName + "','" + GuardPaths.OnConnectTaskName +
                  "','" + GuardPaths.SystemImageTaskName + "')) {")
          .Append("try { $t = Get-ScheduledTask -TaskName $n -ErrorAction Stop; $a = $t.Actions[0];")
          .Append("Write-Output ('ACT ' + $n + '|' + $a.Execute + '|' + $a.Arguments) } catch { } }");
        // Last, so it is only reached once the enumeration above completed.
        sb.Append("Write-Output 'QUERYOK'");

        string? next = null, imageNext = null;
        bool queryOk = false;
        var actions = new List<TaskActionInfo>();
        try
        {
            foreach (var raw in ProcessRunner.RunPowerShellCapture(sb.ToString()).Split('\n'))
            {
                var line = raw.Trim();
                if (line == "QUERYOK") queryOk = true;
                // NEXTIMG before NEXT: its marker shares the NEXT prefix.
                else if (line.StartsWith("NEXTIMG ")) imageNext = line.Substring(8);
                else if (line.StartsWith("NEXT ")) next = line.Substring(5);
                else if (line.StartsWith("ACT "))
                {
                    var parts = line.Substring(4).Split('|');
                    if (parts.Length >= 3)
                        actions.Add(new TaskActionInfo(parts[0], parts[1], parts[2]));
                }
            }
        }
        catch (Exception ex) { DebugLog.Log("tasks", "startup task query failed", ex); }
        return new StartupState(next, imageNext, actions, queryOk);
    }

    // Whether a registered backup-task action launches THIS install's exe in
    // the hidden helper form. False for a moved folder's old path and for the
    // pre-0.6 visible cmd.exe action style; both heal by re-registering.
    public static bool IsCurrentBackupAction(TaskActionInfo a)
    {
        string exe = (a.Execute ?? "").Trim().Trim('"');
        return string.Equals(exe, GuardPaths.ExePath, StringComparison.OrdinalIgnoreCase)
            && (a.Arguments ?? "").TrimStart().StartsWith("--run-backup", StringComparison.Ordinal);
    }

    // The image task stays a cmd.exe action (it runs as SYSTEM in session 0,
    // where no window can show), so currency means its arguments still name
    // this install's script path.
    public static bool IsCurrentImageAction(TaskActionInfo a)
        => (a.Arguments ?? "").IndexOf(GuardPaths.SystemImageScriptPath, StringComparison.OrdinalIgnoreCase) >= 0;

    public static string? QueryNextRun(string name)
    {
        try
        {
            string ps = PsFormatDate + "try { " + PsNextRun(name) + " } catch { '' }";
            string outp = ProcessRunner.RunPowerShellCapture(ps).Trim();
            return string.IsNullOrEmpty(outp) ? null : outp;
        }
        catch { return null; }
    }
}
