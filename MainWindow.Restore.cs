using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using GuardWui3.Models;
using GuardWui3.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GuardWui3;

// File Restore: copies files from the backup back into the folders they came
// from. Launched from the File Backup page's action bar; the choices are made
// in RestoreDialog and the run streams into this page's existing progress bar,
// output box and Stop button.
public sealed partial class MainWindow : Window
{
    private bool _restoreRunning;

    private async void OnRestoreFiles(object sender, RoutedEventArgs e)
    {
        if (_backupRunning || _restoreRunning)
        {
            await ShowMessageAsync("GUARD", "A backup or restore is already running. Wait for it to finish, or press Stop to cancel it.");
            return;
        }
        if (IsAnyJobRunning)
        {
            await ShowMessageAsync("GUARD", Capitalize(RunningJobLabel()!) + " is currently running. Wait for it to finish before restoring.");
            return;
        }
        // The destination as it is on screen, not a harvested one: restoring
        // only READS the backup, so there is nothing here worth committing the
        // page's unsaved edits for.
        string dest = (TxtDest.Text ?? "").Trim();
        if (dest.Length == 0)
        {
            await ShowMessageAsync("GUARD", "Enter the backup destination first, so GUARD knows where to restore from.");
            return;
        }

        // Claimed HERE, before the first await, and not when the copying starts:
        // the destination walk below can block for seconds on a spun-down drive,
        // and Run Now is still live during it. Without the claim a backup could
        // start in that window, and the run below would then overwrite the
        // backup's _runCts - orphaning it, so Stop Backup and the close handler
        // would both act on the wrong job. RunScript's own comment describes the
        // same hazard for the backup path.
        _restoreRunning = true;
        string? outcome = null;
        try
        {
            // Off the UI thread: an unplugged drive or a dead share can make the
            // directory walk block for seconds.
            List<BackupSnapshot> snapshots;
            try
            {
                snapshots = await System.Threading.Tasks.Task.Run(() => RestorePlan.FindSnapshots(dest));
            }
            catch (Exception ex)
            {
                await ShowMessageAsync("GUARD", "Could not read the backup destination:\n\n" + Expand(dest) + "\n\n" + ex.Message);
                return;
            }
            if (snapshots.Count == 0)
            {
                await ShowMessageAsync("GUARD",
                    "No backup was found to restore from:\n\n" + Expand(dest)
                    + "\n\nCheck that the backup drive is connected, or that the destination above is the"
                    + " folder your backups are written to.");
                return;
            }

            var dlg = new Views.RestoreDialog(dest, snapshots, _cfg.Folders)
            {
                XamlRoot = Content.XamlRoot,
                WindowHandle = WindowHandle,
            };
            if (await ShowDialogAsync(dlg) != ContentDialogResult.Primary) return;
            outcome = await RunRestore(dlg.Snapshot, dlg.Picked, dlg.Mode);
        }
        finally { _restoreRunning = false; }

        // Deliberately outside the claim above and outside the run's own lock:
        // the report can sit on screen for as long as the user takes to read it,
        // and while the job still counted as running the close handler cancelled
        // the close and then suppressed its own explanation (only one dialog may
        // be open), so the window button simply did nothing.
        if (outcome != null) await ShowRestoreOutcomeAsync(outcome);
    }

