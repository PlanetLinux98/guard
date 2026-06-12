using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace GuardWui3.Models;

public sealed class Settings
{
    public string Dest = "";                       // any folder: local drive, external disk, or network share
    public string Mode = "Additive";               // Additive | Mirror
    // Versioned mode: each run copies into Dest\YYYY-MM-DD\ and the generated
    // script prunes the oldest dated folders beyond VersionsToKeep. Off by
    // default so the script stays identical to the classic single-copy layout
    // unless the user opts in (see NOTES.md for the design assessment).
    public bool Versioned = false;
    public int VersionsToKeep = 5;
    // Exclusions: ticked preset ids (see ExcludePreset.All) plus user-defined
    // custom entries. A fresh install starts with the system-clutter and
    // developer presets on, covering what the old free-text defaults excluded.
    public List<string> ExcludePresets = new() { "system", "dev" };
    public ObservableCollection<ExcludeItem> Excludes = new();
    // Off by default: a fresh install should not register a scheduled task until
    // the user explicitly opts in.
    public bool ScheduleEnabled = false;
    public string ScheduleTime = "02:00";
    // Which weekdays the scheduled backup runs on. All seven == daily, one ==
    // weekly, any mix == custom. Defaults to all seven so that enabling the
    // schedule (and any legacy ini without a Days key) behaves like a daily run.
    public List<DayOfWeek> ScheduleDays = AllDays();
    // Independent of the day/time schedule: when on, a second scheduled task
    // periodically checks for the destination and backs up once per day when it
    // appears (external drive plugged in, network share reachable). Off by
    // default for the same opt-in reason as ScheduleEnabled.
    public bool TriggerOnConnect = false;
    public ObservableCollection<FolderPair> Folders = new();

    // App Management tab: where the exported app-list.json is written, and
    // whether Export also copies the ticked apps' settings folders alongside
    // the list (off by default; the settings copy adds a confirmation step).
    public string AppListDest = "";
    public bool ExportAppSettings = false;

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
