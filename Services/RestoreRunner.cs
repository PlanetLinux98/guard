using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace GuardWui3.Services;

// How much of the live folder a restore is allowed to disturb. Each step adds
// exactly one destructive power, and the default has none: nothing already at
// the target is ever replaced by an older copy, and nothing is ever deleted.
public enum RestoreMode
{
    // /E /XO - put back what is missing, and update a file the backup has a
    // NEWER copy of. A file edited since the backup is left alone.
    AddAndUpdate,
    // /E - put the backup's copy back whatever the dates say. The only way to
    // recover a live file that is broken but carries a newer timestamp
    // (ransomware, a bad sync, a truncated save); it can overwrite newer work,
    // so it is never the default and is confirmed against a preview first.
    Replace,
}

// Copies files back OUT of a Robocopy backup into the live folders.
//
// Deliberately NOT a generated .cmd, unlike the backup:
//   - guard-backup.cmd is both the scheduled task's file and the file "Backup
//     Now" re-runs, so a restore living in any persisted, schedulable script is
//     one stray double-click away from overwriting live folders with an old
//     copy. Nothing here is ever written to a stable path.
//   - cmd re-parses every line it reads, so a path with a %, &, ^, < or > in it
//     is rewritten before robocopy sees it. GUARD can only WARN about that for
//     backups (SaveValidation.UnresolvedPercentPaths). A restore writes INTO
//     the user's own folders, where a mangled path is unrecoverable, and
//     ArgumentList removes the whole hazard class.
//
// Log encoding, measured rather than assumed: launched without a shell (GUARD
// is a GUI app, so robocopy gets a fresh console at the OEM codepage),
// robocopy's stdout is lossy - a file named "e-acute CJK Cyrillic.txt" arrives
// as "e-acute ??.txt", and /UNICODE only prepends a BOM without changing the
// bytes. /UNILOG: writes true UTF-16 with the name intact, so the durable log
// comes from there and stdout is used only for progress. (/UNILOG+: - the
// append form - is NOT Unicode; it mangles the same name. Never use it.)
public static class RestoreRunner
{
    // Robocopy's exit code is a bit mask; 8 and above means at least one file
    // or folder could not be copied. Below that is success, with the lower bits
    // only reporting what happened (1 copied, 2 extras present, 4 mismatched).
    public const int FailureThreshold = 8;

    // The arguments for one folder. Split from the run so the flags that decide
    // what gets overwritten are testable without touching a disk.
    public static List<string> BuildArgs(
        string source, string target, RestoreMode mode, bool preview, string? uniLogPath)
    {
        var args = new List<string>
        {
            RobocopyPath.Arg(source),
            RobocopyPath.Arg(target),
            // /E, never /MIR: /MIR adds /PURGE, which deletes everything at the
            // target that is not in the backup - on a live Documents folder that
            // is every file created since the last run.
            "/E",
        };
        // Verified by experiment, and the reason this flag is not optional in
        // the default mode: robocopy's default file classes include "Older", so
        // a plain /E restore replaces a file edited today with the backup's
        // three-day-old copy. /XO excludes an older SOURCE, which in the restore
        // direction means "never overwrite newer live work".
        if (mode == RestoreMode.AddAndUpdate) args.Add("/XO");
        args.Add("/R:2");
        args.Add("/W:5");
        args.Add("/MT:16");
        args.Add("/NP");
        args.Add("/NDL");
        // Per-file byte counts feed the progress bar, the same way the backup's
        // UI runs do.
        args.Add("/BYTES");
        // Directory junctions only, matching the backup: the broader /XJ also
        // excludes file-level reparse points, which is what a cloud placeholder
        // is.
        args.Add("/XJD");
        if (preview) args.Add("/L");
        if (!string.IsNullOrEmpty(uniLogPath))
        {
            // Naming a log silences stdout unless /TEE is given, and stdout is
            // where the progress comes from.
            args.Add("/TEE");
            args.Add("/UNILOG:" + uniLogPath);
        }
        return args;
    }

