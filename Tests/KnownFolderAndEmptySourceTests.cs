using System;
using System.Collections.Generic;
using System.IO;
using GuardWui3.Models;
using GuardWui3.Services;
using Xunit;

namespace GuardWui3.Tests;

public class KnownFolderTests
{
    // A fresh configuration must come out fully tracked: every row carries the
    // identity GUARD follows AND already sits at the resolved location, so a new
    // install is correct on day one with nothing to ask about.
    [Fact]
    public void DefaultPairsCarryIdentityAndResolvedPath()
    {
        var pairs = KnownFolders.DefaultFolderPairs();
        Assert.Equal(7, pairs.Count);
        foreach (var f in pairs)
        {
            Assert.True(f.Include);
            Assert.True(f.IsKnownFolder);
            Assert.True(KnownFolders.IsKnownIdentity(f.KnownFolder));
            Assert.False(string.IsNullOrWhiteSpace(f.Source));
            Assert.False(string.IsNullOrWhiteSpace(f.SubFolder));
            // Must expand to a usable absolute path. Asserted as "rooted" rather
            // than "contains no %": a profile path is allowed to contain one,
            // and the real invariant is that BackupScript receives something
            // robocopy can act on.
            Assert.True(Path.IsPathRooted(Environment.ExpandEnvironmentVariables(f.Source)));
        }
        Assert.Contains(pairs, f => f.KnownFolder == "Documents");
        Assert.Contains(pairs, f => f.KnownFolder == "Contacts");   // the SHGetKnownFolderPath one
    }

    // A hand-edited settings file must not be able to make a row track something
    // that resolves nowhere.
    [Fact]
    public void OnlyRealIdentitiesAreAccepted()
    {
        Assert.True(KnownFolders.IsKnownIdentity("Documents"));
        Assert.True(KnownFolders.IsKnownIdentity("documents"));     // case-insensitive
        Assert.False(KnownFolders.IsKnownIdentity("Downloads"));    // a real folder, just not one GUARD defaults to
        Assert.False(KnownFolders.IsKnownIdentity(""));
        Assert.False(KnownFolders.IsKnownIdentity(null));
        Assert.Null(KnownFolders.Resolve("Downloads"));
        Assert.NotNull(KnownFolders.Resolve("Documents"));
    }

    // Rows saved before GUARD tracked identities adopt theirs from the old
    // hard-coded default paths - and only those rows.
    [Fact]
    public void AdoptIdentitiesTagsLegacyRowsOnly()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
        var rows = new List<FolderPair>
        {
            new(true, @"%USERPROFILE%\Documents", "Documents"),          // legacy spelling
            new(true, Path.Combine(profile, "Music"), "Music"),          // same place, spelled out
            new(true, @"D:\My Stuff", "Stuff"),                         // the user's own path
            new(true, @"%USERPROFILE%\Documents", "Docs", "Pictures"),   // already tracked: leave alone
        };
        KnownFolders.AdoptIdentities(rows);
        Assert.Equal("Documents", rows[0].KnownFolder);
        Assert.Equal("Music", rows[1].KnownFolder);
        Assert.Equal("", rows[2].KnownFolder);
        Assert.Equal("Pictures", rows[3].KnownFolder);
    }

    // Adoption must not change WHERE anything is backed up - it only lets GUARD
    // notice a later move. Getting this wrong would silently re-point a backup.
    [Fact]
    public void AdoptIdentitiesNeverTouchesSource()
    {
        var row = new FolderPair(true, @"%USERPROFILE%\Documents", "Documents");
        KnownFolders.AdoptIdentities(new[] { row });
        Assert.Equal(@"%USERPROFILE%\Documents", row.Source);
    }

    [Fact]
    public void FindMovedFlagsOnlyDivergentTrackedRows()
    {
        string here = KnownFolders.Resolve("Documents")!;
        const string gone = @"C:\GuardTest\Gone";
        var rows = new List<FolderPair>
        {
            new(true, here, "Documents", "Documents"),   // agrees: silent
            new(true, gone, "Docs2", "Documents"),       // diverged: flag
            new(false, gone, "Docs3", "Documents"),      // unticked: not being backed up
            new(true, gone, "Docs4"),                    // untracked: the user's own path
        };
        var moved = KnownFolders.FindMoved(rows);
        Assert.Single(moved);
        Assert.Equal("Docs2", moved[0].Pair.SubFolder);
        Assert.Equal(here, moved[0].ResolvedSource);
        Assert.Equal(gone, moved[0].CurrentSource);
    }

    // The declined-move key must name the DESTINATION, not just the folder:
    // keyed on the name alone, declining one move would silence every later move
    // of the same folder somewhere else.
    [Fact]
    public void DeclineKeyDistinguishesDestinations()
    {
        var pair = new FolderPair(true, @"C:\Old", "Documents", "Documents");
        var toOneDrive = new KnownFolders.Moved(pair, @"C:\Old", @"%USERPROFILE%\OneDrive\Documents");
        var toDrive = new KnownFolders.Moved(pair, @"C:\Old", @"D:\Docs");
        Assert.NotEqual(toOneDrive.Key, toDrive.Key);
        Assert.Contains("Documents", toOneDrive.Key, StringComparison.Ordinal);
    }
}

