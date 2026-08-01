using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using GuardWui3.Models;

namespace GuardWui3.Services;

// Save-time checks that inform but never block: missing sources are SKIPped by
// the generated script at run time anyway, and a tight destination may still
// fit an incremental run, so all of this is advisory wording only.
public static class SaveValidation
{
    // How long the size estimate may run before degrading to a partial figure.
    // It feeds a background status line, not a modal dialog, so the cap only
    // bounds runaway walks of enormous trees, not save latency; two minutes
    // normally finishes with the complete total.
    public static readonly TimeSpan EstimateCap = TimeSpan.FromMinutes(2);

    public sealed record EstimateResult(long Bytes, bool Complete);

    // Included sources that don't currently resolve to a reachable directory.
    // Wording elsewhere says "not currently reachable" not "missing": an offline
    // share or unplugged drive is a legitimate state.
    public static List<string> UnreachableSources(IEnumerable<FolderPair> folders)
    {
        var missing = new List<string>();
        foreach (var f in folders)
        {
            if (!f.Include) continue;
            string path = Environment.ExpandEnvironmentVariables(f.Source ?? "");
            bool exists;
            try { exists = path.Length > 0 && Directory.Exists(path); }
            catch { exists = false; }
            if (!exists) missing.Add(f.Source ?? "");
        }
        return missing;
    }

    // How long the whole source-health sweep may run before it gives up.
    // Generous because it almost never binds: every walk below stops at the
    // first file it finds, so a folder holding data costs one directory read.
    // Only a genuinely empty tree is walked in full, and those are small by
    // definition. Shared across all folders, so one pathological source can use
    // up the budget - which degrades to "no warning", never to a false one.
    public static readonly TimeSpan SourceCheckCap = TimeSpan.FromSeconds(20);

    // A reparse point is normally a link the walkers must not follow (loops, and
    // content that belongs to another tree). A cloud sync placeholder is also a
    // reparse point and is NOT a link: it is the user's real file, simply not
    // resident on disk yet. OneDrive Files On-Demand marks those with the recall
    // attributes below, so treating every reparse point as a link measured a
    // fully populated OneDrive folder as empty and its size as zero.
    //
    // Numeric literals because .NET names neither recall bit:
    //   0x00400000 FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS
    //   0x00040000 FILE_ATTRIBUTE_RECALL_ON_OPEN
    private const FileAttributes CloudPlaceholder =
        (FileAttributes)0x00400000 | (FileAttributes)0x00040000 | FileAttributes.Offline;

    // DIRECTORIES only. Files are never skipped: the script passes /XJD, which
    // excludes directory junctions and nothing else, so robocopy copies a file
    // reparse point's contents and a walk that skipped them would measure a real
    // source as empty - and "empty while the backup still holds files from it"
    // is the Mirror-mode alarm that says the next run will DELETE those copies.
    // A false one there is the worst outcome this file can produce.
    //
    // Not a literal match for /XJD, which excludes directory symlinks and
    // junctions: this excludes any directory reparse point except a cloud
    // placeholder. The two agree on every tag seen in practice (junction, mount
    // point, placeholder). An exotic fourth tag would be skipped here yet copied
    // by robocopy - undercounting, which is the direction that can raise the
    // false alarm above, so that is where to look if one ever turns up.
    private static bool IsLinkDirNotContent(FileAttributes a)
        => (a & FileAttributes.ReparsePoint) != 0 && (a & CloudPlaceholder) == 0;

    // Shell metadata that is not the user's data. A folder holding only these
    // is empty for our purposes: when Windows moves a personal folder (OneDrive
    // folder backup, or the Location tab) the vacated folder is typically left
    // behind carrying just a desktop.ini, and treating that as "has files"
    // would defeat the entire check.
    private static readonly string[] NotUserData = { "desktop.ini", "thumbs.db" };

    // A source that has stopped holding anything to copy WHILE the backup still
    // holds files it copied from that source before.
    public sealed record VanishedSource(string Source, string SubFolder);

