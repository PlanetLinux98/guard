using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using GuardWui3.Models;

namespace GuardWui3.Services;

// How well one pillar is protecting the user right now. Ordered worst to best
// so a whole-dashboard verdict is just the minimum.
public enum ProtectionLevel
{
    NotSetUp,    // nothing configured; nothing is being protected
    Attention,   // configured, but something needs doing
    Unavailable, // cannot be used on this PC, and that is not the user's fault
    Protected,   // configured, run, and healthy
}

// One dashboard row. Headline is the same sentence the matching page's status
// bar shows, so the two surfaces cannot say different things about the same
// facts; Detail adds the context a one-line bar has no room for.
public sealed record PillarStatus(ProtectionLevel Level, string Headline, string Detail);

// What GUARD knows about an app-list export that has already happened.
public sealed record AppExportInfo(DateTime When, string Path, int Apps);

// The single evaluator behind both the Protection Status page and the File
// Backup / System Image status lines.
//
// It lives here rather than in the window because the same question was already
// being answered inside RefreshScriptStatus and RefreshImageStatus: a dashboard
// with its own copy of those rules would agree with the status bar on the day
// it was written and drift from then on, which for a health display is the one
// failure nobody would notice.
public static class ProtectionStatus
{
    // ---- File Backup ----------------------------------------------------
    //
    // Only the SAVED-AND-CLEAN state: "unsaved changes" and "nothing saved yet"
    // are page states, not protection states, and the caller handles them.
    public static PillarStatus FileBackup(
        Settings cfg, LastRunInfo? last, DateTime now,
        bool destinationEmpty, int vanishedCount, bool mirrorPurges)
    {
        string vanished = VanishedSuffix(vanishedCount, mirrorPurges);
        // Saved but never run is still "not protected": settings do not copy
        // anything, and this is the state a user is likeliest to mistake for done.
        if (last is null)
            return new PillarStatus(ProtectionLevel.Attention,
                "Backup settings saved. No backup has run yet." + vanished,
                "Your folders are not backed up until a backup actually runs. Click Run Now on the"
                + " File Backup page, or turn on a schedule.");

        // A wiped destination outranks everything else: GUARD's log lives next
        // to the exe, so it keeps reporting a successful run in green long after
        // the drive it wrote to was reformatted or emptied.
        if (destinationEmpty)
            return new PillarStatus(ProtectionLevel.Attention,
                "Backup destination is empty - the backup may have been deleted. Run a backup to rebuild it.",
                "GUARD's records show a backup has run, but there is nothing at the destination now.");

        string when = BackupHealth.FriendlyWhen(last.When, now);
        var expected = cfg.ScheduleEnabled
            ? BackupHealth.PreviousScheduledRun(cfg.ScheduleDays, cfg.ScheduleTime, now)
            : null;
        bool amber = true;
        string text;
        if (last.Outcome == RunOutcome.Errors)
            text = "Last backup had errors (" + when + ") - open the last log.";
        else if (last.Outcome == RunOutcome.DidNotComplete)
            text = "Last backup did not complete (" + when + ") - open the last log.";
        else if (BackupHealth.IsOverdue(last, expected, now))
            text = "Backup overdue - last succeeded " + when + ".";
        else if (cfg.TriggerOnConnect && !cfg.ScheduleEnabled
                 && now - last.When > BackupHealth.OnConnectStale)
            text = "Last backup was over a week ago (" + when + ") - connect your backup drive.";
        else
        {
            amber = false;
            text = "Last backup succeeded " + when + ".";
        }
        // A vanished source never makes the run itself fail, so the healthy
        // "succeeded" line is exactly where it has to be said.
        if (vanishedCount > 0) amber = true;
        return new PillarStatus(amber ? ProtectionLevel.Attention : ProtectionLevel.Protected,
            text + vanished, DescribeSchedule(cfg));
    }

