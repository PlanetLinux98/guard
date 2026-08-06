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
        // Drive-letter drift, before the overlap checks so they validate the
        // real destination. Reachable letter: record the volume's identity for
        // the script's run-time re-find. Unreachable letter with a recorded
        // serial: the volume may simply have come back under a new letter -
        // re-anchor the destination to it and tell the user (via OnSave /
        // RunScript). Off the UI thread; a dead drive query can stall.
        _destDriftNote = null;
        if (_cfg.Dest.Length >= 2 && _cfg.Dest[1] == ':')
        {
            string root = _cfg.Dest.Substring(0, 2);
            var vol = await System.Threading.Tasks.Task.Run(() => VolumeInfo.TryGetForRoot(root + "\\"));
            if (vol != null)
            {
                // A serial already recorded for this letter that doesn't match what's
                // there now means the real backup drive was unplugged and something
                // else - possibly unrelated - has taken its letter. Adopting it blind
                // would re-anchor Mirror mode at a drive GUARD never meant to touch and
                // lose the link back to the real one, so block once and require a
                // second, unchanged Save (the pending-mismatch fields) as confirmation.
                bool mismatched = _cfg.DestVolumeSerial.Length > 0
                    && !_cfg.DestVolumeSerial.Equals(vol.Serial, StringComparison.OrdinalIgnoreCase);
                bool confirmed = _pendingVolumeMismatchRoot != null
                    && _pendingVolumeMismatchRoot.Equals(root, StringComparison.OrdinalIgnoreCase)
                    && _pendingVolumeMismatchSerial == vol.Serial;
                if (mismatched && !confirmed)
                {
                    _pendingVolumeMismatchRoot = root;
                    _pendingVolumeMismatchSerial = vol.Serial;
                    string oldLabel = _cfg.DestVolumeLabel.Length > 0 ? _cfg.DestVolumeLabel : "(unlabeled)";
                    string newLabel = vol.Label.Length > 0 ? vol.Label : "(unlabeled)";
                    await ShowMessageAsync("GUARD", "The drive at " + root + " is not the one GUARD last used there (it was labeled \""
                        + oldLabel + "\", now \"" + newLabel + "\").\n\nIf this is a new drive, click Save again to confirm; if not, reconnect your backup drive.");
                    return false;
                }
                _pendingVolumeMismatchRoot = null;
                _pendingVolumeMismatchSerial = null;
                _cfg.DestVolumeSerial = vol.Serial;
                _cfg.DestVolumeLabel = vol.Label;
            }
            else if (_cfg.DestVolumeSerial.Length > 0)
            {
                string? moved = await System.Threading.Tasks.Task.Run(
                    () => VolumeInfo.FindDriveBySerial(_cfg.DestVolumeSerial));
                if (moved != null && !moved.Equals(root, StringComparison.OrdinalIgnoreCase))
                {
                    _cfg.Dest = moved + _cfg.Dest.Substring(2);
                    TxtDest.Text = _cfg.Dest;
                    string label = _cfg.DestVolumeLabel.Length > 0 ? " (\"" + _cfg.DestVolumeLabel + "\")" : "";
                    _destDriftNote = "Your backup drive" + label + " is now drive " + moved
                        + " - it was " + root + " when you last saved. The destination has been updated to:\n"
                        + _cfg.Dest;
                }
            }
        }
        else
        {
            // UNC and other non-letter destinations have no volume to track.
            _cfg.DestVolumeSerial = "";
            _cfg.DestVolumeLabel = "";
            _pendingVolumeMismatchRoot = null;
            _pendingVolumeMismatchSerial = null;
        }
        // A script with zero included folders copies nothing yet reports
        // FINISHED OK (and still registers the scheduled tasks), so a user could
        // believe they are protected while backing up nothing.
        bool anyIncluded = false;
        foreach (var f in _cfg.Folders) if (f.Include) { anyIncluded = true; break; }
        if (!anyIncluded)
        {
            await ShowMessageAsync("GUARD", "Tick at least one folder to back up.\n\nEvery folder in the list is unticked, so the backup would copy nothing.");
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
        // In Mirror mode, pairs whose destination subfolders coincide or nest
        // purge each other's output on every run (see MirrorSubfolderConflicts),
        // so like the overlap above this can never produce a good backup.
        if (_cfg.Mode == "Mirror")
        {
            var conflicts = SaveValidation.MirrorSubfolderConflicts(_cfg.Folders);
            if (conflicts.Count > 0)
            {
                await ShowMessageAsync("GUARD", DescribeSubfolderConflicts(conflicts));
                return false;
            }
        }
        // Pure string work, so it stays inline like the overlap checks; the
        // result is advisory (see the field's note).
        _percentPaths = SaveValidation.UnresolvedPercentPaths(_cfg.Dest, _cfg.Folders);
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
            try
            {
                // Section-scoped: never commits the image page's unsaved edits
                // (see SettingsStore.SaveFileBackup).
                SettingsStore.SaveFileBackup(_cfg);
                BackupScript.Write(_cfg);
            }
            catch (Exception ex)
            {
                // A read-only install folder or a locked file must fail the save
                // with a dialog, not escape this async-void path and crash GUARD.
                // Names the folder GUARD actually writes to, which is not the
                // exe's folder under a winget install (see GuardPaths.DataDir).
                await ShowMessageAsync("GUARD", "Could not save the settings:\n\n" + ex.Message
                    + "\n\nGUARD writes its settings and backup script into this folder, which must be"
                    + " writable:\n\n" + GuardPaths.DataDir);
                return false;
            }
            _dirty = false;
            // Explicit confirmation, not the resting health line: the user
            // just pressed Save and must hear that it took. The health line
            // returns at the next launch, page revisit, or run end.
            _fileStatusBrush = new SolidColorBrush(StatusGreen);
            SetFileStatusText("Backup settings saved.");
            UpdateSaveEnabled();
            // Save Settings is the single source of truth for both scheduled
            // tasks: each is registered when its own option is on and removed
            // when not, so the schedule and the on-connect trigger toggle
            // independently (ApplyAll handles both plus the legacy-name cleanup).
            var applied = await System.Threading.Tasks.Task.Run(
                () => ScheduledTasks.ApplyAll(_cfg));
            _taskError = applied.Error;
            LblNextRun.Text = applied.NextRun == null
                ? "Next run: (no scheduled task)" : "Next run: " + applied.NextRun;
            // One snapshot for both walks. UnreachableSources needs it as much as
            // CheckSources does: its own comment notes a dead UNC source can make
            // Directory.Exists block for seconds, and nothing disables the folder
            // list meanwhile, so enumerating the live bound collection on a worker
            // thread could throw straight out of this async void.
            var snapshot = SnapshotConfig();
            _missingSources = await System.Threading.Tasks.Task.Run(
                () => SaveValidation.UnreachableSources(snapshot.Folders));
            SetSourceHealth(await System.Threading.Tasks.Task.Run(
                () => SaveValidation.CheckSources(snapshot, SaveValidation.SourceCheckCap)));
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

        if (_destDriftNote != null)
            await ShowMessageAsync("GUARD", _destDriftNote);
        if (_taskError != null)
        {
            await ShowMessageAsync("GUARD", "Settings saved, but registering a scheduled task reported a problem:\n\n" + _taskError);
            return;
        }
        if (_missingSources.Count > 0)
            await ShowMessageAsync("GUARD", "Settings saved. Note: " + DescribeMissingSources(_missingSources)
                + "\n\nThey will be skipped if still unreachable when the backup runs.");
        // Ahead of the source warnings: if the backup itself is gone, that is
        // the thing to say, and the rest is detail about a backup that no longer
        // exists. Only once a run has happened - an empty destination before the
        // first backup is simply a new destination.
        if (_sourceHealth.DestinationEmpty && BackupHealth.ReadLog(GuardPaths.LogPath) is not null)
            await ShowMessageAsync("GUARD",
                "Settings saved. Warning: the backup destination is empty, but GUARD's records show a"
                + " backup has run before.\n\n" + Expand(_cfg.Dest)
                + "\n\nThe backup may have been deleted, or the drive reformatted. Run a backup to"
                + " rebuild it.");
        if (VanishedToReport.Count > 0) await ReportVanishedAsync(VanishedToReport);
        if (_sourceHealth.Unreadable.Count > 0)
            await ShowMessageAsync("GUARD", "Settings saved. Note: " + DescribeUnreadable(_sourceHealth.Unreadable));
        if (_percentPaths.Count > 0)
            await ShowMessageAsync("GUARD", "Settings saved. Warning: " + DescribePercentPaths(_percentPaths));

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
        SetFileStatusText(baseText + " Checking backup size and free space...", announce);

        var estimateTask = SaveValidation.EstimateBackupSizeAsync(
            _cfg.Folders, _cfg.EffectiveExcludeDirs(), _cfg.EffectiveExcludeFiles(), SaveValidation.EstimateCap);
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
            extra = " Free space could not be checked.";
        }
        else
        {
            bool tight = est.Bytes > 0 && est.Bytes > freeBytes * 0.9;
            extra = tight ? " Warning: space may be too low." : "";
            if (est.Bytes > 0)
                extra += " Backup size: " + (est.Complete ? "" : "at least ")
                    + SaveValidation.FormatBytes(est.Bytes) + ";";
            extra += " free space: " + SaveValidation.FormatBytes(freeBytes) + ".";
            if (tight) _fileStatusBrush = new SolidColorBrush(StatusAmber);
        }
        SetFileStatusText(baseText + extra, announce);
    }

    // A detached copy of the live configuration for a worker-thread walk. The
    // folder and exclusion collections are two-way bound, so the user can add or
    // remove rows while a walk is in flight; enumerating them off the UI thread
    // would throw straight out of an async void and take the window down. Only
    // the fields the source check reads are copied.
    private Settings SnapshotConfig()
    {
        var s = new Settings
        {
            Dest = _cfg.Dest,
            Mode = _cfg.Mode,
            Versioned = _cfg.Versioned,
            ExcludePresets = new List<string>(_cfg.ExcludePresets),
        };
        foreach (var f in _cfg.Folders)
            s.Folders.Add(new FolderPair(f.Include, f.Source, f.SubFolder, f.KnownFolder));
        foreach (var x in _cfg.Excludes)
            s.Excludes.Add(new ExcludeItem(x.IsFolder, x.Pattern));
        return s;
    }

    // Launch-time source-health probe, off the UI thread so it never holds up
    // the window. Independent of the space check above, which only runs BEFORE
    // the first backup: a source that has gone empty matters most once backups
    // ARE running, because the healthy-looking "Last backup succeeded" line is
    // exactly what hides it.
    //
    // Repaints only when something was actually found, so the usual no-news run
    // cannot bump the sequence counter and discard an in-flight space check's
    // own rewrite of the same line.
    private async void StartSourceHealthCheck()
    {
        int seq = _spaceCheckSeq;
        var snapshot = SnapshotConfig();
        SaveValidation.SourceHealth health;
        try
        {
            health = await System.Threading.Tasks.Task.Run(
                () => SaveValidation.CheckSources(snapshot, SaveValidation.SourceCheckCap));
        }
        catch (Exception ex)
        {
            DebugLog.Log("folders", "source health check failed", ex);
            return;
        }
        // A save or a run that landed while this walk was out has already
        // computed a fresher answer; do not overwrite it with this one.
        if (seq != _spaceCheckSeq) return;
        SetSourceHealth(health);
        if (VanishedToReport.Count > 0) RefreshScriptStatus(announce: false);
    }

    // Mid-flow file-status updates (the space-check placeholder and result).
    private void SetFileStatusText(string text, bool announce = true)
    {
        _fileStatusText = text;
        CommitPageStatus(0, announce);
    }

    private static string DescribeMissingSources(List<string> missing)
    {
        var sb = new System.Text.StringBuilder(missing.Count == 1
            ? "this source folder is not currently reachable:"
            : "these source folders are not currently reachable:");
        foreach (var m in missing) sb.Append('\n').Append(Expand(m));
        return sb.ToString();
    }

    // The vanished list minus anything the user has acknowledged as
    // deliberately empty. Every consumer reads THIS, never _sourceHealth
    // .Vanished, or an acknowledged folder would keep warning somewhere.
    private List<SaveValidation.VanishedSource> VanishedToReport =>
        SaveValidation.Unacknowledged(_sourceHealth.Vanished, _prefs.AcknowledgedEmpty);

    // Records a fresh result and drops acknowledgements that no longer apply, so
    // a folder that has content again stops being remembered and a real later
    // disappearance is reported instead of inheriting the old answer.
    private void SetSourceHealth(SaveValidation.SourceHealth health)
    {
        _sourceHealth = health;
        // Pruned ONLY when the destination was actually reachable. With the
        // backup drive unplugged - the normal state for a tool whose headline
        // feature is an on-connect trigger - Vanished is empty because nothing
        // could be compared, not because the folders are fine, and pruning on
        // that would silently throw away every "Don't warn again" the user has
        // given. "Could not measure" is not "measured, and all is well".
        if (!health.DestinationReachable) return;
        string pruned = SaveValidation.PruneAcknowledged(health.Vanished, _prefs.AcknowledgedEmpty);
        if (pruned != _prefs.AcknowledgedEmpty)
        {
            _prefs.AcknowledgedEmpty = pruned;
            AppPrefsStore.Save(_prefs);
        }
    }

    // The save-time report, with a way out. In Additive mode the backup never
    // loses the files it already holds, so a folder emptied on purpose meets the
    // vanished condition for ever; without "Don't warn again" the only escape
    // would be to untick the row.
    private async System.Threading.Tasks.Task ReportVanishedAsync(
        List<SaveValidation.VanishedSource> gone)
    {
        var dlg = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "GUARD",
            Content = "Settings saved. Warning: " + DescribeVanished(gone),
            PrimaryButtonText = "OK",
            SecondaryButtonText = "Don't warn again",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await ShowDialogAsync(dlg) != ContentDialogResult.Secondary) return;
        _prefs.AcknowledgedEmpty = SaveValidation.AddAcknowledged(gone, _prefs.AcknowledgedEmpty);
        AppPrefsStore.Save(_prefs);
        RefreshScriptStatus(announce: false);
    }

    // Paths are shown EXPANDED here, as everywhere else the user reads one: a
    // screen reader announces a raw %USERPROFILE% as "percent USERPROFILE
    // percent backslash", which is not a folder anyone recognizes.
    private static string Expand(string p) => Environment.ExpandEnvironmentVariables(p ?? "");

    // States the transition, not the state: these folders have nothing left to
    // copy WHILE the backup still holds files from them, which is the only
    // version of "empty" that is unambiguously worth interrupting someone for.
    // Mirror mode leads with the consequence, because the next run does not just
    // copy nothing - it deletes the copies to match the source.
    private string DescribeVanished(List<SaveValidation.VanishedSource> gone)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(gone.Count == 1
            ? "this folder has nothing left to back up, but your backup still holds files copied from it:"
            : "these folders have nothing left to back up, but your backup still holds files copied from them:");
        foreach (var v in gone) sb.Append('\n').Append(Expand(v.Source));
        sb.Append(MirrorPurges
            ? "\n\nMirror mode makes the backup match the source, so the next backup will DELETE those"
              + " copies. Check the folder before it runs."
            : "\n\nIf you expected files there, Windows may have moved the folder - OneDrive's"
              + " \"Back up your folders\" and the folder's Properties, Location tab both do this.");
        // Says how to stop it. Without this the warning has no exit: in Additive
        // mode the backup never loses those files, so a folder the user emptied
        // on purpose would warn on every launch, save and run for ever.
        sb.Append("\n\nIf you no longer want this folder backed up, untick it in the folder list or"
            + " remove it, and this warning stops.");
        return sb.ToString();
    }

    // Separate from the above on purpose: a folder GUARD cannot read is a
    // different problem from one that has emptied, and telling the user to go
    // looking for a move that never happened wastes their time.
    private static string DescribeUnreadable(List<string> unreadable)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(unreadable.Count == 1
            ? "this folder could not be read, so GUARD cannot tell what is in it:"
            : "these folders could not be read, so GUARD cannot tell what is in them:");
        foreach (var u in unreadable) sb.Append('\n').Append(Expand(u));
        sb.Append("\n\nThe backup will still try to copy them; check the last log afterwards to see"
            + " whether it succeeded.");
        return sb.ToString();
    }

    private static string DescribePercentPaths(List<string> paths)
    {
        string list = "\n" + string.Join("\n", paths);
        return (paths.Count == 1
                ? "this path contains a % that is not an environment variable:" + list
                : "these paths contain a % that is not an environment variable:" + list)
            + "\n\nWindows command scripts treat % specially, so the backup may read or write "
            + "the wrong folder. Renaming the folder to avoid % is the reliable fix.";
    }

    private static string DescribeSubfolderConflicts(List<string> conflicts)
    {
        return "Cannot save these settings. In Mirror mode, these destination subfolders overlap:\n\n"
            + string.Join("\n", conflicts)
            + "\n\nMirror deletes anything at a folder's destination subfolder that is not in "
            + "that folder's own source, so folders sharing or nesting subfolders would erase "
            + "each other's backups on every run. Give each folder its own separate subfolder, "
            + "or switch to Additive mode.";
    }

    // Spells out which source(s) overlap the destination and how to fix it,
    // rather than a bare refusal, so the user can see the problem and act on it.
    private static string DescribeOverlap(string dest, List<string> sources)
    {
        // Expanded like every other path the user reads. These are the BLOCKING
        // dialogs, so they are the loudest place a raw %USERPROFILE% would be
        // read out character by character by a screen reader.
        var sb = new System.Text.StringBuilder();
        foreach (var s in sources) sb.Append('\n').Append(Expand(s));
        string which = sources.Count == 1
            ? "this source folder overlaps the backup destination:"
            : "these source folders overlap the backup destination:";
        return "Cannot save these settings. " + which + sb
            + "\n\nDestination: " + Expand(dest)
            + "\n\nA source cannot contain the destination, or sit inside it, or the "
            + "backup would copy itself into itself and grow without end until the "
            + "folder can no longer be opened or deleted. Choose a destination on a "
            + "separate path (ideally a different drive), or remove the overlapping source.";
    }

    // Off the UI thread: the query launches powershell.exe, whose cold start
    // pays a multi-second module import. The label keeps its "(unknown)"
    // placeholder until the answer arrives. Saves do not call this; ApplyAll
    // returns the next run from its own batched invocation.
    //
    // Doubles as the portable-folder self-heal: the scheduled tasks embed
    // absolute paths from save time, so after the GUARD folder is moved or
    // renamed they keep firing into the old location while looking healthy.
    // The two per-user backup tasks re-register silently (no elevation
    // needed); the SYSTEM image task cannot (re-registering means a UAC
    // prompt nobody asked for), so it flags the image page's status and the
    // next Save re-applies it with the usual consented prompt.
    private async void CheckScheduledTasksAtLaunch()
    {
        if (LblNextRun == null) return;
        var state = await System.Threading.Tasks.Task.Run(ScheduledTasks.QueryStartupState);
        LblNextRun.Text = state.NextRun == null ? "Next run: (no scheduled task)" : "Next run: " + state.NextRun;
        // The image label's only other writer is a schedule-CHANGING save, so
        // without this seed it read "(unknown)" every session even with a
        // healthy scheduled image registered.
        LblImageNextRun.Text = state.ImageNextRun == null
            ? "Next run: (no scheduled image)" : "Next run: " + state.ImageNextRun;

        bool healBackup = false;
        bool imageStale = false;
        bool imageSeen = false;
        foreach (var a in state.Actions)
        {
            if (a.Name == GuardPaths.SystemImageTaskName) imageSeen = true;
            if (a.Name == GuardPaths.FileTaskName && _cfg.ScheduleEnabled
                && !ScheduledTasks.IsCurrentBackupAction(a)) healBackup = true;
            else if (a.Name == GuardPaths.OnConnectTaskName && _cfg.TriggerOnConnect
                && !ScheduledTasks.IsCurrentBackupAction(a)) healBackup = true;
            else if (a.Name == GuardPaths.SystemImageTaskName && _cfg.ImageScheduleEnabled
                && !ScheduledTasks.IsCurrentImageAction(a)) imageStale = true;
        }

        if (healBackup)
        {
            DebugLog.Log("tasks", "backup task actions stale (folder moved or legacy style); re-registering");
            // On-disk settings, not _cfg: by the time this background check
            // lands the user may already be editing the page.
            var applied = await System.Threading.Tasks.Task.Run(
                () => ScheduledTasks.ApplyAll(SettingsStore.Load()));
            if (applied.Error != null)
                DebugLog.Log("tasks", "silent re-register failed: " + applied.Error);
            else if (applied.NextRun != null)
                LblNextRun.Text = "Next run: " + applied.NextRun;
        }
        if (imageStale)
        {
            _imageTaskStale = true;
            _lastImageScheduleSig = "";   // force the next save to re-apply
            RefreshImageStatus(announce: false);
        }
        // A task that is MISSING writes no ACT line at all, so the loop above
        // cannot see it: the schedule was saved but never registered (the one UAC
        // prompt was declined, or the register failed), or something removed it
        // since. Without this the saved config and the startup signature agree
        // forever, so the apply is never retried and the page keeps showing a
        // green "Image settings saved" for images that will never be taken.
        // Gated on QueryOk: an empty action list from a query that did not run is
        // not evidence the task is gone.
        else if (state.QueryOk && _cfg.ImageScheduleEnabled && !imageSeen)
        {
            _imageTaskUnapplied = true;
            _lastImageScheduleSig = "";   // force the next save to re-apply
            RefreshImageStatus(announce: false);
        }
    }

    // Windows can move the personal folders GUARD backs up - OneDrive's "Back up
    // your folders" relocates Documents, Desktop and Pictures under
    // %USERPROFILE%\OneDrive, and a folder's Properties, Location tab can send
    // it anywhere - and the vacated path is usually LEFT BEHIND rather than
    // deleted. A row that kept pointing at the old path would therefore keep
    // copying an empty folder and keep reporting success.
    //
    // GUARD follows, but only after asking. Silently re-pointing would mean a
    // backup tool changing what it protects without telling anyone, and the
    // unattended runs have nobody to ask - so until the answer comes, the
    // scheduled task keeps using the location it was given.
    //
    // Only rows that TRACK a known folder qualify (FolderPair.KnownFolder); a
    // path the user typed, or one they edited by hand, is theirs. A decline is
    // remembered against this specific move - which folder AND where to - so it
    // never nags, yet a later move somewhere else still gets asked about.
    private async System.Threading.Tasks.Task CheckMovedFoldersAsync()
    {
        List<KnownFolders.Moved> moved;
        try
        {
            var folders = new List<FolderPair>(_cfg.Folders);
            moved = await System.Threading.Tasks.Task.Run(() => KnownFolders.FindMoved(folders));
        }
        catch (Exception ex)
        {
            DebugLog.Log("folders", "moved-folder check failed", ex);
            return;
        }

        // Prune the declined list to moves that still apply, so a folder that
        // has since been put back does not leave a stale entry silencing a
        // future move to the same place.
        var declined = new List<string>(_prefs.DeclinedMoves.Split(AppPrefs.ListSeparator, StringSplitOptions.RemoveEmptyEntries));
        var live = new List<string>();
        foreach (var m in moved) if (declined.Contains(m.Key)) live.Add(m.Key);
        if (live.Count != declined.Count)
        {
            _prefs.DeclinedMoves = string.Join(AppPrefs.ListSeparator, live);
            AppPrefsStore.Save(_prefs);
        }

        var offer = new List<KnownFolders.Moved>();
        var blocked = new List<KnownFolders.Moved>();
        foreach (var m in moved)
        {
            if (live.Contains(m.Key)) continue;
            // Already covered by another row - typically a user who noticed the
            // move themselves and ADDED the new location while leaving the old
            // row ticked. Following would give two rows on one tree under two
            // destination subfolders: copied twice, for ever, silently. And if
            // their subfolders happen to match, Mirror mode would then block
            // every save, leaving the prompt to return at each launch with no
            // way through. Neither is something to walk into automatically.
            if (AnotherRowCovers(m)) blocked.Add(m); else offer.Add(m);
        }

        if (blocked.Count > 0)
        {
            var bs = new System.Text.StringBuilder(
                "Windows reports that these folders have moved, but GUARD is already backing up the new"
                + " location under another entry:\n\n");
            foreach (var m in blocked)
                bs.Append(m.Pair.KnownFolder).Append("\n  now at: ")
                  .Append(Expand(m.ResolvedSource)).Append("\n\n");
            bs.Append("Following the move would back the same folder up twice, so GUARD has left the list"
                + " alone. Remove or untick whichever entry you do not want.");
            await ShowMessageAsync("GUARD", bs.ToString());
            // Recorded as answered: the situation needs a human decision, and
            // repeating this at every launch would be nagging about something
            // GUARD has already declined to do.
            foreach (var m in blocked) live.Add(m.Key);
            _prefs.DeclinedMoves = string.Join(AppPrefs.ListSeparator, live);
            AppPrefsStore.Save(_prefs);
        }
        if (offer.Count == 0) return;

        var sb = new System.Text.StringBuilder();
        sb.Append(offer.Count == 1
            ? "Windows reports that a folder GUARD backs up has moved:\n\n"
            : "Windows reports that " + offer.Count + " folders GUARD backs up have moved:\n\n");
        foreach (var m in offer)
            sb.Append(m.Pair.KnownFolder)
              .Append("\n  GUARD is backing up: ").Append(Environment.ExpandEnvironmentVariables(m.CurrentSource))
              .Append("\n  Windows now reports: ").Append(Environment.ExpandEnvironmentVariables(m.ResolvedSource))
              .Append("\n\n");
        sb.Append("This usually means OneDrive's \"Back up your folders\" was turned on, or the folder was"
            + " moved to another drive. The old location is often left behind empty, so those files would"
            + " not be in your backup.\n\nFollow the move and back up the new locations?");

        // "Not now" is the SECONDARY button, not the close button. WinUI returns
        // None for the close button AND for a dialog that never opened (the
        // single-dialog funnel), so putting the decline there would make the two
        // indistinguishable - which is the very thing this dialog is built the
        // long way round to tell apart. Secondary has its own result, so a real
        // decline is recorded and a suppressed dialog simply asks again.
        var dlg = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "GUARD",
            Content = sb.ToString(),
            PrimaryButtonText = "Follow",
            SecondaryButtonText = "Not now",
            DefaultButton = ContentDialogButton.Primary,
        };
        var answer = await ShowDialogAsync(dlg);
        if (answer == ContentDialogResult.None) return;    // never asked; try again next launch
        if (answer != ContentDialogResult.Primary)
        {
            foreach (var m in offer) live.Add(m.Key);
            _prefs.DeclinedMoves = string.Join(AppPrefs.ListSeparator, live);
            AppPrefsStore.Save(_prefs);
            return;
        }

        var previous = new List<string>();
        foreach (var m in offer) previous.Add(m.Pair.Source);
        foreach (var m in offer) m.Pair.Source = m.ResolvedSource;
        // Saved immediately: the point is that the generated script and the
        // scheduled tasks start using the new locations, not just the list on
        // screen.
        if (!await SaveAllAsync())
        {
            // Validation refused (a source now overlapping the destination, say).
            // SaveAllAsync has explained why, but the edits are sitting unsaved
            // in a list the user never touched, and the prompt would return at
            // every launch to repeat a save that cannot succeed. Put the paths
            // back and record the answer, so the config matches disk and the
            // loop ends; the message says what to do instead.
            for (int i = 0; i < offer.Count; i++) offer[i].Pair.Source = previous[i];
            _dirty = false;
            RefreshScriptStatus(announce: false);
            foreach (var m in offer) live.Add(m.Key);
            _prefs.DeclinedMoves = string.Join(AppPrefs.ListSeparator, live);
            AppPrefsStore.Save(_prefs);
            await ShowMessageAsync("GUARD",
                "GUARD has left the folder list as it was. Edit the folder yourself once the problem"
                + " above is resolved, and it will start backing up the new location.");
            return;
        }
        if (_destDriftNote != null) await ShowMessageAsync("GUARD", _destDriftNote);
        if (_taskError != null)
            await ShowMessageAsync("GUARD",
                "Folders updated, but registering a scheduled task reported a problem:\n\n" + _taskError);
    }

    // Whether some OTHER included row already backs up the place this folder has
    // moved to. Compared expanded, so %USERPROFILE%-relative and literal
    // spellings of the same folder count as the same folder.
    private bool AnotherRowCovers(KnownFolders.Moved m)
    {
        string target = Expand(m.ResolvedSource).TrimEnd('\\', '/');
        foreach (var f in _cfg.Folders)
        {
            if (!f.Include || ReferenceEquals(f, m.Pair)) continue;
            if (Expand(f.Source).TrimEnd('\\', '/')
                .Equals(target, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
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
            //
            // Choosing a different FOLDER pins the row: the user has named a
            // location, so GUARD stops tracking the Windows one and stops
            // offering to follow its moves.
            //
            // Compared EXPANDED, which is the whole difficulty: a tracked row
            // stores %USERPROFILE%\Documents, while Browse fills the field from
            // the folder picker as C:\Users\<name>\Documents. Compared raw,
            // opening Edit, browsing to the very folder the row already tracks
            // and pressing OK read as a change - silently pinning the row. That
            // is permanent (AdoptIdentities never re-adopts a pinned row), so an
            // ordinary re-confirmation quietly ended the move tracking that this
            // whole feature exists to provide. Re-confirming must be a no-op.
            bool sameFolder = string.Equals(
                Expand(f.Source).TrimEnd('\\', '/'),
                Expand(dlg.SourcePath).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
            if (!sameFolder)
            {
                f.KnownFolder = "";
                f.Pinned = true;
                f.Source = dlg.SourcePath;
            }
            // Source deliberately untouched when it is the same folder: a
            // tracked row keeps its %USERPROFILE%-relative spelling, which is
            // what keeps the generated script portable (see KnownFolders.Encode).
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
        // Cross-page: a system image, reinstall, export, or scan on another
        // page would otherwise run concurrently with this backup (see
        // IsAnyJobRunning). _backupRunning is already false here (checked
        // above), so this only sees the OTHER pages' flags.
        if (IsAnyJobRunning)
        {
            await ShowMessageAsync("GUARD", Capitalize(RunningJobLabel()!) + " is currently running. Wait for it to finish before starting a backup.");
            return;
        }
        // Claimed and the buttons disabled BEFORE any await below, not after:
        // otherwise a second click landing in that window (SaveAllAsync/the
        // unreachable-sources scan) would slip past the _backupRunning guard
        // above and launch a second robocopy run sharing _runCts, which then
        // races the first run's finally block for the field and can NRE-crash
        // the app when the loser Disposes an already-nulled CTS.
        _backupRunning = true;
        SetFileBusy(true);
        try
        {
            // A clean config skips the save: the script already matches the saved
            // settings, and re-saving would re-apply the scheduled tasks (a
            // multi-second PowerShell call) for nothing. The unreachable-sources
            // note is still refreshed, since a drive can come or go between runs.
            if (_dirty || !File.Exists(GuardPaths.ScriptPath))
            {
                if (!await SaveAllAsync()) return;
                // Same courtesy as RunImage: the run continues, but a scheduled-task
                // registration failure inside that save must not vanish silently.
                if (_taskError != null)
                    await ShowMessageAsync("GUARD", "Settings saved, but registering a scheduled task reported a problem:\n\n" + _taskError);
            }
            else
            {
                // No save ran, so any drift note from an earlier save is stale.
                _destDriftNote = null;
                var snapshot = SnapshotConfig();
                _missingSources = await System.Threading.Tasks.Task.Run(
                    () => SaveValidation.UnreachableSources(snapshot.Folders));
                SetSourceHealth(await System.Threading.Tasks.Task.Run(
                    () => SaveValidation.CheckSources(snapshot, SaveValidation.SourceCheckCap)));
                _percentPaths = SaveValidation.UnresolvedPercentPaths(_cfg.Dest, _cfg.Folders);
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
            // the right weight. Same for the drive-drift note.
            if (_destDriftNote != null)
                AppendOut(TxtOutput, "NOTE: " + _destDriftNote.Replace("\n", "\r\n  ") + "\r\n");
            if (_missingSources.Count > 0)
                AppendOut(TxtOutput, "WARNING: " + DescribeMissingSources(_missingSources).Replace("\n", "\r\n  ")
                    + "\r\nThey will be skipped if still unreachable.\r\n");
            if (VanishedToReport.Count > 0)
                AppendOut(TxtOutput, "WARNING: " + DescribeVanished(VanishedToReport).Replace("\n", "\r\n  ") + "\r\n");
            if (_sourceHealth.Unreadable.Count > 0)
                AppendOut(TxtOutput, "NOTE: " + DescribeUnreadable(_sourceHealth.Unreadable).Replace("\n", "\r\n  ") + "\r\n");
            if (_percentPaths.Count > 0)
                AppendOut(TxtOutput, "WARNING: " + DescribePercentPaths(_percentPaths).Replace("\n", "\r\n  ") + "\r\n");
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

            _runCts = new CancellationTokenSource();
            var ct = _runCts.Token;
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
                        _cfg.Folders, _cfg.EffectiveExcludeDirs(), _cfg.EffectiveExcludeFiles(),
                        SaveValidation.RunSizeCap, ct);
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
                    // The script switches its console to UTF-8 (chcp 65001; see
                    // BackupScript.Generate), so the @@PROGRESS@@ markers and
                    // robocopy lines carrying paths arrive as UTF-8 bytes; the
                    // default decode is the OEM codepage, which would mangle
                    // non-ASCII path characters in the output box.
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8,
                    WorkingDirectory = GuardPaths.DataDir
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
                _runCts?.Dispose();
                _runCts = null;
                // The run just rewrote the log, so the health line has news; the
                // end-of-run summary below is the spoken part, the line updates
                // silently.
                RefreshScriptStatus(announce: false);
            }
            string? spoken = ct.IsCancellationRequested ? "Backup cancelled." : _runDoneAnnounce;
            if (spoken != null) AnnounceSettled(spoken, 2000);
        }
        finally
        {
            _backupRunning = false;
            SetFileBusy(false);
        }
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
            _fileRunLauncher = BeginRunBusy(BtnStopBackup, BtnSave, BtnRunNow, BtnPreview);
            BtnSave.IsEnabled = false;
            BtnRunNow.IsEnabled = false;
            BtnPreview.IsEnabled = false;
        }
        else
        {
            BtnRunNow.IsEnabled = true;
            BtnPreview.IsEnabled = true;
            UpdateSaveEnabled();
            EndRunBusy(BtnStopBackup, _fileRunLauncher);
            _fileRunLauncher = null;
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
    // localized table the parser did not recognize).
    private string? BuildRunSummary()
    {
        var p = _summaryParser;
        if (p == null || p.Blocks == 0) return null;
        bool mirror = _cfg.Mode == "Mirror";
        string copied = CountPhrase(p.FilesCopied, "file");
        string bytes = FormatBytes(p.BytesCopied);
        if (bytes.Length > 0 && p.FilesCopied > 0) copied += " (" + bytes + ")";
        string skipped = p.FilesSkipped.ToString("N0", CultureInfo.CurrentCulture);

        if (p.FilesFailed > 0 || p.FilesMismatch > 0)
        {
            // Failures and mismatches lead so a screen reader hears the problem
            // first. A mismatch (same name, different type - e.g. the destination
            // has a file where the source now has a same-named folder) is not
            // something robocopy resolves on its own, so a run with only
            // mismatches and zero failures must still not read as a clean success.
            var problems = new List<string>();
            if (p.FilesFailed > 0) problems.Add(CountPhrase(p.FilesFailed, "file") + " failed to copy");
            if (p.FilesMismatch > 0) problems.Add(CountPhrase(p.FilesMismatch, "item") + " could not be reconciled");
            string problemText = string.Join(" and ", problems);
            return _runIsPreview
                ? "Preview finished with problems: " + problemText + " - open the last log for details. " +
                  copied + " would be copied, " + skipped + " already up to date."
                : "Backup finished with problems: " + problemText + " - open the last log for details. " +
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

    // Run-summary byte label; SizeText is the shared formatter, so the summary
    // reads the same as the list-row captions elsewhere.
    private static string FormatBytes(double b)
        => b <= 0 ? "" : SizeText.FormatBytes((long)b);
}
