using System;
using System.IO;
using GuardWui3.Services;
using Xunit;

namespace GuardWui3.Tests;

public class BackupHealthTests
{
    [Fact]
    public void ReadLogClassifiesOutcomes()
    {
        string dir = Path.Combine(Path.GetTempPath(), "guard-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string log = Path.Combine(dir, "backup_last.log");

            File.WriteAllText(log, "header\nstuff\nFINISHED OK   2026-07-06\n");
            Assert.Equal(RunOutcome.Ok, BackupHealth.ReadLog(log)!.Outcome);

            // The LAST marker wins: an OK from a past run followed by a newer
            // ERRORS must read as errors.
            File.WriteAllText(log, "FINISHED OK old\nFINISHED WITH ERRORS   later\n");
            Assert.Equal(RunOutcome.Errors, BackupHealth.ReadLog(log)!.Outcome);

            // Header written but no FINISHED at all: the run aborted.
            File.WriteAllText(log, "===========\n Backup ...\nERROR: destination not reachable\n");
            Assert.Equal(RunOutcome.DidNotComplete, BackupHealth.ReadLog(log)!.Outcome);

            Assert.Null(BackupHealth.ReadLog(Path.Combine(dir, "missing.log")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void PreviousScheduledRunFindsTheMostRecentOccurrence()
    {
        var now = new DateTime(2026, 7, 8, 12, 0, 0);   // a Wednesday, noon
        var daily = Models.Settings.AllDays();

        // Time already passed today -> today.
        Assert.Equal(new DateTime(2026, 7, 8, 2, 0, 0),
            BackupHealth.PreviousScheduledRun(daily, "02:00", now));
        // Time still ahead today -> yesterday.
        Assert.Equal(new DateTime(2026, 7, 7, 14, 0, 0),
            BackupHealth.PreviousScheduledRun(daily, "14:00", now));
        // Mondays only -> this past Monday.
        Assert.Equal(new DateTime(2026, 7, 6, 2, 0, 0),
            BackupHealth.PreviousScheduledRun(new[] { DayOfWeek.Monday }, "02:00", now));
        // No days / bad time -> unknown.
        Assert.Null(BackupHealth.PreviousScheduledRun(Array.Empty<DayOfWeek>(), "02:00", now));
        Assert.Null(BackupHealth.PreviousScheduledRun(daily, "junk", now));
    }

    [Fact]
    public void PreviousScheduledImageHandlesEachCadence()
    {
        var now = new DateTime(2026, 7, 8, 12, 0, 0);   // Wednesday

        Assert.Equal(new DateTime(2026, 7, 8, 3, 0, 0),
            BackupHealth.PreviousScheduledImage("Daily", DayOfWeek.Sunday, 1, "03:00", now));
        Assert.Equal(new DateTime(2026, 7, 5, 3, 0, 0),
            BackupHealth.PreviousScheduledImage("Weekly", DayOfWeek.Sunday, 1, "03:00", now));
        // Monthly day ahead of today -> last month's occurrence.
        Assert.Equal(new DateTime(2026, 6, 15, 3, 0, 0),
            BackupHealth.PreviousScheduledImage("Monthly", DayOfWeek.Sunday, 15, "03:00", now));
        // Monthly day already passed -> this month's.
        Assert.Equal(new DateTime(2026, 7, 1, 3, 0, 0),
            BackupHealth.PreviousScheduledImage("Monthly", DayOfWeek.Sunday, 1, "03:00", now));
    }

    [Fact]
    public void OverdueNeedsExpectedPlusGraceAndNoNewerRun()
    {
        var now = new DateTime(2026, 7, 8, 12, 0, 0);
        var expected = new DateTime(2026, 7, 8, 2, 0, 0);
        var oldRun = new LastRunInfo(new DateTime(2026, 7, 6, 2, 0, 0), RunOutcome.Ok);
        var freshRun = new LastRunInfo(new DateTime(2026, 7, 8, 2, 5, 0), RunOutcome.Ok);

        Assert.True(BackupHealth.IsOverdue(oldRun, expected, now));
        Assert.False(BackupHealth.IsOverdue(freshRun, expected, now));
        // Inside the grace window nothing is overdue yet.
        Assert.False(BackupHealth.IsOverdue(oldRun, expected, new DateTime(2026, 7, 8, 5, 0, 0)));
        // No expectation (schedule off) -> never overdue.
        Assert.False(BackupHealth.IsOverdue(oldRun, null, now));
    }

    [Fact]
    public void FriendlyWhenSpeaksRelativeDays()
    {
        var now = new DateTime(2026, 7, 8, 12, 0, 0);
        Assert.Equal("today at 02:00", BackupHealth.FriendlyWhen(new DateTime(2026, 7, 8, 2, 0, 0), now));
        Assert.Equal("yesterday at 02:00", BackupHealth.FriendlyWhen(new DateTime(2026, 7, 7, 2, 0, 0), now));
        Assert.Equal("on 2026-07-01 at 02:00", BackupHealth.FriendlyWhen(new DateTime(2026, 7, 1, 2, 0, 0), now));
    }
}