public class SourceHealthTests
{
    private static Settings Config(string dest, params FolderPair[] folders)
    {
        // ExcludePresets emptied explicitly: the shipped default ticks "system",
        // and a test that did not control the exclusion set would be asserting
        // against whatever that preset happens to contain.
        var cfg = new Settings { Dest = dest, ExcludePresets = new List<string>() };
        foreach (var f in folders) cfg.Folders.Add(f);
        return cfg;
    }

    private static SaveValidation.SourceHealth Run(Settings cfg)
        => SaveValidation.CheckSources(cfg, TimeSpan.FromSeconds(30));

    private static string Dir(params string[] parts)
    {
        string p = Path.Combine(parts);
        Directory.CreateDirectory(p);
        return p;
    }

    private static string TempRoot()
        => Path.Combine(Path.GetTempPath(), "guard-tests-" + Guid.NewGuid().ToString("N"));

    // THE regression test for the design that had to be thrown away. A folder
    // that is empty and has ALWAYS been empty - Contacts on a stock Windows
    // profile, Music on a work machine - must never produce a warning, or GUARD
    // warns out of the box and the warning becomes noise to ignore.
    [Fact]
    public void AlwaysEmptyFolderIsNeverReported()
    {
        string root = TempRoot();
        try
        {
            string src = Dir(root, "contacts");
            string dest = Dir(root, "dest");
            File.WriteAllText(Path.Combine(src, "desktop.ini"), "[.ShellClassInfo]\n");
            // The backup has never held anything for it either.
            Assert.Empty(Run(Config(dest, new FolderPair(true, src, "Contacts"))).Vanished);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    // The case the check exists for: data was there, the backup still has it,
    // and now the source has nothing to copy.
    [Fact]
    public void EmptiedSourceIsReportedWhenBackupStillHoldsFiles()
    {
        string root = TempRoot();
        try
        {
            string src = Dir(root, "documents");
            string dest = Dir(root, "dest");
            File.WriteAllText(Path.Combine(src, "desktop.ini"), "[.ShellClassInfo]\n");
            File.WriteAllText(Path.Combine(Dir(dest, "Documents"), "old-report.docx"), "from a previous run");

            var health = Run(Config(dest, new FolderPair(true, src, "Documents")));
            Assert.Single(health.Vanished);
            Assert.Equal(src, health.Vanished[0].Source);
            Assert.Equal("Documents", health.Vanished[0].SubFolder);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    // A source that still has content is never reported, however much the
    // backup holds.
    [Fact]
    public void PopulatedSourceIsNeverReported()
    {
        string root = TempRoot();
        try
        {
            string src = Dir(root, "documents");
            string dest = Dir(root, "dest");
            File.WriteAllText(Path.Combine(Dir(src, "deep", "deeper"), "buried.txt"), "still here");
            File.WriteAllText(Path.Combine(Dir(dest, "Documents"), "old.docx"), "previous run");
            Assert.Empty(Run(Config(dest, new FolderPair(true, src, "Documents"))).Vanished);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    // Versioned mode writes each run into its own dated folder, so history lives
    // across all of them - checking only the newest would go quiet the moment a
    // run created an empty folder for today.
    [Fact]
    public void VersionedModeFindsHistoryInDatedFolders()
    {
        string root = TempRoot();
        try
        {
            string src = Dir(root, "documents");
            string dest = Dir(root, "dest");
            File.WriteAllText(Path.Combine(src, "desktop.ini"), "[.ShellClassInfo]\n");
            // An older run holds the files; today's run already made an empty one.
            File.WriteAllText(Path.Combine(Dir(dest, "2026-07-01", "Documents"), "old.docx"), "history");
            Dir(dest, "2026-07-25", "Documents");
            // A folder that is not a date stamp must not be mistaken for history.
            File.WriteAllText(Path.Combine(Dir(dest, "notes"), "unrelated.txt"), "x");

            var cfg = Config(dest, new FolderPair(true, src, "Documents"));
            cfg.Versioned = true;
            Assert.Single(Run(cfg).Vanished);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    // Nothing in the destination means no history to have lost - a first-ever
    // run must not warn about every folder that happens to be empty.
    [Fact]
    public void NothingIsReportedBeforeTheFirstBackup()
    {
        string root = TempRoot();
        try
        {
            string src = Dir(root, "documents");
            string dest = Dir(root, "dest");
            File.WriteAllText(Path.Combine(src, "desktop.ini"), "[.ShellClassInfo]\n");
            Assert.Empty(Run(Config(dest, new FolderPair(true, src, "Documents"))).Vanished);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    // Exclusions that newly swallow everything in a folder leave it copying
    // nothing, exactly like a folder that emptied - and just as silently.
    [Fact]
    public void ExclusionsThatSwallowEverythingAreReported()
    {
        string root = TempRoot();
        try
        {
            string src = Dir(root, "proj");
            string dest = Dir(root, "dest");
            File.WriteAllText(Path.Combine(Dir(src, "node_modules"), "pkg.js"), "x");
            File.WriteAllText(Path.Combine(Dir(dest, "Proj"), "old.js"), "previous run");

            var cfg = Config(dest, new FolderPair(true, src, "Proj"));
            cfg.Excludes.Add(new ExcludeItem(isFolder: true, "node_modules"));
            Assert.Single(Run(cfg).Vanished);

            // Without that exclusion the folder plainly has content.
            var open = Config(dest, new FolderPair(true, src, "Proj"));
            Assert.Empty(Run(open).Vanished);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    // A reformatted or emptied backup drive. GUARD's log lives next to the exe,
    // so without this every other signal keeps reporting the last successful run
    // in green while there is nothing at the other end.
    [Fact]
    public void EmptyDestinationIsDetectedOnlyWhenReachable()
    {
        string root = TempRoot();
        try
        {
            string src = Dir(root, "documents");
            File.WriteAllText(Path.Combine(src, "notes.txt"), "still here");

            string wiped = Dir(root, "wiped");
            Assert.True(Run(Config(wiped, new FolderPair(true, src, "Documents"))).DestinationEmpty);

            string populated = Dir(root, "populated");
            File.WriteAllText(Path.Combine(Dir(populated, "Documents"), "old.docx"), "a backup");
            Assert.False(Run(Config(populated, new FolderPair(true, src, "Documents"))).DestinationEmpty);

            // Unplugged drive or offline share: a different, already-reported
            // condition, and calling it "your backup was deleted" would be wrong
            // and alarming.
            string gone = Path.Combine(root, "never-existed");
            Assert.False(Run(Config(gone, new FolderPair(true, src, "Documents"))).DestinationEmpty);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    // Acknowledgement exists because in Additive mode the backup never loses the
    // files it holds, so a folder emptied on purpose would warn for ever.
    [Fact]
    public void AcknowledgementSilencesOnlyWhatWasAcknowledged()
    {
        var docs = new SaveValidation.VanishedSource(@"C:\Data\Docs", "Documents");
        var pics = new SaveValidation.VanishedSource(@"C:\Data\Pics", "Pictures");
        var both = new List<SaveValidation.VanishedSource> { docs, pics };

        string ack = SaveValidation.AddAcknowledged(new[] { docs }, "");
        var report = SaveValidation.Unacknowledged(both, ack);
        Assert.Single(report);
        Assert.Equal(@"C:\Data\Pics", report[0].Source);

        // Acknowledging twice must not duplicate the entry.
        Assert.Equal(ack, SaveValidation.AddAcknowledged(new[] { docs }, ack));

        // Once that folder has content again it drops off the list, so a real
        // later disappearance is reported instead of inheriting the old answer.
        //
        // Note this is the PURE function: it prunes against whatever list it is
        // given, including an empty one. Deciding whether the list is
        // trustworthy belongs to the caller - an unreachable destination yields
        // an empty Vanished because nothing could be measured, and pruning on
        // that would throw away every acknowledgement the user has given. See
        // SetSourceHealth's DestinationReachable gate.
        Assert.Equal("", SaveValidation.PruneAcknowledged(
            new List<SaveValidation.VanishedSource> { pics }, ack));
        Assert.Equal(ack, SaveValidation.PruneAcknowledged(both, ack));
    }

    // A walk that runs out of time must not answer either question. "Empty" is
    // the quiet answer for a source and the ALARM for a destination, so a single
    // fallback cannot be safe for both - the budget is forced to zero here on a
    // setup that would otherwise raise both warnings.
    [Fact]
    public void ExhaustedBudgetRaisesNoAlarm()
    {
        string root = TempRoot();
        try
        {
            string src = Dir(root, "documents");
            string dest = Dir(root, "dest");
            File.WriteAllText(Path.Combine(src, "desktop.ini"), "[.ShellClassInfo]\n");
            File.WriteAllText(Path.Combine(Dir(dest, "Documents"), "old.docx"), "history");

            var cfg = Config(dest, new FolderPair(true, src, "Documents"));
            // Sanity: with a real budget this setup DOES report a vanished source.
            Assert.Single(SaveValidation.CheckSources(cfg, TimeSpan.FromSeconds(30)).Vanished);

            var starved = SaveValidation.CheckSources(cfg, TimeSpan.Zero);
            Assert.Empty(starved.Vanished);
            Assert.False(starved.DestinationEmpty);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    // The signal that stops an unplugged backup drive being read as "measured,
    // and all is well" - which is what silently erased acknowledgements.
    [Fact]
    public void DestinationReachabilityIsReportedHonestly()
    {
        string root = TempRoot();
        try
        {
            string src = Dir(root, "documents");
            File.WriteAllText(Path.Combine(src, "notes.txt"), "x");
            string dest = Dir(root, "dest");

            Assert.True(Run(Config(dest, new FolderPair(true, src, "Documents"))).DestinationReachable);
            Assert.False(Run(Config(Path.Combine(root, "unplugged"),
                new FolderPair(true, src, "Documents"))).DestinationReachable);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    // Unreachable is a different, already-reported condition (UnreachableSources);
    // reporting it here too would put the same folder in two warnings at once.
    // An unticked row is not in the backup at all.
    [Fact]
    public void UnreachableAndUntickedSourcesAreNotReported()
    {
        string root = TempRoot();
        try
        {
            string dest = Dir(root, "dest");
            string src = Dir(root, "src");
            string gone = Path.Combine(root, "never-existed");
            File.WriteAllText(Path.Combine(Dir(dest, "Gone"), "old.txt"), "previous run");
            File.WriteAllText(Path.Combine(Dir(dest, "Unticked"), "old.txt"), "previous run");

            Assert.Empty(Run(Config(dest, new FolderPair(true, gone, "Gone"))).Vanished);
            Assert.Empty(Run(Config(dest, new FolderPair(false, src, "Unticked"))).Vanished);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
