using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
    // caller can label it honestly.
    public static Task<EstimateResult> EstimateBackupSizeAsync(IEnumerable<FolderPair> folders, TimeSpan cap)
    {
        var sources = new List<string>();
        foreach (var f in folders)
            if (f.Include) sources.Add(Environment.ExpandEnvironmentVariables(f.Source ?? ""));

        return Task.Run(() =>
        {
            long total = 0;
            var deadline = DateTime.UtcNow + cap;
            var opts = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            foreach (var src in sources)
            {
                try
                {
                    if (src.Length == 0 || !Directory.Exists(src)) continue;
                    foreach (var file in new DirectoryInfo(src).EnumerateFiles("*", opts))
                    {
                        try { total += file.Length; } catch { }
                        if (DateTime.UtcNow > deadline)
                            return new EstimateResult(total, Complete: false);
                    }
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
        IEnumerable<FolderPair> folders, TimeSpan cap, CancellationToken ct)
    {
        var sources = new List<string>();
        foreach (var f in folders)
            if (f.Include) sources.Add(Environment.ExpandEnvironmentVariables(f.Source ?? ""));

        return Task.Run<List<long>?>(() =>
        {
            var sizes = new List<long>(sources.Count);
            var deadline = DateTime.UtcNow + cap;
            var opts = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            foreach (var src in sources)
            {
                long sum = 0;
                try
                {
                    if (src.Length > 0 && Directory.Exists(src))
                    {
                        foreach (var file in new DirectoryInfo(src).EnumerateFiles("*", opts))
                        {
                            try { sum += file.Length; } catch { }
                            if (ct.IsCancellationRequested || DateTime.UtcNow > deadline) return null;
                        }
                    }
                }
                catch { }
                sizes.Add(sum);
            }
            return sizes;
        }, ct);
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
