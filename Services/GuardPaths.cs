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

    public const string FileTaskName = "Daily GUARD Backup";
    public const string AppListFileName = "app-list.json";
    public const string AppVersion = "0.1";
    public const string RepoUrl = "https://github.com/PlanetLinux98/guard";
}
