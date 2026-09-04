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
    TargetOrigin Origin,
    RestoreDoubt Doubt = RestoreDoubt.None);

// Where SuggestedTarget came from, so the dialog can say why it is proposing
// that folder (and so a guessed one reads differently from a configured one).
public enum TargetOrigin { None, Settings, WindowsFolder }

// Why GUARD will not tick a row for the user even when it has a target to
// suggest. Both cases are ones where the backup itself is ambiguous, so the
// choice has to be made by someone who knows what the folders are: a restore
// writes into live folders, and a wrong row ticked by default is one the user
// never had to agree to.
public enum RestoreDoubt
{
    None,
    // More than one configured pair backs up INTO this folder, so its copies
    // came from two different places and nothing on the destination says which
    // file came from which.
    MergedSources,
    // The backup destination is a whole drive or share, so every folder on it is
    // listed and GUARD cannot tell the ones it wrote from the ones that merely
    // live there.
    WholeVolumeDestination,
}

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
        bool wholeVolume = IsWholeVolume(root);
        var dated = new List<BackupSnapshot>();
        foreach (var d in dirs)
        {
            // At a drive or share root, Windows' own hidden+system folders
            // ($RECYCLE.BIN, System Volume Information) are always there, and
            // counting them as backup content offered a "Latest backup (not
            // versioned)" snapshot holding no backup at all beside the real
            // dated ones. Only at a volume root, and only for hidden AND system
            // together: robocopy carries a source folder's attributes into the
            // backup, so a hidden folder someone deliberately backs up must
            // still be found.
            if (wholeVolume && IsWindowsVolumeArtifact(d)) continue;
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

    // Whether a path IS a whole drive or network share rather than a folder on
    // one. Public because a destination that is a whole volume changes what the
    // restore list can be sure of, and the dialog says so in as many words.
    public static bool IsWholeVolume(string? path)
    {
        string p = Environment.ExpandEnvironmentVariables((path ?? "").Trim());
        if (p.Length == 0) return false;
        try { p = Path.GetFullPath(p); } catch { return false; }
        return IsVolumeRoot(p);
    }

    // The two folders Windows itself puts at the root of every volume. Matched
    // by name AND by hidden+system together, deliberately narrowly: robocopy
    // carries a source folder's attributes into the backup (/DCOPY:DA), so an
    // attribute test alone could hide a hidden folder someone chose to back up.
    // Missing some other root clutter is harmless - it is listed, untargeted and
    // unticked - whereas hiding real backup content is not, so the test errs
    // that way. Attributes come from the enumeration itself, at no extra cost.
    private static bool IsWindowsVolumeArtifact(DirectoryInfo d)
    {
        if (!d.Name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)
            && !d.Name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            const FileAttributes both = FileAttributes.Hidden | FileAttributes.System;
            return (d.Attributes & both) == both;
        }
        catch { return false; }
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
            int already = IndexOfSource(list, source);
            if (already >= 0)
            {
                // A SECOND configured pair backing up into the same destination
                // folder. Additive allows that (only Mirror blocks the
                // collision), and the backup merges both sources into one
                // folder. Dropping this pair silently left a single row aimed at
                // the FIRST pair's path, so restoring it put this pair's files
                // there too and never mentioned that this folder existed.
                // Nothing on the destination says which file came from which, so
                // the suggestion is withdrawn rather than guessed at.
                list[already] = list[already] with
                {
                    SuggestedTarget = "",
                    Origin = TargetOrigin.None,
                    Doubt = RestoreDoubt.MergedSources,
                };
                continue;
            }
            list.Add(new RestoreCandidate(sub, source,
                Environment.ExpandEnvironmentVariables(f.Source ?? "").Trim(), TargetOrigin.Settings));
            claimed.Add(sub);
        }

        AddUnclaimed(snapshotRoot, "", claimed, list, depth: 0, IsWholeVolume(snapshotRoot));
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
        HashSet<string> claimed, List<RestoreCandidate> list, int depth, bool rootIsVolume)
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
            // Windows' own root bookkeeping, which is present on every volume and
            // is not part of anyone's backup. Only where the snapshot root IS a
            // volume, which is the only place it can appear.
            if (rootIsVolume && relative.Length == 0 && IsWindowsVolumeArtifact(d)) continue;
            string rel = relative.Length == 0 ? d.Name : relative + "\\" + d.Name;
            if (claimed.Contains(rel)) continue;              // already a row of its own
            if (depth < MaxClaimDepth && ClaimedUnder(claimed, rel))
            {
                AddUnclaimed(d.FullName, rel, claimed, list, depth + 1, rootIsVolume);
                continue;
            }
            if (Duplicate(list, d.FullName)) continue;
            // Windows knows where "Documents" lives on THIS machine, which is
            // what makes a restore onto a fresh install work with no
            // configuration at all: GUARD's default rows use the known folder's
            // own name as the destination subfolder. Only a top-level folder is
            // asked about - a "Documents" nested inside another folder is not
            // the Windows one.
            //
            // Never at a volume root, though: there EVERY folder on the drive is
            // listed, so a stray "E:\Music" that GUARD never backed up would be
            // matched to the real Music folder and ticked for the user. GUARD
            // cannot tell its own backup from the drive's other contents there,
            // so it declines to guess and the dialog says why.
            string? known = relative.Length == 0 && !rootIsVolume ? KnownFolders.Resolve(d.Name) : null;
            list.Add(new RestoreCandidate(rel, d.FullName,
                known == null ? "" : Environment.ExpandEnvironmentVariables(known),
                known == null ? TargetOrigin.None : TargetOrigin.WindowsFolder,
                rootIsVolume ? RestoreDoubt.WholeVolumeDestination : RestoreDoubt.None));
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
        => IndexOfSource(list, source) >= 0;

    // The row already listing these copies, or -1. Separate from Duplicate
    // because a second CONFIGURED pair pointing here is not a row to skip: it is
    // a row whose suggestion has to be withdrawn (see BuildCandidates).
    private static int IndexOfSource(List<RestoreCandidate> list, string source)
    {
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i].SourcePath.TrimEnd('\\'), source.TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
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
            if (app.Length == 0) continue;
            // A GUARD unzipped to the root of a drive (a portable app on a USB
            // stick, which GuardPaths explicitly expects) does NOT own that whole
            // drive, yet the prefix test below matches every path on it - so the
            // restore refused every location on that drive with a message that
            // made no sense. At a root the containment question is already
            // settled: the root ITSELF is refused by the volume-root rule above,
            // and a folder beside the exe cannot overwrite it. What is left to
            // protect is the one subtree GUARD writes into. IsVolumeRoot trims
            // the separator Key appends, so the key goes in as it is.
            if (IsVolumeRoot(app)) app = Key(Path.Combine(folder, "Logs"));
            if (key.StartsWith(app, StringComparison.OrdinalIgnoreCase)
                || app.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                return "That folder holds GUARD's own files, so restoring there would overwrite the"
                    + " running program or its settings.";
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
