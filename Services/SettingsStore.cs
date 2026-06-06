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
        foreach (var raw in File.ReadAllLines(GuardPaths.IniPath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(";")) continue;
            if (line.StartsWith("[") && line.EndsWith("]")) { section = line.Substring(1, line.Length - 2); continue; }
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

            switch (section + "." + key)
            {
                case "General.Dest": cfg.Dest = val; break;
                case "General.Mode": cfg.Mode = val; break;
                case "General.ExcludeDirs": cfg.ExcludeDirs = Unescape(val); break;
                case "General.ExcludeFiles": cfg.ExcludeFiles = Unescape(val); break;
                case "Schedule.Enabled": cfg.ScheduleEnabled = val == "1"; break;
                case "Schedule.Time": cfg.ScheduleTime = val; break;
                case "AppList.Dest": cfg.AppListDest = val; break;
            }
        }
        if (sawFolders) cfg.Folders = folders;
        return cfg;
    }

    public static void Save(Settings cfg)
    {
        var sb = new StringBuilder();
        sb.AppendLine("; GUARD settings - generated file. Edit via GUARD.exe.");
        sb.AppendLine("[General]");
        sb.AppendLine("Dest=" + cfg.Dest);
        sb.AppendLine("Mode=" + cfg.Mode);
        sb.AppendLine("ExcludeDirs=" + Escape(cfg.ExcludeDirs));
        sb.AppendLine("ExcludeFiles=" + Escape(cfg.ExcludeFiles));
        sb.AppendLine();
        sb.AppendLine("[Schedule]");
        sb.AppendLine("Enabled=" + (cfg.ScheduleEnabled ? "1" : "0"));
        sb.AppendLine("Time=" + cfg.ScheduleTime);
        sb.AppendLine();
        sb.AppendLine("[Folders]");
        sb.AppendLine("; index=include|source|subfolder");
        for (int i = 0; i < cfg.Folders.Count; i++)
        {
            var f = cfg.Folders[i];
            sb.AppendLine(i + "=" + (f.Include ? "1" : "0") + "|" + f.Source + "|" + f.SubFolder);
        }
        sb.AppendLine();
        sb.AppendLine("[AppList]");
        sb.AppendLine("Dest=" + cfg.AppListDest);
        File.WriteAllText(GuardPaths.IniPath, sb.ToString());
    }

    // Multi-line values reach here with whatever newline the source used. A WinUI
    // multi-line TextBox separates its lines with a bare CR ("\r"), so normalize
    // CRLF and lone CR to LF before collapsing every line break to the literal
    // "\n" token; otherwise a raw CR is written into the ini and File.ReadAllLines
    // splits the value across lines on load, dropping everything after the first.
    private static string Escape(string s) =>
        (s ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\\n");
    private static string Unescape(string s) => (s ?? "").Replace("\\n", "\r\n");
}