    // Returns the outcome to report, or null when there is nothing to say.
    private async System.Threading.Tasks.Task<string?> RunRestore(
        BackupSnapshot snapshot, List<RestoreItem> picked, RestoreMode mode)
    {
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;
        SetRestoreBusy(true);
        // The SAME lock the generated backup script takes. A scheduled or
        // on-connect backup firing partway through a restore would see a target
        // folder that is only half filled and, in Mirror mode, make the BACKUP
        // match it - deleting the very copies being restored from. Held across
        // the preview and its confirmation too, so nothing can start in the gap
        // while the user reads the dialog.
        FileStream? runLock = null;
        try
        {
            runLock = RestoreRunner.TryTakeRunLock(out bool heldByBackup);
            if (runLock == null)
            {
                await ShowMessageAsync("GUARD", heldByBackup
                    ? "A backup is running right now, so the restore was not started. Wait for it to"
                      + " finish and try again."
                    : "The restore was not started: GUARD could not write to its own folder, so it"
                      + " cannot keep a scheduled backup from starting partway through.\n\nThis folder"
                      + " must be writable:\n\n" + GuardPaths.DataDir);
                return null;
            }

            TxtOutput.Text = "";
            AppendOut(TxtOutput, "> Restore from " + snapshot.Label + "\r\n");
            _summaryParser = new RobocopySummaryParser();
            _runIsPreview = false;
            _runDoneAnnounce = null;
            SetProgress(FileProgress, FileProgressLabel, 1, 0, "Measuring folders...");
            ShowStatusBarProgress(0, true);
            await MeasureRestoreAsync(picked, ct);

            // Replace overwrites files that are already there, including ones
            // edited since the backup, so it never runs on the strength of a
            // radio button alone: a /L pass says what would actually change and
            // the user confirms that. The safe mode needs no such gate - it
            // cannot lose anything - and paying for a second full scan to prove
            // it would only make the common case slower.
            if (mode == RestoreMode.Replace)
            {
                if (!await ConfirmReplaceAsync(picked, ct)) { EndRestoreCancelled("Restore cancelled."); return null; }
                _summaryParser = new RobocopySummaryParser();
            }
            if (ct.IsCancellationRequested) { EndRestoreCancelled("Restore cancelled."); return null; }

            RestoreRunner.BeginLog(preview: false, snapshot.Label, snapshot.Path, mode);
            string? partLog = RestoreRunner.TryPreparePartLog();
            bool hadErrors = false;
            for (int i = 0; i < picked.Count && !ct.IsCancellationRequested; i++)
            {
                var r = picked[i];
                string line = "Restoring: " + r.FolderName + " (" + (i + 1) + " of " + picked.Count + ")";
                SnapRestoreProgress(i, line);
                if (i == 0) AnnounceSettled(line);
                AppendOut(TxtOutput, line + "\r\n  into " + r.Target + "\r\n");
                RestoreRunner.AppendPairHeader(r.SourcePath, r.Target);

                int code = 0;
                try
                {
                    code = await System.Threading.Tasks.Task.Run(
                        () => RestoreRunner.RunOne(r.SourcePath, r.Target, mode, preview: false,
                            partLog, HandleRestoreLine, ct));
                }
                catch (Exception ex)
                {
                    AppendOut(TxtOutput, "ERROR restoring " + r.FolderName + ": " + ex.Message + "\r\n");
                    hadErrors = true;
                }
                // Folded in whether the folder succeeded or not - a failed one is
                // exactly the one whose log the user needs. Off the UI thread:
                // a folder with many files leaves a log of some size behind.
                await System.Threading.Tasks.Task.Run(() => RestoreRunner.AppendPartLog(partLog));

                if (code >= RestoreRunner.FailureThreshold)
                {
                    hadErrors = true;
                    AppendOut(TxtOutput, "   !! some files could not be restored into " + r.Target
                        + " - see the restore log\r\n");
                }
            }
            RestoreRunner.FinishLog(hadErrors, ct.IsCancellationRequested);

            if (ct.IsCancellationRequested)
            {
                EndRestoreCancelled("Restore stopped. Some folders may be only partly restored.");
                return "The restore was stopped before it finished, so some folders may hold only part of"
                    + " what was being copied back. Nothing was deleted.";
            }

            string summary = BuildRestoreSummary(hadErrors);
            SetProgress(FileProgress, FileProgressLabel, 1, 1, summary);
            _runDoneAnnounce = summary;
            AppendOut(TxtOutput, "\r\n" + summary + "\r\n--- finished ---\r\n");
            return summary;
        }
        catch (Exception ex)
        {
            string failed = "Restore could not run: " + ex.Message;
            AppendOut(TxtOutput, "ERROR: " + ex.Message + "\r\n");
            SetProgress(FileProgress, FileProgressLabel, 1, 0, failed);
            _runDoneAnnounce = failed;
            return failed;
        }
        finally
        {
            ShowStatusBarProgress(0, false);
            // Released before the outcome report is shown (the caller does that
            // once this returns), so a scheduled backup is not held off for as
            // long as the dialog sits unread.
            runLock?.Dispose();
            _runCts?.Dispose();
            _runCts = null;
            // Cleared BEFORE SetRestoreBusy: that call re-enables Save through
            // UpdateSaveEnabled, which now bows out while a restore is running.
            // The caller clears it again for the paths that never got this far.
            _restoreRunning = false;
            SetRestoreBusy(false);
            string? spoken = _runDoneAnnounce;
            if (spoken != null) AnnounceSettled(spoken, 2000);
        }
    }

