using System;
using System.IO;
using System.Text;
using System.Threading;
using GuardWui3.Services;
using Xunit;

namespace GuardWui3.Tests;

// End-to-end against the real robocopy, unlike RestoreRunnerTests which only
// checks the arguments.
//
// It earns its keep because the flag that makes a restore safe is one robocopy
// interprets, not one GUARD implements: robocopy's default file classes include
// "Older", so a restore without /XO puts a three-day-old backup copy over a file
// edited this morning. An argument-string test cannot catch that changing, and
// nothing else in the suite would notice. Robocopy ships with every supported
// Windows, so this needs nothing installed.
public class RestoreRoundTripTests
{
    private sealed class Scratch : IDisposable
    {
        public readonly string Root, Backup, Live;
        public Scratch()
        {
            Root = Path.Combine(Path.GetTempPath(), "guard-restore-run-" + Guid.NewGuid().ToString("N"));
            Backup = Path.Combine(Root, "backup");
            Live = Path.Combine(Root, "live");
            Directory.CreateDirectory(Backup);
            Directory.CreateDirectory(Live);
        }
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }

    private static void WriteFile(string dir, string name, string content, DateTime when)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        File.SetLastWriteTime(path, when);
    }

    private static int Run(Scratch s, RestoreMode mode, bool preview = false, string? log = null)
        => RestoreRunner.RunOne(s.Backup, s.Live, mode, preview, log, _ => { }, CancellationToken.None);

    [Fact]
    public void TheDefaultModeRestoresWhatIsMissingWithoutTouchingNewerWork()
    {
        using var s = new Scratch();
        var now = DateTime.Now;
        // Edited since the backup: must survive.
        WriteFile(s.Backup, "edited.txt", "from-the-backup", now.AddDays(-3));
        WriteFile(s.Live, "edited.txt", "my-newer-work", now);
        // Deleted by accident: must come back.
        WriteFile(s.Backup, "deleted.txt", "recovered", now.AddDays(-3));
        // Created since the backup: must not be removed.
        WriteFile(s.Live, "created-since.txt", "keep me", now);
        // Broken live copy that is OLDER than the backup's: the backup wins.
        WriteFile(s.Backup, "reverted.txt", "good", now);
        WriteFile(s.Live, "reverted.txt", "bad", now.AddDays(-3));

        Assert.True(Run(s, RestoreMode.AddAndUpdate) < RestoreRunner.FailureThreshold);

        Assert.Equal("my-newer-work", File.ReadAllText(Path.Combine(s.Live, "edited.txt")));
        Assert.Equal("recovered", File.ReadAllText(Path.Combine(s.Live, "deleted.txt")));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(s.Live, "created-since.txt")));
        Assert.Equal("good", File.ReadAllText(Path.Combine(s.Live, "reverted.txt")));
    }

    [Fact]
    public void ReplaceOverwritesNewerFilesButStillDeletesNothing()
    {
        using var s = new Scratch();
        var now = DateTime.Now;
        WriteFile(s.Backup, "edited.txt", "from-the-backup", now.AddDays(-3));
        WriteFile(s.Live, "edited.txt", "my-newer-work", now);
        WriteFile(s.Live, "created-since.txt", "keep me", now);

        Assert.True(Run(s, RestoreMode.Replace) < RestoreRunner.FailureThreshold);

        Assert.Equal("from-the-backup", File.ReadAllText(Path.Combine(s.Live, "edited.txt")));
        // The whole reason /MIR is never passed: an "exact" restore would delete
        // this, and it is the file the user made since their last backup.
        Assert.True(File.Exists(Path.Combine(s.Live, "created-since.txt")));
    }

    [Fact]
    public void APreviewChangesNothingOnDisk()
    {
        using var s = new Scratch();
        var now = DateTime.Now;
        WriteFile(s.Backup, "only-in-backup.txt", "x", now);
        Run(s, RestoreMode.Replace, preview: true);
        Assert.False(File.Exists(Path.Combine(s.Live, "only-in-backup.txt")));
    }

    // Measured, and the reason the durable log is not built from stdout:
    // launched without a shell, robocopy's console output down-converts a name
    // like this to question marks before GUARD can read it, while /UNILOG keeps
    // it exactly.
    [Fact]
    public void TheUnicodeLogKeepsFileNamesConsoleOutputWouldDestroy()
    {
        using var s = new Scratch();
        string name = "é中Ж.txt";      // e-acute, CJK, Cyrillic
        WriteFile(s.Backup, name, "x", DateTime.Now);
        string log = Path.Combine(s.Root, "part.log");

        var console = new StringBuilder();
        RestoreRunner.RunOne(s.Backup, s.Live, RestoreMode.AddAndUpdate, preview: true, log,
            line => console.AppendLine(line), CancellationToken.None);

        Assert.Contains(name, File.ReadAllText(log, Encoding.Unicode));
        // The contrast this test exists for: the same run's console output has
        // already lost the characters, so echoing THAT into the restore log
        // would record a file name the user cannot match to anything.
        Assert.DoesNotContain(name, console.ToString());
    }
}
