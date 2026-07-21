using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace GuardWui3.Services;

public enum RunOutcome { Ok, Errors, DidNotComplete }

public sealed record LastRunInfo(DateTime When, RunOutcome Outcome);

// Answers "am I actually protected?" from the artifacts already on disk: the
// generated logs record how the last run ended, and the schedule settings say
// when a run was last expected. The status bar reads this at launch and after
// each run, instead of only ever saying "settings saved".
public static class BackupHealth
{
    // How long past the expected start a missing run stays benign. Covers a
    // slow run plus Task Scheduler's StartWhenAvailable catch-up after a
    // machine that slept through the trigger wakes.
    public static readonly TimeSpan Grace = TimeSpan.FromHours(6);

    // On-connect has no schedule to be "late" against; nag gently once the
    // last success is older than this.
    public static readonly TimeSpan OnConnectStale = TimeSpan.FromDays(7);

    // Progressively larger tail windows to search for the FINISHED marker in.
    // A version prune (BackupScript's :prune, run after FINISHED is written)
    // logs one line per deleted dated folder; lowering VersionsToKeep after
    // letting versions accumulate (up to the 365 allowed) can push FINISHED
    // well past a single small window. Doubling up from a small window keeps
    // the common case (no prune, or a small one) to one short read, while a
    // pathological log still resolves without reading it into memory in one
    // shot - capped, so a truly enormous or corrupt log gives up rather than
    // growing without bound.
    private static readonly int[] TailWindowSizes = { 16 * 1024, 128 * 1024, 1024 * 1024, 8 * 1024 * 1024 };

    // Outcome of the last run recorded in a GUARD-generated log (backup or
    // system image), or null when no run has happened. The WHEN is the file's
    // write time: the log's own "%date% %time%" stamps are locale-formatted
    // and unparseable in general, while the file mtime IS the run's end.
    public static LastRunInfo? ReadLog(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var fi = new FileInfo(path);
            string tail = "";
            foreach (var window in TailWindowSizes)
            {
                tail = ReadTail(path, window);
                bool found = tail.Contains("FINISHED OK", StringComparison.Ordinal)
                    || tail.Contains("FINISHED WITH ERRORS", StringComparison.Ordinal);
                if (found || window >= fi.Length) break;
            }
            int ok = tail.LastIndexOf("FINISHED OK", StringComparison.Ordinal);
            int err = tail.LastIndexOf("FINISHED WITH ERRORS", StringComparison.Ordinal);
            var outcome = ok > err ? RunOutcome.Ok
                : err >= 0 ? RunOutcome.Errors
                : RunOutcome.DidNotComplete;   // header written, no FINISHED: aborted
            return new LastRunInfo(fi.LastWriteTime, outcome);
        }
        catch { return null; }
    }

    private static string ReadTail(string path, int maxBytes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long start = Math.Max(0, fs.Length - maxBytes);
        fs.Seek(start, SeekOrigin.Begin);
        var buf = new byte[fs.Length - start];
        int n = fs.Read(buf, 0, buf.Length);
        return Encoding.UTF8.GetString(buf, 0, Math.Max(n, 0));
    }

    // The most recent moment the day/time schedule expected a run to start,
    // or null when it cannot say (no days, unparseable time).
    public static DateTime? PreviousScheduledRun(
        IReadOnlyCollection<DayOfWeek> days, string timeHHmm, DateTime now)
    {
        if (days.Count == 0) return null;
        if (!TimeSpan.TryParse((timeHHmm ?? "").Trim(), out var t)) return null;
        for (int i = 0; i < 8; i++)
        {
            var candidate = now.Date.AddDays(-i) + t;
            if (candidate <= now && days.Contains(candidate.DayOfWeek)) return candidate;
        }
        return null;
    }

    // Image-cadence analogue (Daily / Weekly on a day / Monthly on day 1-28).
    public static DateTime? PreviousScheduledImage(
        string cadence, DayOfWeek weeklyDay, int monthlyDay, string timeHHmm, DateTime now)
    {
        if (!TimeSpan.TryParse((timeHHmm ?? "").Trim(), out var t)) return null;
        switch (cadence)
        {
            case "Daily":
            {
                var d = now.Date + t;
                return d <= now ? d : d.AddDays(-1);
            }
            case "Monthly":
            {
                int day = Math.Clamp(monthlyDay, 1, 28);
                var d = new DateTime(now.Year, now.Month, day) + t;
                return d <= now ? d : d.AddMonths(-1);
            }
            default: // Weekly
            {
                for (int i = 0; i < 8; i++)
                {
                    var candidate = now.Date.AddDays(-i) + t;
                    if (candidate <= now && candidate.DayOfWeek == weeklyDay) return candidate;
                }
                return null;
            }
        }
    }

    // A run was expected, the grace has passed, and nothing has run since.
    public static bool IsOverdue(LastRunInfo? last, DateTime? expected, DateTime now)
        => expected != null && now >= expected + Grace && (last == null || last.When < expected.Value);

    // Spoken-friendly local phrasing for the status line.
    public static string FriendlyWhen(DateTime when, DateTime now)
    {
        string clock = when.ToString("HH:mm");
        if (when.Date == now.Date) return "today at " + clock;
        if (when.Date == now.Date.AddDays(-1)) return "yesterday at " + clock;
        return "on " + when.ToString("yyyy-MM-dd") + " at " + clock;
    }
}
