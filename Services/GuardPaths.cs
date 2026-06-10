using System;
using System.IO;

namespace GuardWui3.Services;

// Every path derives from the exe folder, so the whole folder stays portable.
public static class GuardPaths
{
    // Resolve from the real executable, not AppContext.BaseDirectory: the
    // shipping single-file build self-extracts its native libraries, which makes
    // BaseDirectory point at a temp extraction cache instead of the exe folder.
    // Environment.ProcessPath is the apphost exe itself, so working files land
    // next to it (the GUARD folder) as the portable design intends.
    public static readonly string BaseDir =
        Path.GetDirectoryName(Environment.ProcessPath)!.TrimEnd('\\');
    public static string IniPath => Path.Combine(BaseDir, "backup-settings.ini");
    public static string ScriptPath => Path.Combine(BaseDir, "guard-backup.cmd");
    public static string LogPath => Path.Combine(BaseDir, @"Logs\backup_last.log");
    public static string ReadmePath => Path.Combine(BaseDir, "README.md");

    // Frequency-neutral name (the schedule can be daily, weekly, or custom).
    public const string FileTaskName = "GUARD Backup";
    // Pre-0.3 name; removed on every save so upgraders don't keep a stale task.
    public const string LegacyFileTaskName = "Daily GUARD Backup";
    // Second task: periodic check that backs up when the destination appears.
    public const string OnConnectTaskName = "GUARD On-Connect Backup";
    public const string AppListFileName = "app-list.json";
    public const string AppVersion = "0.3.0";
    public const string RepoUrl = "https://github.com/PlanetLinux98/guard";
}
