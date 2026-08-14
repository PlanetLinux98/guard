using System;
using System.Collections.Generic;
using GuardWui3.Models;
using GuardWui3.Services;
using Xunit;

namespace GuardWui3.Tests;

public class ScheduledTasksTests
{
    [Theory]
    [InlineData("2:5", "02:05")]
    [InlineData("02:00", "02:00")]
    [InlineData(" 23:59 ", "23:59")]
    [InlineData("25:00", "03:00")]   // a day-plus value must fall back
    [InlineData("garbage", "03:00")]
    [InlineData("", "03:00")]
    [InlineData(null, "03:00")]
    public void NormalizeTimeAcceptsOnlyRealTimes(string? input, string expected)
        => Assert.Equal(expected, ScheduledTasks.NormalizeTime(input));

    [Fact]
    public void BackupActionCurrencyChecksExeAndHelperArgs()
    {
        // Whatever production resolves the running exe to, rather than a
        // rebuilt "BaseDir + GUARD.exe": the filename is not assumed any more,
        // so hard-coding it here would only re-test the assumption that was
        // removed. Under the test host this is testhost.exe/dotnet.exe, which
        // is fine - the check is that the action's path matches GUARD's own.
        string exe = GuardPaths.ExePath;
        Assert.True(ScheduledTasks.IsCurrentBackupAction(
            new ScheduledTasks.TaskActionInfo("GUARD Backup", "\"" + exe + "\"", "--run-backup auto")));
        // Legacy visible cmd.exe action: must read as stale so it re-registers.
        Assert.False(ScheduledTasks.IsCurrentBackupAction(
            new ScheduledTasks.TaskActionInfo("GUARD Backup", "cmd.exe", "/c \"C:\\Old\\guard-backup.cmd\" auto")));
        // Moved folder: same helper form, old exe path.
        Assert.False(ScheduledTasks.IsCurrentBackupAction(
            new ScheduledTasks.TaskActionInfo("GUARD Backup", "C:\\OldPlace\\GUARD.exe", "--run-backup auto")));
    }
}

public class SaveValidationTests
{
    private static List<FolderPair> Pairs(params string[] sources)
    {
        var list = new List<FolderPair>();
        foreach (var s in sources) list.Add(new FolderPair(true, s, "Sub"));
        return list;
    }

    [Fact]
    public void OverlapCatchesContainmentBothWaysButNotSiblings()
    {
        // Source contains the destination.
        Assert.Single(SaveValidation.OverlappingSources(@"C:\Users\x\Documents\Backup", Pairs(@"C:\Users\x\Documents")));
        // Destination contains the source.
        Assert.Single(SaveValidation.OverlappingSources(@"C:\Users\x", Pairs(@"C:\Users\x\Documents")));
        // Equality is overlap.
        Assert.Single(SaveValidation.OverlappingSources(@"C:\Data", Pairs(@"C:\Data")));
        // Siblings and shared drive roots are fine.
        Assert.Empty(SaveValidation.OverlappingSources(@"C:\Backup", Pairs(@"C:\Users\x\Documents")));
        // Prefix without a segment boundary is NOT containment.
        Assert.Empty(SaveValidation.OverlappingSources(@"C:\Foobar", Pairs(@"C:\Foo")));
    }

    [Fact]
    public void UnresolvedPercentPathsFlagOnlyNonVariablePercents()
    {
        var pairs = new List<FolderPair>
        {
            new(true, @"%USERPROFILE%\Documents", "Docs"),  // resolves: fine
            new(true, @"C:\Data\100%", "Data"),             // literal %: flagged
            new(false, @"C:\Skip\50%", "Skip"),             // unticked: ignored
        };
        var bad = SaveValidation.UnresolvedPercentPaths(@"D:\Backup", pairs);
        Assert.Single(bad);
        Assert.Equal(@"C:\Data\100%", bad[0]);

        // The destination is checked too; a percent-free setup flags nothing.
        Assert.Single(SaveValidation.UnresolvedPercentPaths(@"D:\100% Backups", new List<FolderPair>()));
        Assert.Empty(SaveValidation.UnresolvedPercentPaths(@"D:\Backup", new List<FolderPair>()));
    }