    // The status-line tail for sources that have gone empty while the backup
    // still holds their files. Mirror mode names the consequence, since there
    // the next run deletes the copies rather than merely failing to add to them.
    public static string VanishedSuffix(int count, bool mirrorPurges)
    {
        if (count == 0) return "";
        string what = count == 1 ? "1 folder has" : count + " folders have";
        return mirrorPurges
            ? " Warning: " + what + " nothing left to back up; the next backup will delete the copies."
            : " Warning: " + what + " nothing left to back up.";
    }

    private static string DescribeSchedule(Settings cfg)
    {
        var parts = new List<string>();
        if (cfg.ScheduleEnabled)
            parts.Add(cfg.ScheduleDays.Count == 7
                ? "Runs daily at " + cfg.ScheduleTime + "."
                : cfg.ScheduleDays.Count == 0
                    ? "A schedule is on but no days are ticked."
                    : "Runs " + string.Join(", ", cfg.ScheduleDays) + " at " + cfg.ScheduleTime + ".");
        if (cfg.TriggerOnConnect)
            parts.Add("Also runs when the backup destination becomes available.");
        if (parts.Count == 0)
            parts.Add("No schedule is set, so backups only happen when you click Run Now.");
        parts.Add("Destination: " + Environment.ExpandEnvironmentVariables(cfg.Dest ?? ""));
        return string.Join(" ", parts);
    }

    // ---- System Image ---------------------------------------------------
    public static PillarStatus SystemImage(
        Settings cfg, bool available, bool settingsSaved, LastRunInfo? last, DateTime now)
    {
        if (!available)
            return new PillarStatus(ProtectionLevel.Unavailable,
                "System imaging is unavailable on this Windows edition (wbadmin not found). Recovery media still works.",
                "A full-PC image needs the Windows Server Backup feature, which Home editions do not"
                + " include. Your files are still protected by File Backup.");
        if (!settingsSaved)
            return new PillarStatus(ProtectionLevel.NotSetUp,
                "No image settings saved yet - choose a destination and click Save Settings.",
                "Without a system image, recovering from a failed disk means reinstalling Windows and"
                + " your programs by hand.");
        if (last is null)
            return new PillarStatus(ProtectionLevel.Attention,
                "Image settings saved. No image created yet.",
                "Click Create Image Now on the System Image page, or set a schedule.");

        string when = BackupHealth.FriendlyWhen(last.When, now);
        var expected = cfg.ImageScheduleEnabled
            ? BackupHealth.PreviousScheduledImage(cfg.ImageCadence, cfg.ImageWeeklyDay,
                cfg.ImageMonthlyDay, cfg.ImageScheduleTime, now)
            : null;
        bool amber = true;
        string text;
        if (last.Outcome == RunOutcome.Errors)
            text = "Last system image had errors (" + when + ") - open the last log.";
        else if (last.Outcome == RunOutcome.DidNotComplete)
            text = "Last system image did not complete (" + when + ") - open the last log.";
        else if (BackupHealth.IsOverdue(last, expected, now))
            text = "System image overdue - last succeeded " + when + ".";
        else
        {
            amber = false;
            text = "Last system image succeeded " + when + ".";
        }
        return new PillarStatus(amber ? ProtectionLevel.Attention : ProtectionLevel.Protected, text,
            (cfg.ImageScheduleEnabled
                ? "Images are created " + cfg.ImageCadence.ToLowerInvariant() + " at " + cfg.ImageScheduleTime + ". "
                : "No image schedule is set. ")
            + "Destination: " + Environment.ExpandEnvironmentVariables(cfg.ImageTarget ?? ""));
    }

