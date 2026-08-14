using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using GuardWui3.Models;

namespace GuardWui3.Services;

public static class SettingsStore
{
    // Written into [FolderKinds] for a row the user pinned to their own path.
    // Not a valid identity, so it can never collide with a folder name.
    private const string PinnedMarker = "-";

    // Defaults for a missing file AND for an unreadable one. Kept for the
    // read-only consumers that have no better option than degrading (the
    // headless scheduled run, the launch-time task query). Anything that
    // re-writes the file must use LoadOrNull/LoadForMerge instead - see below.
    public static Settings Load()
        => LoadOrNull() ?? new Settings { Folders = Settings.DefaultFolders() };

    // Null ONLY when the file exists but could not be read (locked by an AV
    // scan or an editor, a laggy portable/network copy). A missing file is not
    // a failure - it is a first run, and comes back as defaults.
    //
    // The difference is not cosmetic. Every section-scoped save re-reads this
    // file to carry forward the fields it does not own, so a read failure that
    // silently became "defaults" made saving the File Backup page wipe the
    // System Image settings (and vice versa) with no error anywhere. The same
    // trap sits behind the launch-time task heal, which would have unregistered
    // the user's schedules because a defaulted config says there are none.
    public static Settings? LoadOrNull()
    {
        var cfg = new Settings { Folders = Settings.DefaultFolders() };
        if (!File.Exists(GuardPaths.IniPath)) return cfg;

        // The read+parse below mutates cfg field-by-field as it goes, so a fault
        // partway must not hand back a half-populated object either.
        try
        {
            return LoadFrom(cfg, File.ReadAllLines(GuardPaths.IniPath));
        }
        catch (Exception ex)
        {
            DebugLog.Log("settings", "could not read " + GuardPaths.IniPath, ex);
            return null;
        }
    }

    // The read half of a read-modify-write. Throws rather than degrading: the
    // caller is about to overwrite the file, and writing defaults over settings
    // that are merely unreadable right now destroys them for good. Every caller
    // already surfaces the failure, so the save simply does not happen and the
    // page stays dirty.
    private static Settings LoadForMerge()
        => LoadOrNull() ?? throw new IOException(
            "GUARD could not read its existing settings file, so saving now would replace the"
            + " settings this page does not own with defaults. Close anything that has this file"
            + " open and try again:\n\n" + GuardPaths.IniPath);

    // Lines passed in rather than read here, so the parse can be exercised
    // without an on-disk ini: GuardPaths.IniPath is fixed to the running exe's
    // data folder, which left the [FolderKinds] index mapping and the folder
    // identities untested. See ParseIni.
    private static Settings LoadFrom(Settings cfg, string[] lines)
    {
        var section = "";
        var folders = new ObservableCollection<FolderPair>();
        // The ini index each accepted row came from. Rows failing validation are
        // skipped, so a row's list position is NOT its ini index, and
        // [FolderKinds] is keyed by the latter.
        var folderKeys = new List<int>();
        var folderKinds = new Dictionary<int, string>();
        bool sawFolders = false;
        var excludes = new ObservableCollection<ExcludeItem>();
        // Legacy (pre-preset) excludes were two free-text line lists; collect
        // them and migrate after the loop only when no new-format keys exist.
        bool sawNewExcludes = false;
        string legacyDirs = "", legacyFiles = "";
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(";")) continue;
            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                section = line.Substring(1, line.Length - 2);
                if (section == "Excludes") sawNewExcludes = true;
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            string key = line.Substring(0, eq).Trim();
            string val = line.Substring(eq + 1);

            if (section == "Folders")
            {
                sawFolders = true;
                // Capped at 3 fields: an embedded pipe in Source/SubFolder must
                // land inside parts[2] (not spill into a 4th field), or the
                // Contains('|') check below could never see it post-split.
                var parts = val.Split('|', 3);
                // Mirrors FolderDialog.Validate(): a hand-edited or tampered ini
                // must not let a quote/pipe, an invalid path character, or a
                // blank value reach BackupScript, which trusts Source/SubFolder
                // are already clean and non-empty.
                if (parts.Length == 3 && IsValidFolderEntry(parts[1], parts[2]))
                {
                    folders.Add(new FolderPair(parts[0] == "1", parts[1], parts[2]));
                    folderKeys.Add(int.TryParse(key.Trim(), out int fi) ? fi : -1);
                }
                continue;
            }

