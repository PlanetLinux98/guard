using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using GuardWui3.Models;
using GuardWui3.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace GuardWui3;

// File Backup page: settings harvest and save, folder and exclusion editing,
// and the backup run (script launch, progress parsing, end-of-run summary).
// Shared shell plumbing (status bar, page progress, dialogs) stays in
// MainWindow.xaml.cs.
public sealed partial class MainWindow : Window
{
    // =====================================================================
    //  SETTINGS HARVEST / SAVE
    // =====================================================================
    private void HarvestUi()
    {
        _cfg.Dest = (TxtDest.Text ?? "").Trim();
        _cfg.Mode = RbMirror.IsChecked == true ? "Mirror" : "Additive";
        // Custom exclusions already live in _cfg.Excludes via two-way binding;
        // only the preset checkboxes need harvesting.
        _cfg.ExcludePresets = new List<string>();
        foreach (var (box, id) in _presetBoxes)
            if (box.IsChecked == true) _cfg.ExcludePresets.Add(id);
        _cfg.Versioned = ChkVersioned.IsChecked == true;
        // NumberBox.Value is NaN while the field is cleared; fall back to the
        // last saved count rather than writing NaN into the keep count.
        if (!double.IsNaN(NumVersionsKeep.Value))
            _cfg.VersionsToKeep = Math.Clamp((int)NumVersionsKeep.Value, 1, 365);
        _cfg.ScheduleEnabled = ChkSchedule.IsChecked == true;
        _cfg.TriggerOnConnect = ChkOnConnect.IsChecked == true;
        _cfg.ScheduleDays = new List<DayOfWeek>();
        foreach (var (box, day) in _dayBoxes)
            if (box.IsChecked == true) _cfg.ScheduleDays.Add(day);
        _cfg.ScheduleTime = FormatScheduleTime(TimeSchedule.SelectedTime, _cfg.ScheduleTime);
        HarvestAppUi();
    }

    // Split out so App Management flows (export) can harvest their own fields
    // without also pulling the File Backup page's unsaved edits into the live
    // config (which a later ini write would silently persist).
    private void HarvestAppUi()
    {
        _cfg.AppListDest = (TxtAppDest.Text ?? "").Trim();
        _cfg.ExportAppSettings = ChkExportSettings.IsChecked == true;
    }

    // Returns false (after showing a message) when a required value is missing.
    private async System.Threading.Tasks.Task<bool> SaveAllAsync()
    {
        HarvestUi();
        if (string.IsNullOrEmpty(_cfg.Dest))
        {
            await ShowMessageAsync("GUARD", "Enter a backup destination first.\n\nType a folder path next to \"Backup destination\", or use the Browse button to pick one.");
            return false;
        }
        // A quote would end the generated script's set "DEST=..." early and
        // corrupt every later line that expands it; nothing else needs blocking
        // (the script quotes DEST wherever it is used).
        if (_cfg.Dest.Contains('"'))
        {
            await ShowMessageAsync("GUARD", "The backup destination cannot contain quote (\") characters.");
            return false;
        }
        if (_cfg.ScheduleEnabled && _cfg.ScheduleDays.Count == 0)
        {
            await ShowMessageAsync("GUARD", "Pick at least one day for the scheduled backup, or turn the schedule off.");
            return false;
        }
        // A source containing the destination (or sitting inside it) would make
        // the backup copy itself and nest without bound, so refuse to write a
        // self-recursive script. Pure path math, no disk I/O, so it stays inline
        // with the other required-value blocks. (Unreachable sources and tight
        // space stay advisory; this one can never produce a good backup.)
        var overlapping = SaveValidation.OverlappingSources(_cfg.Dest, _cfg.Folders);
        if (overlapping.Count > 0)
        {
            await ShowMessageAsync("GUARD", DescribeOverlap(_cfg.Dest, overlapping));
            return false;
        }
        // Settings and script are written synchronously (fast, and the ground
        // truth the rest of the app reads), then everything slow runs off the UI
        // thread so the window never freezes: the scheduled-task state applies in
        // one batched PowerShell call (each extra powershell.exe start pays a
        // multi-second module import; this used to be 3-4 sequential ones and
        // froze the UI for tens of seconds), and a dead UNC source can make
        // Directory.Exists block for seconds.
        if (_saving) return false;
        _saving = true;
        try
        {
            // Section-scoped: never commits the image page's unsaved edits
            // (see SettingsStore.SaveFileBackup).
            SettingsStore.SaveFileBackup(_cfg);
            BackupScript.Write(_cfg);
            _dirty = false;
            RefreshScriptStatus();
            // Save Settings is the single source of truth for both scheduled
            // tasks: each is registered when its own option is on and removed
            // when not, so the schedule and the on-connect trigger toggle
            // independently (ApplyAll handles both plus the legacy-name cleanup).
            var applied = await System.Threading.Tasks.Task.Run(
                () => ScheduledTasks.ApplyAll(_cfg));
            _taskError = applied.Error;
            LblNextRun.Text = applied.NextRun == null
                ? "Next run: (no scheduled task)" : "Next run: " + applied.NextRun;
            _missingSources = await System.Threading.Tasks.Task.Run(
                () => SaveValidation.UnreachableSources(_cfg.Folders));
            return true;
        }
        finally { _saving = false; }
    }

