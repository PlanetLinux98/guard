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

// App Management page: the installed-app scan and filter, list export (with
// optional settings folders), and import/reinstall (with optional settings
// restore).
public sealed partial class MainWindow : Window
{
    // =====================================================================
    //  APP SCAN
    // =====================================================================
    private void OnRefreshApps(object sender, RoutedEventArgs e) { _appScanned = true; ScanApps(announceStart: true); }

    private void ScanApps(bool announceStart)
    {
        if (_scanning) return;
        _scanning = true;
        SetAppBusy(true);
        _appStatusText = "Scanning installed apps (this can take a few seconds)...";
        // A tab-switch scan shows this silently (the tab selection is already
        // being announced); an explicit Refresh announces it, since nothing else
        // is speaking then. Either way the completion summary is announced below.
        if (announceStart) AnnounceAppStatus(); else UpdateStatusBar();

        var th = new Thread(() =>
        {
            ScanResult? res = null; string? err = null;
            try { res = AppInventory.DetectApps(); }
            catch (Exception ex) { err = ex.Message; }
            DispatcherQueue.TryEnqueue(() =>
            {
                if (err != null) { _appStatusText = "Scan failed: " + err; }
                else if (res != null)
                {
                    _wingetAvailable = res.WingetAvailable;
                    _allApps.Clear();
                    _allApps.AddRange(res.Apps);
                    int auto = 0, man = 0;
                    foreach (var a in res.Apps) { if (a.CanAuto) auto++; else man++; }
                    if (_wingetAvailable)
                        _appStatusText = res.Apps.Count + " apps found. " + auto + " reinstallable via winget, " + man + " manual.";
                    else
                        _appStatusText = res.Apps.Count + " apps found. winget is not installed, so apps cannot be reinstalled automatically. You can still export the list for reference.";
                    ApplyFilter();
                }
                _scanning = false; SetAppBusy(false);
                AnnounceAppStatus();
            });
        }) { IsBackground = true };
        th.Start();
    }

    private void SetAppBusy(bool busy)
    {
        bool e = !busy;
        BtnAppRefresh.IsEnabled = e;
        BtnAppExport.IsEnabled = e;
        BtnAppImport.IsEnabled = e;
        ChkExportSettings.IsEnabled = e;
        BtnAppAll.IsEnabled = e;
        BtnAppNone.IsEnabled = e;
        SetNavBusy(1, busy);
    }

    // Mark a page's job as running or stopped. The nav ring (driven from the snapshot
    // in ApplyPageProgress) keeps an in-progress job discoverable from any page;
    // start/finish speech is left to the existing job notifications (raised on the
    // always-present status bar), so this adds none.
    private void SetNavBusy(int page, bool busy)
    {
        Progress(page, p =>
        {
            p.Running = busy;
            // A fresh job starts indeterminate (spinning) until its first real
            // percentage arrives; the per-job progress calls then switch the ring to
            // a determinate arc. A job that never reports a percentage (the app scan)
            // just keeps spinning.
            if (busy) { p.Indeterminate = true; p.Value = 0; }
        });
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        _appFilter = (TxtAppFilter.Text ?? "").Trim();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        AppRows.Clear();
        string f = _appFilter.ToLowerInvariant();
        foreach (var a in _allApps)
        {
            if (f.Length == 0 || MatchesFilter(a, f))
                AppRows.Add(a);
        }
        UpdateAppCount();
    }

    private void UpdateAppCount()
    {
        if (LblAppCount == null) return;
        string text = _appFilter.Length == 0
            ? Plural(_allApps.Count)
            : AppRows.Count + " of " + Plural(_allApps.Count);
        if (text == LblAppCount.Text) return;
        LblAppCount.Text = text;
        // Announce the new count only while the user is typing in the filter box
        // (the immediate feedback they need); a scan or import already announces
        // its own summary, so speaking the count too would double up. Identical
        // counts across keystrokes stay silent (text unchanged).
        if (TxtAppFilter.FocusState != FocusState.Unfocused)
            Announce(LblAppCount);
    }

    private static string Plural(int n) => n == 1 ? "1 app" : n + " apps";

    private static bool MatchesFilter(AppEntry a, string f)
    {
        if (Contains(a.Name, f) || Contains(a.Publisher, f) || Contains(a.Id, f) || Contains(a.SourceLabel, f))
            return true;

        // Type aliases not covered by SourceLabel
        return a.Source switch
        {
            "manual"  => f is "installer",
            "msstore" => f is "msstore" or "ms store" or "microsoft store",
            _         => false,
        };
    }

