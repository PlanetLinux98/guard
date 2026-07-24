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
        // Matches production's resolution (GuardPaths.BaseDir + "GUARD.exe"), not
        // the test host's own Environment.ProcessPath (testhost.exe/dotnet.exe).
        string exe = System.IO.Path.Combine(GuardPaths.BaseDir, "GUARD.exe");
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

    private static AppSettingsManifestEntry Entry(string folder, string rel) => new()
    {
        Folder = folder,
        RootAnchor = "%APPDATA%",
        DestRelativePath = rel,
    };
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
