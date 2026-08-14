using System;
using System.Collections.Generic;
using System.IO;
using GuardWui3.Models;
using GuardWui3.Services;
using Xunit;

namespace GuardWui3.Tests;

public class RestorePlanTests
{
    private static string TempRoot()
    {
        string dir = Path.Combine(Path.GetTempPath(), "guard-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void SnapshotsListDatedVersionsNewestFirstAndTheRootOnlyWhenItHoldsFolders()
    {
        string dest = TempRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(dest, "2026-08-01"));
            Directory.CreateDirectory(Path.Combine(dest, "2026-08-14"));
            var versioned = RestorePlan.FindSnapshots(dest);
            Assert.Equal(new[] { "2026-08-14", "2026-08-01" },
                versioned.ConvertAll(s => s.Label).ToArray());

            // A destination holding BOTH shapes offers both: turning versioning
            // on does not delete the older single copy, and a restore that could
            // not see it would hide the only copy of anything deleted before the
            // switch.
            Directory.CreateDirectory(Path.Combine(dest, "Documents"));
            var mixed = RestorePlan.FindSnapshots(dest);
            Assert.Equal(3, mixed.Count);
            Assert.Equal("Latest backup (not versioned)", mixed[^1].Label);
            Assert.Equal(dest, mixed[^1].Path);

            Assert.Empty(RestorePlan.FindSnapshots(Path.Combine(dest, "nope")));
            Assert.Empty(RestorePlan.FindSnapshots(""));
        }
        finally { Directory.Delete(dest, true); }
    }

    [Fact]
    public void CandidatesComeFromTheBackupItselfNotOnlyFromTheSettings()
    {
        string dest = TempRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(dest, "Documents"));
            Directory.CreateDirectory(Path.Combine(dest, "Work", "Reports"));
            // A folder this machine's settings know nothing about - the case a
            // settings-driven list would silently omit on a fresh install.
            Directory.CreateDirectory(Path.Combine(dest, "Projects"));

            var folders = new List<FolderPair>
            {
                new(true, @"%USERPROFILE%\Documents", "Documents"),
                new(true, @"D:\Work\Reports", @"Work\Reports"),
                // Configured but not present in this backup: nothing to restore.
                new(true, @"C:\Nope", "Missing"),
            };
            var list = RestorePlan.BuildCandidates(dest, folders);
            var names = list.ConvertAll(c => c.FolderName);
            Assert.Contains("Documents", names);
            Assert.Contains(@"Work\Reports", names);
            Assert.Contains("Projects", names);
            Assert.DoesNotContain("Missing", names);
            // "Work" only CONTAINS a configured pair, so it is not a row of its
            // own - but it must not swallow its other children either.
            Assert.DoesNotContain("Work", names);

            var reports = list.Find(c => c.FolderName == @"Work\Reports")!;
            Assert.Equal(@"D:\Work\Reports", reports.SuggestedTarget);
            Assert.Equal(TargetOrigin.Settings, reports.Origin);

