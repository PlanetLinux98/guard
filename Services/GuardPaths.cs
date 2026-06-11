using System;
using System.IO;
using System.Reflection;

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
    public const string AppListFileName = "app-list.json";
    // MinVer derives the version from the latest vX.Y.Z git tag at build time, so
    // releases never need a hand-edited constant. The informational version is the
    // full semver (a dev build between tags carries a pre-release label like
    // 0.3.0-alpha.0.5); the "+<sha>" build metadata is stripped because the About
    // dialog is aimed at end users, who match builds by release tag, not commit.
    public static string AppVersion { get; } = ComputeAppVersion();

    private static string ComputeAppVersion()
    {
        string? info = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(info))
            return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
        int plus = info.IndexOf('+');
        return plus >= 0 ? info[..plus] : info;
    }

    public const string RepoUrl = "https://github.com/PlanetLinux98/guard";
}
