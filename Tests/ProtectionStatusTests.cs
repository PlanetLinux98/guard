using System;
using System.Collections.Generic;
using System.IO;
using GuardWui3.Models;
using GuardWui3.Services;
using Dest = GuardWui3.Services.SaveValidation.DestState;
using Xunit;

namespace GuardWui3.Tests;

public class ProtectionStatusTests
{
    private static readonly DateTime Now = new(2026, 8, 14, 12, 0, 0);   // a Friday, noon

    private static Settings Cfg() => new()
    {
        Dest = @"E:\Backups",
        ImageTarget = @"E:\",
        Folders = { new FolderPair(true, @"%USERPROFILE%\Documents", "Documents") },
    };

    // These strings are also what the File Backup status bar shows: the two
    // surfaces read the same evaluator precisely so they cannot drift, and
    // pinning them here is what would catch a change to only one of them.
    [Fact]
    public void FileBackupReportsTheLastRunTheWayTheStatusBarAlwaysHas()
    {
        var cfg = Cfg();
        var ok = new LastRunInfo(Now.AddHours(-10), RunOutcome.Ok);

        var healthy = ProtectionStatus.FileBackup(cfg, ok, Now, Dest.HasFiles, 0, false, false);
        Assert.Equal(ProtectionLevel.Protected, healthy.Level);
        Assert.Equal("Last backup succeeded today at 02:00.", healthy.Headline);

        Assert.Equal("Last backup had errors (today at 02:00). Open the last log.",
            ProtectionStatus.FileBackup(cfg, new LastRunInfo(ok.When, RunOutcome.Errors), Now, Dest.HasFiles, 0, false, false).Headline);
        Assert.Equal("Last backup did not complete (today at 02:00). Open the last log.",
            ProtectionStatus.FileBackup(cfg, new LastRunInfo(ok.When, RunOutcome.DidNotComplete), Now, Dest.HasFiles, 0, false, false).Headline);

        // Settings saved but never run is not protection.
        var never = ProtectionStatus.FileBackup(cfg, null, Now, Dest.HasFiles, 0, false, false);
        Assert.Equal(ProtectionLevel.Attention, never.Level);
        Assert.Equal("Backup settings saved. No backup has run yet.", never.Headline);
    }

    [Fact]
    public void AnOverdueScheduleAndAStaleOnConnectBothCount()
    {
        var cfg = Cfg();
        cfg.ScheduleEnabled = true;
        cfg.ScheduleTime = "02:00";
        var old = new LastRunInfo(Now.AddDays(-3), RunOutcome.Ok);
        var overdue = ProtectionStatus.FileBackup(cfg, old, Now, Dest.HasFiles, 0, false, false);
        Assert.Equal(ProtectionLevel.Attention, overdue.Level);
        Assert.StartsWith("Backup overdue; last succeeded", overdue.Headline);

        var onConnect = Cfg();
        onConnect.TriggerOnConnect = true;
        var stale = ProtectionStatus.FileBackup(onConnect, new LastRunInfo(Now.AddDays(-9), RunOutcome.Ok), Now, Dest.HasFiles, 0, false, false);
        Assert.Equal(ProtectionLevel.Attention, stale.Level);
        Assert.Contains("Connect your backup drive", stale.Headline);
    }

    // The log lives next to the exe, so it keeps reporting a successful run long
    // after the drive it wrote to was reformatted. That outranks the run itself.
    [Fact]
    public void AnEmptyDestinationOutranksASuccessfulRun()
    {
        var wiped = ProtectionStatus.FileBackup(Cfg(), new LastRunInfo(Now.AddHours(-2), RunOutcome.Ok),
            Now, Dest.Empty, vanishedCount: 0, mirrorPurges: false, mirrorHeld: false);
        Assert.Equal(ProtectionLevel.Attention, wiped.Level);
        Assert.StartsWith("Backup destination is empty", wiped.Headline);
    }

