using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GuardWui3.Models;

namespace GuardWui3.Services;

// Checks GitHub Releases for a newer GUARD, downloads and verifies the release
// zip, and stages a one-shot cmd script that applies it after GUARD exits.
// The swap cannot happen in-process: the running exe and the WinUI native DLLs
// are all locked, so a helper script waits for exit, unpacks over the folder
// with the in-box tar.exe (present since Windows 10 1803; GUARD's minimum is
// 17763), and optionally relaunches. Runtime files (backup-settings.ini,
// guard-backup.cmd, Logs\, guard-prefs.ini) are not in the zip, so an
// overwrite-extract leaves them untouched.
public static class Updater
{
    // releases/latest returns the newest NON-prerelease, non-draft release, so
    // betas never reach users - provided "Set as a pre-release" is ticked when
    // publishing. Offering betas would need /releases plus real SemVer
    // pre-release ordering in IsNewer (core-only today, by design).
    private const string ApiLatest = "https://api.github.com/repos/PlanetLinux98/guard/releases/latest";
    public const string ZipAssetName = "GUARD.zip";
    private const string ChecksumAssetName = "SHA256SUMS";

    private static readonly HttpClient Http = GitHubDownloads.CreateClient(TimeSpan.FromMinutes(5));

    // Null on any failure (offline, rate-limited, bad response); callers treat
    // that as "could not check", never as "up to date".
    public static async Task<GitHubRelease?> FetchLatestAsync(CancellationToken ct = default)
    {
        var rel = await GitHubDownloads.FetchLatestAsync(Http, ApiLatest, ct);
        return string.IsNullOrEmpty(rel?.TagName) ? null : rel;
    }

    // Release bodies are GitHub markdown; shown raw, a screen reader speaks the
    // syntax ("number number Fixed", "star star"). Reduce to plain text: fence
    // lines dropped, links and images kept as their text, heading / emphasis /
    // code markers stripped, * and + bullets normalized to -. Single-underscore
    // emphasis is left alone: underscores in real names (update_last.log) are
    // likelier than _italics_ in release notes.
    public static string NotesToPlainText(string markdown)
    {
        string s = markdown.Replace("\r\n", "\n");
        s = Regex.Replace(s, @"^\s*(```|~~~).*\n?", "", RegexOptions.Multiline);
        s = Regex.Replace(s, @"!\[([^\]]*)\]\([^)]*\)", "$1");
        s = Regex.Replace(s, @"\[([^\]]+)\]\([^)]*\)", "$1");
        s = Regex.Replace(s, @"^#{1,6}\s+", "", RegexOptions.Multiline);
        s = Regex.Replace(s, @"^(\s*)[*+]\s+", "$1- ", RegexOptions.Multiline);
        s = Regex.Replace(s, @"^>\s?", "", RegexOptions.Multiline);
        s = Regex.Replace(s, @"(\*\*|__)(?=\S)(.+?)(?<=\S)\1", "$2");
        s = Regex.Replace(s, @"\*(?=\S)(.+?)(?<=\S)\*", "$1");
        s = Regex.Replace(s, @"~~(?=\S)(.+?)(?<=\S)~~", "$1");
        s = Regex.Replace(s, @"`([^`]+)`", "$1");
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        return s.Trim();
    }

    // Tag vs the running version, compared on the Major.Minor.Patch core only.
    // Dev builds carry a MinVer pre-release (e.g. 0.5.0-alpha.0.7) already AHEAD
    // of the last tag; a strict SemVer compare would re-offer the release that
    // pre-release is building towards, so pre-release labels are ignored.
    public static bool IsNewer(string tag)
        => TryParseCore(tag, out var t) && TryParseCore(GuardPaths.AppVersion, out var c)
           && (t.Item1, t.Item2, t.Item3).CompareTo((c.Item1, c.Item2, c.Item3)) > 0;

    private static bool TryParseCore(string version, out (int, int, int) core)
    {
        core = default;
        string v = (version ?? "").Trim().TrimStart('v', 'V');
        int cut = v.IndexOfAny(new[] { '-', '+' });
        if (cut >= 0) v = v[..cut];
        var parts = v.Split('.');
        if (parts.Length < 3) return false;
        if (!int.TryParse(parts[0], out int a) || !int.TryParse(parts[1], out int b) ||
            !int.TryParse(parts[2], out int c)) return false;
        core = (a, b, c);
        return true;
    }