    private static bool Contains(string? s, string f) =>
        !string.IsNullOrEmpty(s) && s.ToLowerInvariant().IndexOf(f, StringComparison.Ordinal) >= 0;

    private void OnSelectAll(object sender, RoutedEventArgs e) { foreach (var a in AppRows) a.Include = true; }
    private void OnSelectNone(object sender, RoutedEventArgs e) { foreach (var a in AppRows) a.Include = false; }

    // =====================================================================
    //  EXPORT / IMPORT
    // =====================================================================
    // One Export action covers both outputs: the app-list JSON always, plus
    // the ticked apps' settings folders when "Also export app settings" is
    // ticked. The settings step runs first because it is the only part the
    // user can cancel, and cancelling must abort the whole export - a Cancel
    // that still wrote the list would be a surprise partial result (untick
    // the option to export the list alone).
    private async void OnExportApps(object sender, RoutedEventArgs e)
    {
        if (_exporting)
        {
            await ShowMessageAsync("GUARD", "An export is already running. Wait for it to finish.");
            return;
        }
        // Only this page's fields: HarvestUi would pull the File Backup page's
        // unsaved edits into the live config, and the save below would then
        // persist edits the user never saved.
        HarvestAppUi();
        if (string.IsNullOrEmpty(_cfg.AppListDest))
        {
            await ShowMessageAsync("GUARD", "Enter an app list destination first.\n\nType a folder path next to \"List destination\", or use the Browse button to pick one.");
            return;
        }
        var picked = new List<AppEntry>();
        foreach (var a in _allApps) if (a.Include) picked.Add(a);
        if (picked.Count == 0)
        {
            await ShowMessageAsync("GUARD", "Tick at least one app to export.");
            return;
        }
        try { if (!Directory.Exists(_cfg.AppListDest)) Directory.CreateDirectory(_cfg.AppListDest); }
        catch (Exception ex)
        {
            await ShowMessageAsync("GUARD", "Destination is not reachable:\n" + _cfg.AppListDest + "\n\n" + ex.Message);
            return;
        }

        _exporting = true;
        SetAppBusy(true);
        try
        {
            // ---- Settings confirmation step (only when opted in) ----
            bool wantSettings = _cfg.ExportAppSettings;
            bool noMatches = false;
            List<AppSettingsCandidate>? chosen = null;
            if (wantSettings)
            {
                // Progress slot (not the main line), with an indeterminate bar
                // while the disk is walked. Focus has not moved yet, so the
                // spoken cue is a plain (undelayed) UIA notification.
                SetExportProgress("Looking for settings folders for " + picked.Count + " ticked app(s)...", indeterminate: true);
                AnnounceNotification("Looking for settings folders for " + picked.Count + " app(s)...");

                // Candidate matching walks the disk, so it runs off the UI
                // thread; sizes are measured later, inside the open dialog.
                var candidates = await System.Threading.Tasks.Task.Run(
                    () => AppSettingsExport.FindCandidates(picked));
                if (candidates.Count == 0)
                {
                    // Nothing to confirm and nothing the user can act on here;
                    // the list export still goes ahead and the summary says
                    // why no settings folders came with it.
                    noMatches = true;
                }
                else
                {
                    // The matching is heuristic; nothing is copied until the
                    // user has confirmed (and could untick) every match.
                    var dlg = new Views.AppSettingsDialog(candidates) { XamlRoot = Content.XamlRoot };
                    if (await ShowDialogAsync(dlg) != ContentDialogResult.Primary)
                    {
                        // Settled: the dialog just closed (a focus change) which
                        // would otherwise cut the cancellation message off.
                        SetExportOutcome("Export cancelled. Nothing was exported.");
                        AnnounceSettled("Export cancelled. Nothing was exported.");
                        return;
                    }
                    chosen = new List<AppSettingsCandidate>();
                    foreach (var c in candidates) if (c.Include) chosen.Add(c);
                }
            }

            // ---- Export folder: one per export, so the app list and its
            // settings are always paired and repeated exports never overwrite
            // each other. The folder name carries the uniqueness, so the list
            // inside is always app-list.json (no numbered names). ----
            var file = new AppListFile
            {
                Exported = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                Machine = Environment.MachineName
            };
            var items = new List<AppListItem>();
            foreach (var a in picked)
                items.Add(new AppListItem
                {
                    Name = a.Name, Id = a.Id, Source = a.Source, Version = a.Version,
                    Publisher = a.Publisher, InstallLocation = a.InstallLocation, PublisherUrl = a.PublisherUrl
                });
            file.Apps = items.ToArray();

            string exportDir = MakeUniqueExportDir(_cfg.AppListDest);
            Directory.CreateDirectory(exportDir);
            string path = Path.Combine(exportDir, GuardPaths.AppListFileName);
            AppListIo.Write(path, file);

            // ---- Settings copy (after the list, so a cancel above never
            // leaves either output behind) ----
            string summary = "Exported " + picked.Count + " app(s) to " + Path.GetFileName(exportDir) + ".";
            string detail = "Exported " + picked.Count + " apps to:\n" + path;
            if (wantSettings)
            {
                if (noMatches)
                {
                    string why = " No settings folders matched the ticked apps. Apps that keep settings in the registry, in ProgramData, or under a folder name unlike the app name are not found by this search.";
                    summary += why;
                    detail += "\n" + why.Trim();
                }
                else if (chosen!.Count == 0)
                {
                    summary += " No settings folders were ticked, so none were copied.";
                    detail += "\n\nNo settings folders were ticked, so none were copied.";
                }
                else
                {
                    // A determinate progress bar (by bytes, from the measured
                    // sizes) plus a spoken first line: a large folder (an Electron
                    // profile, say) otherwise copies with no feedback between
                    // per-folder lines and looks frozen, with no screen-reader cue.
                    // Per convention only the first line is spoken; the bar carries
                    // the rest and the following dialog reads the summary.
                    long totalBytes = 0;
                    foreach (var c in chosen) totalBytes += c.Bytes;
                    double barMax = totalBytes > 0 ? totalBytes : 1;
                    AppProgress.IsIndeterminate = false;
                    SetProgress(AppProgress, AppProgressLabel, barMax, 0, "Copying settings...");
                    bool announced = false;
                    var stats = await System.Threading.Tasks.Task.Run(() =>
                        AppSettingsExport.CopyCandidates(chosen, exportDir,
                            onFolder: msg => DispatcherQueue.TryEnqueue(() =>
                            {
                                AppProgressLabel.Text = msg;
                                _pageProg[1].Text = msg; ApplyPageProgress(1);
                                if (!announced) { announced = true; AnnounceSettled(msg); }
                            }),
                            onBytes: done => DispatcherQueue.TryEnqueue(() =>
                            {
                                AppProgress.Value = done;
                                _pageProg[1].Value = done; ApplyPageProgress(1);
                            })));
                    string copied = "Copied " + stats.Folders + " settings folder(s): " + stats.Files + " file(s)."
                        + (stats.SkippedFiles > 0
                            ? " " + stats.SkippedFiles + " file(s) were locked or unreadable and were skipped."
                            : "");
                    summary += " " + copied;
                    detail += "\n\n" + copied
                        + "\nThe settings are inside this export's folder. To restore later, use "
                        + "Import List and pick this app-list.json.";
                }
            }

            SettingsStore.SaveAppList(_cfg);
            // Outcome goes in the progress slot (like a backup/reinstall outcome),
            // not the main line: it is not announced here (the dialog below reads
            // it), and the main line keeps the inventory status so reading the bar
            // does not say the same thing twice.
            SetExportOutcome(summary);
            await ShowMessageAsync("GUARD", detail);
        }
        catch (Exception ex)
        {
            SetExportOutcome("Export failed: " + ex.Message);
            await ShowMessageAsync("GUARD", "Export failed:\n" + ex.Message);
        }
        finally
        {
            // Never leave a spinning/indeterminate bar behind on any exit path.
            AppProgress.IsIndeterminate = false;
            Progress(1, p => p.Indeterminate = false);
            _exporting = false;
            SetAppBusy(false);
        }
    }

