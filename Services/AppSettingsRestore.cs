using System;
using System.Collections.Generic;
using System.Globalization;
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
    // The saved copy could not be read at all (the drive holding the bundle was
    // disconnected, the folder was deleted). Counted apart from SkippedFolders
    // because the REASON differs and the wording the user sees has to match:
    // "in use" sends them to close an app, which would not help here. The
    // target is left exactly as it was either way.
    public int SourceUnavailable;
    // A subtree FileTreeCopy could not fully enumerate while copying back (e.g.
    // an ACL-restricted subfolder in the saved copy), distinct from
    // SkippedFolders above: the target folder WAS restored, just not
    // completely - part of its contents were abandoned outright, not one
    // locked file (that's SkippedFiles).
    public int PartialFolders;
    // The move-aside succeeded but the copy-back then failed AND moving the
    // aside folder back to TargetPath also failed: unlike SkippedFolders (never
    // touched), the original is gone from TargetPath and sitting under one of
    // these paths, so the user needs to move it back by hand.
    public List<string> ManualRecoveryPaths = new();
}

// Puts the exported settings folders back where they came from. Export records
// every copied folder in app-settings-manifest.json with its rootAnchor
// (%APPDATA% etc.); restore reads it, re-anchors each target to the CURRENT
// profile, and (after the user confirms) copies the folder back. An existing
// target is renamed aside (never deleted) before replacing, so a restore is
// always reversible.
public static class AppSettingsRestore
{
    // The only rootAnchor values AppSettingsExport ever writes (see its
    // GetRoots). A manifest is untrusted input - hand-editable JSON, or a
    // deliberately crafted one - and IsSafeFolderName only keeps Folder from
    // escaping the anchor; without this allowlist a rootAnchor of, say,
    // "%USERPROFILE%" would expand to a real, writable root and let Folder
    // ("Desktop") target it directly. Case-insensitive since Windows env var
    // names are.
    private static readonly string[] KnownRootAnchors =
        { "%APPDATA%", "%LOCALAPPDATA%", "%USERPROFILE%\\.config" };

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
        // The manifest is hand-editable JSON, so its path fields get the same
        // distrust as a hand-edited ini: Folder must be a bare name (or the
        // target escapes the anchor root - Path.Combine returns a rooted second
        // argument verbatim), and the copied-source path must resolve to inside
        // the bundle folder.
        string bundleRoot;
        try { bundleRoot = WithTrailingSeparator(listDir); }
        catch { return list; }
        foreach (var e in manifest.Entries)
        {
            // A literal "entries": [null] deserializes to a real null ELEMENT,
            // which the Length check above cannot see; dereferencing it took the
            // whole app down rather than dropping one bad row.
            if (e is null) continue;
            if (string.IsNullOrEmpty(e.Folder) || string.IsNullOrEmpty(e.RootAnchor)
                || string.IsNullOrEmpty(e.DestRelativePath)) continue;
            if (!IsSafeFolderName(e.Folder)) continue;
            // The anchor itself needs the same distrust as Folder: expanding an
            // arbitrary env var name (e.g. "%USERPROFILE%") would hand Folder a
            // real, writable root to target that was never one of the three
            // roots GUARD's own export ever uses.
            bool knownAnchor = false;
            foreach (var known in KnownRootAnchors)
                if (known.Equals(e.RootAnchor, StringComparison.OrdinalIgnoreCase)) { knownAnchor = true; break; }
            if (!knownAnchor) continue;

            string source;
            try { source = Path.GetFullPath(Path.Combine(listDir, e.DestRelativePath)); }
            catch { continue; }
            if (!source.StartsWith(bundleRoot, StringComparison.OrdinalIgnoreCase)) continue;
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

    // Restores the confirmed folders. The saved copy is re-checked first, so a
    // bundle that has gone away (the drive was unplugged during the install
    // phase) never disturbs the live folder at all. An existing target is then
    // renamed to <name>.guard-old-<timestamp>; if that rename fails (a file
    // inside is locked by the running app) the target is left untouched and
    // counted skipped, never half-overwritten. If the rename succeeds but the
    // copy-back then does not complete, the rename is undone (best effort) so the
    // original still ends up back at TargetPath; only if THAT also fails is the
    // folder left under its aside name and reported via ManualRecoveryPaths,
    // never miscounted as "skipped" (which would wrongly imply nothing changed).
    // progress gets one line per folder; the walk honours the cancellation token
    // between folders so Stop Reinstall halts cleanly.
    public static AppSettingsRestoreStats RestoreCandidates(
        List<AppSettingsRestoreCandidate> picked, Action<string>? progress, CancellationToken ct)
    {
        var stats = new AppSettingsRestoreStats();
        for (int i = 0; i < picked.Count; i++)
        {
            if (ct.IsCancellationRequested) break;
            var c = picked[i];
            progress?.Invoke("Restoring settings: " + c.DisplayPath + " (" + (i + 1) + " of " + picked.Count + ")...");

            // Re-checked HERE, not just when the candidates were built: the
            // install phase runs between the two and can take many minutes, so
            // the drive holding the bundle may be long gone by now. Checked
            // BEFORE the move-aside below, so the common case never disturbs
            // the live folder at all.
            if (!Directory.Exists(c.SourcePath)) { stats.SourceUnavailable++; continue; }

            string? aside = null;
            try
            {
                if (Directory.Exists(c.TargetPath))
                {
                    // Move the live folder aside before replacing. Directory.Move
                    // throws if a file inside is open (the locked-app case); leave
                    // existing settings intact and skip.
                    aside = MakeAsidePath(c.TargetPath);
                    try { Directory.Move(c.TargetPath, aside); }
                    catch { stats.SkippedFolders++; continue; }
                    stats.Replaced++;
                }
            }
            catch { stats.SkippedFolders++; continue; }

            // The copy-back is judged by its RETURN VALUE, not only by whether it
            // threw: FileTreeCopy reports every per-file and per-subfolder problem
            // through counters and throws nothing, so a source that vanished
            // between the check above and here comes back as a silent no-op. Left
            // unchecked that counted as a restored folder while the move-aside had
            // already emptied TargetPath - the user was told the restore succeeded
            // and their real settings were sitting under an aside name nothing
            // named. Anything short of a completed walk therefore undoes the move.
            bool restored = false;
            try
            {
                var folderStats = new TreeCopyStats();
                if (FileTreeCopy.Copy(new DirectoryInfo(c.SourcePath), c.TargetPath, folderStats))
                {
                    restored = true;
                    stats.Folders++;
                    stats.Files += folderStats.Files;
                    stats.SkippedFiles += folderStats.SkippedFiles;
                    stats.PartialFolders += folderStats.SkippedFolders;
                }
            }
            catch { }
            if (restored) continue;

            // Nothing reached the target. FileTreeCopy returns false before it
            // creates anything, so there is no half-written folder to clear here
            // beyond the defensive check below.
            stats.SourceUnavailable++;
            if (aside == null) continue;                  // target was never touched
            try
            {
                if (Directory.Exists(c.TargetPath)) Directory.Delete(c.TargetPath, true);
                Directory.Move(aside, c.TargetPath);
                // The move-aside is undone, so nothing was replaced after all;
                // leaving Replaced counted would report an aside copy that is no
                // longer there.
                stats.Replaced--;
            }
            catch { stats.ManualRecoveryPaths.Add(aside); }
        }
        return stats;
    }

    // A bare folder name only: no separators or drive colon, and not a
    // relative-navigation name ("." would make the restore rename the anchor
    // root itself aside; ".." would climb out of it).
    private static bool IsSafeFolderName(string name)
    {
        string t = name.Trim();
        return t.Length > 0 && t != "." && t != ".."
            && t.IndexOfAny(new[] { '\\', '/', ':' }) < 0;
    }

    private static string WithTrailingSeparator(string dir)
    {
        string full = Path.GetFullPath(dir);
        return full.EndsWith(Path.DirectorySeparatorChar) ? full : full + Path.DirectorySeparatorChar;
    }

    // A timestamped sibling name for the displaced folder, made unique so two
    // restores in the same second (or a leftover from a prior run) never collide.
    private static string MakeAsidePath(string target)
    {
        string baseName = target.TrimEnd('\\') + ".guard-old-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string aside = baseName;
        for (int n = 1; Directory.Exists(aside) || File.Exists(aside); n++)
            aside = baseName + "-" + n;
        return aside;
    }

}
