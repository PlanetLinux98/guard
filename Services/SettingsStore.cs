using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using GuardWui3.Models;

namespace GuardWui3.Services;

public static class SettingsStore
{
    public static Settings Load()
    {
        var cfg = new Settings { Folders = Settings.DefaultFolders() };
        if (!File.Exists(GuardPaths.IniPath)) return cfg;

        var section = "";
        var folders = new ObservableCollection<FolderPair>();
        bool sawFolders = false;
        var excludes = new ObservableCollection<ExcludeItem>();
        // Legacy (pre-preset) excludes were two free-text line lists; collect
        // them and migrate after the loop only when no new-format keys exist.
        bool sawNewExcludes = false;
        string legacyDirs = "", legacyFiles = "";
        foreach (var raw in File.ReadAllLines(GuardPaths.IniPath))
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
                var parts = val.Split('|');
                if (parts.Length >= 3)
                    folders.Add(new FolderPair(parts[0] == "1", parts[1], parts[2]));
                continue;
            }

            if (section == "Excludes")
            {
                var parts = val.Split('|');
                if (parts.Length >= 2)
                    excludes.Add(new ExcludeItem(parts[0] == "D", parts[1]));
                continue;
            }

            switch (section + "." + key)
            {
                case "General.Dest": cfg.Dest = val; break;
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
                case "Schedule.Time": cfg.ScheduleTime = val; break;
                // Only override the all-seven default when the key is actually
                // present; a legacy ini without it stays daily.
                case "Schedule.Days": cfg.ScheduleDays = ParseDays(val); break;
                case "Schedule.OnConnect": cfg.TriggerOnConnect = val == "1"; break;
                case "AppList.Dest": cfg.AppListDest = val; break;
                case "AppList.ExportSettings": cfg.ExportAppSettings = val == "1"; break;
            }
        }
        if (sawFolders) cfg.Folders = folders;
        if (sawNewExcludes) cfg.Excludes = excludes;
        else if (legacyDirs.Length > 0 || legacyFiles.Length > 0)
            MigrateLegacyExcludes(cfg, legacyDirs, legacyFiles);
        return cfg;
    }

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

    public static void Save(Settings cfg)
    {
        var sb = new StringBuilder();
        sb.AppendLine("; GUARD settings - generated file. Edit via GUARD.exe.");
        sb.AppendLine("[General]");
        sb.AppendLine("Dest=" + cfg.Dest);
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
        sb.AppendLine("[Folders]");
        sb.AppendLine("; index=include|source|subfolder");
        for (int i = 0; i < cfg.Folders.Count; i++)
        {
            var f = cfg.Folders[i];
            sb.AppendLine(i + "=" + (f.Include ? "1" : "0") + "|" + f.Source + "|" + f.SubFolder);
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
        File.WriteAllText(GuardPaths.IniPath, sb.ToString());
    }

    // Legacy multi-line exclude values were stored with line breaks collapsed
    // to a literal "\n" token; expand them back for migration.
    private static string Unescape(string s) => (s ?? "").Replace("\\n", "\r\n");

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