    // A unique, sortable folder for one export under the chosen destination, so
    // each export's list and settings stay paired and repeats never collide.
    private static string MakeUniqueExportDir(string dest)
    {
        string baseName = "app-export-" + DateTime.Now.ToString("yyyy-MM-dd_HHmm");
        string dir = Path.Combine(dest, baseName);
        for (int n = 2; Directory.Exists(dir); n++) dir = Path.Combine(dest, baseName + "-" + n);
        return dir;
    }

    // Export status shows in the status bar's progress slot, never the main
    // status line, so reading the bar never says the same thing twice (the main
    // line keeps the inventory status, as during a backup or reinstall). The slot
    // is not a live region; spoken cues are raised separately via
    // AnnounceNotification / AnnounceSettled.
    private void SetExportProgress(string text, bool indeterminate)
    {
        AppProgress.IsIndeterminate = indeterminate;
        AppProgressLabel.Text = text;
        Progress(1, p =>
        {
            p.Indeterminate = indeterminate;
            p.Text = text;
            p.AreaVisible = true;
            p.BarVisible = true;
        });
    }

    // Terminal export state (success summary, cancellation, failure): show it in
    // the progress slot with no moving bar, and keep the slot visible so the
    // read-status-bar hotkey reports the outcome while App Management is focused.
    private void SetExportOutcome(string text)
    {
        AppProgress.IsIndeterminate = false;
        AppProgressLabel.Text = text;
        Progress(1, p =>
        {
            p.Indeterminate = false;
            p.BarVisible = false;
            p.AreaVisible = true;
            p.Text = text;
        });
    }