    // DestinationEmpty: the destination is reachable and holds nothing at all.
    // Combined by the caller with "a backup has run before", that means the
    // backup itself is gone - a reformatted or emptied backup drive. Worth
    // singling out because GUARD's own log lives next to the exe, not on the
    // destination, so every other signal keeps reporting the last successful run
    // in green while there is nothing left at the other end.
    // DestinationReachable: whether this check was able to look at the
    // destination at all. Without it an unplugged backup drive is
    // indistinguishable from "nothing is wrong", and callers that act on an
    // empty Vanished list - notably the acknowledgement pruning - would treat
    // "could not measure" as "measured, and it is fine".
    public sealed record SourceHealth(
        List<VanishedSource> Vanished, List<string> Unreadable,
        bool DestinationEmpty, bool DestinationReachable)
    {
        public static readonly SourceHealth None = new(new(), new(), false, false);
    }

    // The check that answers "does my backup still contain what I think it
    // does?" - and the reason it is a TRANSITION, not a state.
    //
    // "This folder is empty" cannot carry that meaning. A folder is empty
    // because Windows moved it away (dangerous), or because nobody ever used it
    // (Contacts, on virtually every PC), or because the user emptied it on
    // purpose. One dangerous case, several benign, and the benign ones include
    // GUARD's own shipped defaults - so warning on emptiness alone fires out of
    // the box, every night, and teaches the user to ignore it.
    //
    // "This folder is empty AND the backup still holds files from it" is
    // unambiguous: data was there, and now there is none to copy. A folder that
    // was always empty never trips it. In Mirror mode it is urgent rather than
    // advisory, because the next run makes the backup match the source - it will
    // delete the copies too.
    //
    // Unreadable sources are reported separately: a folder the account cannot
    // enumerate is a different problem, and calling it empty would send the user
    // looking for a move that never happened.
    public static SourceHealth CheckSources(Settings cfg, TimeSpan cap)
    {
        var vanished = new List<VanishedSource>();
        var unreadable = new List<string>();
        var deadline = DateTime.UtcNow + cap;

        // Where a previous run's files would be. Nothing to compare against
        // means nothing to say - a first-ever run has no history to lose.
        var roots = BackupRoots(cfg);
        string[] exDirs = cfg.EffectiveExcludeDirs().ToArray();
        string[] exFiles = cfg.EffectiveExcludeFiles().ToArray();

        foreach (var f in cfg.Folders)
        {
            if (!f.Include) continue;
            try
            {
                string src = Environment.ExpandEnvironmentVariables(f.Source ?? "");
                // Unreachable is a different, already-reported condition.
                if (src.Length == 0 || !Directory.Exists(src)) continue;

                switch (Scan(src, exDirs, exFiles, deadline))
                {
                    // Unknown alongside HasFiles: both mean "no warning from
                    // this folder", which is where an unmeasured source belongs.
                    case TreeContent.HasFiles:
                    case TreeContent.Unknown:
                        continue;
                    case TreeContent.Unreadable:
                        unreadable.Add(f.Source ?? "");
                        continue;
                }

                if (roots.Count > 0 && BackupHoldsFilesFor(roots, f.SubFolder, deadline))
                    vanished.Add(new VanishedSource(f.Source ?? "", f.SubFolder ?? ""));
            }
            catch { }
        }

        // Only meaningful when the destination is actually there: an unplugged
        // drive is a different, already-reported condition, and roots is empty
        // for both cases, so reachability is asked separately.
        bool destEmpty = false, destReachable = false;
        try
        {
            string dest = Environment.ExpandEnvironmentVariables((cfg.Dest ?? "").Trim());
            if (dest.Length > 0 && Directory.Exists(dest))
            {
                destReachable = true;
                // Empty is the ALARM for this caller, unlike everywhere else, so
                // a walk that ran out of budget must not answer it: Unknown
                // leaves destEmpty false and stays quiet.
                destEmpty = Scan(dest, Array.Empty<string>(), Array.Empty<string>(), deadline)
                            == TreeContent.Empty;
            }
        }
        catch { }

        return new SourceHealth(vanished, unreadable, destEmpty, destReachable);
    }