    // Runs robocopy for one folder and returns its exit code. onLine receives
    // every stdout line for progress; cancelling kills the whole tree, which is
    // what makes WaitForExit return.
    //
    // Latin1, not UTF-8 or the OEM codepage: stdout is single-byte console text
    // whose codepage GUARD cannot set without a shell, and .NET does not carry
    // the OEM codepages at all. Latin1 maps every byte to a char without
    // throwing or dropping anything, which keeps the parts this stream is read
    // for - byte counts and the summary table's shape - exact. The readable
    // text comes from the UTF-16 log instead.
    public static int RunOne(
        string source, string target, RestoreMode mode, bool preview,
        string? uniLogPath, Action<string> onLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("robocopy.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.Latin1,
            StandardErrorEncoding = Encoding.Latin1,
        };
        foreach (var a in BuildArgs(source, target, mode, preview, uniLogPath))
            psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        p.Start();
        using var reg = ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch { } });
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        p.WaitForExit();
        return p.ExitCode;
    }

    // ---- The run lock ---------------------------------------------------
    //
    // The SAME lock file the generated backup script opens on fd 9. Holding it
    // for the whole restore is not politeness: a scheduled or on-connect backup
    // firing partway through a restore sees a target folder that is only half
    // filled, and in Mirror mode makes the BACKUP match it - deleting the very
    // copies the restore is reading from. Verified against the script: while
    // this handle is open its redirect fails, :main never runs, and it exits 3
    // ("nothing to do") exactly as it does when two backups race.
    //
    // Returns null when the lock could not be taken, and the restore must not
    // start. heldByAnotherRun tells the two failures apart: a sharing violation
    // means a backup really is running, while anything else (a read-only or
    // missing folder) is GUARD's own install being unwritable - reporting that
    // as "a backup is running" would send the user off waiting for something
    // that is never going to finish.
    public static FileStream? TryTakeRunLock(out bool heldByAnotherRun)
    {
        heldByAnotherRun = false;
        try
        {
            GuardPaths.EnsureLogsDir();
            return new FileStream(GuardPaths.RunLockPath, FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        // Caught BEFORE IOException, which both of these derive from:
        // EnsureLogsDir swallows its own failure, so on a read-only or missing
        // data folder the open below throws one of these - and reporting that as
        // "a backup is running" sends the user off to wait for something that is
        // never going to finish.
        catch (DirectoryNotFoundException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (IOException)
        {
            // What is left is the sharing violation: the file is open with
            // FileShare.None by a backup that is running right now.
            heldByAnotherRun = true;
            return null;
        }
        catch { return null; }
    }

    // ---- The restore log ------------------------------------------------
    //
    // Written by GUARD rather than by robocopy, because robocopy cannot APPEND
    // a Unicode log (see the file header): each folder gets its own UTF-16 log,
    // which is read back and folded in here as UTF-8. The FINISHED OK / FINISHED
    // WITH ERRORS markers match what the generated scripts write, so
    // BackupHealth.ReadLog reads this file with no changes.

    // A scratch log for one folder, or null when the Logs folder cannot be
    // written. The restore itself still runs in that case - the log is a record,
    // not the job - so this must never throw.
    public static string? TryPreparePartLog()
    {
        try
        {
            GuardPaths.EnsureLogsDir();
            string path = GuardPaths.RestorePartLogPath;
            // Created here, not left to robocopy: this doubles as the
            // writability probe, and /UNILOG: truncates an existing file anyway.
            File.WriteAllBytes(path, Array.Empty<byte>());
            return path;
        }
        catch { return null; }
    }

    public static void BeginLog(bool preview, string snapshotLabel, string source, RestoreMode mode)
    {
        var sb = new StringBuilder();
        sb.AppendLine("===========================================================");
        sb.AppendLine(" Restore    " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        sb.AppendLine(" Restoring from: \"" + source + "\"");
        sb.AppendLine(" Backup: " + snapshotLabel);
        sb.AppendLine(" Mode: " + (mode == RestoreMode.AddAndUpdate
            ? "add missing files and update older ones (nothing newer is replaced, nothing is deleted)"
            : "replace live files with the backup copies (nothing is deleted)"));
        if (preview) sb.AppendLine(" *** PREVIEW ONLY - no files were changed ***");
        sb.AppendLine("===========================================================");
        Write(sb.ToString(), append: false);
    }

    public static void AppendPairHeader(string source, string target)
        => Write(Environment.NewLine + "--- \"" + source + "\"  =>  \"" + target + "\"" + Environment.NewLine,
                 append: true);

    // Folds one folder's UTF-16 robocopy log into the restore log and clears it
    // for the next folder.
    public static void AppendPartLog(string? partPath)
    {
        if (string.IsNullOrEmpty(partPath)) return;
        try
        {
            if (!File.Exists(partPath)) return;
            // Encoding detected from the file's own BOM, which /UNILOG: always
            // writes; a log robocopy did not manage to write is simply empty.
            string text = File.ReadAllText(partPath, Encoding.Unicode);
            if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);
            Write(text, append: true);
            File.WriteAllBytes(partPath, Array.Empty<byte>());
        }
        catch { }
    }

    public static void FinishLog(bool hadErrors, bool cancelled)
    {
        string stamp = "   " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        // "FINISHED WITH ERRORS" for a cancelled run too: it stopped partway, so
        // reporting it as a clean finish would leave a half-restored folder
        // recorded as complete.
        Write(Environment.NewLine
            + (cancelled ? "FINISHED WITH ERRORS - stopped before it completed" + stamp
               : hadErrors ? "FINISHED WITH ERRORS" + stamp
               : "FINISHED OK" + stamp)
            + Environment.NewLine, append: true);
        try { File.Delete(GuardPaths.RestorePartLogPath); } catch { }
    }

    private static void Write(string text, bool append)
    {
        try
        {
            GuardPaths.EnsureLogsDir();
            if (append) File.AppendAllText(GuardPaths.RestoreLogPath, text, Encoding.UTF8);
            else File.WriteAllText(GuardPaths.RestoreLogPath, text, Encoding.UTF8);
        }
        catch { }
    }
}