    private async void OnImportApps(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
        picker.FileTypeFilter.Add(".json");
        picker.FileTypeFilter.Add("*");
        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        AppListFile? f;
        try { f = AppListIo.Read(file.Path); }
        catch (Exception ex)
        {
            await ShowMessageAsync("GUARD", "That file could not be read as an app list:\n\n" + ex.Message);
            return;
        }
        if (f?.Apps == null || f.Apps.Length == 0)
        {
            await ShowMessageAsync("GUARD", "The app list is empty.");
            return;
        }

        // The imported apps live in their OWN list (this dialog), so the
        // installed-apps list behind the tab - the source for Export - is left
        // untouched. _allApps and the main list are deliberately not modified.
        var imported = new List<AppEntry>();
        int auto = 0, man = 0;
        foreach (var it in f.Apps)
        {
            var a = new AppEntry
            {
                Name = it.Name ?? "",
                Id = it.Id ?? "",
                Source = string.IsNullOrEmpty(it.Source) ? (string.IsNullOrEmpty(it.Id) ? "manual" : "winget") : it.Source!,
                Version = it.Version ?? "",
                Publisher = it.Publisher ?? "",
                InstallLocation = it.InstallLocation ?? "",
                PublisherUrl = it.PublisherUrl ?? "",
                Include = true
            };
            if (a.CanAuto) auto++; else man++;
            imported.Add(a);
        }

        // Settings bundle written beside the list (sibling AppSettings folder +
        // manifest)? Its presence enables the dialog's Reinstall & Restore option.
        string listDir = Path.GetDirectoryName(file.Path) ?? "";
        var manifest = AppSettingsRestore.TryLoadBundle(listDir);

        string mac = f.Machine ?? "?";
        string exp = f.Exported ?? "";
        string header = imported.Count + " app(s) from " + mac +
            (exp.Length > 0 ? " (" + exp + ")" : "") + ". " + auto + " reinstallable, " + man + " manual." +
            (manifest != null
                ? " A settings bundle was found beside this list."
                : " No settings bundle was found beside this list.");

        var dlg = new Views.AppImportDialog(imported, header, manifest != null) { XamlRoot = Content.XamlRoot };
        var result = await ShowDialogAsync(dlg);
        if (result == ContentDialogResult.None)
        {
            _appStatusText = "Import cancelled. Nothing was changed.";
            AnnounceAppStatus();
            return;
        }

        // Ticked winget/store apps become reinstall targets; the rest are counted
        // as manual (skipped by winget, but their settings can still be restored).
        var targets = new List<AppEntry>();
        int manual = 0;
        foreach (var a in imported)
        {
            if (!a.Include) continue;
            if (a.CanAuto && !string.IsNullOrEmpty(a.Id)) targets.Add(a);
            else manual++;
        }

        // Primary = Reinstall Selected; Secondary = Reinstall & Restore Settings.
        if (result == ContentDialogResult.Primary)
        {
            if (targets.Count == 0)
            {
                await ShowMessageAsync("GUARD", "None of the ticked apps can be reinstalled automatically.\n\nOnly \"Winget\" and \"Store\" apps reinstall automatically; \"Manual\" apps must be reinstalled by hand.");
                return;
            }
            await ExecuteReinstall(targets, restore: null, BtnAppImport);
            return;
        }

        // ---- Reinstall & Restore Settings ----
        var candidates = AppSettingsRestore.BuildCandidates(manifest!, listDir);
        if (candidates.Count == 0)
        {
            // The manifest names folders that are no longer on disk beside the
            // list; fall back to reinstalling the apps alone.
            if (targets.Count == 0)
            {
                await ShowMessageAsync("GUARD", "The settings folders named by this list could not be found beside it, and none of the ticked apps reinstall automatically. Nothing to do.");
                return;
            }
            await ShowMessageAsync("GUARD", "The settings folders named by this list could not be found beside it, so only the apps will be reinstalled.\n\nKeep the AppSettings folder next to the list file to restore settings.");
            await ExecuteReinstall(targets, restore: null, BtnAppImport);
            return;
        }

        var restoreHeader = new System.Text.StringBuilder();
        if (targets.Count > 0)
        {
            restoreHeader.Append("GUARD will reinstall " + targets.Count + " app(s) via winget, one at a time");
            if (manual > 0) restoreHeader.Append(", skipping " + manual + " ticked \"Manual\" app(s)");
            restoreHeader.Append(", then restore the ticked settings folders below. ");
        }
        else
        {
            restoreHeader.Append("None of the ticked apps reinstall automatically, so GUARD will only restore the ticked settings folders below. ");
        }
        restoreHeader.Append("A folder marked \"Replaces existing\" already exists on this PC; your current copy is renamed aside (.guard-old-) before it is replaced, so you can undo.");

        var sdlg = new Views.AppSettingsRestoreDialog(candidates, restoreHeader.ToString())
        {
            XamlRoot = Content.XamlRoot,
            Title = targets.Count > 0 ? "Reinstall and Restore App Settings" : "Restore App Settings",
            PrimaryButtonText = targets.Count > 0 ? "Reinstall & Restore" : "Restore",
        };
        if (await ShowDialogAsync(sdlg) != ContentDialogResult.Primary)
        {
            _appStatusText = "Reinstall cancelled. Nothing was changed.";
            AnnounceAppStatus();
            return;
        }

        var chosen = new List<AppSettingsRestoreCandidate>();
        foreach (var c in candidates) if (c.Include) chosen.Add(c);
        if (targets.Count == 0 && chosen.Count == 0)
        {
            _appStatusText = "Nothing was selected to reinstall or restore.";
            AnnounceAppStatus();
            return;
        }

        await ExecuteReinstall(targets, chosen.Count > 0 ? chosen : null, BtnAppImport);
    }