    // An unplugged backup drive is the RESTING state of the on-connect workflow,
    // so it must never read as a fault; a destination folder deleted off a drive
    // that IS connected means the backup itself is gone, so it must. And a state
    // no sweep has answered yet has to say nothing at all, or every launch would
    // report a fault before the first walk lands.
    [Fact]
    public void AnUnpluggedDriveIsNotAFaultButADeletedFolderIs()
    {
        var ok = new LastRunInfo(Now.AddHours(-2), RunOutcome.Ok);

        var unchecked_ = ProtectionStatus.FileBackup(Cfg(), ok, Now, Dest.Unchecked, 0, false, false);
        Assert.Equal(ProtectionLevel.Protected, unchecked_.Level);
        Assert.Equal("Last backup succeeded today at 10:00.", unchecked_.Headline);

        var absent = ProtectionStatus.FileBackup(Cfg(), ok, Now, Dest.Absent, 0, false, false);
        Assert.Equal(ProtectionLevel.Protected, absent.Level);
        Assert.Contains("not connected right now", absent.Headline);

        var onConnect = Cfg();
        onConnect.TriggerOnConnect = true;
        Assert.Contains("GUARD will back up when it is",
            ProtectionStatus.FileBackup(onConnect, ok, Now, Dest.Absent, 0, false, false).Headline);

        var gone = ProtectionStatus.FileBackup(Cfg(), ok, Now, Dest.FolderMissing, 0, false, false);
        Assert.Equal(ProtectionLevel.Attention, gone.Level);
        Assert.StartsWith("Backup destination folder is missing", gone.Headline);
    }

    // The pause is a standing state in which the mode the user configured is not
    // in force, so it has to outrank a run that went perfectly well.
    [Fact]
    public void APausedMirrorOutranksAHealthyRun()
    {
        var held = ProtectionStatus.FileBackup(Cfg(), new LastRunInfo(Now.AddHours(-2), RunOutcome.Ok),
            Now, Dest.HasFiles, 0, mirrorPurges: true, mirrorHeld: true);
        Assert.Equal(ProtectionLevel.Attention, held.Level);
        Assert.StartsWith("Mirror deleting is paused", held.Headline);

        // But an empty destination still outranks the pause: the backup being
        // gone is the bigger fact.
        Assert.StartsWith("Backup destination is empty",
            ProtectionStatus.FileBackup(Cfg(), new LastRunInfo(Now.AddHours(-2), RunOutcome.Ok),
                Now, Dest.Empty, 0, true, true).Headline);
    }

    // A vanished source never makes the run fail, so the healthy line is exactly
    // where it has to be said or the one state that looks fine hides the problem.
    [Fact]
    public void AVanishedSourceDowngradesAnOtherwiseHealthyBackup()
    {
        var s = ProtectionStatus.FileBackup(Cfg(), new LastRunInfo(Now.AddHours(-2), RunOutcome.Ok),
            Now, Dest.HasFiles, vanishedCount: 1, mirrorPurges: true, mirrorHeld: false);
        Assert.Equal(ProtectionLevel.Attention, s.Level);
        Assert.Contains("the next backup will delete the copies", s.Headline);
        Assert.Equal(" Warning: 2 folders have nothing left to back up.",
            ProtectionStatus.VanishedSuffix(2, false));
        Assert.Equal("", ProtectionStatus.VanishedSuffix(0, true));
    }

    [Fact]
    public void SystemImageSeparatesUnavailableFromUnconfigured()
    {
        var cfg = Cfg();
        var missing = ProtectionStatus.SystemImage(cfg, available: false, settingsSaved: false,
            ProtectionStatus.ImageTaskState.Ok, null, Now);
        Assert.Equal(ProtectionLevel.Unavailable, missing.Level);
        Assert.StartsWith("System imaging is unavailable", missing.Headline);

        var unset = ProtectionStatus.SystemImage(cfg, true, false, ProtectionStatus.ImageTaskState.Ok, null, Now);
        Assert.Equal(ProtectionLevel.NotSetUp, unset.Level);
        Assert.Equal("No image settings saved yet. Choose a destination and click Save Settings.", unset.Headline);

        Assert.Equal("Image settings saved. No image created yet.",
            ProtectionStatus.SystemImage(cfg, true, true, ProtectionStatus.ImageTaskState.Ok, null, Now).Headline);
        Assert.Equal("Last system image succeeded today at 02:00.",
            ProtectionStatus.SystemImage(cfg, true, true, ProtectionStatus.ImageTaskState.Ok,
                new LastRunInfo(Now.AddHours(-10), RunOutcome.Ok), Now).Headline);
    }

