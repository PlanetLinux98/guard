using System;
using System.IO;

namespace GuardWui3.Services;

public sealed class TreeCopyStats
{
    public int Files;
    public long Bytes;
    public int SkippedFiles;
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
    public static void Copy(DirectoryInfo src, string dest, TreeCopyStats stats,
        Func<string, bool>? skipDirName = null, string? skipFullPath = null,
        Action<long>? addBytes = null)
        => CopyCore(src, dest, stats, skipDirName, skipFullPath, addBytes, top: true);

    private static void CopyCore(DirectoryInfo src, string dest, TreeCopyStats stats,
        Func<string, bool>? skipDirName, string? skipFullPath, Action<long>? addBytes, bool top)
    {
        if (IsReparse(src)) return;
        if (!top && skipDirName != null && skipDirName(src.Name)) return;
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
                    stats.Bytes += f.Length;
                    addBytes?.Invoke(f.Length);
                }
                catch { stats.SkippedFiles++; }
            }
            foreach (var sub in src.EnumerateDirectories())
            {
                if (skipFullPath != null && string.Equals(
                        sub.FullName.TrimEnd('\\'), skipFullPath.TrimEnd('\\'),
                        StringComparison.OrdinalIgnoreCase)) continue;
                CopyCore(sub, Path.Combine(dest, sub.Name), stats, skipDirName, skipFullPath, addBytes, top: false);
            }
        }
        catch { stats.SkippedFiles++; }
    }

    private static bool IsReparse(DirectoryInfo d)
    {
        try { return (d.Attributes & FileAttributes.ReparsePoint) != 0; }
        catch { return true; }
    }
}
