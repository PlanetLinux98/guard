using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using GuardWui3.Models;

namespace GuardWui3.Services;

// One restorable folder inside a backup: where its copies sit, and where they
// would go back to.
public sealed record RestoreCandidate(
    string FolderName,       // the subfolder under the snapshot root ("Documents", "Work\Reports")
    string SourcePath,       // <snapshot>\<FolderName>, the copies to restore FROM
    string SuggestedTarget,  // the live folder to restore INTO, expanded, or "" when unknown
    TargetOrigin Origin);

// Where SuggestedTarget came from, so the dialog can say why it is proposing
// that folder (and so a guessed one reads differently from a configured one).
public enum TargetOrigin { None, Settings, WindowsFolder }

// One backup to restore from: a dated version folder, or the destination
// itself when the backup is not versioned.
public sealed record BackupSnapshot(string Path, string Label, DateTime? Taken);

// Reads a backup destination and works out what could be restored from it.
//
// Deliberately driven by what is ON THE DESTINATION rather than by the
// configured folder list. The configured list describes what THIS machine
// backs up; a restore happens on the machine that lost the data, which is
// often a fresh install whose configuration is GUARD's seven defaults - so a
// list built from settings would look plausible while silently omitting every
// folder the user had added on the old PC. The configured pairs still get used
// where they help: they name the folder a backup subfolder came from.
public static class RestorePlan
{
    // The dated folders BackupScript's :prune recognizes, matched the same way
    // (and the same shape SaveValidation keys its history check on).
    public static bool IsDateStamp(string name)
    {
        if (name.Length != 10 || name[4] != '-' || name[7] != '-') return false;
        foreach (int i in new[] { 0, 1, 2, 3, 5, 6, 8, 9 })
            if (!char.IsAsciiDigit(name[i])) return false;
        return true;
    }

    // Every backup at this destination, newest first.
    //
    // Both kinds are offered whenever both exist, rather than trusting the
    // CURRENT Versioned setting: a user who turned versioning on last month
    // still has the older single-copy layout sitting in the destination root,
    // and a restore that could not see it would be hiding the only copy of
    // anything deleted before the switch.
    public static List<BackupSnapshot> FindSnapshots(string dest)
    {
        var snapshots = new List<BackupSnapshot>();
        string root = Environment.ExpandEnvironmentVariables((dest ?? "").Trim());
        if (root.Length == 0) return snapshots;
        List<DirectoryInfo> dirs;
        try
        {
            if (!Directory.Exists(root)) return snapshots;
            dirs = new List<DirectoryInfo>(new DirectoryInfo(root).EnumerateDirectories());
        }
        catch { return snapshots; }

        bool anyPlainFolder = false;
        var dated = new List<BackupSnapshot>();
        foreach (var d in dirs)
        {
            if (IsDateStamp(d.Name))
            {
                // Parsed exactly, invariant: the folder name is composed from
                // Gregorian parts by the generated script, never locale-formatted.
                DateTime.TryParseExact(d.Name, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var taken);
                dated.Add(new BackupSnapshot(d.FullName, d.Name, taken));
            }
            else anyPlainFolder = true;
        }
        dated.Sort((a, b) => string.CompareOrdinal(b.Label, a.Label));   // names sort in date order
        snapshots.AddRange(dated);
        // The root only counts as a backup of its own when it holds folders
        // that are not version folders; otherwise it is merely the container.
        if (anyPlainFolder)
            snapshots.Add(new BackupSnapshot(root, "Latest backup (not versioned)", null));
        return snapshots;
    }

    // What can be restored from one snapshot, and where each folder came from.
    //
    // Configured pairs are matched first so a nested subfolder ("Work\Reports")
    // is found as one row; the remaining top-level folders are then listed on
    // their own, since a backup can easily hold folders this machine's settings
    // know nothing about.
    public static List<RestoreCandidate> BuildCandidates(
        string snapshotRoot, IEnumerable<FolderPair> folders)
    {
        var list = new List<RestoreCandidate>();
        // Every subfolder a configured pair already accounts for, as its full
        // relative path ("Work\Reports"), so the plain-folder pass below can
        // tell "already listed" from "sits beside something already listed".
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(snapshotRoot)) return list;