    // A schedule Windows has no task for means no image will EVER be taken, which
    // the last run's log cannot reveal. It has to outrank a healthy run, or the
    // page and the dashboard disagree until the overdue check catches up.
    [Fact]
    public void ABrokenScheduledImageTaskOutranksAHealthyLastRun()
    {
        var cfg = Cfg();
        var healthy = new LastRunInfo(Now.AddHours(-10), RunOutcome.Ok);
        var gone = ProtectionStatus.SystemImage(cfg, true, true,
            ProtectionStatus.ImageTaskState.Missing, healthy, Now);
        Assert.Equal(ProtectionLevel.Attention, gone.Level);
        Assert.StartsWith("The image schedule is saved but Windows has no matching task", gone.Headline);

        var moved = ProtectionStatus.SystemImage(cfg, true, true,
            ProtectionStatus.ImageTaskState.Moved, healthy, Now);
        Assert.Equal(ProtectionLevel.Attention, moved.Level);
        Assert.StartsWith("GUARD's folder has moved", moved.Headline);

        // A PC that cannot image at all still reports that first: a broken task
        // is not the user's problem to fix there.
        Assert.Equal(ProtectionLevel.Unavailable,
            ProtectionStatus.SystemImage(cfg, false, true,
                ProtectionStatus.ImageTaskState.Missing, healthy, Now).Level);
    }

    // Windows withholding wbadmin is not the user's failing, so it must not drag
    // the whole verdict down to "not protected".
    [Fact]
    public void OverallTakesTheWorstPillarButIgnoresOneThisPcCannotUse()
    {
        var good = new PillarStatus(ProtectionLevel.Protected, "", "");
        var unavailable = new PillarStatus(ProtectionLevel.Unavailable, "", "");
        var attention = new PillarStatus(ProtectionLevel.Attention, "", "");
        var unset = new PillarStatus(ProtectionLevel.NotSetUp, "", "");

        Assert.Equal(ProtectionLevel.Protected, ProtectionStatus.Overall(good, unavailable, good));
        Assert.Equal(ProtectionLevel.Attention, ProtectionStatus.Overall(good, attention, good));
        Assert.Equal(ProtectionLevel.NotSetUp, ProtectionStatus.Overall(attention, unset, good));
    }

    // No timestamp is stored anywhere for app exports, on purpose: a local
    // record would keep claiming a list was exported after the drive holding it
    // was wiped. The answer comes from the exports that are actually there.
    [Fact]
    public void AppListStatusComesFromTheExportsAtTheDestination()
    {
        Assert.Equal(ProtectionLevel.NotSetUp, ProtectionStatus.AppList("", true, null, Now).Level);

        var unreachable = ProtectionStatus.AppList(@"E:\Exports", false, null, Now);
        Assert.Equal(ProtectionLevel.Attention, unreachable.Level);
        Assert.Contains("not reachable", unreachable.Headline);

        Assert.Equal(ProtectionLevel.NotSetUp, ProtectionStatus.AppList(@"E:\Exports", true, null, Now).Level);

        var done = ProtectionStatus.AppList(@"E:\Exports", true,
            new AppExportInfo(Now.AddDays(-1), @"E:\Exports\app-export-2026-08-13_0900\app-list.json", 42), Now);
        Assert.Equal(ProtectionLevel.Protected, done.Level);
        Assert.Equal("App list exported yesterday at 12:00 (42 apps).", done.Headline);
    }

    [Fact]
    public void TheNewestExportWinsAndItsOwnStampIsWhatCounts()
    {
        string dest = Path.Combine(Path.GetTempPath(), "guard-exports-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dest);
        try
        {
            Write(dest, "app-export-2026-08-01_0900", "2026-08-01 09:00", 3);
            Write(dest, "app-export-2026-08-13_1830", "2026-08-13 18:30", 7);
            // A folder with no list in it is not an export.
            Directory.CreateDirectory(Path.Combine(dest, "notes"));

            var newest = ProtectionStatus.FindNewestExport(dest);
            Assert.NotNull(newest);
            Assert.Equal(new DateTime(2026, 8, 13, 18, 30, 0), newest!.When);
            Assert.Equal(7, newest.Apps);

            Assert.Null(ProtectionStatus.FindNewestExport(Path.Combine(dest, "gone")));
            Assert.Null(ProtectionStatus.FindNewestExport(""));
        }
        finally { Directory.Delete(dest, true); }

        static void Write(string dest, string folder, string exported, int apps)
        {
            string dir = Path.Combine(dest, folder);
            Directory.CreateDirectory(dir);
            var items = new List<string>();
            for (int i = 0; i < apps; i++) items.Add("{\"name\":\"App" + i + "\"}");
            File.WriteAllText(Path.Combine(dir, "app-list.json"),
                "{\"exported\":\"" + exported + "\",\"machine\":\"PC\",\"apps\":[" + string.Join(",", items) + "]}");
        }
    }
}