    // =====================================================================
    //  REINSTALL
    // =====================================================================
    // Runs the winget install phase, then (when a restore set is given) the
    // settings-restore phase, as one cancellable job under the run-feedback focus
    // discipline. Launched from the Import List dialog (see OnImportApps), not a
    // tab button, so it acts on the saved list, not the installed apps.
    // launcherButton is the button that opened that dialog, so focus returns there
    // when the job ends. restore is null for an apps-only run.
    private async System.Threading.Tasks.Task ExecuteReinstall(
        List<AppEntry> targets, List<AppSettingsRestoreCandidate>? restore, Control launcherButton)
    {
        // Belt-and-suspenders: SetAppBusy already disables Import while a run is
        // live, so this path should be unreachable during one.
        if (_reinstalling) return;
        _reinstalling = true;
        _reinstallCts = new CancellationTokenSource();
        var ct = _reinstallCts.Token;
        // Same focus discipline as SetFileBusy: enable Stop and hand it focus
        // before SetAppBusy greys out the button that launched the job, so
        // focus never gets thrown at an arbitrary neighbour.
        BtnAppStop.IsEnabled = true;
        var focused = FocusManager.GetFocusedElement(Content.XamlRoot) as Control;
        Control? launcher = ReferenceEquals(focused, launcherButton) ? launcherButton : null;
        if (launcher != null) BtnAppStop.Focus(FocusState.Programmatic);
        SetAppBusy(true);
        TxtAppOutput.Text = "";
        int restoreCount = restore?.Count ?? 0;
        int totalSteps = targets.Count + restoreCount;
        SetProgress(AppProgress, AppProgressLabel, totalSteps > 0 ? totalSteps : 1, 0, "Starting...");
        ShowStatusBarProgress(1, true);

        int ok = 0, fail = 0, attempted = 0;
        AppSettingsRestoreStats? rstats = null;
        await System.Threading.Tasks.Task.Run(() =>
        {
            // ---- Install phase ----
            for (int i = 0; i < targets.Count; i++)
            {
                if (ct.IsCancellationRequested) break;
                var app = targets[i];
                string installing = "Installing: " + app.Name + " (" + (i + 1) + " of " + targets.Count + ")";
                SetProgress(AppProgress, AppProgressLabel, totalSteps, i, installing);
                // First item only, like the backup run's start announcement.
                if (i == 0) AnnounceSettled(installing);
                AppendOut(TxtAppOutput, "\r\n=== Installing " + app.Name + "  [" + app.Id + "] ===\r\n");
                int code;
                try { code = ProcessRunner.RunWingetInstall(app.Id, s => AppendOut(TxtAppOutput, s), ct); }
                catch (Exception ex) { AppendOut(TxtAppOutput, "ERROR: " + ex.Message + "\r\n"); code = -1; }
                // A cancel mid-install kills winget, which surfaces as a nonzero
                // exit code; do not count a killed item as a failure.
                if (ct.IsCancellationRequested) break;
                attempted++;
                if (code == 0) ok++; else fail++;
                SetProgress(AppProgress, AppProgressLabel, totalSteps, attempted, "");
            }

            // ---- Restore phase (after the installs, so an app's freshly
            // installed defaults are overwritten with the restored settings, and
            // a cancel during installs skips restore entirely). Per-folder
            // progress updates the bar silently, like the export copy. ----
            if (!ct.IsCancellationRequested && restore != null)
            {
                AppendOut(TxtAppOutput, "\r\n=== Restoring app settings ===\r\n");
                int baseStep = targets.Count;
                int done = 0;
                rstats = AppSettingsRestore.RestoreCandidates(restore, msg =>
                {
                    int step = baseStep + done;
                    done++;
                    SetProgress(AppProgress, AppProgressLabel, totalSteps, step, msg);
                    AppendOut(TxtAppOutput, msg + "\r\n");
                    // When there is no install phase, the restore start is the
                    // job's first spoken line (the install loop spoke otherwise).
                    if (targets.Count == 0 && step == 0) AnnounceSettled(msg);
                }, ct);
            }
        });

        // Back on the UI thread; the DispatcherQueue is FIFO, so everything the
        // worker enqueued (output, progress) has already landed by now and the
        // summary below always prints last.
        string outcome = BuildReinstallOutcome(ct.IsCancellationRequested, targets.Count, attempted, ok, fail, rstats);
        AppProgressLabel.Text = outcome;
        Progress(1, p => p.Text = outcome);
        AppendOut(TxtAppOutput, "\r\n--- " + outcome + " ---\r\n");

        _reinstalling = false;
        _reinstallCts.Dispose();
        _reinstallCts = null;
        // Re-enable the launchers and put focus back on the launcher before Stop
        // greys out, then announce last (after the focus events) so the focus
        // announcement cannot cancel the summary speech.
        SetAppBusy(false);
        if (launcher != null && ReferenceEquals(FocusManager.GetFocusedElement(Content.XamlRoot), BtnAppStop))
            launcher.Focus(FocusState.Programmatic);
        BtnAppStop.IsEnabled = false;
        ShowStatusBarProgress(1, false);
        AnnounceSettled(outcome, 2000);
    }

