using GuardWui3.Models;
using GuardWui3.Services;
using Xunit;

namespace GuardWui3.Tests;

public class BackupScriptTests
{
    private static Settings BaseSettings() => new()
    {
        Dest = @"E:\Backups",
        Folders = { new FolderPair(true, @"%USERPROFILE%\Documents", "Documents") },
    };

    [Fact]
    public void MirrorAndAdditiveChooseTheRightFlag()
    {
        var cfg = BaseSettings();
        cfg.Mode = "Mirror";
        Assert.Contains("/MIR", BackupScript.Generate(cfg));
        cfg.Mode = "Additive";
        string additive = BackupScript.Generate(cfg);
        Assert.Contains("/E ", additive);
        Assert.DoesNotContain("/MIR", additive);
        // The junction guard is not optional in either mode.
        Assert.Contains("/XJ", additive);
    }

    [Fact]
    public void RunLockAndExitCodesAreEmitted()
    {
        string s = BackupScript.Generate(BaseSettings());
        Assert.Contains("9>\"%LOCKFILE%\" call :main", s);
        Assert.Contains("endlocal & exit /b %RC%", s);
        // On-connect skips are 3 (nothing to do), never 0/1/2.
        Assert.Contains("if not exist \"%DEST%\\\" exit /b 3", s);
        // Unreachable destination inside a real run is 2 (could not run).
        Assert.Contains("set \"RC=2\"", s);
    }

    [Fact]
    public void VersionedModeClampsKeepAndEmitsPrune()
    {
        var cfg = BaseSettings();
        cfg.Versioned = true;
        cfg.VersionsToKeep = 0;   // hand-edited ini: must clamp to 1, never 0
        string s = BackupScript.Generate(cfg);
        Assert.Contains("set \"KEEP=1\"", s);
        Assert.Contains(":prunedel", s);

        cfg.Versioned = false;
        Assert.DoesNotContain(":prune", BackupScript.Generate(cfg));
    }

    [Fact]
    public void DriftPrologueOnlyForLetterRootedDestWithSerial()
    {
        var cfg = BaseSettings();
        cfg.DestVolumeSerial = "A1B2C3D4";
        string s = BackupScript.Generate(cfg);
        Assert.Contains("set \"DESTROOT=E:\"", s);
        Assert.Contains("set \"DESTTAIL=\\Backups\"", s);
        Assert.Contains("-EncodedCommand", s);

        cfg.Dest = @"\\server\share";
        Assert.DoesNotContain("DESTROOT", BackupScript.Generate(cfg));

        cfg.Dest = @"E:\Backups";
        cfg.DestVolumeSerial = "";
        Assert.DoesNotContain("DESTROOT", BackupScript.Generate(cfg));
    }

    [Fact]
    public void DriveRootPathsStaySafeForRobocopy()
    {
        var cfg = BaseSettings();
        cfg.Dest = "E:";                                  // bare drive
        cfg.Folders.Clear();
        cfg.Folders.Add(new FolderPair(true, "D:", ""));  // bare drive source, root subfolder
        string s = BackupScript.Generate(cfg);
        Assert.Contains("set \"DEST=E:\\\"", s);           // root keeps its slash
        Assert.Contains("call :backup \"D:\\.\" \"%DEST%\\.\"", s);
    }

    [Fact]
    public void SpacedExcludeTokensAreQuoted()
    {
        var cfg = BaseSettings();
        cfg.ExcludePresets = new() { "system" };          // holds "System Volume Information"
        string s = BackupScript.Generate(cfg);
        Assert.Contains("\"System Volume Information\"", s);
        Assert.Contains("$RECYCLE.BIN", s);
    }
}
