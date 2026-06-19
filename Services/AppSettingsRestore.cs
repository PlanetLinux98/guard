using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using GuardWui3.Models;

namespace GuardWui3.Services;

public sealed class AppSettingsRestoreStats
{
    public int Folders;        // folders successfully restored
    public int Files;          // files copied into the targets
    public int Replaced;       // existing targets renamed aside before replacing
    public int SkippedFolders; // targets left untouched (locked, or move-aside failed)
    public int SkippedFiles;   // individual files that could not be copied
}

// Puts the exported settings folders back where they came from. Export records
// every copied folder in app-settings-manifest.json with its rootAnchor
// (%APPDATA% etc.); restore reads it, re-anchors each target to the CURRENT
// profile, and (after the user confirms) copies the folder back. An existing
// target is renamed aside (never deleted) before replacing, so a restore is
// always reversible.
public static class AppSettingsRestore
{
    // Reads the manifest in the AppSettings folder beside an imported app-list
    // file, or null when the import carried no settings bundle. Folder/file names
    // match what AppSettingsExport wrote.
    public static AppSettingsManifest? TryLoadBundle(string listDir)
    {
        if (string.IsNullOrEmpty(listDir)) return null;
        string manifestPath = Path.Combine(
            listDir, AppSettingsExport.OutputFolderName, AppSettingsExport.ManifestFileName);
        if (!File.Exists(manifestPath)) return null;
        try
        {
            using var fs = File.OpenRead(manifestPath);
            var m = JsonSerializer.Deserialize(fs, GuardJsonContext.Default.AppSettingsManifest);
            return m?.Entries is { Length: > 0 } ? m : null;
        }
        catch { return null; }
    }

    // Turns the manifest entries into confirmable rows. Each entry's copied
    // folder must still exist under listDir (the AppSettings bundle); entries
    // whose source is missing are dropped. Targets resolve by expanding the
    // rootAnchor against this machine's profile.
    public static List<AppSettingsRestoreCandidate> BuildCandidates(
        AppSettingsManifest manifest, string listDir)
    {
        var list = new List<AppSettingsRestoreCandidate>();
        if (manifest.Entries == null) return list;
        foreach (var e in manifest.Entries)
        {
            if (string.IsNullOrEmpty(e.Folder) || string.IsNullOrEmpty(e.RootAnchor)
                || string.IsNullOrEmpty(e.DestRelativePath)) continue;

            string source = Path.Combine(listDir, e.DestRelativePath);
            if (!Directory.Exists(source)) continue;

            string expandedRoot = Environment.ExpandEnvironmentVariables(e.RootAnchor);
            // A rootAnchor still containing % failed to expand (variable not set
            // here); without a real root, skip rather than write under a literal
            // "%APPDATA%".
            if (string.IsNullOrEmpty(expandedRoot) || expandedRoot.Contains('%')) continue;
            string target = Path.Combine(expandedRoot, e.Folder);

            var c = new AppSettingsRestoreCandidate
            {
                SourcePath = source,
                FolderName = e.Folder,
                RootName = e.Root ?? "",
                RootAnchor = e.RootAnchor,
                TargetPath = target,
                Bytes = e.Bytes,
                Files = e.Files,
                TargetExists = Directory.Exists(target),
            };
            if (e.Apps != null) c.MatchedApps.AddRange(e.Apps);
            list.Add(c);
        }
        return list;
    }

    // Restores the confirmed folders. An existing target is renamed to
    // <name>.guard-old-<timestamp> first; if that rename fails (a file inside is
    // locked by the running app) the target is left untouched and counted
    // skipped, never half-overwritten. progress gets one line per folder; the
    // walk honours the cancellation token between folders so Stop Reinstall halts
    // cleanly.
    public static AppSettingsRestoreStats RestoreCandidates(
        List<AppSettingsRestoreCandidate> picked, Action<string>? progress, CancellationToken ct)
    {
        var stats = new AppSettingsRestoreStats();
        for (int i = 0; i < picked.Count; i++)
        {
            if (ct.IsCancellationRequested) break;
            var c = picked[i];
            progress?.Invoke("Restoring settings: " + c.DisplayPath + " (" + (i + 1) + " of " + picked.Count + ")...");

            try
            {
                if (Directory.Exists(c.TargetPath))
                {
                    // Move the live folder aside before replacing. Directory.Move
                    // throws if a file inside is open (the locked-app case); leave
                    // existing settings intact and skip.
                    string aside = MakeAsidePath(c.TargetPath);
                    try { Directory.Move(c.TargetPath, aside); }
                    catch { stats.SkippedFolders++; continue; }
                    stats.Replaced++;
                }

                var folderStats = new AppSettingsRestoreStats();
                CopyTree(new DirectoryInfo(c.SourcePath), c.TargetPath, folderStats);
                stats.Folders++;
                stats.Files += folderStats.Files;
                stats.SkippedFiles += folderStats.SkippedFiles;
            }
            catch { stats.SkippedFolders++; }
        }
        return stats;
    }

    // A timestamped sibling name for the displaced folder, made unique so two
    // restores in the same second (or a leftover from a prior run) never collide.
    private static string MakeAsidePath(string target)
    {
        string baseName = target.TrimEnd('\\') + ".guard-old-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string aside = baseName;
        for (int n = 1; Directory.Exists(aside) || File.Exists(aside); n++)
            aside = baseName + "-" + n;
        return aside;
    }

    private static void CopyTree(DirectoryInfo src, string dest, AppSettingsRestoreStats stats)
    {
        // Junctions/symlinks are skipped rather than followed (the export should
        // not have copied any, but a hand-edited bundle might contain one).
        if (IsReparse(src)) return;
        try { Directory.CreateDirectory(dest); }
        catch { stats.SkippedFiles++; return; }
        try
        {
            foreach (var f in src.EnumerateFiles())
            {
                try
                {
                    f.CopyTo(Path.Combine(dest, f.Name), true);
                    stats.Files++;
                }
                catch { stats.SkippedFiles++; }
            }
            foreach (var sub in src.EnumerateDirectories())
                CopyTree(sub, Path.Combine(dest, sub.Name), stats);
        }
        catch { stats.SkippedFiles++; }
    }

    private static bool IsReparse(DirectoryInfo d)
    {
        try { return (d.Attributes & FileAttributes.ReparsePoint) != 0; }
        catch { return true; }
    }
}
