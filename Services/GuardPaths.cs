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
    // No TrimEnd: GetDirectoryName keeps a trailing separator only for a drive
    // root, and trimming that yields "E:", a drive-RELATIVE path that resolves
    // against E:'s current directory - so an exe run from a USB stick's root
    // would scatter its working files.
    public static readonly string BaseDir =
        Path.GetDirectoryName(Environment.ProcessPath)!;
    // Short stable id for THIS install folder (paths are case-insensitive, so
    // hash the uppercased path). Keys everything that must not collide between
    // two portable copies: the single-instance mutex and the update staging
    // folder in %TEMP% (a shared folder let copy B's staged zip overwrite
    // copy A's, so A's exit applied the wrong install's update - or none).
    public static readonly string InstallId = ComputeInstallId();

    private static string ComputeInstallId()
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.Unicode.GetBytes(BaseDir.ToUpperInvariant()));
        return Convert.ToHexString(bytes.AsSpan(0, 8));
    }

    public static string IniPath => Path.Combine(BaseDir, "backup-settings.ini");
    // GUARD's own app preferences (updates, theme, startup page); separate from
    // backup-settings.ini because prefs save immediately, not via Save Settings.
    public static string PrefsPath => Path.Combine(BaseDir, "guard-prefs.ini");
    public static string ScriptPath => Path.Combine(BaseDir, "guard-backup.cmd");
    public static string LogPath => Path.Combine(BaseDir, @"Logs\backup_last.log");
    // HTML, not the .md source: every PC opens .html in a browser, while .md
    // often has no file association and Help dead-ended on Windows' picker.
    public static string ManualPath => Path.Combine(BaseDir, "USER_GUIDE.html");
    // The manual as older zips shipped it; deleted at startup once the HTML
    // exists (updates extract over BaseDir and never remove old files).
    public static string LegacyManualPath => Path.Combine(BaseDir, "USER_GUIDE.md");
    // System Image tab: the generated wbadmin script and its own log, kept apart
    // from the file-backup pair so a system image never clobbers backup_last.log.
    public static string SystemImageScriptPath => Path.Combine(BaseDir, "guard-system-image.cmd");
    public static string SystemImageLogPath => Path.Combine(BaseDir, @"Logs\system-image_last.log");
    // Recovery-media (bootable USB) build log, tailed by the wizard for progress.
    public static string RecoveryMediaLogPath => Path.Combine(BaseDir, @"Logs\recovery-media_last.log");
    // "View Existing Images" output (wbadmin get versions runs elevated, so its
    // text comes back through a log like the image run's).
    public static string ImageVersionsLogPath => Path.Combine(BaseDir, @"Logs\image-versions_last.log");
    // "Update All Apps" output: winget upgrade --all runs elevated (so MSIX and
    // machine-scope packages can install), so its output comes back via a log.
    public static string AppUpdateLogPath => Path.Combine(BaseDir, @"Logs\app-update_last.log");
    // Update All Apps output: the winget upgrade pass runs elevated (MSIX
    // packages cannot be elevated per-app), so its stream comes back through a
    // log the app tails, like the system image's.
    public static string AppUpdatesLogPath => Path.Combine(BaseDir, @"Logs\app-updates_last.log");
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
