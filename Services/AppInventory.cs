using System;
using System.Collections.Generic;
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
            foreach (var row in WingetListParser.Parse(raw))
            {
                // Store rows carry their real source so the UI can label them
                // "Store" and an exported list is honest about needing the
                // Store on the target PC. Sourceless auto rows default to
                // winget (older winget builds omitted the Source column).
                string source = row.Source == "msstore" ? "msstore" : "winget";

                // A truncated name cannot match its registry entry exactly, so
                // fall back to a prefix match; only a UNIQUE prefix hit is
                // trusted (an ambiguous one would enrich the wrong app). This
                // is what created duplicate rows: the truncated winget row
                // missed the registry entry and was added as a second app.
                AppEntry? target;
                if (!byName.TryGetValue(row.Name, out target) && row.NameTruncated)
                    target = UniquePrefixMatch(ordered, row.Name);

                if (target != null)
                {
                    // Prefer winget over msstore when both know the app: a
                    // winget-source id reinstalls anywhere winget runs, while
                    // an msstore id also needs the Store (absent on LTSC and
                    // similar); never downgrade winget to msstore.
                    if (row.CanAuto &&
                        (!target.CanAuto || (target.Source == "msstore" && source == "winget")))
                    {
                        target.Source = source;
                        target.Id = row.Id;
                    }
                    if (target.Version.Length == 0 && row.Version.Length > 0)
                        target.Version = row.Version;
                }
                else if (row.CanAuto && (!row.NameTruncated || !AnyPrefixMatch(ordered, row.Name)))
                {
                    // Unknown to the registry scan: add it. A truncated name
                    // with MULTIPLE registry candidates is skipped instead -
                    // it is almost certainly one of them, and a second row
                    // with a chopped name helps nobody.
                    var e = new AppEntry
                    {
                        Name = row.Name,
                        Version = row.Version,
                        Source = source,
                        Id = row.Id,
                        Include = true
                    };
                    byName[row.Name] = e;
                    ordered.Add(e);
                }
            }
        }

        ordered.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        result.Apps = ordered;
        return result;
    }

    private static AppEntry? UniquePrefixMatch(List<AppEntry> apps, string prefix)
    {
        AppEntry? found = null;
        foreach (var a in apps)
        {
            if (!a.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (found != null) return null; // ambiguous
            found = a;
        }
        return found;
    }

    private static bool AnyPrefixMatch(List<AppEntry> apps, string prefix)
    {
        foreach (var a in apps)
            if (a.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
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
