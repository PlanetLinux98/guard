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
            relaunch: true, appDir: @"C:\Users\Élise\GUARD", pid: 1234);
        Assert.Contains("chcp 65001 >nul", update);
        Assert.True(IndexOf(update, "chcp 65001") < IndexOf(update, "Élise"));
        // The relaunch variant must still restart from the install folder.
        Assert.Contains("start \"\" \"%APPDIR%\\GUARD.exe\"", update);
        Assert.DoesNotContain("start \"\"",
            Updater.GenerateApplyScript(@"C:\z.zip", relaunch: false, appDir: @"C:\G", pid: 1));
    }

    private static int IndexOf(string haystack, string needle)
        => haystack.IndexOf(needle, System.StringComparison.Ordinal);

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
