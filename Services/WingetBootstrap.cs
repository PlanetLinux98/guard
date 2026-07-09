using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GuardWui3.Models;

namespace GuardWui3.Services;

// The downloaded winget package set, staged and ready to install.
public sealed class WingetPayload
{
    public string BundlePath = "";
    public List<string> Dependencies = new();
}

// Installs winget (Microsoft's App Installer package) for the current user by
// sideloading the Microsoft-signed msixbundle from the official winget-cli
// GitHub release. Sideload, not the Microsoft Store: the systems most likely
// to lack winget (LTSC, Server, Store removed) have no Store to get it from,
// and no third-party app can drive a Store install anyway (that API is
// OEM-restricted). Windows verifies the bundle's Microsoft signature at
// deployment, so no separate checksum step is needed. A sideloaded App
// Installer is not stranded on this version: the Store updates installed
// packages by identity regardless of install source where it exists, and
// winget can update itself (winget upgrade Microsoft.AppInstaller).
// Per-user Add-AppxPackage needs no elevation and no license file (the
// license only matters for machine-wide provisioning).
public static class WingetBootstrap
{
    private const string ApiLatest = "https://api.github.com/repos/microsoft/winget-cli/releases/latest";

    // The bundle is a couple hundred MB; give slow links headroom.
    private static readonly HttpClient Http = GitHubDownloads.CreateClient(TimeSpan.FromMinutes(30));

    // Same "can I run it" semantic as the app scan's enrichment probe: the
    // winget alias resolving and answering is the only definition that matters.
    public static bool Probe()
    {
        try { ProcessRunner.RunCapture("winget", "--version"); return true; }
        catch { return false; }
    }

    // Null on any failure (offline, rate-limited, release without a bundle);
    // callers treat that as "could not check", with their own message.
    public static async Task<GitHubRelease?> FetchLatestAsync(CancellationToken ct = default)
    {
        var rel = await GitHubDownloads.FetchLatestAsync(Http, ApiLatest, ct);
        return rel is null || FindBundle(rel) is null ? null : rel;
    }

    // Matched by extension, not full name, in case Microsoft renames the asset
    // (today: Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle).
    public static GitHubAsset? FindBundle(GitHubRelease rel)
    {
        foreach (var a in rel.Assets)
            if (a.Name.EndsWith(".msixbundle", StringComparison.OrdinalIgnoreCase)) return a;
        return null;
    }

    private static GitHubAsset? FindDependencies(GitHubRelease rel)
    {
        foreach (var a in rel.Assets)
            if (a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                && a.Name.Contains("Dependencies", StringComparison.OrdinalIgnoreCase)) return a;
        return null;
    }

    private static string StageDir => Path.Combine(Path.GetTempPath(), "GUARD-winget");

    // What the whole install will pull down (the bundle plus its dependencies
    // zip, which is itself ~100 MB), for the dialog's "N MB" status line.
    public static long DownloadSizeBytes(GitHubRelease rel)
        => (FindBundle(rel)?.Size ?? 0) + (FindDependencies(rel)?.Size ?? 0);