            // Windows knows where Documents is on this machine, which is what
            // makes a restore work on a PC that has never been configured.
            var unconfigured = RestorePlan.BuildCandidates(dest, new List<FolderPair>());
            var docs = unconfigured.Find(c => c.FolderName == "Documents")!;
            Assert.Equal(TargetOrigin.WindowsFolder, docs.Origin);
            Assert.NotEqual("", docs.SuggestedTarget);
            // Nothing matches "Projects", so the user has to say where it goes.
            var projects = unconfigured.Find(c => c.FolderName == "Projects")!;
            Assert.Equal(TargetOrigin.None, projects.Origin);
            Assert.Equal("", projects.SuggestedTarget);
        }
        finally { Directory.Delete(dest, true); }
    }

    // The regression this guards: claiming the whole top-level name for a nested
    // configured pair hid every sibling beside it, so a folder sitting in the
    // backup could not be restored by any route - the exact silent omission the
    // destination-driven design exists to prevent.
    [Fact]
    public void AFolderBesideAConfiguredNestedPairIsStillOffered()
    {
        string dest = TempRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(dest, "Work", "Reports"));
            Directory.CreateDirectory(Path.Combine(dest, "Work", "Invoices"));
            Directory.CreateDirectory(Path.Combine(dest, "Work", "Archive", "2019"));
            var folders = new List<FolderPair> { new(true, @"D:\Work\Reports", @"Work\Reports") };

            var names = RestorePlan.BuildCandidates(dest, folders).ConvertAll(c => c.FolderName);
            Assert.Contains(@"Work\Reports", names);     // the configured pair
            Assert.Contains(@"Work\Invoices", names);    // its unconfigured sibling
            Assert.Contains(@"Work\Archive", names);     // listed whole, not descended into further
            Assert.DoesNotContain(@"Work\Archive\2019", names);
            Assert.DoesNotContain("Work", names);

            // A sibling has no Windows folder to guess from, so the user says
            // where it goes; it must not inherit the configured pair's target.
            var invoices = RestorePlan.BuildCandidates(dest, folders)
                .Find(c => c.FolderName == @"Work\Invoices")!;
            Assert.Equal("", invoices.SuggestedTarget);
            Assert.Equal(TargetOrigin.None, invoices.Origin);
        }
        finally { Directory.Delete(dest, true); }
    }

    [Fact]
    public void VersionFoldersAreSnapshotsAndNeverContentToRestore()
    {
        string dest = TempRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(dest, "2026-08-14", "Documents"));
            // A stray dated folder inside a snapshot must not become a row
            // called "2026-08-13" that a restore would copy into a live folder.
            Directory.CreateDirectory(Path.Combine(dest, "2026-08-14", "2026-08-13"));
            var list = RestorePlan.BuildCandidates(Path.Combine(dest, "2026-08-14"), new List<FolderPair>());
            Assert.Equal(new[] { "Documents" }, list.ConvertAll(c => c.FolderName).ToArray());
        }
        finally { Directory.Delete(dest, true); }
    }

    [Fact]
    public void APairBackedUpIntoTheDestinationRootGetsNoRowOfItsOwn()
    {
        string dest = TempRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(dest, "Documents"));
            // "." normalizes to the destination root, whose contents are every
            // other pair's folders; restoring it as one row would drag the whole
            // backup into a single live folder.
            var folders = new List<FolderPair> { new(true, @"C:\Everything", ".") };
            var list = RestorePlan.BuildCandidates(dest, folders);
            Assert.Equal(new[] { "Documents" }, list.ConvertAll(c => c.FolderName).ToArray());
        }
        finally { Directory.Delete(dest, true); }
    }

    [Fact]
    public void TargetsThatWouldLoopOrOverwriteGuardAreRefused()
    {
        const string dest = @"E:\Backups";
        const string app = @"C:\Tools\GUARD";
        Assert.Null(RestorePlan.ValidateTarget(@"C:\Users\Someone\Documents", dest, app));

        Assert.NotNull(RestorePlan.ValidateTarget("", dest, app));
        Assert.NotNull(RestorePlan.ValidateTarget(@"Documents", dest, app));
        // A drive root is what a mis-picked Browse leaves behind, and the copies
        // would land loose across the whole drive.
        Assert.NotNull(RestorePlan.ValidateTarget(@"D:\", dest, app));
        // Restoring into the backup copies the backup into itself...
        Assert.NotNull(RestorePlan.ValidateTarget(@"E:\Backups\Documents", dest, app));
        // ...and restoring into a folder that CONTAINS it copies over the very
        // files being read from.
        Assert.NotNull(RestorePlan.ValidateTarget(@"E:\", dest, app));
        Assert.NotNull(RestorePlan.ValidateTarget(@"C:\Tools\GUARD\Logs", dest, app));
        // Sharing an ancestor is not containment.
        Assert.Null(RestorePlan.ValidateTarget(@"C:\Tools\Other", dest, app));

        // The root of a network share is the same mistake as a drive root, one
        // level up: the copies would land loose across the whole share.
        Assert.NotNull(RestorePlan.ValidateTarget(@"\\server\share", dest, app));
        Assert.NotNull(RestorePlan.ValidateTarget(@"\\server\share\", dest, app));
        Assert.Null(RestorePlan.ValidateTarget(@"\\server\share\Documents", dest, app));

        // A % that did not expand would be created as a folder literally named
        // "%NOSUCHVAR%" inside the user's own tree.
        Assert.NotNull(RestorePlan.ValidateTarget(@"C:\Data\%NOSUCHVAR%", dest, app));

        // Under a winget install the program and its settings live in different
        // folders, and overwriting either mid-restore is equally bad.
        Assert.NotNull(RestorePlan.ValidateTarget(
            @"C:\Users\Someone\AppData\Local\GUARD\Logs", dest, app,
            @"C:\Users\Someone\AppData\Local\GUARD"));
    }
}
