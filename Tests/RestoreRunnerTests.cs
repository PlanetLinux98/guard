using GuardWui3.Services;
using Xunit;

namespace GuardWui3.Tests;

public class RestoreRunnerTests
{
    private static string Join(RestoreMode mode, bool preview, string? log = null)
        => string.Join(" ", RestoreRunner.BuildArgs(@"E:\Backups\Documents",
            @"C:\Users\Someone\Documents", mode, preview, log));

    // The safety property the whole feature rests on, and the one the plain
    // robocopy default gets wrong: without /XO a restore replaces a file edited
    // since the backup with the backup's older copy.
    [Fact]
    public void TheDefaultModeNeverOverwritesNewerWork()
    {
        string safe = Join(RestoreMode.AddAndUpdate, preview: false);
        Assert.Contains("/XO", safe);
        // Replace exists precisely to overwrite regardless of date (a file that
        // is broken but carries a newer timestamp), so it must NOT carry /XO.
        Assert.DoesNotContain("/XO", Join(RestoreMode.Replace, preview: false));
    }

    // Restore copies INTO live folders, so nothing it runs may ever delete
    // what is already there. /MIR (and its /PURGE half) is what would.
    [Theory]
    [InlineData(RestoreMode.AddAndUpdate)]
    [InlineData(RestoreMode.Replace)]
    public void NoModeEverDeletesAtTheDestination(RestoreMode mode)
    {
        string args = Join(mode, preview: false);
        Assert.DoesNotContain("/MIR", args);
        Assert.DoesNotContain("/PURGE", args);
        Assert.Contains("/E", args);
    }

    [Fact]
    public void PreviewOnlyListsAndOnlyWhenAsked()
    {
        Assert.DoesNotContain(" /L", Join(RestoreMode.Replace, preview: false));
        Assert.Contains("/L", Join(RestoreMode.Replace, preview: true));
    }

    // Naming a log silences stdout unless /TEE comes with it, and stdout is
    // where the progress bar's byte counts come from - so the two must never be
    // separated.
    [Fact]
    public void ALogAlwaysBringsTeeWithIt()
    {
        string withLog = Join(RestoreMode.AddAndUpdate, preview: false, @"C:\GUARD\Logs\restore-part.log");
        Assert.Contains(@"/UNILOG:C:\GUARD\Logs\restore-part.log", withLog);
        Assert.Contains("/TEE", withLog);

        string noLog = Join(RestoreMode.AddAndUpdate, preview: false);
        Assert.DoesNotContain("/UNILOG", noLog);
        Assert.DoesNotContain("/TEE", noLog);
    }

    // Measured: /UNILOG+ (the append form) does not write Unicode at all and
    // mangles non-Latin file names, which is why each folder gets its own log
    // that GUARD folds together itself.
    [Fact]
    public void TheLogIsNeverOpenedInAppendMode()
        => Assert.DoesNotContain("/UNILOG+", Join(RestoreMode.AddAndUpdate, preview: false, @"C:\a\b.log"));

    [Fact]
    public void JunctionsAreExcludedAsDirectoriesOnly()
    {
        string args = Join(RestoreMode.AddAndUpdate, preview: false);
        Assert.Contains("/XJD", args);
        // Plain /XJ would also exclude file-level reparse points, which is what
        // a cloud placeholder is.
        Assert.DoesNotContain("/XJ ", args);
        Assert.DoesNotContain("/XJF", args);
    }

    // Robocopy parses its own command line, so a path argument that ends in a
    // backslash reads the closing quote as escaped and mangles everything after
    // it. Shared with the generated backup script; see RobocopyPath.
    [Fact]
    public void PathArgumentsNeverEndInABackslash()
    {
        var args = RestoreRunner.BuildArgs(@"E:\Backups\Docs\", @"D:\", RestoreMode.AddAndUpdate, false, null);
        Assert.Equal(@"E:\Backups\Docs", args[0]);
        Assert.Equal(@"D:\.", args[1]);
        Assert.Equal(@"D:\.", RobocopyPath.Arg("D:"));
        // A destination other segments compose onto keeps a drive root's
        // backslash ("D:" alone is drive-relative).
        Assert.Equal(@"D:\", RobocopyPath.Root("D:"));
        Assert.Equal(@"E:\Backups", RobocopyPath.Root(@"E:\Backups\"));
    }
}
