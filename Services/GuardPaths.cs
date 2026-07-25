using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

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
    public static readonly string BaseDir = ResolveBaseDir();

    // ProcessPath reflects the path CreateProcess was invoked with, not the
    // file's real location: winget's portable packages launch through an NTFS
    // symlink in %LOCALAPPDATA%\Microsoft\WinGet\Links (a file-level reparse
    // point), and a junctioned or symlinked ANCESTOR folder anywhere in the
    // invoked path has the same effect - Windows reports the alias verbatim
    // rather than the reparse-resolved target. Unresolved, every working file
    // would land next to the alias instead of the real exe.
    // GetFinalPathNameByHandle, not File.ResolveLinkTarget: ResolveLinkTarget
    // only follows a reparse point on the file itself, so it would miss a
    // symlinked/junctioned ancestor directory - GetFinalPathNameByHandle asks
    // the OS for the canonical path of the actually-open file, resolving every
    // reparse point in the chain, wherever it sits.
    private static string ResolveBaseDir()
    {
        string exePath = Environment.ProcessPath!;
        try
        {
            using var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var sb = new StringBuilder(1024);
            uint len = GetFinalPathNameByHandleW(fs.SafeFileHandle, sb, (uint)sb.Capacity, 0);
            // On success len is the copied length, strictly less than capacity
            // (which includes room for the null terminator); a too-small buffer
            // returns the required size instead - always > 0, so a bare ">0"
            // check alone would misread that as success and read an empty buffer.
            if (len > 0 && len < sb.Capacity)
                exePath = StripExtendedPrefix(sb.ToString());
        }
        catch { /* unreadable path - fall back to the invoked one */ }
        return Path.GetDirectoryName(exePath)!;
    }

    // GetFinalPathNameByHandle always returns the \\?\ (or \\?\UNC\) extended-
    // length form; downstream consumers (robocopy, tar.exe, cmd.exe scripts)
    // do not expect it, so it is stripped back to an ordinary path here, once.
    private static string StripExtendedPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + path[8..];
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path[4..];
        return path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandleW(
        Microsoft.Win32.SafeHandles.SafeFileHandle hFile, StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);

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

    // Whether this copy was installed by winget, which owns its package folder.
    // Matched on the folder layout rather than asked of winget: the check runs
    // during startup path resolution, long before it would be safe or fast to
    // shell out.
    public static readonly bool IsWingetManaged = ComputeWingetManaged();

    private static bool ComputeWingetManaged()
    {
        try
        {
            // Matched on "\WinGet\Packages" alone, NOT the per-user
            // "Microsoft\WinGet\Packages": winget has three portable roots and
            // only the user one sits under Microsoft. A machine-scope install
            // (`--scope machine`) lands in "C:\Program Files\WinGet\Packages",
            // which winget deletes just as recursively - and which a non-elevated
            // GUARD cannot write to at all, so missing it was doubly wrong.
            return BaseDir.Contains(Path.Combine("WinGet", "Packages"),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // Where GUARD's own working files live: the settings, the generated scripts
    // and the logs. Normally the exe's folder, which is what makes a hand-
    // extracted copy portable - move the folder and the configuration goes with
    // it.
    //
    // NOT under a winget install. Verified by experiment: `winget upgrade` and
    // `winget uninstall` both delete the package directory RECURSIVELY, taking
    // anything the app wrote there with them - no warning, no prompt, stock
    // defaults. The portable index winget keeps beside the install tracks the
    // directory itself, not the files it extracted, so the first upgrade a user
    // ran would silently destroy their entire backup configuration.
    //
    // A winget-managed folder was never portable in the first place: winget owns
    // it and replaces it on its own schedule. Redirecting keeps the portability
    // promise where it is real and stops applying it where it never held.
    public static readonly string DataDir = ComputeDataDir();

    private static string ComputeDataDir()
    {
        if (!IsWingetManaged) return BaseDir;
        try
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (local.Length > 0) return Path.Combine(local, "GUARD");
        }
        catch { }
        return BaseDir;   // nowhere better; the old hazard beats no settings at all
    }

    // Move working files left beside the exe by an older version into DataDir.
    // Called once at startup, before anything reads them, from both the window
    // and the headless run.
    //
    // Mostly a safety net rather than a rescue: under winget the very upgrade
    // that delivers this change is the one that deletes the old folder, so there
    // is usually nothing left to move. It matters for a copy that reached the
    // new layout some other way - a hand-extracted install later re-installed
    // through winget, say - and it costs nothing when the folders are the same.
    public static void MigrateWorkingFiles()
    {
        if (string.Equals(DataDir, BaseDir, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            Directory.CreateDirectory(DataDir);
            foreach (string name in new[]
                     {
                         "backup-settings.ini", "guard-prefs.ini", "guard-backup.cmd",
                         "guard-system-image.cmd", "onconnect-stamp.txt",
                     })
            {
                string from = Path.Combine(BaseDir, name), to = Path.Combine(DataDir, name);
                // Never overwrite: a file already in DataDir is the live one.
                if (File.Exists(from) && !File.Exists(to)) File.Move(from, to);
            }
            string logsFrom = Path.Combine(BaseDir, "Logs"), logsTo = Path.Combine(DataDir, "Logs");
            if (Directory.Exists(logsFrom) && !Directory.Exists(logsTo))
                Directory.Move(logsFrom, logsTo);
        }
        catch (Exception ex) { DebugLog.Log("paths", "could not migrate working files", ex); }
    }

    public static string IniPath => Path.Combine(DataDir, "backup-settings.ini");
    // GUARD's own app preferences (updates, theme, startup page); separate from
    // backup-settings.ini because prefs save immediately, not via Save Settings.
    public static string PrefsPath => Path.Combine(DataDir, "guard-prefs.ini");
    public static string ScriptPath => Path.Combine(DataDir, "guard-backup.cmd");
    public static string LogPath => Path.Combine(DataDir, @"Logs\backup_last.log");
    // Preview (Test) runs write here instead of LogPath, so a preview never
    // overwrites the real last-backup log BackupHealth and Open Last Log read.
    public static string PreviewLogPath => Path.Combine(DataDir, @"Logs\backup_preview.log");
    // HTML, not the .md source: every PC opens .html in a browser, while .md
    // often has no file association and Help dead-ended on Windows' picker.
    public static string ManualPath => Path.Combine(BaseDir, "USER_GUIDE.html");
    // The manual as older zips shipped it; deleted at startup once the HTML
    // exists (updates extract over BaseDir and never remove old files).
    public static string LegacyManualPath => Path.Combine(BaseDir, "USER_GUIDE.md");
    // System Image tab: the generated wbadmin script and its own log, kept apart
    // from the file-backup pair so a system image never clobbers backup_last.log.
    public static string SystemImageScriptPath => Path.Combine(DataDir, "guard-system-image.cmd");
    public static string SystemImageLogPath => Path.Combine(DataDir, @"Logs\system-image_last.log");
    // Recovery-media (bootable USB) build log, tailed by the wizard for progress.
    public static string RecoveryMediaLogPath => Path.Combine(DataDir, @"Logs\recovery-media_last.log");
    // "View Existing Images" output (wbadmin get versions runs elevated, so its
    // text comes back through a log like the image run's).
    public static string ImageVersionsLogPath => Path.Combine(DataDir, @"Logs\image-versions_last.log");
    // "Update All Apps" output: winget upgrade --all runs elevated (so MSIX and
    // machine-scope packages can install), so its output comes back via a log.
    public static string AppUpdateLogPath => Path.Combine(DataDir, @"Logs\app-update_last.log");
    // Sentinel the wizard writes to ask the elevated build to stop at the next
    // stage boundary (the elevated process can't be killed by the un-elevated app).
    public static string RecoveryMediaCancelPath => Path.Combine(DataDir, @"Logs\recovery-media.cancel");

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