    // Cheap reachability probe for callers that would otherwise walk every
    // source tree to learn nothing: with no destination there is no history to
    // compare against, so Vanished is provably empty before the walk begins.
    public static bool DestinationReachable(Settings cfg)
    {
        try
        {
            string dest = Environment.ExpandEnvironmentVariables((cfg.Dest ?? "").Trim());
            return dest.Length > 0 && Directory.Exists(dest);
        }
        catch { return false; }
    }

    // Acknowledgement is keyed on the expanded source path: the stored form may
    // be %USERPROFILE%-relative, and a row that later gets pinned to the literal
    // path is still the same folder to the user.
    private static string AckKey(string source) =>
        Environment.ExpandEnvironmentVariables(source ?? "").TrimEnd('\\', '/');

    private static bool HasKey(List<string> keys, string key)
    {
        foreach (var k in keys)
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static List<string> SplitAck(string? raw)
    {
        var list = new List<string>();
        foreach (var p in (raw ?? "").Split(AppPrefs.ListSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = p.Trim();
            if (t.Length > 0) list.Add(t);
        }
        return list;
    }

    // The entries worth reporting: everything the user has not already said is
    // deliberately empty.
    public static List<VanishedSource> Unacknowledged(
        List<VanishedSource> vanished, string? acknowledged)
    {
        var ack = SplitAck(acknowledged);
        if (ack.Count == 0) return vanished;
        var report = new List<VanishedSource>();
        foreach (var v in vanished)
            if (!HasKey(ack, AckKey(v.Source))) report.Add(v);
        return report;
    }

    // The acknowledgement list with anything that is no longer vanished dropped,
    // so a folder that has content again stops being remembered and a real later
    // disappearance is reported rather than silently inheriting the old answer.
    public static string PruneAcknowledged(List<VanishedSource> vanished, string? acknowledged)
    {
        var ack = SplitAck(acknowledged);
        if (ack.Count == 0) return "";
        var live = new List<string>();
        foreach (var a in ack)
            foreach (var v in vanished)
                if (string.Equals(AckKey(v.Source), a, StringComparison.OrdinalIgnoreCase))
                { live.Add(a); break; }
        return string.Join(AppPrefs.ListSeparator, live);
    }

    public static string AddAcknowledged(
        IEnumerable<VanishedSource> vanished, string? acknowledged)
    {
        var ack = SplitAck(acknowledged);
        foreach (var v in vanished)
        {
            string key = AckKey(v.Source);
            if (!HasKey(ack, key)) ack.Add(key);
        }
        return string.Join(AppPrefs.ListSeparator, ack);
    }

    // The destination folders a previous run's files could be sitting in. In
    // versioned mode each run writes its own dated folder, so every one of them
    // counts as history - checking only the newest would go quiet the moment a
    // run created an empty folder for today.
    private static List<string> BackupRoots(Settings cfg)
    {
        var roots = new List<string>();
        try
        {
            string dest = Environment.ExpandEnvironmentVariables((cfg.Dest ?? "").Trim());
            if (dest.Length == 0 || !Directory.Exists(dest)) return roots;
            if (!cfg.Versioned) { roots.Add(dest); return roots; }
            foreach (var d in new DirectoryInfo(dest).EnumerateDirectories())
                if (IsDateStamp(d.Name)) roots.Add(d.FullName);
        }
        catch { }
        return roots;
    }

    // The YYYY-MM-DD names BackupScript's :prune recognizes, matched the same way.
    private static bool IsDateStamp(string name)
    {
        if (name.Length != 10 || name[4] != '-' || name[7] != '-') return false;
        foreach (int i in new[] { 0, 1, 2, 3, 5, 6, 8, 9 })
            if (!char.IsAsciiDigit(name[i])) return false;
        return true;
    }

    private static bool BackupHoldsFilesFor(List<string> roots, string? subFolder, DateTime deadline)
    {
        string sub = (subFolder ?? "").Trim().Trim('\\');
        foreach (var root in roots)
        {
            try
            {
                string target = sub.Length > 0 ? Path.Combine(root, sub) : root;
                if (!Directory.Exists(target)) continue;
                // No exclusion tokens here: the destination holds whatever a run
                // actually copied, not what the current rules would copy.
                // Only a definite HasFiles counts as history. An exhausted
                // budget (Unknown) reads as "no history", so an always-empty
                // source is never reported vanished on the strength of a walk
                // that did not finish.
                if (Scan(target, Array.Empty<string>(), Array.Empty<string>(), deadline)
                    == TreeContent.HasFiles)
                    return true;
            }
            catch { }
        }
        return false;
    }

    // Unknown = the walk ran out of budget before it could answer. A real
    // value, not a synonym for either outcome: "empty" is the quiet answer for a
    // source and the ALARM for a destination, so a single fallback cannot be
    // safe for both. Each caller maps Unknown to its own silent answer.
    private enum TreeContent { HasFiles, Empty, Unreadable, Unknown }

    // Whether the tree holds at least one file the backup would actually copy.
    // Mirrors TrySumTree's rules (the same exclusion tokens, and links skipped
    // the way robocopy's /XJD skips directory junctions) but returns the moment
    // it finds one. See IsLinkDirNotContent for why files are never skipped and
    // why a cloud placeholder still counts as content.
    //
    // A failure to enumerate the ROOT is reported as Unreadable rather than
    // Empty; deeper failures are not, since a partial walk that found files has
    // already answered the question. Running out of budget returns Unknown,
    // which every caller maps to its own quiet answer - so an unmeasured tree
    // can never become a false alarm in either direction.
    private static TreeContent Scan(string src, string[] exDirs, string[] exFiles, DateTime deadline)
    {
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(src));
        bool root = true;
        while (stack.Count > 0)
        {
            if (DateTime.UtcNow > deadline) return TreeContent.Unknown;
            var dir = stack.Pop();
            try
            {
                foreach (var f in dir.EnumerateFiles())
                {
                    if (MatchesAny(f.Name, exFiles)) continue;
                    if (IsNotUserData(f.Name)) continue;
                    return TreeContent.HasFiles;
                }
                foreach (var d in dir.EnumerateDirectories())
                {
                    if (IsLinkDirNotContent(d.Attributes)) continue;
                    if (MatchesAny(d.Name, exDirs)) continue;
                    stack.Push(d);
                }
            }
            catch when (root) { return TreeContent.Unreadable; }
            catch { }
            root = false;
        }
        return TreeContent.Empty;
    }

    private static bool IsNotUserData(string name)
    {
        foreach (var n in NotUserData)
            if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // The one overlap that must BLOCK a save (everything else here is advisory):
    // a source tree containing the destination, or sitting inside it, makes the
    // script copy the backup into itself. Robocopy walks the source recursively
    // and re-copies the destination it just wrote, so every run nests one level
    // deeper (DEST\Sub\DEST\Sub\...) until paths pass MAX_PATH and the tree can't
    // be read or deleted. Returns the raw Source strings of included pairs
    // overlapping DEST, in list order. Sharing a drive root is NOT overlap: C:\
    // is a common ancestor, not containment, so C:\Users\me\Documents -> C:\Backup
    // is allowed.
    public static List<string> OverlappingSources(string dest, IEnumerable<FolderPair> folders)
    {
        var bad = new List<string>();
        string destKey = CompareKey(dest);
        if (destKey.Length == 0) return bad;
        foreach (var f in folders)
        {
            if (!f.Include) continue;
            string srcKey = CompareKey(f.Source);
            if (srcKey.Length == 0) continue;
            // Both keys end in a separator, so StartsWith only matches whole-
            // segment boundaries and catches containment in either direction
            // (and equality). C:\Foo does not "contain" C:\Foobar.
            if (destKey.StartsWith(srcKey, StringComparison.OrdinalIgnoreCase)
                || srcKey.StartsWith(destKey, StringComparison.OrdinalIgnoreCase))
                bad.Add(f.Source ?? "");
        }
        return bad;
    }

    // Mirror-mode destination collisions, which must BLOCK a save like the
    // source/destination overlap: /MIR deletes destination files not present in
    // its own source, so two included pairs whose subfolders coincide (or nest)
    // have each run purge the other pair's output - the backup silently ends up
    // holding only the later pair. A legacy empty subfolder means the destination
    // root itself, which collides with every other pair. Additive mode never
    // deletes, so merged subfolders stay allowed there.
    public static List<string> MirrorSubfolderConflicts(IEnumerable<FolderPair> folders)
    {
        var keys = new List<(string Key, string Label)>();
        foreach (var f in folders)
        {
            if (!f.Include) continue;
            string sub = (f.SubFolder ?? "").Trim().Trim('\\');
            // Empty (root) keys as "" so it prefixes everything; non-empty keys
            // end in a separator so prefix tests match whole segments only.
            keys.Add((sub.Length > 0 ? sub + "\\" : "",
                      sub.Length > 0 ? sub : "(the destination root)"));
        }
        var conflicts = new List<string>();
        for (int i = 0; i < keys.Count; i++)
            for (int j = i + 1; j < keys.Count; j++)
                if (keys[i].Key.StartsWith(keys[j].Key, StringComparison.OrdinalIgnoreCase)
                    || keys[j].Key.StartsWith(keys[i].Key, StringComparison.OrdinalIgnoreCase))
                    conflicts.Add(keys[i].Label + "  and  " + keys[j].Label);
        return conflicts;
    }

    // Destination and included sources holding a % that does not resolve as an
    // environment variable. cmd expands %...% at parse time in the generated
    // script, so an unresolved percent silently rewrites the path (a lone
    // trailing % is dropped, an accidental %pair% vanishes) and the backup can
    // read or write the wrong folder. Advisory, like UnreachableSources: the
    // name is legal on disk, so the save proceeds with a warning.
    public static List<string> UnresolvedPercentPaths(string? dest, IEnumerable<FolderPair> folders)
    {
        var bad = new List<string>();
        Check(dest);
        foreach (var f in folders)
            if (f.Include) Check(f.Source);
        return bad;

        void Check(string? raw)
        {
            string p = (raw ?? "").Trim();
            if (p.Contains('%') && Environment.ExpandEnvironmentVariables(p).Contains('%'))
                bad.Add(p);
        }
    }

    // Full path, env-expanded, normalized, and forced to end in a separator so a
    // StartsWith prefix test compares whole path segments. Empty for a blank or
    // unparseable path so the caller skips it rather than guessing at overlap.
    private static string CompareKey(string? raw)
    {
        string p = Environment.ExpandEnvironmentVariables((raw ?? "").Trim());
        if (p.Length == 0) return "";
        try { p = Path.GetFullPath(p); }
        catch { return ""; }
        return p.EndsWith(Path.DirectorySeparatorChar) ? p : p + Path.DirectorySeparatorChar;
    }

    // GetDiskFreeSpaceEx handles both local paths and UNC shares, unlike
    // DriveInfo which throws on \\server\share roots.
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetDiskFreeSpaceExW(
        string lpDirectoryName, out ulong freeBytesAvailable,
        out ulong totalBytes, out ulong totalFreeBytes);

    public static long? TryGetFreeSpace(string dest)
    {
        try
        {
            string path = Environment.ExpandEnvironmentVariables((dest ?? "").Trim());
            if (path.Length == 0 || !Directory.Exists(path)) return null;
            if (!path.EndsWith(Path.DirectorySeparatorChar)) path += Path.DirectorySeparatorChar;
            if (GetDiskFreeSpaceExW(path, out ulong avail, out _, out _))
                return (long)avail;
        }
        catch { }
        return null;
    }

    // Sums the included source trees on a worker thread under a hard time cap.
    // If the cap hits, the partial total is returned flagged incomplete so the
    // caller can label it honestly. Honours the exclusion tokens so the figure
    // reflects what a run would actually copy - counting excluded trees (a
    // node_modules, say) overstated the size and raised false low-space warnings.
    public static Task<EstimateResult> EstimateBackupSizeAsync(
        IEnumerable<FolderPair> folders, List<string> excludeDirs, List<string> excludeFiles, TimeSpan cap)
    {
        var sources = new List<string>();
        foreach (var f in folders)
            if (f.Include) sources.Add(Environment.ExpandEnvironmentVariables(f.Source ?? ""));
        string[] exDirs = excludeDirs.ToArray(), exFiles = excludeFiles.ToArray();

        return Task.Run(() =>
        {
            long total = 0;
            var deadline = DateTime.UtcNow + cap;
            foreach (var src in sources)
            {
                try
                {
                    if (src.Length == 0 || !Directory.Exists(src)) continue;
                    if (!TrySumTree(src, exDirs, exFiles, deadline, CancellationToken.None, ref total))
                        return new EstimateResult(total, Complete: false);
                }
                catch { }
            }
            return new EstimateResult(total, Complete: true);
        });
    }

    // Shorter cap than the status-line estimate: this runs at the start of a
    // backup and adds startup latency, so bound it tighter and fall back to
    // per-folder progress if a giant tree doesn't finish in time.
    public static readonly TimeSpan RunSizeCap = TimeSpan.FromSeconds(60);

    // Per-included-folder byte totals, in the SAME order the script processes
    // them (both walk `folders where Include`), so indexes line up with the
    // script's @@PROGRESS@@ markers. One entry per included folder, 0 for a
    // missing/unreadable source (still emitted, to keep alignment). Returns null
    // if cancelled or the cap is hit (a partial set would give wrong offsets), so
    // the caller falls back.
    public static Task<List<long>?> MeasureIncludedFolderSizesAsync(
        IEnumerable<FolderPair> folders, List<string> excludeDirs, List<string> excludeFiles,
        TimeSpan cap, CancellationToken ct)
    {
        var sources = new List<string>();
        foreach (var f in folders)
            if (f.Include) sources.Add(Environment.ExpandEnvironmentVariables(f.Source ?? ""));
        string[] exDirs = excludeDirs.ToArray(), exFiles = excludeFiles.ToArray();

        return Task.Run<List<long>?>(() =>
        {
            var sizes = new List<long>(sources.Count);
            var deadline = DateTime.UtcNow + cap;
            foreach (var src in sources)
            {
                long sum = 0;
                try
                {
                    if (src.Length > 0 && Directory.Exists(src)
                        && !TrySumTree(src, exDirs, exFiles, deadline, ct, ref sum))
                        return null;
                }
                catch { }
                sizes.Add(sum);
            }
            return sizes;
        }, ct);
    }

    // Walks one source tree adding file sizes to total, honouring the exclusion
    // tokens the generated script passes to robocopy (/XD folder names, /XF file
    // patterns; wildcards allowed) and skipping directory links like /XJD, so both
    // size figures track what robocopy would copy. The source root itself is
    // never name-matched (robocopy /XD only excludes subdirectories). Returns
    // false when the deadline or token cut the walk short (total holds a partial
    // sum); inaccessible directories are skipped, not fatal.
    private static bool TrySumTree(string src, string[] exDirs, string[] exFiles,
        DateTime deadline, CancellationToken ct, ref long total)
    {
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(src));
        while (stack.Count > 0)
        {
            if (ct.IsCancellationRequested || DateTime.UtcNow > deadline) return false;
            var dir = stack.Pop();
            try
            {
                foreach (var f in dir.EnumerateFiles())
                {
                    if (MatchesAny(f.Name, exFiles)) continue;
                    total += f.Length;
                    if (DateTime.UtcNow > deadline) return false;
                }
                foreach (var d in dir.EnumerateDirectories())
                {
                    if (IsLinkDirNotContent(d.Attributes)) continue;
                    if (MatchesAny(d.Name, exDirs)) continue;
                    stack.Push(d);
                }
            }
            catch { }
        }
        return true;
    }

    private static bool MatchesAny(string name, string[] patterns)
    {
        foreach (var p in patterns)
            if (FileSystemName.MatchesSimpleExpression(p, name, ignoreCase: true)) return true;
        return false;
    }

    public static string FormatBytes(long bytes)
    {
        const double KB = 1024, MB = KB * 1024, GB = MB * 1024, TB = GB * 1024;
        double b = bytes;
        string s;
        if (b >= TB) s = (b / TB).ToString("0.0", CultureInfo.InvariantCulture) + " TB";
        else if (b >= GB) s = (b / GB).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
        else if (b >= MB) s = (b / MB).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        else if (b >= KB) s = (b / KB).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
        else s = bytes.ToString(CultureInfo.InvariantCulture) + " bytes";
        return s;
    }
}