        foreach (var f in folders)
        {
            string sub = SaveValidation.NormalizeSubFolder(f.SubFolder);
            // A legacy pair whose subfolder resolves to the destination ROOT has
            // no folder of its own to restore - its files are mixed in with
            // every other pair's. The plain-folder pass below lists what is
            // actually there instead of inventing a row that would drag the
            // whole backup into one live folder.
            if (sub.Length == 0) continue;
            string source;
            try { source = Path.Combine(snapshotRoot, sub); }
            catch { continue; }
            try { if (!Directory.Exists(source)) continue; }
            catch { continue; }
            if (Duplicate(list, source)) continue;
            list.Add(new RestoreCandidate(sub, source,
                Environment.ExpandEnvironmentVariables(f.Source ?? "").Trim(), TargetOrigin.Settings));
            claimed.Add(sub);
        }

        AddUnclaimed(snapshotRoot, "", claimed, list, depth: 0);
        list.Sort((a, b) => string.Compare(a.FolderName, b.FolderName, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    // How deep the walk below will follow a claimed path before giving up. A
    // subfolder can nest, but only as deep as the configured pairs do, and a
    // bound keeps a pathological backup from being enumerated in full.
    private const int MaxClaimDepth = 8;

    // Lists the folders under dir that no configured pair already accounts for.
    //
    // A folder that CONTAINS a claimed one is descended into rather than
    // skipped: with "Work\Reports" configured, skipping all of "Work" would
    // hide "Work\Invoices" completely - a folder sitting in the backup that
    // nothing in the dialog could ever restore, which is exactly the silent
    // omission this whole destination-driven design exists to avoid.
    private static void AddUnclaimed(string dir, string relative,
        HashSet<string> claimed, List<RestoreCandidate> list, int depth)
    {
        List<DirectoryInfo> children;
        try { children = new List<DirectoryInfo>(new DirectoryInfo(dir).EnumerateDirectories()); }
        catch { return; }

        foreach (var d in children)
        {
            // Version folders are snapshots, not content: they are offered by
            // FindSnapshots and must never be restored AS a folder called
            // "2026-08-14". Only at the top, where they can actually appear.
            if (relative.Length == 0 && IsDateStamp(d.Name)) continue;
            string rel = relative.Length == 0 ? d.Name : relative + "\\" + d.Name;
            if (claimed.Contains(rel)) continue;              // already a row of its own
            if (depth < MaxClaimDepth && ClaimedUnder(claimed, rel))
            {
                AddUnclaimed(d.FullName, rel, claimed, list, depth + 1);
                continue;
            }
            if (Duplicate(list, d.FullName)) continue;
            // Windows knows where "Documents" lives on THIS machine, which is
            // what makes a restore onto a fresh install work with no
            // configuration at all: GUARD's default rows use the known folder's
            // own name as the destination subfolder. Only a top-level folder is
            // asked about - a "Documents" nested inside another folder is not
            // the Windows one.
            string? known = relative.Length == 0 ? KnownFolders.Resolve(d.Name) : null;
            list.Add(new RestoreCandidate(rel, d.FullName,
                known == null ? "" : Environment.ExpandEnvironmentVariables(known),
                known == null ? TargetOrigin.None : TargetOrigin.WindowsFolder));
        }
    }

    // Whether some configured pair's subfolder sits INSIDE this one.
    private static bool ClaimedUnder(HashSet<string> claimed, string relative)
    {
        string prefix = relative + "\\";
        foreach (var c in claimed)
            if (c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool Duplicate(List<RestoreCandidate> list, string source)
    {
        foreach (var c in list)
            if (string.Equals(c.SourcePath.TrimEnd('\\'), source.TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // Why this target cannot be restored into, or null when it can be.
    //
    // Restore writes into LIVE folders, so these are refusals rather than the
    // advisory warnings a save gets: there is no run-time SKIP to fall back on
    // and no second chance once files have been written over.
    public static string? ValidateTarget(string? target, string backupDest, params string[] appFolders)
    {
        string raw = (target ?? "").Trim();
        if (raw.Length == 0) return "Choose the folder to restore into.";
        string t = Environment.ExpandEnvironmentVariables(raw);
        // A % that did not expand would be created as a folder literally named
        // "%SOMEVAR%" in the middle of the user's own tree. A backup only gets
        // warned about this (SaveValidation.UnresolvedPercentPaths) because it
        // writes to a folder of its own; a restore writes into theirs.
        if (t.Contains('%'))
            return "The restore location contains a % that is not an environment variable, so GUARD"
                + " cannot tell which folder is meant. Use Change Restore Location to pick it.";
        string full;
        try
        {
            if (!Path.IsPathFullyQualified(t)) return "The restore location must be a full path, like C:\\Users\\You\\Documents.";
            full = Path.GetFullPath(t);
        }
        catch { return "The restore location is not a valid folder path."; }
        // A drive root - or the root of a network share, which is the same
        // mistake one level up - is never a folder anyone means to restore INTO.
        // It is what a mis-picked Browse leaves behind, and the copies would land
        // loose across the whole drive with nothing to undo them by.
        if (IsVolumeRoot(full))
            return "Restoring into the root of a drive or network share is not allowed. Choose a folder on it instead.";
        string key = Key(full);
        string dest = Key(Environment.ExpandEnvironmentVariables((backupDest ?? "").Trim()));
        // Either direction is a loop: restoring into the backup copies the
        // backup into itself, and restoring into a folder that CONTAINS the
        // backup copies it over the copies being read from.
        if (dest.Length > 0 && (key.StartsWith(dest, StringComparison.OrdinalIgnoreCase)
                                || dest.StartsWith(key, StringComparison.OrdinalIgnoreCase)))
            return "That folder is inside the backup destination (or contains it), so restoring there would copy the backup into itself.";
        // Both of GUARD's own folders: under a winget install the program and
        // its settings/logs live in different places (GuardPaths.DataDir), and
        // overwriting either one mid-restore is equally bad.
        foreach (var folder in appFolders)
        {
            string app = Key(folder);
            if (app.Length > 0 && (key.StartsWith(app, StringComparison.OrdinalIgnoreCase)
                                   || app.StartsWith(key, StringComparison.OrdinalIgnoreCase)))
                return "That folder holds GUARD itself, so restoring there would overwrite the running program.";
        }
        return null;
    }

    // "C:\", "C:", and a bare "\\server\share" (with or without a trailing
    // separator). A UNC path with anything after the share name is a real
    // folder and passes.
    private static bool IsVolumeRoot(string full)
    {
        string t = full.TrimEnd('\\', '/');
        if (t.Length <= 2 && t.Length >= 2 && t[1] == ':') return true;
        if (!t.StartsWith("\\\\", StringComparison.Ordinal)) return false;
        // \\server\share -> two segments after the leading slashes.
        return t.Substring(2).Split('\\', StringSplitOptions.RemoveEmptyEntries).Length <= 2;
    }

    // Full path forced to end in a separator, so a StartsWith prefix test only
    // matches whole path segments (SaveValidation.CompareKey's rule).
    private static string Key(string? raw)
    {
        string p = Environment.ExpandEnvironmentVariables((raw ?? "").Trim());
        if (p.Length == 0) return "";
        try { p = Path.GetFullPath(p); }
        catch { return ""; }
        return p.EndsWith(Path.DirectorySeparatorChar) ? p : p + Path.DirectorySeparatorChar;
    }
}