    // Downloads the bundle and its dependency packages into a fresh staging
    // folder. Progress spans BOTH files (the dependencies zip is a third of the
    // total, so a bundle-only bar would stall at 100% for a long tail).
    public static async Task<WingetPayload> DownloadAsync(
        GitHubRelease rel, IProgress<double>? progress, CancellationToken ct)
    {
        var bundle = FindBundle(rel)
            ?? throw new InvalidOperationException("The winget release has no .msixbundle download.");
        var depAsset = FindDependencies(rel);

        if (Directory.Exists(StageDir)) Directory.Delete(StageDir, recursive: true);
        Directory.CreateDirectory(StageDir);

        long totalBytes = bundle.Size + (depAsset?.Size ?? 0);
        long doneBase = 0;
        void Report(long fileDone)
        {
            if (totalBytes > 0) progress?.Report((double)(doneBase + fileDone) / totalBytes);
        }

        var payload = new WingetPayload { BundlePath = Path.Combine(StageDir, bundle.Name) };
        await GitHubDownloads.DownloadAssetAsync(Http, bundle, payload.BundlePath, Report, ct);
        doneBase += bundle.Size;

        // Releases without a dependencies zip still install where the
        // dependencies already exist, so a missing asset degrades rather
        // than fails.
        if (depAsset is not null)
        {
            string zipPath = Path.Combine(StageDir, depAsset.Name);
            await GitHubDownloads.DownloadAssetAsync(Http, depAsset, zipPath, Report, ct);
            string depDir = Path.Combine(StageDir, "deps");
            ZipFile.ExtractToDirectory(zipPath, depDir);

            // One folder per architecture inside the zip; hand the OS's folder
            // to -DependencyPath. Dependencies already present (or newer) are
            // skipped by the deployment itself.
            string arch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X86 => "x86",
                _ => "x64",
            };
            foreach (string f in Directory.GetFiles(depDir, "*", SearchOption.AllDirectories))
                if (IsPackage(f) && (Path.GetFileName(Path.GetDirectoryName(f)) ?? "")
                        .Equals(arch, StringComparison.OrdinalIgnoreCase))
                    payload.Dependencies.Add(f);

            // Layout fallback: if the zip ever stops sorting by architecture
            // folder, take every package that is not under some OTHER
            // architecture's folder rather than silently passing none.
            if (payload.Dependencies.Count == 0)
                foreach (string f in Directory.GetFiles(depDir, "*", SearchOption.AllDirectories))
                {
                    string parent = Path.GetFileName(Path.GetDirectoryName(f)) ?? "";
                    if (IsPackage(f) && parent is not ("x64" or "x86" or "arm64"))
                        payload.Dependencies.Add(f);
                }
        }
        return payload;
    }

    private static bool IsPackage(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(".appx", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".msix", StringComparison.OrdinalIgnoreCase);
    }

    // Runs Add-AppxPackage for the current user and confirms the winget command
    // answers. Throws with the deployment's own message on failure. Not
    // cancellable by design: the deployment is transactional and takes a minute
    // or two against the download's much longer window, so killing it buys
    // nothing but a confusing half-state.
    public static void InstallPayload(WingetPayload p)
    {
        // Each dependency installs separately and best-effort, NOT as one
        // -DependencyPath call with the bundle: VCLibs / WindowsAppRuntime are
        // shared by many packaged apps, and updating one that a running app has
        // loaded fails 0x80073D02 ("resources in use") - which, in a combined
        // call, sinks the whole install even though the installed version
        // already satisfies the bundle. Only the bundle's own install decides
        // success. $_.Exception.Message alone: the full error record drags in
        // script position, CategoryInfo and a tilde underline, unreadable in
        // the dialog's status line.
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        foreach (string dep in p.Dependencies)
            sb.Append("try { Add-AppxPackage -Path '").Append(Q(dep)).AppendLine("' } catch { }");
        sb.Append("try { Add-AppxPackage -Path '").Append(Q(p.BundlePath)).AppendLine("' }");
        sb.AppendLine("catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }");
        sb.AppendLine("exit 0");

        // A script file, not -Command: the paths stay data (no quoting games)
        // and the exit codes are honoured reliably under -File.
        string ps1 = Path.Combine(StageDir, "install-winget.ps1");
        // Encoding.UTF8 (not the 2-arg WriteAllText overload, which omits the
        // BOM) so Windows PowerShell reads the file as UTF-8 instead of
        // guessing the system codepage; without a BOM a non-ASCII path (e.g.
        // an accented Windows username under StageDir) corrupts the quoted
        // paths above.
        File.WriteAllText(ps1, sb.ToString(), Encoding.UTF8);
        int code = ProcessRunner.RunPowerShellFileCapture(ps1, out string output);
        if (code != 0)
            throw new InvalidOperationException(output.Length > 0
                ? TrimDeploymentNoise(output)
                : "the deployment failed (exit code " + code + ").");

        // The winget execution alias appears under WindowsApps moments after
        // registration (alias creation is asynchronous); give it a few seconds
        // before declaring the install broken.
        for (int i = 0; i < 10; i++)
        {
            if (Probe()) return;
            Thread.Sleep(1000);
        }
        throw new InvalidOperationException(
            "the package installed, but the winget command has not appeared yet. Sign out and back in (or restart Windows), then check again.");
    }

    private static string Q(string s) => s.Replace("'", "''");

    // Deployment messages end with an "additional information" pointer at the
    // Event Log / an ActivityId GUID; useless in a dialog, so cut it. The part
    // kept names the actual failure (and any apps that must be closed).
    private static string TrimDeploymentNoise(string message)
    {
        int cut = message.IndexOf("NOTE: For additional information", StringComparison.OrdinalIgnoreCase);
        return (cut > 0 ? message[..cut] : message).Trim();
    }

    public static void Cleanup()
    {
        try { if (Directory.Exists(StageDir)) Directory.Delete(StageDir, recursive: true); }
        catch { }
    }
}
