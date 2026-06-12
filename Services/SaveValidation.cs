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
    // The estimate feeds a background status-line update rather than a modal
    // dialog, so the cap only needs to bound runaway walks of enormous trees,
    // not protect perceived save latency; two minutes lets it normally finish
    // and report the complete total.
    public static readonly TimeSpan EstimateCap = TimeSpan.FromMinutes(2);

    public sealed record EstimateResult(long Bytes, bool Complete);

    // Included sources that do not currently resolve to a reachable directory.
    // Wording elsewhere says "not currently reachable" rather than "missing":
    // an offline network source or unplugged drive is a legitimate state.
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
    // caller can label it honestly instead of overstating certainty.
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