    // The end-of-job line, covering whichever phases ran: a cancelled run
    // reports how far the installs got; a completed run reports install counts
    // (when any) and the restore tally (when settings were restored).
    private static string BuildReinstallOutcome(
        bool cancelled, int targetCount, int attempted, int ok, int fail, AppSettingsRestoreStats? rstats)
    {
        if (cancelled)
            return "Cancelled after " + attempted + " of " + targetCount + " app(s): " + ok +
                " installed, " + fail + " failed. Apps already installed stay installed.";

        string s = "";
        if (targetCount > 0)
            s = "Done. " + ok + " installed, " + fail + " failed.";
        if (rstats != null)
        {
            string r = "Restored " + rstats.Folders + " settings folder(s)"
                + (rstats.Replaced > 0 ? " (" + rstats.Replaced + " existing folder(s) kept aside)" : "") + ".";
            if (rstats.SkippedFolders > 0)
                r += " " + rstats.SkippedFolders + " folder(s) were in use and skipped.";
            if (rstats.SkippedFiles > 0)
                r += " " + rstats.SkippedFiles + " file(s) were locked and skipped.";
            s = s.Length > 0 ? s + " " + r : r;
        }
        return s.Length > 0 ? s : "Done.";
    }

    private void OnStopReinstall(object sender, RoutedEventArgs e) => _reinstallCts?.Cancel();
}