    // The apply script rewrites everything in the install folder, so it must be
    // writable; when it isn't (e.g. someone parked GUARD under Program Files),
    // self-update is off the table and the user updates by hand.
    public static bool BaseDirWritable()
    {
        try
        {
            string probe = Path.Combine(GuardPaths.BaseDir, ".guard-write-probe");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    // Keyed to the install folder: a shared staging dir let a second portable
    // copy overwrite this copy's staged zip and apply script, so an exit here
    // applied the other install's update (or a dead script).
    private static string StageDir =>
        Path.Combine(Path.GetTempPath(), "GUARD-update-" + GuardPaths.InstallId);

    // Startup housekeeping: remove a leftover staging folder from a previous
    // session (an applied update, or a stage that never got launched). Called
    // only while nothing is staged this session. Best effort - the apply
    // script from a just-relaunched update still holds its own .cmd open for
    // a moment, and that leftover simply goes on the next launch.
    public static void CleanupStage()
    {
        try { if (Directory.Exists(StageDir)) Directory.Delete(StageDir, recursive: true); }
        catch (Exception ex) { DebugLog.Log("updater", "stage cleanup failed", ex); }
    }

    // Downloads GUARD.zip into a fresh staging folder, verifies it against the
    // release's SHA256SUMS asset, and writes the apply script. Returns the
    // script path; the caller launches it (LaunchApplier) as GUARD exits.
    // relaunch: true for an explicit Install and Relaunch, false for the
    // install-on-exit mode (the user was leaving; don't reopen the app on them).
    public static async Task<string> DownloadAndStageAsync(
        GitHubRelease release, bool relaunch, IProgress<double>? progress, CancellationToken ct)
    {
        GitHubAsset? zipAsset = null, sumAsset = null;
        foreach (var a in release.Assets)
        {
            if (a.Name.Equals(ZipAssetName, StringComparison.OrdinalIgnoreCase)) zipAsset = a;
            else if (a.Name.Equals(ChecksumAssetName, StringComparison.OrdinalIgnoreCase)) sumAsset = a;
        }
        if (zipAsset is null)
            throw new InvalidOperationException("This release has no " + ZipAssetName + " download.");

        if (Directory.Exists(StageDir)) Directory.Delete(StageDir, recursive: true);
        Directory.CreateDirectory(StageDir);
        string zipPath = Path.Combine(StageDir, ZipAssetName);

        long total = zipAsset.Size;
        await GitHubDownloads.DownloadAssetAsync(Http, zipAsset, zipPath,
            done => { if (total > 0) progress?.Report((double)done / total); }, ct);

        // Releases before the updater shipped carry no SHA256SUMS, but those
        // can never be the LATEST release once this code runs - so a missing
        // manifest means a mis-published release, and the update refuses to
        // install unverified rather than silently skipping the check (which
        // would hide the mistake forever).
        if (sumAsset is null)
            throw new InvalidOperationException("This release has no " + ChecksumAssetName
                + " file, so the download cannot be verified. The release may still be publishing; try again later.");
        string sums = await Http.GetStringAsync(sumAsset.DownloadUrl, ct);
        string? expected = ParseChecksum(sums, ZipAssetName);
        if (expected is null)
            throw new InvalidOperationException("The release's SHA256SUMS has no entry for " + ZipAssetName + ".");
        string actual;
        using (var f = File.OpenRead(zipPath))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(f, ct));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The downloaded file failed its integrity check.");

        return WriteApplyScript(zipPath, relaunch);
    }

    // "<hex>  <name>" per line (sha256sum format; a leading * on the name marks
    // binary mode and is tolerated).
    private static string? ParseChecksum(string sums, string assetName)
    {
        foreach (var raw in sums.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            int sp = line.IndexOf(' ');
            if (sp <= 0) continue;
            string name = line[(sp + 1)..].Trim().TrimStart('*');
            if (name.Equals(assetName, StringComparison.OrdinalIgnoreCase)) return line[..sp];
        }
        return null;
    }

    private static string WriteApplyScript(string zipPath, bool relaunch)
    {
        string script = Path.Combine(StageDir, "guard-update.cmd");
        File.WriteAllText(script,
            GenerateApplyScript(zipPath, relaunch, GuardPaths.BaseDir, Environment.ProcessId));
        return script;
    }

    // Split from WriteApplyScript so the generated text is testable without
    // touching disk (the BackupScript Write/Generate split).
    public static string GenerateApplyScript(string zipPath, bool relaunch, string appDir, int pid)
    {
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("setlocal EnableExtensions");
        // UTF-8 console first; see BackupScript.Generate. cmd otherwise parses
        // this UTF-8 file's APPDIR/ZIP literals in the OEM codepage - and the
        // zip path runs through %TEMP%, which contains the account name, so a
        // non-ASCII Windows username broke every self-update (the extract
        // pointed at a mangled path that does not exist).
        sb.AppendLine("chcp 65001 >nul");
        sb.AppendLine("REM guard-update.cmd - GENERATED by GUARD.exe. Waits for GUARD to exit,");
        sb.AppendLine("REM unpacks the downloaded release over the install folder, and " + (relaunch ? "restarts it." : "finishes."));
        sb.AppendLine("set \"PID=" + pid + "\"");
        sb.AppendLine("set \"APPDIR=" + appDir + "\"");
        sb.AppendLine("set \"ZIP=" + zipPath + "\"");
        sb.AppendLine("set \"LOG=%APPDIR%\\Logs\\update_last.log\"");
        sb.AppendLine("if not exist \"%APPDIR%\\Logs\" md \"%APPDIR%\\Logs\"");
        sb.AppendLine(">\"%LOG%\" echo GUARD update started %date% %time%");
        sb.AppendLine();
        sb.AppendLine("REM Wait (up to ~2 minutes) for the GUARD process to release its files. The");
        sb.AppendLine("REM IMAGENAME filter guards against the PID being reused by another process.");
        sb.AppendLine("set /a TRIES=0");
        sb.AppendLine(":wait");
        sb.AppendLine("tasklist /FI \"PID eq %PID%\" /FI \"IMAGENAME eq GUARD.exe\" 2>nul | find \" %PID% \" >nul");
        sb.AppendLine("if errorlevel 1 goto :apply");
        sb.AppendLine("set /a TRIES+=1");
        sb.AppendLine("if %TRIES% geq 120 (");
        sb.AppendLine("   >>\"%LOG%\" echo ERROR: GUARD did not exit - update abandoned.");
        sb.AppendLine("   exit /b 1");
        sb.AppendLine(")");
        sb.AppendLine("REM ping as the delay: timeout.exe needs a real stdin and this runs hidden.");
        sb.AppendLine("ping -n 2 127.0.0.1 >nul");
        sb.AppendLine("goto :wait");
        sb.AppendLine();
        sb.AppendLine(":apply");
        sb.AppendLine("REM The zip holds a single GUARD\\ root folder; --strip-components drops it so");
        sb.AppendLine("REM the contents land in the install folder whatever that folder is named.");
        sb.AppendLine("REM Retried: an antivirus scan can briefly hold a just-closed file.");
        sb.AppendLine("set /a ATTEMPT=0");
        sb.AppendLine(":extract");
        sb.AppendLine("set /a ATTEMPT+=1");
        sb.AppendLine("tar.exe -x -f \"%ZIP%\" --strip-components=1 -C \"%APPDIR%\" >>\"%LOG%\" 2>&1");
        sb.AppendLine("if not errorlevel 1 goto :done");
        sb.AppendLine("if %ATTEMPT% geq 3 (");
        sb.AppendLine("   >>\"%LOG%\" echo ERROR: could not unpack the update after %ATTEMPT% attempts.");
        sb.AppendLine("   exit /b 1");
        sb.AppendLine(")");
        sb.AppendLine("ping -n 3 127.0.0.1 >nul");
        sb.AppendLine("goto :extract");
        sb.AppendLine();
        sb.AppendLine(":done");
        sb.AppendLine(">>\"%LOG%\" echo Update applied %date% %time%");
        sb.AppendLine("del /q \"%ZIP%\" >nul 2>nul");
        if (relaunch)
            sb.AppendLine("start \"\" \"%APPDIR%\\GUARD.exe\"");
        sb.AppendLine("endlocal");
        return sb.ToString();
    }

    // Fire the staged script detached and hidden; it idles on the PID wait until
    // this process is gone. Called as the window closes (Window.Closed).
    public static void LaunchApplier(string scriptPath)
    {
        var psi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(scriptPath)!,
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add(scriptPath);
        Process.Start(psi);
    }
}