    [Fact]
    public void MirrorConflictsCatchDuplicateNestedAndRootSubfolders()
    {
        var dup = new List<FolderPair>
        {
            new(true, @"C:\A", "Docs"),
            new(true, @"C:\B", "docs"),      // case-insensitive duplicate
        };
        Assert.Single(SaveValidation.MirrorSubfolderConflicts(dup));

        var nested = new List<FolderPair>
        {
            new(true, @"C:\A", "Docs"),
            new(true, @"C:\B", @"Docs\Inner"),
        };
        Assert.Single(SaveValidation.MirrorSubfolderConflicts(nested));

        var root = new List<FolderPair>
        {
            new(true, @"C:\A", ""),          // legacy: the destination root
            new(true, @"C:\B", "Docs"),
        };
        Assert.Single(SaveValidation.MirrorSubfolderConflicts(root));

        var fine = new List<FolderPair>
        {
            new(true, @"C:\A", "Docs"),
            new(true, @"C:\B", "Pictures"),
        };
        Assert.Empty(SaveValidation.MirrorSubfolderConflicts(fine));
    }

    [Fact]
    public void MirrorConflictsCatchDotSubfoldersResolvingToTheSameDestination()
    {
        // "." IS the destination root, so a Mirror pair using it purges every
        // other pair's output. It used to key as ".\", which prefix-matched
        // nothing, so the save passed and the next run ran /MIR over the root.
        var dot = new List<FolderPair>
        {
            new(true, @"C:\A", "."),
            new(true, @"C:\B", "Docs"),
        };
        Assert.Single(SaveValidation.MirrorSubfolderConflicts(dot));

        // ".\Docs" and "Docs" are one folder, so each run purged the other.
        var dotted = new List<FolderPair>
        {
            new(true, @"C:\A", @".\Docs"),
            new(true, @"C:\B", "Docs"),
        };
        Assert.Single(SaveValidation.MirrorSubfolderConflicts(dotted));

        // Normalizing must not invent conflicts between genuinely distinct rows.
        var fine = new List<FolderPair>
        {
            new(true, @"C:\A", @".\Docs"),
            new(true, @"C:\B", @"Pictures\"),
        };
        Assert.Empty(SaveValidation.MirrorSubfolderConflicts(fine));
    }

    [Fact]
    public void NormalizeSubFolderDropsDotSegmentsAndStraySeparators()
    {
        Assert.Equal("", SaveValidation.NormalizeSubFolder(null));
        Assert.Equal("", SaveValidation.NormalizeSubFolder("."));
        Assert.Equal("", SaveValidation.NormalizeSubFolder(@".\"));
        // The bare separator resolves to the root too. These three are exactly
        // what FolderDialog.Validate refuses: not blank, but they name the
        // destination itself rather than a folder under it.
        Assert.Equal("", SaveValidation.NormalizeSubFolder(@"\"));
        Assert.Equal("Docs", SaveValidation.NormalizeSubFolder(@".\Docs"));
        Assert.Equal("Docs", SaveValidation.NormalizeSubFolder(@"\Docs\"));
        Assert.Equal("Docs", SaveValidation.NormalizeSubFolder("  Docs  "));
        Assert.Equal(@"Docs\Inner", SaveValidation.NormalizeSubFolder(@"Docs\.\Inner"));
        Assert.Equal(@"Docs\Inner", SaveValidation.NormalizeSubFolder(@"Docs\\Inner"));
    }
}

public class AppSettingsRestoreTests
{
    // The manifest is hand-editable JSON; folder/destRelativePath values that
    // are rooted or climb with ".." must be dropped, or a doctored bundle
    // could read from or restore over arbitrary folders.
    [Fact]
    public void BuildCandidatesDropsEntriesThatEscapeTheBundleOrAnchor()
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "guard-test-" + Guid.NewGuid().ToString("N"));
        string bundle = System.IO.Path.Combine(root, "bundle");
        string outside = System.IO.Path.Combine(root, "outside");
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(bundle, @"AppSettings\AppData\GoodApp"));
        System.IO.Directory.CreateDirectory(outside);
        try
        {
            const string goodRel = @"AppSettings\AppData\GoodApp";
            var manifest = new AppSettingsManifest
            {
                Entries = new[]
                {
                    Entry("GoodApp", goodRel),          // kept
                    Entry("GoodApp", @"..\outside"),    // exists, but escapes the bundle
                    Entry("GoodApp", outside),          // rooted source outside the bundle
                    Entry("..", goodRel),               // folder climbs out of the anchor
                    Entry(".", goodRel),                // folder IS the anchor root
                    Entry(@"Sub\Inner", goodRel),       // folder is not a bare name
                    Entry(@"C:\evil", goodRel),         // rooted folder
                },
            };
            var rows = AppSettingsRestore.BuildCandidates(manifest, bundle);
            Assert.Single(rows);
            Assert.Equal("GoodApp", rows[0].FolderName);
        }
        finally
        {
            System.IO.Directory.Delete(root, recursive: true);
        }
    }

    // A hand-edited manifest can carry a literal "entries": [null], which
    // deserializes to a real null ELEMENT that the Length check cannot see.
    // Reading one used to take the whole app down instead of dropping a row.
    [Fact]
    public void BuildCandidatesDropsNullEntriesInsteadOfThrowing()
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "guard-test-" + Guid.NewGuid().ToString("N"));
        string bundle = System.IO.Path.Combine(root, "bundle");
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(bundle, @"AppSettings\AppData\GoodApp"));
        try
        {
            var manifest = new AppSettingsManifest
            {
                Entries = new[] { null!, Entry("GoodApp", @"AppSettings\AppData\GoodApp"), null! },
            };
            var rows = AppSettingsRestore.BuildCandidates(manifest, bundle);
            Assert.Single(rows);
            Assert.Equal("GoodApp", rows[0].FolderName);
        }
        finally
        {
            System.IO.Directory.Delete(root, recursive: true);
        }
    }

    // The bundle's drive is gone by the time the restore runs (the install phase
    // between the confirmation and here can take many minutes). The live folder
    // must be left exactly as it was, and above all must not be counted as
    // restored - it used to be renamed aside and then reported as a success,
    // leaving the user's real settings under a name nothing mentioned.
    [Fact]
    public void RestoreLeavesTheLiveFolderAloneWhenTheSavedCopyIsGone()
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "guard-test-" + Guid.NewGuid().ToString("N"));
        string live = System.IO.Path.Combine(root, "live");
        string target = System.IO.Path.Combine(live, "MyApp");
        System.IO.Directory.CreateDirectory(target);
        System.IO.File.WriteAllText(System.IO.Path.Combine(target, "settings.json"), "the user's real settings");
        try
        {
            var picked = new List<AppSettingsRestoreCandidate>
            {
                new()
                {
                    // Never created: this is the bundle that went away.
                    SourcePath = System.IO.Path.Combine(root, "bundle", @"AppSettings\AppData\MyApp"),
                    FolderName = "MyApp",
                    RootAnchor = "%APPDATA%",
                    TargetPath = target,
                    TargetExists = true,
                },
            };
            var stats = AppSettingsRestore.RestoreCandidates(
                picked, null, System.Threading.CancellationToken.None);

            Assert.Equal(0, stats.Folders);            // nothing claimed as restored
            Assert.Equal(0, stats.Replaced);           // nothing displaced
            Assert.Equal(1, stats.SourceUnavailable);
            Assert.Empty(stats.ManualRecoveryPaths);
            Assert.True(System.IO.Directory.Exists(target));
            Assert.Equal("the user's real settings",
                System.IO.File.ReadAllText(System.IO.Path.Combine(target, "settings.json")));
            Assert.Empty(System.IO.Directory.GetDirectories(live, "*.guard-old-*"));
        }
        finally
        {
            System.IO.Directory.Delete(root, recursive: true);
        }
    }

    // The source passes the existence pre-check but still cannot be walked, so
    // the copy comes back empty AFTER the live folder has been moved aside. The
    // move has to be undone, and "kept aside" must not be reported for a folder
    // that was put back. A junction stands in for the general case, since
    // FileTreeCopy refuses to walk a link as a copy root.
    [Fact]
    public void RestorePutsTheOriginalBackWhenTheCopyCannotRun()
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "guard-test-" + Guid.NewGuid().ToString("N"));
        string live = System.IO.Path.Combine(root, "live");
        string target = System.IO.Path.Combine(live, "MyApp");
        string real = System.IO.Path.Combine(root, "real");
        string link = System.IO.Path.Combine(root, "link");
        System.IO.Directory.CreateDirectory(target);
        System.IO.Directory.CreateDirectory(real);
        System.IO.File.WriteAllText(System.IO.Path.Combine(target, "settings.json"), "the user's real settings");
        System.IO.File.WriteAllText(System.IO.Path.Combine(real, "other.json"), "not theirs");
        try
        {
            var mk = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "cmd.exe", "/c mklink /J \"" + link + "\" \"" + real + "\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
            mk!.WaitForExit();
            // No junction support on this volume: nothing to assert, and a
            // filesystem-dependent failure would be a false alarm.
            if (mk.ExitCode != 0) return;

            var picked = new List<AppSettingsRestoreCandidate>
            {
                new()
                {
                    SourcePath = link,
                    FolderName = "MyApp",
                    RootAnchor = "%APPDATA%",
                    TargetPath = target,
                    TargetExists = true,
                },
            };
            var stats = AppSettingsRestore.RestoreCandidates(
                picked, null, System.Threading.CancellationToken.None);

            Assert.Equal(0, stats.Folders);
            Assert.Equal(0, stats.Replaced);           // the move-aside was undone
            Assert.Equal(1, stats.SourceUnavailable);
            Assert.Empty(stats.ManualRecoveryPaths);
            Assert.True(System.IO.Directory.Exists(target));
            Assert.Equal("the user's real settings",
                System.IO.File.ReadAllText(System.IO.Path.Combine(target, "settings.json")));
            Assert.Empty(System.IO.Directory.GetDirectories(live, "*.guard-old-*"));
        }
        finally
        {
            // The junction goes first and NON-recursively: a recursive delete
            // walks through it and fails on the link itself, which would turn a
            // passing test into a teardown error.
            try { System.IO.Directory.Delete(link); } catch { }
            System.IO.Directory.Delete(root, recursive: true);
        }
    }

    private static AppSettingsManifestEntry Entry(string folder, string rel) => new()
    {
        Folder = folder,
        RootAnchor = "%APPDATA%",
        DestRelativePath = rel,
    };
}

