using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using GuardWui3.Models;
using Microsoft.Win32;

namespace GuardWui3.Services;

public sealed class ScanResult
{
    public List<AppEntry> Apps = new();
    public bool WingetAvailable;
}

public static class AppInventory
{
    // Detect installed apps. The uninstall registry is the primary source
    // (always present); winget is an optional enrichment layer whose package
    // ids tell us which apps can be reinstalled automatically.
    public static ScanResult DetectApps()
    {
        var result = new ScanResult();
        var ordered = new List<AppEntry>();
        var byName = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);

        // 1. Registry: the always-present base list.
        foreach (var kv in ReadRegistryApps())
        {
            var ri = kv.Value;
            var e = new AppEntry
            {
                Name = ri.DisplayName,
                Version = ri.Version,
                Publisher = ri.Publisher,
                InstallLocation = ri.InstallLocation,
                PublisherUrl = ri.Url,
                Source = "manual",
                Id = "",
                Include = true
            };
            if (!byName.ContainsKey(e.Name)) { byName[e.Name] = e; ordered.Add(e); }
        }

        // 2. winget enrichment. RunCapture throws when winget is not on PATH,
        //    so a missing winget simply leaves the registry list as-is.
        string? raw = null;
        try { raw = ProcessRunner.RunCapture("winget", "list --accept-source-agreements"); result.WingetAvailable = true; }
        catch { result.WingetAvailable = false; }

        if (result.WingetAvailable && raw != null)
        {
            // winget prints a CR-only spinner preamble; split on both CR and LF
            // (dropping empties) so the header line stands alone.
            string[] lines = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            int hi = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                var l = lines[i];
                if (l.IndexOf("Name", StringComparison.Ordinal) >= 0 &&
                    l.IndexOf("Id", StringComparison.Ordinal) >= 0 &&
                    l.IndexOf("Version", StringComparison.Ordinal) >= 0)
                { hi = i; break; }
            }
            if (hi < 0)
            {
                // Localized winget translates the column words, so the English
                // match fails; the all-dash separator under the header is
                // locale-neutral, and the header above it keeps the same column
                // ORDER (Name, Id, ...) in every locale.
                for (int i = 1; i < lines.Length; i++)
                {
                    var t = lines[i].Trim();
                    if (t.Length >= 10 && IsAllDashes(t)) { hi = i - 1; break; }
                }
            }
            if (hi >= 0)
            {
                // The Id column starts at the header's "Id" word or, on a
                // localized header, at its second whitespace-delimited word.
                int idStart = lines[hi].IndexOf("Id", StringComparison.Ordinal);
                if (idStart < 0)
                {
                    var m = Regex.Match(lines[hi], @"^\S+\s+(\S)");
                    idStart = m.Success ? m.Groups[1].Index : -1;
                }
                int start = hi + 1;
                if (start < lines.Length && lines[start].TrimStart().StartsWith("---")) start++;

                for (int i = start; idStart > 0 && i < lines.Length; i++)
                {
                    string row = lines[i];
                    if (row.Trim().Length == 0) continue;

                    string name = (idStart <= row.Length ? row.Substring(0, idStart) : row).Trim();
                    name = name.TrimEnd('…').Trim();   // strip trailing ellipsis
                    if (name.Length == 0) continue;

                    string tail = row.Length > idStart ? row.Substring(idStart) : "";
                    var toks = Regex.Matches(tail, "\\S+");
                    string id = toks.Count > 0 ? toks[0].Value : "";
                    string ver = toks.Count > 1 ? toks[1].Value : "";
                    if (ver == "…") ver = "";
                    bool auto = id.Length > 0 && !id.StartsWith("ARP\\") && id.IndexOf('\\') < 0 && !id.EndsWith("…");

                    if (byName.TryGetValue(name, out var existing))
                    {
                        if (auto && !existing.CanAuto) { existing.Source = "winget"; existing.Id = id; }
                        if (existing.Version.Length == 0 && ver.Length > 0) existing.Version = ver;
                    }
                    else if (auto)
                    {
                        var e = new AppEntry
                        {
                            Name = name,
                            Version = ver,
                            Source = "winget",
                            Id = id,
                            Include = true
                        };
                        byName[name] = e;
                        ordered.Add(e);
                    }
                }
            }
        }

        ordered.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        result.Apps = ordered;
        return result;
    }

    private static bool IsAllDashes(string s)
    {
        foreach (char c in s) if (c != '-') return false;
        return true;
    }

    private sealed class RegInfo
    {
        public string DisplayName = "";
        public string Version = "";
        public string Publisher = "";
        public string InstallLocation = "";
        public string Url = "";
    }

    private static Dictionary<string, RegInfo> ReadRegistryApps()
    {
        var d = new Dictionary<string, RegInfo>();
        AddRegRoot(d, Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        AddRegRoot(d, Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall");
        AddRegRoot(d, Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        return d;
    }

    private static void AddRegRoot(Dictionary<string, RegInfo> d, RegistryKey root, string path)
    {
        try
        {
            using var k = root.OpenSubKey(path);
            if (k == null) return;
            foreach (var sub in k.GetSubKeyNames())
            {
                try
                {
                    using var s = k.OpenSubKey(sub);
                    if (s == null) continue;
                    var name = s.GetValue("DisplayName") as string;
                    if (string.IsNullOrEmpty(name)) continue;
                    if (s.GetValue("SystemComponent") is int sc && sc == 1) continue; // hidden system entry
                    if (s.GetValue("ParentKeyName") != null) continue;                // update/child entry
                    var key = name.ToLowerInvariant();
                    if (d.ContainsKey(key)) continue;
                    d[key] = new RegInfo
                    {
                        DisplayName = name,
                        Version = (s.GetValue("DisplayVersion") as string) ?? "",
                        Publisher = (s.GetValue("Publisher") as string) ?? "",
                        InstallLocation = (s.GetValue("InstallLocation") as string) ?? "",
                        Url = (s.GetValue("URLInfoAbout") as string) ?? ""
                    };
                }
                catch { }
            }
        }
        catch { }
    }
}
