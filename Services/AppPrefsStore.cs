using System;
using System.IO;
using System.Text;
using GuardWui3.Models;

namespace GuardWui3.Services;

public static class AppPrefsStore
{
    public static AppPrefs Load()
    {
        if (!File.Exists(GuardPaths.PrefsPath)) return new AppPrefs();
        // A locked file (AV scan, open in another editor, a laggy portable/
        // network copy) must not crash the interactive launch or the headless
        // scheduled run (HeadlessBackupRunner reads this first thing); fall
        // back to defaults, same as a missing file.
        try
        {
            return LoadFrom(new AppPrefs());
        }
        catch (Exception ex)
        {
            DebugLog.Log("prefs", "could not read " + GuardPaths.PrefsPath, ex);
            return new AppPrefs();
        }
    }

    private static AppPrefs LoadFrom(AppPrefs p)
    {
        var section = "";
        foreach (var raw in File.ReadAllLines(GuardPaths.PrefsPath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(";")) continue;
            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                section = line.Substring(1, line.Length - 2);
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            string key = line.Substring(0, eq).Trim();
            string val = line.Substring(eq + 1).Trim();

            switch (section + "." + key)
            {
                case "Updates.AutoCheck": p.UpdateAutoCheck = val == "1"; break;
                case "Updates.AutoInstall": p.UpdateAutoInstall = val == "1"; break;
                case "Updates.SkippedVersion": p.SkippedVersion = val; break;
                case "Updates.LastCheck": p.LastUpdateCheck = val; break;
                // Accept only the known theme tokens so a hand-edited value can
                // never leave the theme radios with nothing selected.
                case "App.Theme":
                    p.Theme = val == "Light" ? "Light" : val == "Dark" ? "Dark" : "System";
                    break;
                // Any tag is accepted here; the startup combo falls back to the
                // first page when the tag no longer matches a nav item.
                case "App.StartupPage": p.StartupPage = val; break;
                case "Notifications.OnFailure": p.NotifyFailure = val == "1"; break;
                case "Notifications.OnSuccess": p.NotifySuccess = val == "1"; break;
                case "App.DeclinedMoves": p.DeclinedMoves = val; break;
                case "App.AcknowledgedEmpty": p.AcknowledgedEmpty = val; break;
            }
        }
        return p;
    }

    public static void Save(AppPrefs p)
    {
        var sb = new StringBuilder();
        sb.AppendLine("; GUARD preferences - generated file. Edit via GUARD.exe (Settings page).");
        sb.AppendLine("[Updates]");
        sb.AppendLine("AutoCheck=" + (p.UpdateAutoCheck ? "1" : "0"));
        sb.AppendLine("AutoInstall=" + (p.UpdateAutoInstall ? "1" : "0"));
        sb.AppendLine("SkippedVersion=" + p.SkippedVersion);
        sb.AppendLine("LastCheck=" + p.LastUpdateCheck);
        sb.AppendLine();
        sb.AppendLine("[App]");
        sb.AppendLine("Theme=" + p.Theme);
        sb.AppendLine("StartupPage=" + p.StartupPage);
        sb.AppendLine("DeclinedMoves=" + p.DeclinedMoves);
        sb.AppendLine("AcknowledgedEmpty=" + p.AcknowledgedEmpty);
        sb.AppendLine();
        sb.AppendLine("[Notifications]");
        sb.AppendLine("OnFailure=" + (p.NotifyFailure ? "1" : "0"));
        sb.AppendLine("OnSuccess=" + (p.NotifySuccess ? "1" : "0"));
        // Prefs save on every Settings-page change (no Save button), so a
        // read-only folder (GUARD run from a locked location) must not crash the
        // change handler; the preference simply doesn't persist.
        try { AtomicFile.WriteAllText(GuardPaths.PrefsPath, sb.ToString()); } catch { }
    }
}
