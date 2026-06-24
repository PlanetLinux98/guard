using System;
using System.IO;
using System.Reflection;

namespace GuardWui3.Services;

// Every path derives from the exe folder, so the whole folder stays portable.
public static class GuardPaths
{
    // Resolve from the real exe, not AppContext.BaseDirectory: the single-file
    // build self-extracts native libs, pointing BaseDirectory at a temp cache.
    // ProcessPath is the apphost exe, so working files land next to it (portable).
    public static readonly string BaseDir =
        Path.GetDirectoryName(Environment.ProcessPath)!.TrimEnd('\\');
    public static string IniPath => Path.Combine(BaseDir, "backup-settings.ini");
    public static string ScriptPath => Path.Combine(BaseDir, "guard-backup.cmd");
    public static string LogPath => Path.Combine(BaseDir, @"Logs\backup_last.log");
    public static string ManualPath => Path.Combine(BaseDir, "USER_GUIDE.md");
    // System Image tab: the generated wbadmin script and its own log, kept apart
    // from the file-backup pair so a system image never clobbers backup_last.log.
    public static string SystemImageScriptPath => Path.Combine(BaseDir, "guard-system-image.cmd");
    public static string SystemImageLogPath => Path.Combine(BaseDir, @"Logs\system-image_last.log");
    // Recovery-media (bootable USB) build log, tailed by the wizard for progress.
    public static string RecoveryMediaLogPath => Path.Combine(BaseDir, @"Logs\recovery-media_last.log");
    // Sentinel the wizard writes to ask the elevated build to stop at the next
    // stage boundary (the elevated process can't be killed by the un-elevated app).
    public static string RecoveryMediaCancelPath => Path.Combine(BaseDir, @"Logs\recovery-media.cancel");

    // Frequency-neutral name (the schedule can be daily, weekly, or custom).
    public const string FileTaskName = "GUARD Backup";
    // Pre-0.3 name; removed on every save so upgraders don't keep a stale task.
    public const string LegacyFileTaskName = "Daily GUARD Backup";
    // Second task: periodic check that backs up when the destination appears.
    public const string OnConnectTaskName = "GUARD On-Connect Backup";
    // Third task: the scheduled full system image. Runs as SYSTEM with highest
    // privileges (wbadmin needs admin), so registering it needs an elevated call.
    public const string SystemImageTaskName = "GUARD System Image";
    public const string AppListFileName = "app-list.json";
    // MinVer derives the version from the latest vX.Y.Z git tag at build, so no
    // hand-edited constant. Informational version is the full semver (dev builds
    // carry a pre-release label like 0.3.0-alpha.0.5); the "+<sha>" build metadata
    // is stripped since the About dialog targets end users, who match by release
    // tag, not commit.
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
