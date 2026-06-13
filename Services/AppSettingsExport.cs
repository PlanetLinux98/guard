using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using GuardWui3.Models;

namespace GuardWui3.Services;

public sealed class AppSettingsCopyStats
{
    public int Folders;
    public int Files;
    public long Bytes;
    public int SkippedFiles;
}

// Finds and copies per-user settings folders for selected apps. There is no
// reliable general mapping from an installed app to its config folder, so this
// is a name-matching heuristic over the three common per-user config roots; the
// caller must show the results for confirmation before CopyCandidates runs.
public static class AppSettingsExport
{
    public const string OutputFolderName = "AppSettings";
    public const string ManifestFileName = "app-settings-manifest.json";
    public const string ReadmeFileName = "README.txt";

    public sealed record SettingsRoot(string Name, string Anchor, string Path);

    // Generic words that never identify an app's folder on their own; matching
    // on them would drag in unrelated directories (or, for "microsoft", half
    // the profile). Applied to both app-name and publisher tokens.
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "microsoft", "windows", "version", "edition", "update", "setup",
        "installer", "runtime", "redistributable", "application", "program",
        "tools", "driver", "package", "win64", "win32", "64bit", "32bit",
        "corporation", "incorporated", "limited", "gmbh", "software",
        "systems", "technologies", "company", "foundation", "project", "team",
    };

    // Root children that are shared OS/platform buckets, never a single app's
    // settings: offering them would invite copying gigabytes of unrelated (or
    // unrestorable, in Packages' case) state.
    private static readonly HashSet<string> DeniedRootChildren = new(StringComparer.Ordinal)
    {
        "microsoft", "packages", "temp", "programs", "comms",
        "connecteddevicesplatform", "packagestaging",
    };

    public static List<SettingsRoot> GetRoots()
    {
        var roots = new List<SettingsRoot>();
        Add("AppData", "%APPDATA%",
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        Add("LocalAppData", "%LOCALAPPDATA%",
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Add("DotConfig", "%USERPROFILE%\\.config",
            string.IsNullOrEmpty(profile) ? "" : Path.Combine(profile, ".config"));
        return roots;

        void Add(string name, string anchor, string path)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                roots.Add(new SettingsRoot(name, anchor, path));
        }
    }

    public static List<AppSettingsCandidate> FindCandidates(IEnumerable<AppEntry> apps)
    {
        // Pre-compute each app's match keys once; the roots' children are then
        // compared against every app in one pass per directory.
        var keyed = new List<(AppEntry App, string NormName, HashSet<string> Keys)>();
        foreach (var a in apps)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in SignificantTokens(a.Name)) keys.Add(t);
            string normPub = Normalize(a.Publisher);
            if (normPub.Length >= 4 && !StopWords.Contains(normPub)) keys.Add(normPub);
            foreach (var t in SignificantTokens(a.Publisher)) keys.Add(t);
            keyed.Add((a, Normalize(a.Name), keys));
        }

        var byPath = new Dictionary<string, AppSettingsCandidate>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<AppSettingsCandidate>();
        foreach (var root in GetRoots())
        {
            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(root.Path); }
            catch { continue; }
            foreach (var dir in dirs)
            {
                string leaf = Path.GetFileName(dir);
                string norm = Normalize(leaf);
                if (norm.Length < 3 || DeniedRootChildren.Contains(norm)) continue;
                try
                {
                    if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch { continue; }

                foreach (var (app, normName, keys) in keyed)
                {
                    // Exact full-name match, or a significant name/publisher
                    // token match (tokens are pre-filtered to >= 4 chars and
                    // non-generic, so "Code" can match VS Code's folder but
                    // "App" or "x64" never match anything).
                    bool hit = norm == normName || keys.Contains(norm);
                    if (!hit) continue;
                    if (!byPath.TryGetValue(dir, out var cand))
                    {
                        cand = new AppSettingsCandidate
                        {
                            FolderPath = dir,
                            FolderName = leaf,
                            RootName = root.Name,
                            RootAnchor = root.Anchor,
                        };
                        byPath[dir] = cand;
                        ordered.Add(cand);
                    }
                    if (!cand.MatchedApps.Contains(app.Name)) cand.MatchedApps.Add(app.Name);
                }
            }
        }
        return ordered;
    }

    // Size pre-scan for the confirmation list. Runs in the background after
    // the dialog opens (rows show "Calculating..." meanwhile), so the caps can
    // be generous; they remain only so one pathological tree cannot spin the
    // scan forever. When a cap is hit the result is marked partial and shown
    // as a floor ("at least ...").
    public static void MeasureCandidate(AppSettingsCandidate c, int maxFiles = 200_000, int maxMs = 15_000)
    {
        long bytes = 0; int files = 0; bool partial = false;
        var sw = Stopwatch.StartNew();
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(c.FolderPath));
        bool top = true;
        while (stack.Count > 0)
        {
            if (files >= maxFiles || sw.ElapsedMilliseconds >= maxMs) { partial = true; break; }
            var d = stack.Pop();
            // Apply the same skip rules as the copy, so the size shown matches
            // what would actually be copied (the ticked folder itself is never
            // cache-skipped; only its subfolders are).
            if (!top && (IsCacheDir(d.Name) || IsReparse(d))) continue;
            top = false;
            try
            {
                foreach (var f in d.EnumerateFiles())
                {
                    bytes += f.Length;
                    if (++files >= maxFiles) break;
                }
                foreach (var sub in d.EnumerateDirectories()) stack.Push(sub);
            }
            catch { partial = true; }
        }
        c.Bytes = bytes;
        c.Files = files;
        c.SizePartial = partial || stack.Count > 0;
    }

    // Copies the confirmed folders under destBase\AppSettings\<Root>\<Folder>
    // and writes the manifest + readme. Locked/unreadable files are skipped and
    // counted, never fatal. progress receives one line per folder.
    public static AppSettingsCopyStats CopyCandidates(
        List<AppSettingsCandidate> picked, string destBase, Action<string>? progress)
    {
        string outBase = Path.Combine(destBase, OutputFolderName);
        Directory.CreateDirectory(outBase);
        var stats = new AppSettingsCopyStats();
        var entries = new List<AppSettingsManifestEntry>();

        for (int i = 0; i < picked.Count; i++)
        {
            var c = picked[i];
            progress?.Invoke("Copying settings: " + c.DisplayPath + " (" + (i + 1) + " of " + picked.Count + ")...");
            string dest = Path.Combine(outBase, c.RootName, c.FolderName);
            var folderStats = new AppSettingsCopyStats();
            CopyTree(new DirectoryInfo(c.FolderPath), dest, outBase, folderStats, top: true);
            stats.Folders++;
            stats.Files += folderStats.Files;
            stats.Bytes += folderStats.Bytes;
            stats.SkippedFiles += folderStats.SkippedFiles;
            entries.Add(new AppSettingsManifestEntry
            {
                Apps = c.MatchedApps.ToArray(),
                Root = c.RootName,
                RootAnchor = c.RootAnchor,
                Folder = c.FolderName,
                SourcePath = c.FolderPath,
                DestRelativePath = OutputFolderName + "\\" + c.RootName + "\\" + c.FolderName,
                Files = folderStats.Files,
                Bytes = folderStats.Bytes,
                SkippedFiles = folderStats.SkippedFiles,
            });
        }

        WriteManifest(outBase, entries);
        WriteReadme(outBase);
        return stats;
    }

    private static void CopyTree(DirectoryInfo src, string dest, string outBase,
        AppSettingsCopyStats stats, bool top)
    {
        // Junctions/symlinks are skipped rather than followed: following them
        // can loop forever or pull in trees the user never confirmed.
        if (IsReparse(src)) return;
        if (!top && IsCacheDir(src.Name)) return;
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
                }
                catch { stats.SkippedFiles++; }
            }
            foreach (var sub in src.EnumerateDirectories())
            {
                // Never recurse into our own output (possible if the export
                // destination sits inside a folder being copied).
                if (string.Equals(sub.FullName.TrimEnd('\\'), outBase.TrimEnd('\\'),
                        StringComparison.OrdinalIgnoreCase)) continue;
                CopyTree(sub, Path.Combine(dest, sub.Name), outBase, stats, top: false);
            }
        }
        catch { stats.SkippedFiles++; }
    }

    private static void WriteManifest(string outBase, List<AppSettingsManifestEntry> entries)
    {
        var manifest = new AppSettingsManifest
        {
            Exported = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            Machine = Environment.MachineName,
            UserProfile = Environment.UserName,
            RestoreNote = "To restore: install the app, then copy each folder back under the " +
                "location its rootAnchor names (e.g. AppSettings\\AppData\\Foo goes to %APPDATA%\\Foo). " +
                "The environment variables resolve to the NEW user profile automatically.",
            Entries = entries.ToArray(),
        };
        using var fs = File.Create(Path.Combine(outBase, ManifestFileName));
        JsonSerializer.Serialize(fs, manifest, GuardJsonContext.Default.AppSettingsManifest);
    }

    private static void WriteReadme(string outBase)
    {
        var sb = new StringBuilder();
        sb.AppendLine("GUARD app settings export");
        sb.AppendLine("=========================");
        sb.AppendLine();
        sb.AppendLine("Created " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") +
                      " on " + Environment.MachineName +
                      " for Windows user " + Environment.UserName + ".");
        sb.AppendLine();
        sb.AppendLine("Each folder in here was copied from one of these per-user locations:");
        sb.AppendLine();
        sb.AppendLine("  AppSettings\\AppData\\<name>       came from  %APPDATA%\\<name>");
        sb.AppendLine("  AppSettings\\LocalAppData\\<name>  came from  %LOCALAPPDATA%\\<name>");
        sb.AppendLine("  AppSettings\\DotConfig\\<name>     came from  %USERPROFILE%\\.config\\<name>");
        sb.AppendLine();
        sb.AppendLine("To restore after reinstalling Windows: install the app first, then copy");
        sb.AppendLine("each folder back into the matching location shown above. The environment");
        sb.AppendLine("variables resolve to the NEW user profile automatically, so this works");
        sb.AppendLine("even if the Windows username changed.");
        sb.AppendLine();
        sb.AppendLine("Not included in this export:");
        sb.AppendLine("  - settings stored in the Windows registry");
        sb.AppendLine("  - per-machine data in C:\\ProgramData");
        sb.AppendLine("  - Microsoft Store packaged app state (%LOCALAPPDATA%\\Packages)");
        sb.AppendLine("  - cache subfolders (any subfolder whose name contains \"cache\",");
        sb.AppendLine("    plus crash-dump folders) - apps rebuild these on their own");
        sb.AppendLine();
        sb.AppendLine(ManifestFileName + " lists exactly what was copied and where it came from.");
        File.WriteAllText(Path.Combine(outBase, ReadmeFileName), sb.ToString());
    }

    // Cache subtrees are bulk an app rebuilds on its own (browser caches alone
    // can be gigabytes); copying them bloats the export for zero restore value.
    private static bool IsCacheDir(string name)
    {
        string n = Normalize(name);
        return n.Contains("cache") || n == "crashpad" || n == "crashreports" || n == "crashdumps";
    }

    private static bool IsReparse(DirectoryInfo d)
    {
        try { return (d.Attributes & FileAttributes.ReparsePoint) != 0; }
        catch { return true; }
    }

    // Lowercase letters/digits only, so "Notepad++" == "notepad" and spacing or
    // punctuation differences never break a match.
    private static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (char ch in s)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    // Words of an app/publisher name that are specific enough to identify a
    // folder: at least 4 characters, contain a letter (so bare version numbers
    // never match), and not a generic stop word.
    private static IEnumerable<string> SignificantTokens(string? s)
    {
        if (string.IsNullOrEmpty(s)) yield break;
        var sb = new StringBuilder();
        foreach (char ch in s + " ")
        {
            if (char.IsLetterOrDigit(ch)) { sb.Append(char.ToLowerInvariant(ch)); continue; }
            if (sb.Length > 0)
            {
                string tok = sb.ToString();
                sb.Clear();
                if (tok.Length >= 4 && HasLetter(tok) && !StopWords.Contains(tok))
                    yield return tok;
            }
        }
    }

    private static bool HasLetter(string s)
    {
        foreach (char ch in s) if (char.IsLetter(ch)) return true;
        return false;
    }
}
