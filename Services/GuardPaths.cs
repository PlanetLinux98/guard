using System;
using System.IO;

namespace GuardWui3.Services;

// Every path derives from the exe folder, so the whole folder stays portable.
public static class GuardPaths
{
    public static readonly string BaseDir = AppContext.BaseDirectory.TrimEnd('\\');
    public static string IniPath => Path.Combine(BaseDir, "backup-settings.ini");
    public static string ScriptPath => Path.Combine(BaseDir, "guard-backup.cmd");
    public static string LogPath => Path.Combine(BaseDir, @"Logs\backup_last.log");
    public static string ReadmePath => Path.Combine(BaseDir, "README.md");

    public const string FileTaskName = "Daily GUARD Backup";
    public const string AppListFileName = "app-list.json";
    public const string AppVersion = "0.1";
    public const string RepoUrl = "https://github.com/PlanetLinux98/guard";
}
