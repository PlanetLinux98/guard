using System;
using System.IO;

namespace GuardWui3.Services;

public sealed class TreeCopyStats
{
    public int Files;
    public long Bytes;
    public int SkippedFiles;
    // An enumeration-level failure (e.g. UnauthorizedAccessException reading an
    // ACL-restricted folder), distinct from SkippedFiles: that failure abandons
    // everything under the folder, not one file, so counting it as SkippedFiles
    // would understate a possibly large abandoned subtree as "1 file skipped".
    public int SkippedFolders;
}

// The one tree-copy walker behind the app-settings export AND restore (they
// had drifted into two near-identical private copies). Locked or unreadable
// files are skipped and counted, never fatal; junctions/symlinks are skipped
// rather than followed, since following them can loop forever or pull in
// trees the user never confirmed.
public static class FileTreeCopy
{
    // skipDirName: subdirectory filter by name (export's cache-skip); never
    // applied to the root itself, matching robocopy's /XD semantics.
    // skipFullPath: one absolute path never to descend into (export skips its
    // own output when the export destination sits inside a copied folder).
    // addBytes: per-file byte callback; throttling is the caller's business.
    //
    // Returns FALSE when the source root could not be walked at all - it is
    // missing, unreadable, or itself a link - and so nothing whatsoever was
    // copied. That case has to be distinguishable from a completed copy: the
    // recursion below reports every other problem through the counters and
    // throws nothing, so a caller that has already moved the live folder aside
    // (AppSettingsRestore) would otherwise take a silent no-op for a finished
    // restore and leave the user's data stranded under its aside name. The
    // root check is separate from the per-directory reparse skip inside the
    // walk, which is a deliberate "do not follow links" rule for SUBfolders.
    public static bool Copy(DirectoryInfo src, string dest, TreeCopyStats stats,
        Func<string, bool>? skipDirName = null, string? skipFullPath = null,
        Action<long>? addBytes = null)
    {
        // Refreshed first: DirectoryInfo caches its existence/attribute state
        // from whenever it was constructed, and the callers build theirs from a
        // path that was checked earlier (an app install can run for minutes in
        // between - long enough for the USB holding the bundle to be pulled).
        src.Refresh();
        if (!src.Exists || IsReparse(src)) return false;
        CopyCore(src, dest, stats, skipDirName, skipFullPath, addBytes, top: true);
        return true;
    }

    private static void CopyCore(DirectoryInfo src, string dest, TreeCopyStats stats,
        Func<string, bool>? skipDirName, string? skipFullPath, Action<long>? addBytes, bool top)
    {
        if (IsReparse(src)) return;
        if (!top && skipDirName != null && skipDirName(src.Name)) return;
        try { Directory.CreateDirectory(dest); }
        catch { stats.SkippedFiles++; return; }

        // Enumeration is lazy - EnumerateFiles()/EnumerateDirectories() only
        // throw once MoveNext() actually reads the directory, which can happen
        // partway through a large listing (an ACL-restricted subtree, say).
        // That failure is walled off from the per-item try/catch below with its
        // own counter (SkippedFolders), a manual enumerator loop rather than a
        // foreach around the whole thing, so it can never fall into the same
        // catch as an individual file's copy failure and be undercounted as
        // "1 file skipped" for what could be an entire abandoned subtree.
        using (var e = src.EnumerateFiles().GetEnumerator())
        {
            while (true)
            {
                FileInfo f;
                try { if (!e.MoveNext()) break; f = e.Current; }
                catch { stats.SkippedFolders++; break; }
                // Skip symlink/hard-link reparse points rather than following
                // them, matching the directory-level skip above (IsReparse) and
                // SaveValidation.TrySumTree's convention for the equivalent
                // file case.
                if ((f.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                try
                {
                    f.CopyTo(Path.Combine(dest, f.Name), true);
                    stats.Files++;
                    stats.Bytes += f.Length;
                    addBytes?.Invoke(f.Length);
                }
                catch { stats.SkippedFiles++; }
            }
        }
        using (var e = src.EnumerateDirectories().GetEnumerator())
        {
            while (true)
            {
                DirectoryInfo sub;
                try { if (!e.MoveNext()) break; sub = e.Current; }
                catch { stats.SkippedFolders++; break; }
                if (skipFullPath != null && string.Equals(
                        sub.FullName.TrimEnd('\\'), skipFullPath.TrimEnd('\\'),
                        StringComparison.OrdinalIgnoreCase)) continue;
                CopyCore(sub, Path.Combine(dest, sub.Name), stats, skipDirName, skipFullPath, addBytes, top: false);
            }
        }
    }

    private static bool IsReparse(DirectoryInfo d)
    {
        try { return (d.Attributes & FileAttributes.ReparsePoint) != 0; }
        catch { return true; }
    }
}