public class FileTreeCopyTests
{
    // The signal the restore depends on. FileTreeCopy reports every per-file and
    // per-subfolder problem through counters and throws nothing, so "the root
    // could not be walked at all" has to come back some other way - otherwise a
    // caller that has already moved the live folder aside reads a silent no-op
    // as a finished copy.
    [Fact]
    public void CopyReportsWhetherTheSourceRootCouldBeWalked()
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "guard-test-" + Guid.NewGuid().ToString("N"));
        string src = System.IO.Path.Combine(root, "src");
        System.IO.Directory.CreateDirectory(src);
        System.IO.File.WriteAllText(System.IO.Path.Combine(src, "a.txt"), "x");
        try
        {
            var ok = new TreeCopyStats();
            Assert.True(FileTreeCopy.Copy(new System.IO.DirectoryInfo(src),
                System.IO.Path.Combine(root, "dst"), ok));
            Assert.Equal(1, ok.Files);

            var gone = new TreeCopyStats();
            string dst2 = System.IO.Path.Combine(root, "dst2");
            Assert.False(FileTreeCopy.Copy(
                new System.IO.DirectoryInfo(System.IO.Path.Combine(root, "not-here")), dst2, gone));
            Assert.Equal(0, gone.Files);
            // Nothing is created for a root that cannot be walked, so a caller
            // rolling back has no half-written folder to clear.
            Assert.False(System.IO.Directory.Exists(dst2));
        }
        finally
        {
            System.IO.Directory.Delete(root, recursive: true);
        }
    }
}