    // A successful save shows no dialog: the status line (a live region, so it
    // is announced) already reads "Settings saved..." and later gains the
    // space/size figures from the background check. Dialogs are reserved for
    // actual problems - a task-registration failure or unreachable sources.
    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (!await SaveAllAsync()) return;

        if (_taskError != null)
        {
            await ShowMessageAsync("GUARD", "Settings saved, but registering a scheduled task reported a problem:\n\n" + _taskError);
            return;
        }
        if (_missingSources.Count > 0)
            await ShowMessageAsync("GUARD", "Settings saved. Note: " + DescribeMissingSources(_missingSources)
                + "\n\nThey will be skipped if still unreachable when the backup runs.");

        StartSpaceStatusCheck();
    }

    // Background space/size check; appends its findings to the saved-status line
    // when done. Out of the modal path, the size estimate can afford a long-enough
    // cap to usually finish, so the figure is normally the full total, not a lower
    // bound. The sequence counter plus the dirty re-check drop a stale result if
    // the user edited or saved again mid-walk.
    // announce=false on the launch run: the seeded status is repainted silently
    // at startup (RefreshScriptStatus(announce:false)), so the figures it gains
    // here ride along silently too rather than speaking over the window opening;
    // a manual save passes announce=true so the result is still spoken.
    private async void StartSpaceStatusCheck(bool announce = true)
    {
        int seq = ++_spaceCheckSeq;

        // Interim placeholder so the line never sits silently mid-check; the
        // result replaces it (rebuilt from baseText, not appended) when done.
        string baseText = _fileStatusText;
        SetFileStatusText(baseText + " Calculating backup size and destination space...", announce);

        var estimateTask = SaveValidation.EstimateBackupSizeAsync(_cfg.Folders, SaveValidation.EstimateCap);
        var freeTask = System.Threading.Tasks.Task.Run(() => SaveValidation.TryGetFreeSpace(_cfg.Dest));
        long? free = await freeTask;
        var est = await estimateTask;

        // A stale result (the user edited or saved again) leaves the line to
        // whoever rewrote it; RefreshScriptStatus has already replaced the text.
        if (seq != _spaceCheckSeq || _dirty) return;

        // Deliberately terse: this rides on the end of the status line, so a
        // sentence per fact would scroll the line off the window and pad the
        // screen-reader announcement. Tight space gets a Warning: prefix and
        // keeps the dot amber; the full explanation lives in the user manual.
        string extra;
        if (free is not long freeBytes)
        {
            extra = " Destination space could not be checked.";
        }
        else
        {
            bool tight = est.Bytes > 0 && est.Bytes > freeBytes * 0.9;
            extra = tight ? " Warning: space may be too low." : "";
            if (est.Bytes > 0)
                extra += " Backup size: " + (est.Complete ? "" : "at least ")
                    + SaveValidation.FormatBytes(est.Bytes) + ";";
            extra += " destination available space: " + SaveValidation.FormatBytes(freeBytes) + ".";
            if (tight) _fileStatusBrush = new SolidColorBrush(StatusAmber);
        }
        SetFileStatusText(baseText + extra, announce);
    }

    // Mid-flow file-status updates (the space-check placeholder and result)
    // route through the status bar like RefreshScriptStatus does: repaint the
    // bar, announce only while File Backup is the active page, and record the
    // text so an unchanged status is not re-spoken.
    private void SetFileStatusText(string text, bool announce = true)
    {
        _fileStatusText = text;
        UpdateStatusBar();
        if (announce && _activePage == 0 && _fileStatusText != _lastAnnouncedStatus)
            Announce(StatusBarText);
        _lastAnnouncedStatus = _fileStatusText;
    }

    private static string DescribeMissingSources(List<string> missing)
    {
        string list = "\n" + string.Join("\n", missing);
        return missing.Count == 1
            ? "this source folder is not currently reachable:" + list
            : "these source folders are not currently reachable:" + list;
    }

    // Spells out which source(s) overlap the destination and how to fix it,
    // rather than a bare refusal, so the user can see the problem and act on it.
    private static string DescribeOverlap(string dest, List<string> sources)
    {
        string list = "\n" + string.Join("\n", sources);
        string which = sources.Count == 1
            ? "this source folder overlaps the backup destination:"
            : "these source folders overlap the backup destination:";
        return "Cannot save these settings. " + which + list
            + "\n\nDestination: " + dest
            + "\n\nA source cannot contain the destination, or sit inside it, or the "
            + "backup would copy itself into itself and grow without end until the "
            + "folder can no longer be opened or deleted. Choose a destination on a "
            + "separate path (ideally a different drive), or remove the overlapping source.";
    }

    // Off the UI thread: the query launches powershell.exe, whose cold start
    // pays a multi-second module import. The label keeps its "(unknown)"
    // placeholder until the answer arrives. Saves do not call this; ApplyAll
    // returns the next run from its own batched invocation.
    private async void RefreshNextRun()
    {
        if (LblNextRun == null) return;
        var next = await System.Threading.Tasks.Task.Run(
            () => ScheduledTasks.QueryNextRun(GuardPaths.FileTaskName));
        LblNextRun.Text = next == null ? "Next run: (no scheduled task)" : "Next run: " + next;
    }

    // Settings store the daily run time as an "HH:mm" string; the TimePicker
    // works in TimeSpan. These convert between the two, falling back to the
    // existing value if the picker has no selection.
    private static TimeSpan? ParseScheduleTime(string? text)
    {
        text = (text ?? "").Trim();
        if (DateTime.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            return t.TimeOfDay;
        if (DateTime.TryParseExact(text, "H:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out t))
            return t.TimeOfDay;
        return null;
    }

    private static string FormatScheduleTime(TimeSpan? selected, string fallback)
    {
        if (selected is not TimeSpan ts) return fallback;
        return new TimeOnly(ts.Hours, ts.Minutes).ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    // =====================================================================
    //  FOLDER ADD / REMOVE
    // =====================================================================
    private async void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new Views.FolderDialog { XamlRoot = Content.XamlRoot, WindowHandle = WindowHandle };
        var result = await ShowDialogAsync(dlg);
        if (result == ContentDialogResult.Primary)
            _cfg.Folders.Add(new FolderPair(true, dlg.SourcePath, dlg.SubFolder));
    }

    private async void OnEditFolder(object sender, RoutedEventArgs e)
    {
        var f = _currentFolder;
        if (f == null)
        {
            await ShowMessageAsync("GUARD", "Highlight the folder you want to edit from the folder list, then press Edit Folder.");
            return;
        }
        var dlg = new Views.FolderDialog { XamlRoot = Content.XamlRoot, WindowHandle = WindowHandle };
        dlg.LoadFolder(f);
        if (await ShowDialogAsync(dlg) == ContentDialogResult.Primary)
        {
            // Update the existing item rather than replacing it: its property
            // change notifications refresh the bound row in place and flow into
            // dirty tracking via OnFolderItemChanged, and the row keeps its
            // position and focus memory.
            f.Source = dlg.SourcePath;
            f.SubFolder = dlg.SubFolder;
        }
    }

    private async void OnRemoveFolder(object sender, RoutedEventArgs e)
    {
        var f = _currentFolder;
        if (f == null)
        {
            await ShowMessageAsync("GUARD", "Highlight the folder you want to remove from the folder list, then press Remove Folder.");
            return;
        }
        // Spell out the untick alternative: Remove forgets the pair entirely,
        // while unticking keeps it listed but out of the generated script.
        if (await ShowConfirmAsync("GUARD",
            "Remove this folder from the list entirely?\n\n" + f.Source +
            "\n\nIf you only want to skip it for now, choose No and untick it instead."))
        {
            _cfg.Folders.Remove(f);
            _currentFolder = null;
        }
    }

    // =====================================================================
    //  EXCLUSION ADD / REMOVE
    // =====================================================================
    private async void OnAddExclude(object sender, RoutedEventArgs e)
    {
        var dlg = new Views.ExcludeDialog { XamlRoot = Content.XamlRoot };
        var result = await ShowDialogAsync(dlg);
        if (result == ContentDialogResult.Primary)
            _cfg.Excludes.Add(new ExcludeItem(dlg.IsFolder, dlg.Pattern));
    }

    private async void OnRemoveExclude(object sender, RoutedEventArgs e)
    {
        if (ExcludeList.SelectedItem is not ExcludeItem x)
        {
            await ShowMessageAsync("GUARD", "Select the exclusion you want to remove in the custom exclusions list, then press Remove Exclusion.");
            return;
        }
        if (await ShowConfirmAsync("GUARD", "Remove this exclusion?\n\n" + x.Caption))
            _cfg.Excludes.Remove(x);
    }

    // =====================================================================
    //  RUN SCRIPT
    // =====================================================================
    private async void OnRunNow(object sender, RoutedEventArgs e) => await RunScript("");
    private async void OnPreview(object sender, RoutedEventArgs e) => await RunScript("test");

    private async System.Threading.Tasks.Task RunScript(string arg)
    {
        if (_backupRunning)
        {
            await ShowMessageAsync("GUARD", "A backup is already running. Wait for it to finish, or press Stop Backup to cancel it.");
            return;
        }
        // A clean config skips the save: the script already matches the saved
        // settings, and re-saving would re-apply the scheduled tasks (a
        // multi-second PowerShell call) for nothing. The unreachable-sources
        // note is still refreshed, since a drive can come or go between runs.
        if (_dirty || !File.Exists(GuardPaths.ScriptPath))
        {
            if (!await SaveAllAsync()) return;
        }
        else
        {
            _missingSources = await System.Threading.Tasks.Task.Run(
                () => SaveValidation.UnreachableSources(_cfg.Folders));
        }
        string script = GuardPaths.ScriptPath;
        if (!File.Exists(script))
        {
            // Parallels the System Image page's wording; no internal path dump.
            await ShowMessageAsync("GUARD", "Backup script not found. Click Save Settings first.");
            return;
        }

        TxtOutput.Text = "";
        AppendOut(TxtOutput, "> " + Path.GetFileName(script) + (arg.Length > 0 ? " " + arg : "") + "\r\n");
        // A modal here would interrupt the run the user just asked for; the
        // script SKIPs unreachable sources itself, so a line in the output is
        // the right weight.
        if (_missingSources.Count > 0)
            AppendOut(TxtOutput, "WARNING: " + DescribeMissingSources(_missingSources).Replace("\n", "\r\n  ")
                + "\r\nThey will be skipped if still unreachable.\r\n");
        _progTotal = 0;
        _progByBytes = false;
        _progSizes = null;
        _progOffsets = null;
        _progTotalBytes = 0;
        _summaryParser = new RobocopySummaryParser();
        _runIsPreview = arg == "test";
        _runDoneAnnounce = null;
        SetProgress(FileProgress, FileProgressLabel, 1, 0, "Measuring folders...");
        ShowStatusBarProgress(0, true);

        _backupRunning = true;
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;
        SetFileBusy(true);
        try
        {
            // Best-effort: pre-scan the included folders so the bar can advance by
            // bytes copied within each folder (see _progByBytes). On failure, empty
            // result, timeout or cancel it stays in per-folder mode. Cancellable, so
            // Stop during a long measure aborts cleanly - the launch below then
            // starts and is killed at once by the already-cancelled token.
            try
            {
                var sizes = await SaveValidation.MeasureIncludedFolderSizesAsync(
                    _cfg.Folders, SaveValidation.RunSizeCap, ct);
                if (sizes != null && sizes.Count > 0)
                {
                    long tot = 0;
                    foreach (var s in sizes) tot += s;
                    if (tot > 0)
                    {
                        _progSizes = sizes.ToArray();
                        _progOffsets = new long[sizes.Count];
                        long acc = 0;
                        for (int k = 0; k < sizes.Count; k++) { _progOffsets[k] = acc; acc += sizes[k]; }
                        _progTotalBytes = tot;
                        // Throttle per-file bar pushes to ~500 over the whole run so
                        // a large backup cannot flood the dispatcher.
                        _curPushStep = Math.Max(4L * 1024 * 1024, tot / 500);
                        _progByBytes = true;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch { }

            var psi = new ProcessStartInfo("cmd.exe", "/c \"\"" + script + "\" " + arg + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                WorkingDirectory = GuardPaths.BaseDir
            };
            psi.EnvironmentVariables["GUARD_UI"] = "1";
            using var proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (_, ev) => HandleScriptLine(ev.Data);
            proc.ErrorDataReceived += (_, ev) => { if (ev.Data != null) AppendOut(TxtOutput, ev.Data + "\r\n"); };
            proc.Start();
            // Cancel kills the whole tree: cmd.exe alone would die while its
            // robocopy child kept copying. Kill throws if the process already
            // exited naturally in the same instant; that race is harmless.
            using var reg = ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            proc.StandardInput.Close();
            await proc.WaitForExitAsync();
            // Parameterless WaitForExit additionally drains the async output
            // handlers, so the completion line below always lands after the
            // script's own last output.
            proc.WaitForExit();
            if (ct.IsCancellationRequested)
            {
                AppendOut(TxtOutput, "\r\n--- cancelled by user ---\r\n");
                // Enqueued (not set directly) so it lands after any progress
                // update the output handlers enqueued during the drain above;
                // a direct set could be overwritten by a stale "Backing up"
                // line still sitting in the queue.
                DispatcherQueue.TryEnqueue(() =>
                {
                    FileProgressLabel.Text = "Backup cancelled.";
                    // Mirror into the page's progress slot; SetProgress last wrote
                    // a stale "Backing up..." line there.
                    _pageProg[0].Text = "Backup cancelled."; ApplyPageProgress(0);
                });
            }
            else
            {
                AppendOut(TxtOutput, "\r\n--- finished ---\r\n");
            }
        }
        catch (Exception ex)
        {
            AppendOut(TxtOutput, "ERROR launching script: " + ex.Message + "\r\n");
        }
        finally
        {
            // Whatever the outcome (finish, cancel, launch error), the bar's
            // progress area must not outlive the job it mirrors.
            ShowStatusBarProgress(0, false);
            _backupRunning = false;
            _runCts.Dispose();
            _runCts = null;
            SetFileBusy(false);
        }
        string? spoken = ct.IsCancellationRequested ? "Backup cancelled." : _runDoneAnnounce;
        if (spoken != null) AnnounceSettled(spoken, 2000);
    }

    private void OnStopBackup(object sender, RoutedEventArgs e) => _runCts?.Cancel();

    // Lock out the actions that conflict with a running backup. Save Settings is
    // included because it rewrites guard-backup.cmd, and cmd.exe reads batch files
    // incrementally, so rewriting one mid-run corrupts the run. The Stop button is
    // the inverse: only operable while something is running.
    // _fileRunLauncher is the button that launched the run, so focus can return
    // there when it ends. Focus is managed explicitly around the enable/disable
    // flips: disabling a focused button lets WinUI throw focus at an arbitrary
    // neighbour (it landed on Open Last Log), and the screen reader announcing
    // that surprise focus cancels whatever was being spoken - which ate the
    // end-of-run summary.
    private Control? _fileRunLauncher;

    private void SetFileBusy(bool busy)
    {
        SetNavBusy(0, busy);
        if (busy)
        {
            // Enable Stop and hand it focus before the launchers grey out;
            // Stop is the one action available during the run.
            BtnStopBackup.IsEnabled = true;
            _fileRunLauncher = FocusManager.GetFocusedElement(Content.XamlRoot) as Control;
            if (_fileRunLauncher == BtnSave || _fileRunLauncher == BtnRunNow || _fileRunLauncher == BtnPreview)
                BtnStopBackup.Focus(FocusState.Programmatic);
            else
                _fileRunLauncher = null;
            BtnSave.IsEnabled = false;
            BtnRunNow.IsEnabled = false;
            BtnPreview.IsEnabled = false;
        }
        else
        {
            BtnRunNow.IsEnabled = true;
            BtnPreview.IsEnabled = true;
            UpdateSaveEnabled();
            // Return focus to the launcher before Stop greys out, so rerunning
            // is one keypress away and focus never lands somewhere arbitrary.
            if (_fileRunLauncher != null &&
                ReferenceEquals(FocusManager.GetFocusedElement(Content.XamlRoot), BtnStopBackup))
                _fileRunLauncher.Focus(FocusState.Programmatic);
            _fileRunLauncher = null;
            BtnStopBackup.IsEnabled = false;
        }
    }

    private void HandleScriptLine(string? data)
    {
        if (data == null) return;
        if (data.StartsWith("@@PROGRESS@@"))
        {
            string rest = data.Substring("@@PROGRESS@@".Length).Trim();
            if (rest == "DONE")
            {
                // The DONE marker is emitted after every robocopy call, so all
                // summary blocks have been fed by now. A null summary (nothing
                // parsed) keeps the original completion message untouched.
                string done = (_runIsPreview ? "Preview" : "Backup") +
                    " complete (" + _progTotal + " of " + _progTotal + ").";
                string? summary = null;
                try { summary = BuildRunSummary(); } catch { }
                if (summary != null)
                {
                    done = summary;
                    AppendOut(TxtOutput, "\r\n" + summary + "\r\n");
                }
                if (_progByBytes)
                    SetProgress(FileProgress, FileProgressLabel, _progTotalBytes, _progTotalBytes, done);
                else
                    SetProgress(FileProgress, FileProgressLabel, _progTotal > 0 ? _progTotal : 1, _progTotal, done);
                // Stashed, not announced here: the announcement waits until
                // RunScript has finished its end-of-run focus handling, or the
                // focus change would cancel the speech mid-summary.
                _runDoneAnnounce = done;
                return;
            }
            var m = Regex.Match(rest, "^(\\d+)\\s+(\\d+)\\s*(.*)$");
            if (m.Success)
            {
                int n = int.Parse(m.Groups[1].Value);
                int tot = int.Parse(m.Groups[2].Value);
                string nm = m.Groups[3].Value.Trim();
                _progTotal = tot;
                string prog = "Backing up: " + nm + " (" + n + " of " + tot + ")";
                if (_progByBytes && _progOffsets != null && _progSizes != null
                    && n >= 1 && n <= _progOffsets.Length)
                {
                    // Snap to this folder's start (= the previous folder's end), so
                    // skipped files are accounted for at the boundary; the per-file
                    // lines below then move the bar within the folder.
                    _curBase = _progOffsets[n - 1];
                    _curSize = _progSizes[n - 1];
                    _curCopied = 0;
                    _curLastPush = _curBase;
                    SetProgress(FileProgress, FileProgressLabel, _progTotalBytes, _curBase, prog);
                }
                else
                {
                    SetProgress(FileProgress, FileProgressLabel, tot, n - 1, prog);
                }
                // Speak the first progress line so a screen-reader user hears
                // the run actually begin; the rest of the stream stays silent
                // (a per-folder announcement stream would be noisy).
                if (n == 1) AnnounceSettled(prog);
            }
            return;
        }
        // Robocopy's per-file lines (the UI build drops /NFL and adds /BYTES) start
        // with a tab and end "<bytes>\t<path>". A large backup has tens of thousands,
        // so they feed progress ONLY, never echoed to the output box or the summary
        // parser: each AppendOut forces a TextBox relayout, and echoing every line
        // froze the UI. The full list still reaches the log via robocopy's /LOG+
        // (Open Last Log). Runs for every GUARD_UI run, even when byte-progress is
        // off, since the script emits these whenever GUARD_UI is set.
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
                    return; // identified per-file line: do not echo or feed
                }
            }
        }
        // Summary parsing must never break run handling; on any parser fault the
        // run degrades to the plain completion message.
        try { _summaryParser?.Feed(data); } catch { _summaryParser = null; }
        AppendOut(TxtOutput, data + "\r\n");
    }

    // Builds the human-readable end-of-run summary from the accumulated robocopy
    // tables, or null when nothing parsed (parse failure, zero folders, or a
    // localized table the parser did not recognise).
    private string? BuildRunSummary()
    {
        var p = _summaryParser;
        if (p == null || p.Blocks == 0) return null;
        bool mirror = _cfg.Mode == "Mirror";
        string copied = CountPhrase(p.FilesCopied, "file");
        string bytes = FormatBytes(p.BytesCopied);
        if (bytes.Length > 0 && p.FilesCopied > 0) copied += " (" + bytes + ")";
        string skipped = p.FilesSkipped.ToString("N0", CultureInfo.CurrentCulture);

        if (p.FilesFailed > 0)
        {
            // Failures lead so a screen reader hears the problem first.
            string failed = CountPhrase(p.FilesFailed, "file");
            return _runIsPreview
                ? "Preview finished with problems: " + failed + " could not be read - open the last log for details. " +
                  copied + " would be copied, " + skipped + " already up to date."
                : "Backup finished with problems: " + failed + " failed to copy - open the last log for details. " +
                  copied + " copied, " + skipped + " skipped.";
        }

        string extras = "";
        if (p.FilesExtras > 0)
        {
            string ex = CountPhrase(p.FilesExtras, "extra file");
            // Extras are only deleted in Mirror mode; in Additive they just sit
            // in the destination, so the wording must not claim a removal.
            extras = _runIsPreview
                ? (mirror ? " " + ex + " would be removed from the backup." : " " + ex + " found in the backup.")
                : (mirror ? " " + ex + " removed from the backup." : " " + ex + " found in the backup.");
        }

        return _runIsPreview
            ? "Preview complete: " + copied + " would be copied, " + skipped + " already up to date." + extras
            : "Backup complete: " + copied + " copied, " + skipped + " skipped (already up to date)." + extras;
    }

    private static string CountPhrase(long n, string noun)
        => n.ToString("N0", CultureInfo.CurrentCulture) + " " + noun + (n == 1 ? "" : "s");

    private static string FormatBytes(double b)
    {
        if (b <= 0) return "";
        string[] units = { "bytes", "KB", "MB", "GB", "TB" };
        int u = 0;
        while (b >= 1024 && u < units.Length - 1) { b /= 1024; u++; }
        return (u == 0 ? b.ToString("N0", CultureInfo.CurrentCulture)
                       : b.ToString("0.#", CultureInfo.CurrentCulture)) + " " + units[u];
    }
}