    // ---- App Management -------------------------------------------------
    //
    // Judged from the exports actually sitting at the destination, not from a
    // timestamp GUARD stores locally. A local record would keep claiming the app
    // list was exported last Tuesday after the drive holding it was wiped -
    // exactly the trap the empty-destination check exists to catch for backups.
    public static PillarStatus AppList(
        string? appListDest, bool destinationReachable, AppExportInfo? newest, DateTime now)
    {
        string dest = (appListDest ?? "").Trim();
        if (dest.Length == 0)
            return new PillarStatus(ProtectionLevel.NotSetUp,
                "No app list destination set.",
                "An exported app list lets you reinstall your programs on a new PC in one pass."
                + " Choose a destination on the App Management page and click Export List.");
        if (!destinationReachable)
            return new PillarStatus(ProtectionLevel.Attention,
                "App list destination is not reachable, so GUARD cannot tell when you last exported.",
                "Destination: " + Environment.ExpandEnvironmentVariables(dest));
        if (newest is null)
            return new PillarStatus(ProtectionLevel.NotSetUp,
                "No app list has been exported yet.",
                "Click Export List on the App Management page to record which programs are installed.");
        return new PillarStatus(ProtectionLevel.Protected,
            "App list exported " + BackupHealth.FriendlyWhen(newest.When, now)
            + " (" + newest.Apps + (newest.Apps == 1 ? " app)." : " apps)."),
            "Exports are never overwritten, so this is the newest of what is at the destination:\n"
            + newest.Path);
    }

    // The newest export at a destination, or null when there is none.
    //
    // Each export writes <dest>\app-export-<date>_<time>\app-list.json and the
    // list records its own "exported" stamp, so nothing extra has to be stored
    // anywhere for this to be answerable.
    public static AppExportInfo? FindNewestExport(string? appListDest)
    {
        string dest = Environment.ExpandEnvironmentVariables((appListDest ?? "").Trim());
        if (dest.Length == 0) return null;
        AppExportInfo? best = null;
        try
        {
            if (!Directory.Exists(dest)) return null;
            foreach (var dir in new DirectoryInfo(dest).EnumerateDirectories())
            {
                string list = Path.Combine(dir.FullName, GuardPaths.AppListFileName);
                if (!File.Exists(list)) continue;
                var info = ReadExport(list);
                if (info != null && (best == null || info.When > best.When)) best = info;
            }
        }
        catch { }
        return best;
    }

    private static AppExportInfo? ReadExport(string listPath)
    {
        try
        {
            AppListFile? file;
            using (var fs = File.OpenRead(listPath))
                file = JsonSerializer.Deserialize(fs, GuardJsonContext.Default.AppListFile);
            if (file is null) return null;
            // The file's own stamp is what the export wrote; its write time is
            // the fallback for a hand-made or older list that carries none.
            if (!DateTime.TryParseExact(file.Exported ?? "", "yyyy-MM-dd HH:mm",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var when))
                when = File.GetLastWriteTime(listPath);
            return new AppExportInfo(when, listPath, file.Apps?.Length ?? 0);
        }
        catch { return null; }
    }

    // ---- Whole-dashboard verdict ----------------------------------------
    //
    // The worst pillar wins, and a pillar this PC cannot use never drags the
    // verdict down: telling a Home user they are unprotected because Windows
    // withholds wbadmin would be blaming them for someone else's decision.
    public static ProtectionLevel Overall(params PillarStatus[] pillars)
    {
        var worst = ProtectionLevel.Protected;
        foreach (var p in pillars)
        {
            if (p.Level == ProtectionLevel.Unavailable) continue;
            if (p.Level < worst) worst = p.Level;
        }
        return worst;
    }

    // Named "on this Protection Status page", never "below": the same sentence is
    // the page's headline, the status bar's line at the bottom of the window, and
    // what Check Again speaks, and only the first of those is above anything.
    public static string OverallHeadline(ProtectionLevel level) => level switch
    {
        ProtectionLevel.Protected => "You are protected. Everything GUARD looks after is set up and healthy.",
        ProtectionLevel.Attention =>
            "Something needs your attention. Check the items marked on this Protection Status page.",
        _ =>
            "You are not fully protected yet. Set up the items marked on this Protection Status page.",
    };
}