public class UpdaterVersionTests
{
    // The test csproj pins <Version>0.5.0</Version>, which is what GuardPaths
    // reports as the running version inside this assembly.
    [Theory]
    [InlineData("v0.5.1", true)]
    [InlineData("v0.6.0", true)]
    [InlineData("v1.0.0", true)]
    [InlineData("v0.5.0", false)]
    [InlineData("0.5.0", false)]
    [InlineData("v0.4.9", false)]
    [InlineData("v0.6.0-rc.1+abc123", true)]   // pre-release/build metadata trimmed
    [InlineData("not-a-version", false)]
    [InlineData("v1.2", false)]                // needs all three core parts
    public void IsNewerComparesTheCoreOnly(string tag, bool expected)
        => Assert.Equal(expected, Updater.IsNewer(tag));

    [Fact]
    public void NotesToPlainTextStripsMarkdownForSpeech()
    {
        string notes = "## What's new\r\n" +
                       "* **Bold** fix for [issue](https://x/1)\r\n" +
                       "+ `code` and ~~gone~~\r\n" +
                       "```\r\nfence line\r\n```\r\n" +
                       "> quoted\r\n";
        string plain = Updater.NotesToPlainText(notes);
        Assert.Contains("- Bold fix for issue", plain);
        Assert.Contains("- code and gone", plain);
        Assert.DoesNotContain("##", plain);
        Assert.DoesNotContain("**", plain);
        Assert.DoesNotContain("```", plain);
        Assert.DoesNotContain(">", plain);
    }
}