            // Its own section rather than a fourth field in [Folders]: an older
            // GUARD splits those lines into exactly three parts, so a fourth
            // would land inside the subfolder, fail its validity check and
            // silently drop the row. An unknown SECTION is simply ignored, so a
            // downgrade keeps every folder and loses only the identities.
            if (section == "FolderKinds")
            {
                // "-" marks a row the user pinned to a path of their own, which
                // must be remembered as distinct from a row that simply never
                // had an identity - see FolderPair.Pinned.
                if (int.TryParse(key.Trim(), out int ki)
                    && (val.Trim() == PinnedMarker || KnownFolders.IsKnownIdentity(val.Trim())))
                    folderKinds[ki] = val.Trim();
                continue;
            }

            if (section == "Excludes")
            {
                // Capped at 2 fields, same reasoning as Folders above.
                var parts = val.Split('|', 2);
                // Mirrors ExcludeDialog's own check.
                if (parts.Length == 2 && IsValidExcludePattern(parts[1]))
                    excludes.Add(new ExcludeItem(parts[0] == "D", parts[1]));
                continue;
            }

            switch (section + "." + key)
            {
                case "General.Dest": cfg.Dest = val; break;
                case "General.DestVolumeSerial": cfg.DestVolumeSerial = val.Trim(); break;
                case "General.DestVolumeLabel": cfg.DestVolumeLabel = val; break;
                case "General.Mode": cfg.Mode = val; break;
                case "General.ExcludeDirs": legacyDirs = Unescape(val); break;
                case "General.ExcludeFiles": legacyFiles = Unescape(val); break;
                case "General.ExcludePresets":
                    sawNewExcludes = true;
                    cfg.ExcludePresets = ParsePresets(val);
                    break;
                case "General.Versioned": cfg.Versioned = val == "1"; break;
                // Clamp so a hand-edited ini cannot produce a keep count the
                // prune logic would mishandle (0 would delete today's backup).
                case "General.VersionsToKeep":
                    if (int.TryParse(val.Trim(), out var keep))
                        cfg.VersionsToKeep = Math.Clamp(keep, 1, 365);
                    break;
                case "Schedule.Enabled": cfg.ScheduleEnabled = val == "1"; break;
                // Normalize like the VersionsToKeep clamp: a hand-edited time
                // would otherwise be spliced verbatim into the PowerShell
                // Register-ScheduledTask command (see ScheduledTasks.TriggerArgs).
                case "Schedule.Time": cfg.ScheduleTime = ScheduledTasks.NormalizeTime(val, "02:00"); break;
                // Only override the all-seven default when the key is actually
                // present; a legacy ini without it stays daily.
                case "Schedule.Days": cfg.ScheduleDays = ParseDays(val); break;
                case "Schedule.OnConnect": cfg.TriggerOnConnect = val == "1"; break;
                case "AppList.Dest": cfg.AppListDest = val; break;
                case "AppList.ExportSettings": cfg.ExportAppSettings = val == "1"; break;
                case "SystemImage.Target": cfg.ImageTarget = val; break;
                case "SystemImage.TargetKind":
                    cfg.ImageTargetKind = val == "NetworkShare" ? "NetworkShare" : "LocalDisk";
                    break;
                case "SystemImage.ScheduleEnabled": cfg.ImageScheduleEnabled = val == "1"; break;
                case "SystemImage.Cadence": cfg.ImageCadence = ParseCadence(val); break;
                case "SystemImage.Time": cfg.ImageScheduleTime = ScheduledTasks.NormalizeTime(val, "03:00"); break;
                case "SystemImage.WeeklyDay":
                    if (Enum.TryParse<DayOfWeek>(val.Trim(), ignoreCase: true, out var iwd))
                        cfg.ImageWeeklyDay = iwd;
                    break;
                // Clamp to 1..28 so a scheduled monthly image fires every month
                // (29-31 would skip short months).
                case "SystemImage.MonthlyDay":
                    if (int.TryParse(val.Trim(), out var imd))
                        cfg.ImageMonthlyDay = Math.Clamp(imd, 1, 28);
                    break;
            }
        }
        if (sawFolders)
        {
            for (int i = 0; i < folders.Count; i++)
            {
                if (folderKeys[i] < 0 || !folderKinds.TryGetValue(folderKeys[i], out var kind)) continue;
                if (kind == PinnedMarker) folders[i].Pinned = true;
                else folders[i].KnownFolder = kind;
            }
            cfg.Folders = folders;
        }
        // Rows written before GUARD tracked identities adopt theirs from the old
        // hard-coded default paths. Silent on purpose: it does not change what is
        // backed up, it only lets GUARD notice later if that folder moves.
        KnownFolders.AdoptIdentities(cfg.Folders);
        if (sawNewExcludes) cfg.Excludes = excludes;
        else if (legacyDirs.Length > 0 || legacyFiles.Length > 0)
            MigrateLegacyExcludes(cfg, legacyDirs, legacyFiles);
        return cfg;
    }

    // A quote breaks the generated script's quoted robocopy arguments; the
    // subfolder also becomes path segments under the destination, so it must
    // hold no other invalid-filename character and no ".." segment that would
    // climb out of the destination root. Blank Source/SubFolder is rejected
    // too, matching FolderDialog.Validate() - the ini parser has no other
    // required-value check, so a hand-edited blank entry would otherwise load
    // as a broken row that SKIPs on every run.
    private static bool IsValidFolderEntry(string source, string subFolder)
    {
        if (source.Trim().Length == 0 || subFolder.Trim().Length == 0) return false;
        if (source.Contains('"') || source.Contains('|')) return false;
        foreach (char c in subFolder)
            if (c is '"' or '|' or '<' or '>' or ':' or '?' or '*' or '/' or '%') return false;
        foreach (var seg in subFolder.Split('\\'))
            if (seg.Trim() == "..") return false;
        return true;
    }

    // A quote would break the generated robocopy line; a pipe the ini's own
    // Excludes format; the cmd operator/expansion characters (& ^ < > %) act
    // as operators when the unquotable OPTS variable expands on the robocopy
    // line. Blank pattern is rejected too, matching ExcludeDialog.
    private static readonly char[] UnsafePatternChars = { '"', '|', '&', '^', '<', '>', '%' };
    private static bool IsValidExcludePattern(string pattern)
        => pattern.Trim().Length > 0 && pattern.IndexOfAny(UnsafePatternChars) < 0;

    // Fold a pre-preset ini's free-text exclude lines into the preset/custom
    // model: a preset is ticked when any of its patterns appears among the legacy
    // lines (this can pull in sibling patterns, but the result shows in the UI
    // before the next save), absorbed lines are dropped, and the rest become
    // custom entries.
    private static void MigrateLegacyExcludes(Settings cfg, string legacyDirs, string legacyFiles)
    {
        var dirs = SplitLines(legacyDirs);
        var files = SplitLines(legacyFiles);
        cfg.ExcludePresets = new List<string>();
        cfg.Excludes = new ObservableCollection<ExcludeItem>();
        foreach (var p in ExcludePreset.All)
        {
            bool hit = dirs.Exists(d => ContainsIgnoreCase(p.Dirs, d))
                    || files.Exists(f => ContainsIgnoreCase(p.Files, f));
            if (!hit) continue;
            cfg.ExcludePresets.Add(p.Id);
            dirs.RemoveAll(d => ContainsIgnoreCase(p.Dirs, d));
            files.RemoveAll(f => ContainsIgnoreCase(p.Files, f));
        }
        foreach (var d in dirs) cfg.Excludes.Add(new ExcludeItem(isFolder: true, d));
        foreach (var f in files) cfg.Excludes.Add(new ExcludeItem(isFolder: false, f));
    }

    private static bool ContainsIgnoreCase(string[] patterns, string value)
    {
        foreach (var p in patterns)
            if (string.Equals(p, value, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static List<string> SplitLines(string multiline)
    {
        var keep = new List<string>();
        foreach (var p in multiline.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
        {
            var t = p.Trim();
            if (t.Length > 0) keep.Add(t);
        }
        return keep;
    }

    // "system,dev" -> distinct known preset ids, ignoring unknown tokens.
    private static List<string> ParsePresets(string val)
    {
        var ids = new List<string>();
        foreach (var token in (val ?? "").Split(','))
        {
            var t = token.Trim();
            if (t.Length == 0 || ids.Contains(t)) continue;
            foreach (var p in ExcludePreset.All)
                if (p.Id == t) { ids.Add(t); break; }
        }
        return ids;
    }

    // Section-scoped saves. The live Settings can carry another page's
    // harvested-but-unsaved edits (a failed validation leaves them behind, and
    // the folder/exclude lists are two-way bound so they mutate immediately), so
    // a flow that persists the whole object would silently commit edits the user
    // never saved. Each save therefore re-reads the on-disk settings and
    // overlays only the fields it owns.
    public static void SaveFileBackup(Settings live)
    {
        var s = LoadForMerge();
        s.Dest = live.Dest;
        s.DestVolumeSerial = live.DestVolumeSerial;
        s.DestVolumeLabel = live.DestVolumeLabel;
        s.Mode = live.Mode;
        s.ExcludePresets = live.ExcludePresets;
        s.Excludes = live.Excludes;
        s.Versioned = live.Versioned;
        s.VersionsToKeep = live.VersionsToKeep;
        s.ScheduleEnabled = live.ScheduleEnabled;
        s.ScheduleTime = live.ScheduleTime;
        s.ScheduleDays = live.ScheduleDays;
        s.TriggerOnConnect = live.TriggerOnConnect;
        s.Folders = live.Folders;
        // The app-list fields ride along: they are harvested fresh from the UI
        // (no unsaved state exists for them, the App page has no dirty tracking).
        s.AppListDest = live.AppListDest;
        s.ExportAppSettings = live.ExportAppSettings;
        Save(s);
    }

    public static void SaveSystemImage(Settings live)
    {
        var s = LoadForMerge();
        s.ImageTarget = live.ImageTarget;
        s.ImageTargetKind = live.ImageTargetKind;
        s.ImageScheduleEnabled = live.ImageScheduleEnabled;
        s.ImageCadence = live.ImageCadence;
        s.ImageScheduleTime = live.ImageScheduleTime;
        s.ImageWeeklyDay = live.ImageWeeklyDay;
        s.ImageMonthlyDay = live.ImageMonthlyDay;
        Save(s);
    }

    public static void SaveAppList(Settings live)
    {
        var s = LoadForMerge();
        s.AppListDest = live.AppListDest;
        s.ExportAppSettings = live.ExportAppSettings;
        Save(s);
    }

    public static void Save(Settings cfg) => AtomicFile.WriteAllText(GuardPaths.IniPath, BuildIni(cfg));

    // The parse half as a pure function, for tests: the save/load round trip is
    // where the folder identities live or die, and [FolderKinds] is keyed by ini
    // index rather than list position - the kind of mapping that breaks quietly.
    internal static Settings ParseIni(string ini)
        => LoadFrom(new Settings { Folders = Settings.DefaultFolders() },
                    ini.Replace("\r\n", "\n").Split('\n'));

    internal static string BuildIni(Settings cfg)
    {
        var sb = new StringBuilder();
        sb.AppendLine("; GUARD settings - generated file. Edit via GUARD.exe.");
        sb.AppendLine("[General]");
        sb.AppendLine("Dest=" + cfg.Dest);
        sb.AppendLine("DestVolumeSerial=" + cfg.DestVolumeSerial);
        sb.AppendLine("DestVolumeLabel=" + cfg.DestVolumeLabel);
        sb.AppendLine("Mode=" + cfg.Mode);
        sb.AppendLine("ExcludePresets=" + string.Join(",", cfg.ExcludePresets));
        sb.AppendLine("Versioned=" + (cfg.Versioned ? "1" : "0"));
        sb.AppendLine("VersionsToKeep=" + cfg.VersionsToKeep);
        sb.AppendLine();
        sb.AppendLine("[Schedule]");
        sb.AppendLine("Enabled=" + (cfg.ScheduleEnabled ? "1" : "0"));
        sb.AppendLine("Time=" + cfg.ScheduleTime);
        sb.AppendLine("Days=" + string.Join(",", cfg.ScheduleDays));
        sb.AppendLine("OnConnect=" + (cfg.TriggerOnConnect ? "1" : "0"));
        sb.AppendLine();
        sb.AppendLine("[SystemImage]");
        sb.AppendLine("Target=" + cfg.ImageTarget);
        sb.AppendLine("TargetKind=" + cfg.ImageTargetKind);
        sb.AppendLine("ScheduleEnabled=" + (cfg.ImageScheduleEnabled ? "1" : "0"));
        sb.AppendLine("Cadence=" + cfg.ImageCadence);
        sb.AppendLine("Time=" + cfg.ImageScheduleTime);
        sb.AppendLine("WeeklyDay=" + cfg.ImageWeeklyDay);
        sb.AppendLine("MonthlyDay=" + cfg.ImageMonthlyDay);
        sb.AppendLine();
        sb.AppendLine("[Folders]");
        sb.AppendLine("; index=include|source|subfolder");
        for (int i = 0; i < cfg.Folders.Count; i++)
        {
            var f = cfg.Folders[i];
            sb.AppendLine(i + "=" + (f.Include ? "1" : "0") + "|" + f.Source + "|" + f.SubFolder);
        }
        sb.AppendLine();
        sb.AppendLine("[FolderKinds]");
        sb.AppendLine("; index=the Windows folder that row follows (\"-\" = a path you chose yourself)");
        for (int i = 0; i < cfg.Folders.Count; i++)
        {
            var f = cfg.Folders[i];
            if (f.IsKnownFolder) sb.AppendLine(i + "=" + f.KnownFolder);
            else if (f.Pinned) sb.AppendLine(i + "=" + PinnedMarker);
        }
        sb.AppendLine();
        sb.AppendLine("[Excludes]");
        sb.AppendLine("; index=kind|pattern   (kind: D = folder name, F = file pattern)");
        for (int i = 0; i < cfg.Excludes.Count; i++)
        {
            var x = cfg.Excludes[i];
            sb.AppendLine(i + "=" + (x.IsFolder ? "D" : "F") + "|" + x.Pattern);
        }
        sb.AppendLine();
        sb.AppendLine("[AppList]");
        sb.AppendLine("Dest=" + cfg.AppListDest);
        sb.AppendLine("ExportSettings=" + (cfg.ExportAppSettings ? "1" : "0"));
        return sb.ToString();
    }

    // Legacy multi-line exclude values were stored with line breaks collapsed
    // to a literal "\n" token; expand them back for migration.
    private static string Unescape(string s) => (s ?? "").Replace("\\n", "\r\n");

    // Accept only the three known cadences; anything else (typo, future value)
    // falls back to Weekly so the scheduler never sees a bad token.
    private static string ParseCadence(string val) => (val ?? "").Trim() switch
    {
        "Daily" => "Daily",
        "Monthly" => "Monthly",
        _ => "Weekly",
    };

    // "Monday,Wednesday,Friday" -> distinct DayOfWeek list, ignoring unknown tokens.
    private static List<DayOfWeek> ParseDays(string val)
    {
        var days = new List<DayOfWeek>();
        foreach (var token in (val ?? "").Split(','))
        {
            var t = token.Trim();
            if (t.Length == 0) continue;
            if (Enum.TryParse<DayOfWeek>(t, ignoreCase: true, out var d) && !days.Contains(d))
                days.Add(d);
        }
        return days;
    }
}