    // The end-of-restore report, with the log one press away: a restore is rare,
    // high-stakes and writes into the user's own folders, so unlike a backup it
    // does not end on a status line alone. The log carries the exact file names
    // (it is written from robocopy's Unicode log, not its console output).
    private async System.Threading.Tasks.Task ShowRestoreOutcomeAsync(string text)
    {
        var dlg = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "GUARD",
            Content = text,
            PrimaryButtonText = "OK",
            SecondaryButtonText = "Open Restore Log",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await ShowDialogAsync(dlg) == ContentDialogResult.Secondary)
            OpenPath(GuardPaths.RestoreLogPath, "No restore log was written.");
    }

    // A /L pass over the picked folders, then the confirmation it feeds. Returns
    // false when the user backs out (or the scan was stopped).
    private async System.Threading.Tasks.Task<bool> ConfirmReplaceAsync(
        List<RestoreItem> picked, CancellationToken ct)
    {
        SetProgress(FileProgress, FileProgressLabel, 1, 0, "Checking what would change...");
        AnnounceSettled("Checking what would change...");
        for (int i = 0; i < picked.Count && !ct.IsCancellationRequested; i++)
        {
            var r = picked[i];
            SnapRestoreProgress(i, "Checking: " + r.FolderName + " (" + (i + 1) + " of " + picked.Count + ")");
            try
            {
                await System.Threading.Tasks.Task.Run(
                    () => RestoreRunner.RunOne(r.SourcePath, r.Target, RestoreMode.Replace,
                        preview: true, null, HandleRestoreLine, ct));
            }
            catch (Exception ex) { AppendOut(TxtOutput, "Could not check " + r.FolderName + ": " + ex.Message + "\r\n"); }
        }
        if (ct.IsCancellationRequested) return false;

        var p = _summaryParser;
        string what = p != null && p.Blocks > 0
            ? CountPhrase(p.FilesCopied, "file") + " ("
              + SaveValidation.FormatBytes((long)p.BytesCopied) + ") would be copied back, and "
              + CountPhrase(p.FilesSkipped, "file") + " already match the backup."
            : "GUARD could not work out how many files would change.";
        var folders = new System.Text.StringBuilder();
        foreach (var r in picked) folders.Append('\n').Append(r.Target);
        return await ShowConfirmAsync("GUARD",
            "Replace files with the backup copies?\n\n" + what
            + "\n\nAny file with the same name in these folders is replaced by the backup's copy,"
            + " even if you have changed it since the backup was taken. Nothing is deleted.\n"
            + folders + "\n\nContinue?");
    }

    // Per-folder byte weighting for the bar, exactly as a backup run does it:
    // the offsets snap the bar at each folder boundary and robocopy's per-file
    // byte lines move it within the folder. Falls back to per-folder counting
    // when the walk is cut short, so a huge backup never delays the restore.
    private async System.Threading.Tasks.Task MeasureRestoreAsync(
        List<RestoreItem> picked, CancellationToken ct)
    {
        _progTotal = picked.Count;
        _progByBytes = false;
        _progSizes = null;
        _progOffsets = null;
        _progTotalBytes = 0;
        try
        {
            // Synthetic pairs so the measurement goes through the same walker
            // the backup uses; no exclusions, since the backup holds whatever a
            // run actually copied rather than what today's rules would copy.
            var pairs = new List<FolderPair>();
            foreach (var r in picked) pairs.Add(new FolderPair(true, r.SourcePath, r.FolderName));
            var sizes = await SaveValidation.MeasureIncludedFolderSizesAsync(
                pairs, new List<string>(), new List<string>(), SaveValidation.RunSizeCap, ct);
            if (sizes == null || sizes.Count == 0) return;
            long tot = 0;
            foreach (var s in sizes) tot += s;
            if (tot <= 0) return;
            _progSizes = sizes.ToArray();
            _progOffsets = new long[sizes.Count];
            long acc = 0;
            for (int k = 0; k < sizes.Count; k++) { _progOffsets[k] = acc; acc += sizes[k]; }
            _progTotalBytes = tot;
            _curPushStep = Math.Max(4L * 1024 * 1024, tot / 500);
            _progByBytes = true;
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    // Moves the bar to the start of folder index i and labels it.
    private void SnapRestoreProgress(int i, string label)
    {
        if (_progByBytes && _progOffsets != null && _progSizes != null && i < _progOffsets.Length)
        {
            _curBase = _progOffsets[i];
            _curSize = _progSizes[i];
            _curCopied = 0;
            _curLastPush = _curBase;
            SetProgress(FileProgress, FileProgressLabel, _progTotalBytes, _curBase, label);
        }
        else
        {
            SetProgress(FileProgress, FileProgressLabel, _progTotal > 0 ? _progTotal : 1, i, label);
        }
    }

    // Robocopy's stdout during a restore. The per-file lines (a tab, then
    // "<bytes>\t<path>") feed the bar and are never echoed - a large restore has
    // tens of thousands and each AppendOut forces a TextBox relayout. The
    // readable record is the restore log, which comes from robocopy's Unicode
    // log rather than this stream, because console output down-converts non-Latin
    // file names to question marks before GUARD ever sees them.
    private void HandleRestoreLine(string data)
    {
        if (data.Length > 0 && data[0] == '\t')
        {
            int lastTab = data.LastIndexOf('\t');
            if (lastTab > 0)
            {
                var ms = Regex.Match(data.Substring(0, lastTab), "(\\d+)\\s*$");
                if (ms.Success && long.TryParse(ms.Groups[1].Value, out long b))
                {
                    if (_progByBytes)
                    {
                        _curCopied += b;
                        long val = _curBase + Math.Min(_curCopied, _curSize);
                        if (val - _curLastPush >= _curPushStep)
                        {
                            _curLastPush = val;
                            SetFileProgressValue(val);
                        }
                    }
                    return;
                }
            }
        }
        try { _summaryParser?.Feed(data); } catch { _summaryParser = null; }
        // Only the failures are echoed live. Everything else robocopy prints is
        // its banner and the summary table, both of which the log already holds.
        if (data.Contains("ERROR", StringComparison.Ordinal)) AppendOut(TxtOutput, data + "\r\n");
    }

    private string BuildRestoreSummary(bool hadErrors)
    {
        var p = _summaryParser;
        if (p == null || p.Blocks == 0)
            return hadErrors
                ? "Restore finished with problems - open the restore log."
                : "Restore finished.";
        string copied = CountPhrase(p.FilesCopied, "file");
        string bytes = FormatBytes(p.BytesCopied);
        if (bytes.Length > 0 && p.FilesCopied > 0) copied += " (" + bytes + ")";
        string skipped = p.FilesSkipped.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
        if (hadErrors || p.FilesFailed > 0)
            return "Restore finished with problems: " + CountPhrase(p.FilesFailed, "file")
                + " could not be restored - open the restore log. " + copied + " restored, "
                + skipped + " already up to date.";
        return "Restore complete: " + copied + " restored, " + skipped + " already up to date.";
    }

    private void EndRestoreCancelled(string text)
    {
        SetProgress(FileProgress, FileProgressLabel, 1, 0, text);
        _runDoneAnnounce = text;
        AppendOut(TxtOutput, "\r\n--- " + text + " ---\r\n");
    }

    // Same lockout and focus discipline as a backup run (see SetFileBusy):
    // Save rewrites guard-backup.cmd, and Run/Preview would race this restore.
    private void SetRestoreBusy(bool busy)
    {
        SetNavBusy(0, busy);
        if (busy)
        {
            // The Stop button is shared with the backup run, so it says what it
            // would stop; a button labelled "Stop Backup" during a restore would
            // be read out as exactly the wrong reassurance.
            BtnStopBackup.Content = "Stop Restore";
            _fileRunLauncher = BeginRunBusy(BtnStopBackup, BtnSave, BtnRunNow, BtnPreview, BtnRestore);
            BtnSave.IsEnabled = false;
            BtnRunNow.IsEnabled = false;
            BtnPreview.IsEnabled = false;
            BtnRestore.IsEnabled = false;
        }
        else
        {
            BtnRunNow.IsEnabled = true;
            BtnPreview.IsEnabled = true;
            BtnRestore.IsEnabled = true;
            UpdateSaveEnabled();
            EndRunBusy(BtnStopBackup, _fileRunLauncher);
            BtnStopBackup.Content = "Stop Backup";
            _fileRunLauncher = null;
        }
    }
}
