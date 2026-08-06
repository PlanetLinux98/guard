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
        // The junction guard is not optional in either mode, and it must stay
        // scoped to DIRECTORY junctions: plain /XJ also excludes file-level
        // reparse points, which is what a OneDrive Files On-Demand placeholder
        // is, so it could silently drop every non-resident file from the backup.
        Assert.Contains("/XJD", additive);
        Assert.DoesNotContain("/XJ ", additive);
        Assert.DoesNotContain("/XJF", additive);
    }

    [Fact]
    public void AMissingSourceMakesTheRunReportErrorsRatherThanSuccess()
    {
        // A source that is not there was not copied, so the run must not end on
        // FINISHED OK / RC=0: an external disk that did not mount otherwise gave
        // a green status line and a "finished successfully" toast forever.
        string s = BackupScript.Generate(BaseSettings());
        int skip = IndexOf(s, "SKIP source not found");
        Assert.True(skip >= 0, "the not-found branch should log a SKIP line");
        int haderr = s.IndexOf("set \"HADERR=1\"", skip, System.StringComparison.Ordinal);
        int eof = s.IndexOf("goto :eof", skip, System.StringComparison.Ordinal);
        Assert.True(haderr > skip && haderr < eof,
            "the not-found branch must set HADERR before it returns");
    }

    [Fact]
    public void DotSubfoldersEmitTheSameDestinationTheMirrorGuardKeysOn()
    {
        // The guard normalizes "." away, so the generated path must too, or the
        // script would write somewhere the save was never checked against.
        var cfg = BaseSettings();
        cfg.Folders.Clear();
        cfg.Folders.Add(new FolderPair(true, @"C:\A", @".\Docs"));
        Assert.Contains(@"""%DEST%\Docs""", BackupScript.Generate(cfg));

        // "." alone means the destination root, spelled "." so the quoted
        // argument never ends in a backslash.
        cfg.Folders.Clear();
        cfg.Folders.Add(new FolderPair(true, @"C:\A", "."));
        Assert.Contains(@"""%DEST%\.""", BackupScript.Generate(cfg));
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

    [Fact]
    public void GeneratedScriptsSwitchTheConsoleToUtf8BeforeAnyEmbeddedPath()
    {
        // The generated scripts are written as UTF-8, but cmd parses batch
        // lines in the OEM console codepage; without the chcp switch, any
        // non-ASCII character in an embedded path was mangled and every path
        // test failed (self-update broke the same way for non-ASCII Windows
        // usernames, via %TEMP% in the staged zip path). The switch must land
        // before the first embedded value.
        var cfg = BaseSettings();
        cfg.Dest = @"E:\Sauvegardes-Élise";
        string backup = BackupScript.Generate(cfg);
        Assert.Contains("chcp 65001 >nul", backup);
        Assert.True(IndexOf(backup, "chcp 65001") < IndexOf(backup, cfg.Dest));

        cfg.ImageTarget = @"\\nas\images-élise";
        cfg.ImageTargetKind = "NetworkShare";
        string image = SystemImageScript.Generate(cfg);
        Assert.Contains("chcp 65001 >nul", image);
        Assert.True(IndexOf(image, "chcp 65001") < IndexOf(image, cfg.ImageTarget));

        string update = Updater.GenerateApplyScript(
            @"C:\Users\Élise\AppData\Local\Temp\GUARD-update-X\GUARD.zip",
            relaunch: true, appDir: @"C:\Users\Élise\GUARD");
        Assert.Contains("chcp 65001 >nul", update);
        Assert.True(IndexOf(update, "chcp 65001") < IndexOf(update, "Élise"));
        // The relaunch variant must still restart from the install folder.
        Assert.Contains("start \"\" \"%APPDIR%\\GUARD.exe\"", update);
        Assert.DoesNotContain("start \"\"",
            Updater.GenerateApplyScript(@"C:\z.zip", relaunch: false, appDir: @"C:\G"));
    }

    private static int IndexOf(string haystack, string needle)
        => haystack.IndexOf(needle, System.StringComparison.Ordinal);

    [Fact]
    public void TheApplierWaitsForEveryGuardInstanceNotJustTheWindowPid()
    {
        // The scheduled backup runs a SECOND GUARD.exe, so a PID-only wait
        // cleared while that one still held the exe open and tar then overwrote
        // part of the install and skipped the rest.
        string s = Updater.GenerateApplyScript(
            @"C:\stage\GUARD.zip", relaunch: false, appDir: @"C:\Tools\GUARD");
        Assert.Contains("tasklist /FI \"IMAGENAME eq GUARD.exe\"", s);
        Assert.DoesNotContain("PID eq", s);

        // A renamed portable copy must still be matched by its real filename,
        // or the wait falls straight through again.
        string renamed = Updater.GenerateApplyScript(
            @"C:\stage\GUARD.zip", relaunch: false, appDir: @"C:\Tools\G", exeName: "Backup.exe");
        Assert.Contains("tasklist /FI \"IMAGENAME eq Backup.exe\"", renamed);
    }

    [Fact]
    public void UpdateScriptSurvivesDriveRootAndPercentInstallPaths()
    {
        // A drive-root install keeps BaseDir's trailing backslash, and embedded
        // verbatim it made tar's -C argument end in \" (an escaped quote to its
        // parser), so a root install could never self-update. The root embeds
        // as X:\. because "X:" alone is drive-relative.
        string root = Updater.GenerateApplyScript(
            @"C:\stage\GUARD.zip", relaunch: false, appDir: @"E:\");
        Assert.Contains("set \"APPDIR=E:\\.\"", root);

        // cmd drops an unmatched % when it parses a batch line, so a literal %
        // in either embedded path must be escaped as %%.
        string pct = Updater.GenerateApplyScript(
            @"C:\100% temp\GUARD.zip", relaunch: false, appDir: @"C:\100% backups\GUARD");
        Assert.Contains("set \"APPDIR=C:\\100%% backups\\GUARD\"", pct);
        Assert.Contains("set \"ZIP=C:\\100%% temp\\GUARD.zip\"", pct);

        // A normal install path passes through untouched.
        string plain = Updater.GenerateApplyScript(
            @"C:\stage\GUARD.zip", relaunch: false, appDir: @"C:\Tools\GUARD");
        Assert.Contains("set \"APPDIR=C:\\Tools\\GUARD\"", plain);
    }

    [Fact]
    public void PreviewRunsRedirectToASeparateLogFile()
    {
        // A preview (test) run must never write to backup_last.log: BackupHealth
        // reads that file's FINISHED line and mtime for the "last backup" status,
        // and Open Last Log opens it too - either would show a no-op preview
        // instead of the real last backup if they shared the file.
        string s = BackupScript.Generate(BaseSettings());
        Assert.Contains("if defined DRY set \"LOG=%LOGDIR%\\backup_preview.log\"", s);
    }
}
