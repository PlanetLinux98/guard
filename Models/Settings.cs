using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace GuardWui3.Models;

public sealed class Settings
{
    public string Dest = "";                       // any folder: local drive, external disk, or network share
    // The destination volume's serial (8 hex digits) and label, recorded at
    // save time when Dest is drive-letter-rooted. USB drives change letters
    // between plugs; the generated script re-finds the volume by serial when
    // the saved letter is unreachable, and a save re-anchors Dest to the new
    // letter. Empty for UNC destinations or when the volume was unreadable.
    public string DestVolumeSerial = "";
    public string DestVolumeLabel = "";
    public string Mode = "Additive";               // Additive | Mirror
    // Versioned mode: each run copies into Dest\YYYY-MM-DD\ and the script prunes
    // oldest dated folders beyond VersionsToKeep. Off by default so the script
    // matches the classic single-copy layout unless opted in (see NOTES.md).
    public bool Versioned = false;
    public int VersionsToKeep = 5;
    // Exclusions: ticked preset ids (see ExcludePreset.All) plus custom entries.
    // Fresh install starts with the system-clutter and developer presets on
    // (matching the old free-text defaults).
    public List<string> ExcludePresets = new() { "system", "dev" };
    public ObservableCollection<ExcludeItem> Excludes = new();
    // Off by default: don't register a scheduled task until the user opts in.
    public bool ScheduleEnabled = false;
    public string ScheduleTime = "02:00";
    // Weekdays the backup runs. All seven == daily, one == weekly, mix == custom.
    // Defaults to all seven so enabling the schedule (and any legacy ini with no
    // Days key) acts daily.
    public List<DayOfWeek> ScheduleDays = AllDays();
    // Independent of the day/time schedule: a second task periodically checks for
    // the destination and backs up once/day when it appears (drive plugged in,
    // share reachable). Off by default, same opt-in reason as ScheduleEnabled.
    public bool TriggerOnConnect = false;
    public ObservableCollection<FolderPair> Folders = new();

    // App Management tab: where exported app-list.json is written, and whether
    // Export also copies ticked apps' settings folders (off by default; adds a
    // confirmation step).
    public string AppListDest = "";
    public bool ExportAppSettings = false;

    // System Image tab: a full bare-metal image via the built-in wbadmin tool.
    // ImageTarget is a drive root (E:\) for a local/external disk or a UNC path
    // for a share; ImageTargetKind picks the retention story - a local disk keeps
    // multiple versions automatically (wbadmin's circular buffer), a share keeps
    // only the latest. All off/default until the user opts in, like the schedule.
    public string ImageTarget = "";
    public string ImageTargetKind = "LocalDisk";   // LocalDisk | NetworkShare
    public bool ImageScheduleEnabled = false;
    public string ImageCadence = "Weekly";          // Weekly | Monthly | Daily
    // Distinct default from the file backup's 02:00 so the two jobs don't collide.
    public string ImageScheduleTime = "03:00";
    public DayOfWeek ImageWeeklyDay = DayOfWeek.Sunday;
    public int ImageMonthlyDay = 1;                 // 1..28: every month has a 28th

    // The robocopy /XD and /XF token lists: ticked presets first, then active
    // custom entries, de-duplicated case-insensitively.
    public List<string> EffectiveExcludeDirs() => EffectiveExcludes(dirs: true);
    public List<string> EffectiveExcludeFiles() => EffectiveExcludes(dirs: false);

    private List<string> EffectiveExcludes(bool dirs)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in ExcludePreset.All)
            if (ExcludePresets.Contains(p.Id))
                foreach (var t in dirs ? p.Dirs : p.Files)
                    if (seen.Add(t)) list.Add(t);
        foreach (var e in Excludes)
            if (e.IsFolder == dirs && seen.Add(e.Pattern))
                list.Add(e.Pattern);
        return list;
    }

    public static List<DayOfWeek> AllDays() => new()
    {
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
    };

    public static ObservableCollection<FolderPair> DefaultFolders() => new()
    {
        new FolderPair(true, @"%USERPROFILE%\Documents",  "Documents"),
        new FolderPair(true, @"%USERPROFILE%\Videos",     "Videos"),
        new FolderPair(true, @"%USERPROFILE%\Desktop",    "Desktop"),
        new FolderPair(true, @"%USERPROFILE%\Pictures",   "Pictures"),
        new FolderPair(true, @"%USERPROFILE%\Music",      "Music"),
        new FolderPair(true, @"%USERPROFILE%\Favorites",  "Favorites"),
        new FolderPair(true, @"%USERPROFILE%\Contacts",   "Contacts"),
    };
}
