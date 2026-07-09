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

// System Image page: wbadmin availability probe, image settings save and
// schedule, the elevated on-demand image run with log tailing, and the
// recovery-media / restore-help launchers.
public sealed partial class MainWindow : Window
{
    // =====================================================================
    //  SYSTEM IMAGE
    // =====================================================================
    // wbadmin is absent on some editions (notably Home). Probe once, lazily, and
    // self-disable the on-demand and scheduled imaging if it's missing; recovery
    // media and the restore help stay available regardless.
    private async void CheckImageAvailability()
    {
        bool ok = await System.Threading.Tasks.Task.Run(() => SystemImageScript.IsWbadminAvailable());
        _imageAvailable = ok;
        if (!ok)
        {
            BtnCreateImage.IsEnabled = false;
            BtnViewImages.IsEnabled = false;
            ChkImageSchedule.IsChecked = false;
            ChkImageSchedule.IsEnabled = false;
            UpdateImageScheduleEnabledState();
        }
        RefreshImageStatus(announce: false);

        // First visit with image settings saved but no image made yet: show
        // the destination space, as the File Backup page does on launch. Once
        // images exist, the status line carries the last run's health instead
        // and the space figure refreshes only on a manual save. Silent
        // (announce:false) so it does not speak over the nav's page
        // announcement; the amber dot still flags a tight destination.
        if (_imageAvailable && File.Exists(GuardPaths.SystemImageScriptPath) && !_imageDirty
            && BackupHealth.ReadLog(GuardPaths.SystemImageLogPath) is null)
            StartImageSpaceCheck(announce: false);
    }

    private void RefreshImageStatus(bool announce = true)
    {
        if (StatusBarText == null) return;
        // Terse on purpose, like RefreshScriptStatus: one bar line.
        if (!_imageAvailable)
        {
            _imageStatusBrush = new SolidColorBrush(StatusAmber);
            _imageStatusText = "System imaging is unavailable on this Windows edition (wbadmin not found). Recovery media still works.";
        }
        else if (!File.Exists(GuardPaths.SystemImageScriptPath))
        {
            _imageStatusBrush = new SolidColorBrush(StatusAmber);
            _imageStatusText = "No image settings saved yet - choose a destination and click Save Settings.";
        }
        else if (_imageDirty)
        {
            _imageStatusBrush = new SolidColorBrush(StatusAmber);
            _imageStatusText = "Unsaved changes - click Save Settings to apply them.";
        }
        else if (_imageTaskStale)
        {
            _imageStatusBrush = new SolidColorBrush(StatusAmber);
            _imageStatusText = "GUARD's folder has moved - click Save Settings to repoint the scheduled image (needs Administrator approval).";
        }
        else
        {
            // Same health-first idea as the File Backup status: once images
            // have run, report how the last one went rather than when the
            // settings file was written. The SYSTEM scheduled image cannot
            // toast (session 0), so this line is where its outcome surfaces.
            var now = DateTime.Now;
            var last = BackupHealth.ReadLog(GuardPaths.SystemImageLogPath);
            if (last is null)
            {
                _imageStatusBrush = new SolidColorBrush(StatusGreen);
                _imageStatusText = "Image settings saved. No image created yet.";
            }
            else
            {
                string when = BackupHealth.FriendlyWhen(last.When, now);
                var expected = _cfg.ImageScheduleEnabled
                    ? BackupHealth.PreviousScheduledImage(_cfg.ImageCadence, _cfg.ImageWeeklyDay,
                        _cfg.ImageMonthlyDay, _cfg.ImageScheduleTime, now)
                    : null;
                bool amber = true;
                string text;
                if (last.Outcome == RunOutcome.Errors)
                    text = "Last system image had errors (" + when + ") - open the last log.";
                else if (last.Outcome == RunOutcome.DidNotComplete)
                    text = "Last system image did not complete (" + when + ") - open the last log.";
                else if (BackupHealth.IsOverdue(last, expected, now))
                    text = "System image overdue - last succeeded " + when + ".";
                else
                {
                    amber = false;
                    text = "Last system image succeeded " + when + ".";
                }
                _imageStatusBrush = new SolidColorBrush(amber ? StatusAmber : StatusGreen);
                _imageStatusText = text;
            }
        }
        UpdateImageSaveEnabled();
        CommitPageStatus(2, announce);
    }

    // Mirror of UpdateSaveEnabled for the System Image page: disable Save once the
    // saved script matches the config, keep it enabled for unsaved edits or a
    // first save. A running image owns the button (SetImageBusy). On disabling a
    // focused Save, fall to Create Image (or Recovery Media if imaging is
    // unavailable and Create Image is disabled) so focus is never stranded.
    private void UpdateImageSaveEnabled()
    {
        if (BtnSaveImage == null || _imageRunning) return;
        bool enable = _imageDirty || _imageTaskStale || !File.Exists(GuardPaths.SystemImageScriptPath);
        // See UpdateSaveEnabled: guard on the XAML root so the constructor-time
        // seeded status does not query focus before Content.XamlRoot exists.
        if (!enable && BtnSaveImage.IsEnabled && Content?.XamlRoot is not null &&
            ReferenceEquals(FocusManager.GetFocusedElement(Content.XamlRoot), BtnSaveImage))
            (BtnCreateImage.IsEnabled ? BtnCreateImage : BtnRecoveryMedia).Focus(FocusState.Programmatic);
        BtnSaveImage.IsEnabled = enable;
    }

    private void SetImageStatusText(string text, bool announce = true)
    {
        _imageStatusText = text;
        CommitPageStatus(2, announce);
    }

    // ---- dirty tracking ----
    private void OnImageDirtyChanged(object sender, TextChangedEventArgs e)
    {
        UpdateImageTargetKindLabel();
        _imageDirty = true;
        RefreshImageStatus();
    }
    private void OnImageDirtySelection(object sender, SelectionChangedEventArgs e) { _imageDirty = true; RefreshImageStatus(); }

    // The destination kind is derived from the path (a UNC is a share, anything
    // else a local/external disk), so the two can never disagree. The caption
    // reflects what the current path means for retention.
    private static string ClassifyImageTarget(string? path) =>
        (path ?? "").Trim().StartsWith(@"\\") ? "NetworkShare" : "LocalDisk";

    private void UpdateImageTargetKindLabel()
    {
        if (LblImageTargetKind == null) return;
        string p = (TxtImageTarget.Text ?? "").Trim();
        string msg;
        if (p.Length == 0)
            msg = "Enter a drive such as E:\\ for a local or external disk, or a path such as \\\\server\\share for a network share.";
        else if (p.StartsWith(@"\\"))
            msg = "This is a network share: only the most recent image is kept, and a scheduled image cannot sign in to it.";
        else
        {
            msg = "This is a local or external disk: several past images are kept automatically.";
            // wbadmin only takes a volume for local disks (TargetArg reduces
            // the path to "E:"), so a typed or browsed subfolder is silently
            // ignored - say so rather than let the user hunt for it.
            if (p.Length > 3 && p[1] == ':')
                msg += " Images always go to the drive's root (in a WindowsImageBackup folder); the folder part of this path is ignored.";
        }
        LblImageTargetKind.Text = msg;
        // Spoken when focus lands on the destination field (the visible captions are
        // AccessibilityView=Raw, so this is how a screen reader hears the kind).
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(TxtImageTarget, msg);
    }
    private void OnImageMonthlyDayChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) { _imageDirty = true; RefreshImageStatus(); }
    private void OnImageTimeChanged(TimePicker sender, TimePickerSelectedValueChangedEventArgs args) { _imageDirty = true; RefreshImageStatus(); }

    private void OnImageScheduleEnabledChanged(object sender, RoutedEventArgs e)
    {
        UpdateImageScheduleEnabledState();
        _imageDirty = true;
        RefreshImageStatus();
    }

    private void OnImageCadenceChanged(object sender, RoutedEventArgs e)
    {
        UpdateImageCadenceRows();
        _imageDirty = true;
        RefreshImageStatus();
    }

    private void UpdateImageScheduleEnabledState()
    {
        // StackPanel has no IsEnabled (it is a Panel, not a Control), so grey out
        // the interactive leaves directly, matching UpdateScheduleEnabledState.
        bool on = ChkImageSchedule.IsChecked == true;
        if (ImageCadenceRadios != null) ImageCadenceRadios.IsEnabled = on;
        if (CmbImageWeeklyDay != null) CmbImageWeeklyDay.IsEnabled = on;
        if (NumImageMonthlyDay != null) NumImageMonthlyDay.IsEnabled = on;
        if (TimeImage != null) TimeImage.IsEnabled = on;
    }

    private void UpdateImageCadenceRows()
    {
        if (ImageWeeklyDayRow == null || ImageMonthlyDayRow == null) return;
        ImageWeeklyDayRow.Visibility = RbImageWeekly.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ImageMonthlyDayRow.Visibility = RbImageMonthly.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- harvest / save ----
    private void HarvestImageUi()
    {
        _cfg.ImageTarget = (TxtImageTarget.Text ?? "").Trim();
        _cfg.ImageTargetKind = ClassifyImageTarget(_cfg.ImageTarget);
        _cfg.ImageScheduleEnabled = ChkImageSchedule.IsChecked == true;
        _cfg.ImageCadence = RbImageMonthly.IsChecked == true ? "Monthly"
            : RbImageDaily.IsChecked == true ? "Daily" : "Weekly";
        _cfg.ImageScheduleTime = FormatScheduleTime(TimeImage.SelectedTime, _cfg.ImageScheduleTime);
        int idx = CmbImageWeeklyDay.SelectedIndex;
        if (idx >= 0 && idx < _imageDayOrder.Length) _cfg.ImageWeeklyDay = _imageDayOrder[idx];
        if (!double.IsNaN(NumImageMonthlyDay.Value))
            _cfg.ImageMonthlyDay = Math.Clamp((int)NumImageMonthlyDay.Value, 1, 28);
    }

    private static string ImageScheduleSignature(Settings c) =>
        (c.ImageScheduleEnabled ? "1" : "0") + "|" + c.ImageCadence + "|" +
        c.ImageScheduleTime + "|" + c.ImageWeeklyDay + "|" + c.ImageMonthlyDay;

    // Returns false (after showing a message) when a required value is missing or
    // the target is the system drive. Writes the ini + image script, then applies
    // the scheduled task only when a schedule-affecting setting changed (the apply
    // needs a UAC prompt, so an unchanged save must not re-prompt).
    private async System.Threading.Tasks.Task<bool> SaveImageAsync()
    {
        HarvestImageUi();
        if (string.IsNullOrEmpty(_cfg.ImageTarget))
        {
            await ShowMessageAsync("GUARD", "Enter an image destination first.\n\nType a drive (like E:\\) or a network share path, or use Browse to pick one.");
            return false;
        }
        // Same reasoning as the backup destination: a quote would corrupt the
        // generated script's set "TARGET=..." line.
        if (_cfg.ImageTarget.Contains('"'))
        {
            await ShowMessageAsync("GUARD", "The image destination cannot contain quote (\") characters.");
            return false;
        }
        if (_cfg.ImageTargetKind == "LocalDisk" && SystemImageScript.IsSystemDrive(_cfg.ImageTarget))
        {
            await ShowMessageAsync("GUARD", "The image destination cannot be on the same drive as Windows.\n\nA system image includes the Windows drive, so it must be written to a separate disk or a network share. Choose another destination.");
            return false;
        }
        if (_imageSaving) return false;
        _imageSaving = true;
        try
        {
            // Section-scoped: never commits the File Backup page's unsaved
            // edits (see SettingsStore.SaveSystemImage).
            SettingsStore.SaveSystemImage(_cfg);
            SystemImageScript.Write(_cfg);
            _imageDirty = false;
            // Explicit confirmation, like SaveAllAsync's: the health line
            // returns at the next launch, page revisit, or run end.
            _imageStatusBrush = new SolidColorBrush(StatusGreen);
            SetImageStatusText("Image settings saved.");
            UpdateImageSaveEnabled();
            string sig = ImageScheduleSignature(_cfg);
            if (sig != _lastImageScheduleSig)
            {
                var applied = await System.Threading.Tasks.Task.Run(() => ScheduledTasks.ApplySystemImage(_cfg));
                _imageTaskError = applied.Error;
                if (applied.Error == null)
                {
                    _lastImageScheduleSig = sig;
                    // A successful re-apply also repoints a task left behind by
                    // a folder move (the stale flag forced sig to differ).
                    _imageTaskStale = false;
                    UpdateImageSaveEnabled();
                }
                LblImageNextRun.Text = applied.NextRun == null
                    ? "Next run: (no scheduled image)" : "Next run: " + applied.NextRun;
            }
            else
            {
                _imageTaskError = null;
            }
            return true;
        }
        finally { _imageSaving = false; }
    }

    private async void OnSaveImage(object sender, RoutedEventArgs e)
    {
        if (!await SaveImageAsync()) return;
        if (_imageTaskError != null)
        {
            await ShowMessageAsync("GUARD", "Settings saved, but scheduling the system image reported a problem:\n\n" + _imageTaskError);
            return;
        }
        if (_cfg.ImageScheduleEnabled && _cfg.ImageTargetKind == "NetworkShare")
            await ShowMessageAsync("GUARD", "Note: a scheduled image runs as SYSTEM, which cannot supply network share sign-in details. If the scheduled image cannot reach the share, store images on a local or external disk for the schedule, or create images to the share on demand.");
        StartImageSpaceCheck();
    }

    // Advisory free-space check appended to the saved-status line, like the File
    // Backup space check. No precise image-size estimate (the source is the whole
    // system drive); a low free-space floor is flagged as a warning.
    private async void StartImageSpaceCheck(bool announce = true)
    {
        int seq = ++_imageSpaceSeq;
        string baseText = _imageStatusText;
        SetImageStatusText(baseText + " Checking free space...", announce);
        long? free = await System.Threading.Tasks.Task.Run(() => SaveValidation.TryGetFreeSpace(_cfg.ImageTarget));
        if (seq != _imageSpaceSeq || _imageDirty) return;
        string extra;
        if (free is long freeBytes)
        {
            extra = " Free space: " + SaveValidation.FormatBytes(freeBytes) + ".";
            if (freeBytes < 32L * 1024 * 1024 * 1024)
            {
                extra += " Warning: may be too small for a full image.";
                _imageStatusBrush = new SolidColorBrush(StatusAmber);
            }
        }
        else
        {
            extra = " Free space could not be checked.";
        }
        SetImageStatusText(baseText + extra, announce);
    }

    // ---- run ----
    private async void OnCreateImageNow(object sender, RoutedEventArgs e) => await RunImage();

    private async System.Threading.Tasks.Task RunImage()
    {
        if (_imageRunning)
        {
            await ShowMessageAsync("GUARD", "A system image is already running. Wait for it to finish, or press Stop Image to cancel it.");
            return;
        }
        if (!_imageAvailable)
        {
            await ShowMessageAsync("GUARD", "System imaging is not available on this edition of Windows (the wbadmin tool was not found).");
            return;
        }
        if (!await SaveImageAsync()) return;
        if (_imageTaskError != null)
            await ShowMessageAsync("GUARD", "Settings saved, but scheduling the system image reported a problem:\n\n" + _imageTaskError);
        if (!File.Exists(GuardPaths.SystemImageScriptPath))
        {
            await ShowMessageAsync("GUARD", "Image script not found. Click Save Settings first.");
            return;
        }
        if (!await ShowConfirmAsync("GUARD",
            "Create a full system image now?\n\nThis can take a long time and needs Administrator approval. You can keep using your PC while it runs.",
            "Create", "Cancel")) return;

        TxtImageOutput.Text = "";
        AppendOut(TxtImageOutput, "> Creating system image to " + _cfg.ImageTarget + "\r\n");
        // Tail only THIS run's lines (startAtEnd). The previous run's log is
        // still on disk until the elevated script truncates it after the UAC
        // prompt; reading from 0 would replay it, inflating the volume tally
        // so the bar jumped straight to 100%. LogTail's shrink guard rewinds
        // to 0 when the truncation lands.
        _imageTail = new LogTail(GuardPaths.SystemImageLogPath, startAtEnd: true);
        _imageTotalVols = 0;
        _imageDoneVols = 0;
        _imageOverall = 0;
        _imageStopRequested = false;
        // Indeterminate until the first volume percent arrives: wbadmin spends the
        // first stretch taking a VSS snapshot with no percentage, and a determinate
        // 0% there reads as stalled.
        SetImageProgressIndeterminate("Starting system image...");
        ShowStatusBarProgress(2, true);
        _imageRunning = true;
        SetImageBusy(true);

        string? err = null;
        bool ok = false;
        try
        {
            // Output can't cross the elevation boundary (see RunPowerShellElevated),
            // so the elevated script writes to the log and we tail it for progress
            // while it runs; the exit code is the authoritative result.
            string elevated = "& cmd.exe /c '\"" + GuardPaths.SystemImageScriptPath + "\"'; exit $LASTEXITCODE";
            var runTask = System.Threading.Tasks.Task.Run(() => ProcessRunner.RunPowerShellElevated(elevated, out err));
            while (!runTask.IsCompleted)
            {
                await System.Threading.Tasks.Task.Delay(700);
                PumpImageLog();
            }
            PumpImageLog();
            ok = await runTask;
        }
        catch (Exception ex) { err = ex.Message; }
        finally
        {
            ShowStatusBarProgress(2, false);
            _imageRunning = false;
            SetImageBusy(false);
            // The elevated run just rewrote the image log; repaint the health
            // line silently (the outcome announcement below does the talking).
            RefreshImageStatus(announce: false);
        }

        string outcome;
        if (ok)
        {
            outcome = "System image completed successfully.";
            SetImageProgressDeterminate(100, outcome);
        }
        else if (_imageStopRequested)
        {
            outcome = "System image stopped.";
            SetImageProgressDeterminate(_imageOverall, outcome);
        }
        else if (err != null && err.Contains("declined"))
        {
            outcome = "System image cancelled - Administrator approval was declined.";
            SetImageProgressDeterminate(0, "System image cancelled.");
        }
        else
        {
            outcome = "System image failed. See the output details and the last log.";
            SetImageProgressDeterminate(_imageOverall, "System image failed.");
        }
        AppendOut(TxtImageOutput, "\r\n--- " + outcome + " ---\r\n");
        if (ok && _cfg.ImageTargetKind == "NetworkShare")
        {
            string? winre = await System.Threading.Tasks.Task.Run(() => SystemImageScript.ResolveUncToIp(_cfg.ImageTarget));
            AppendOut(TxtImageOutput,
                "\r\nTo restore from this image later: boot the recovery USB, choose System Image\r\n" +
                "Recovery, and when it asks for the network location enter  " + (winre ?? _cfg.ImageTarget) + "\r\n" +
                "(the recovery tool cannot look up server names, so use the IP address" + (winre != null ? " shown" : "") + ").\r\n");
        }
        AnnounceSettled(outcome, 2000);
    }

    // Read new lines appended to the image log by the elevated run and feed
    // them to the parser; LogTail handles offsets, rewinds and partial lines.
    private void PumpImageLog()
    {
        if (_imageTail == null) return;
        foreach (var line in _imageTail.ReadNewLines())
            HandleImageLine(line);
    }

    // wbadmin reports progress per volume ("copied (NN%)"), resetting to 0% for each
    // new volume. Showing the raw per-volume percent makes the bar jump to 100% when
    // the small EFI partition finishes, then restart at 0% - which looks like it
    // finished and started over. Instead, fold the per-volume percents into one
    // monotonic overall figure using the volume count from wbadmin's plan line.
    // Everything that is not a percent line is echoed to the output box.
    private void HandleImageLine(string? data)
    {
        if (string.IsNullOrWhiteSpace(data)) return;

        // Volume count from the plan line, e.g. "This will back up (EFI System
        // Partition),(C:) to E:." Counting "(" is robust to comma/spacing style.
        if (_imageTotalVols == 0)
        {
            var plan = Regex.Match(data, @"back\s*up\s+(.+?)\s+to\b", RegexOptions.IgnoreCase);
            if (plan.Success)
            {
                int vols = 0;
                foreach (char c in plan.Groups[1].Value) if (c == '(') vols++;
                if (vols > 0) _imageTotalVols = vols;
            }
        }
        // A finished volume (not the final "backup operation completed" summary).
        if (Regex.IsMatch(data, @"backup of volume.*completed successfully", RegexOptions.IgnoreCase))
            _imageDoneVols++;

        var m = Regex.Match(data, @"copied\s*\(\s*(\d+)\s*%\s*\)", RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out int pct))
        {
            // A stop was requested but wbadmin is still winding down; hold the
            // "Stopping..." state rather than let a late percent line move the bar,
            // which would look like the image resumed.
            if (_imageStopRequested) { SetImageProgressIndeterminate("Stopping system image..."); return; }
            if (_imageTotalVols > 0)
            {
                double overall = (_imageDoneVols + pct / 100.0) / _imageTotalVols * 100.0;
                if (overall < _imageOverall) overall = _imageOverall;   // never go backwards
                if (overall > 100) overall = 100;
                _imageOverall = overall;
                SetImageProgressDeterminate(overall, "Creating system image... " + (int)overall + "%");
            }
            else
            {
                // Total not known yet (plan line unseen): keep it indeterminate
                // rather than show a misleading per-volume percent.
                SetImageProgressIndeterminate("Creating system image...");
            }
            return;
        }
        AppendOut(TxtImageOutput, data + "\r\n");
    }

    private void SetImageProgressDeterminate(double pct, string text)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ImageProgress.IsIndeterminate = false;
            ImageProgress.Maximum = 100; ImageProgress.Value = pct;
            ImageProgressLabel.Text = text;
        });
        // Visibility stays with ShowStatusBarProgress: this also paints the post-run
        // outcome (100% / stopped / failed) after the bar has been hidden, and must
        // not re-show it.
        Progress(2, p => { p.Indeterminate = false; p.Max = 100; p.Value = pct; p.Text = text; });
    }

    private void SetImageProgressIndeterminate(string text)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ImageProgress.IsIndeterminate = true;
            ImageProgressLabel.Text = text;
        });
        Progress(2, p => { p.Indeterminate = true; p.Text = text; });
    }

    // Mirror of SetFileBusy; the shared Begin/EndRunBusy carry the focus
    // discipline (see MainWindow.xaml.cs).
    private void SetImageBusy(bool busy)
    {
        SetNavBusy(2, busy);
        if (busy)
        {
            _imageRunLauncher = BeginRunBusy(BtnStopImage, BtnSaveImage, BtnCreateImage);
            BtnSaveImage.IsEnabled = false;
            BtnCreateImage.IsEnabled = false;
            BtnRecoveryMedia.IsEnabled = false;
            BtnViewImages.IsEnabled = false;
        }
        else
        {
            BtnCreateImage.IsEnabled = _imageAvailable;
            BtnRecoveryMedia.IsEnabled = true;
            BtnViewImages.IsEnabled = _imageAvailable && !_imageListing;
            UpdateImageSaveEnabled();
            EndRunBusy(BtnStopImage, _imageRunLauncher);
            _imageRunLauncher = null;
        }
    }

    // The elevated image can't be killed from this un-elevated process, so stopping
    // goes through wbadmin's own "stop job", which needs its own elevation. The
    // running image then exits with an error, which the run treats as "stopped".
    private async void OnStopImage(object sender, RoutedEventArgs e)
    {
        if (!_imageRunning || _imageStopRequested) return;
        _imageStopRequested = true;
        // Stopping needs its own elevation (a second UAC prompt) and wbadmin then
        // takes a few seconds to wind down, so the run does not end the instant Stop
        // is pressed. Show a clear "stopping" state at once - bar to indeterminate
        // and spoken - instead of leaving the bar creeping as if nothing happened.
        // HandleImageLine freezes further percent updates while _imageStopRequested
        // is set, and the run's end path swaps in "System image stopped."
        SetImageProgressIndeterminate("Stopping system image...");
        AnnounceNotification("Stopping system image...");
        string? err = null;
        bool ok = await System.Threading.Tasks.Task.Run(
            () => ProcessRunner.RunPowerShellElevated("wbadmin stop job -quiet; exit 0", out err));
        if (!ok)
        {
            _imageStopRequested = false;
            // The image is still running; resume showing its real progress.
            SetImageProgressDeterminate(_imageOverall, "Creating system image... " + (int)_imageOverall + "%");
            await ShowMessageAsync("GUARD", "Could not stop the system image"
                + (err != null ? " - " + err : "") + "\n\nIt will keep running.");
        }
    }

    // ---- existing images ----
    private bool _imageListing;

    // Lists what wbadmin already holds on the destination. wbadmin needs
    // Administrator even to QUERY, so this is a button (one consented UAC per
    // click), not an automatic probe; output comes back through a log file
    // like the image run's, since it cannot cross the elevation boundary.
    private async void OnViewExistingImages(object sender, RoutedEventArgs e)
    {
        if (_imageListing || _imageRunning) return;
        if (!_imageAvailable)
        {
            await ShowMessageAsync("GUARD", "System imaging is not available on this edition of Windows (the wbadmin tool was not found).");
            return;
        }
        HarvestImageUi();
        if (string.IsNullOrEmpty(_cfg.ImageTarget))
        {
            await ShowMessageAsync("GUARD", "Enter an image destination first.\n\nType a drive (like E:\\) or a network share path, or use Browse to pick one.");
            return;
        }
        if (_cfg.ImageTarget.Contains('"'))
        {
            await ShowMessageAsync("GUARD", "The image destination cannot contain quote (\") characters.");
            return;
        }

        _imageListing = true;
        BtnViewImages.IsEnabled = false;
        TxtImageOutput.Text = "";
        AppendOut(TxtImageOutput, "> Listing system images on " + _cfg.ImageTarget + "\r\n\r\n");
        AnnounceNotification("Checking the destination for existing system images. This needs Administrator approval.");
        string target = SystemImageScript.TargetArg(_cfg);
        string log = GuardPaths.ImageVersionsLogPath;
        // *> catches wbadmin's stderr too ("No backups were found..." lands
        // there); the exit code is not trusted for success (localized wbadmin
        // uses it inconsistently for the empty case), the text is the answer.
        string script =
            "& wbadmin get versions ('-backupTarget:' + '" + PsQuote(target) + "') *> '" + PsQuote(log) + "'\n" +
            "exit 0";
        string? err = null;
        bool ok;
        try
        {
            ok = await System.Threading.Tasks.Task.Run(
                () => ProcessRunner.RunPowerShellElevated(script, out err));
        }
        finally
        {
            _imageListing = false;
            BtnViewImages.IsEnabled = !_imageRunning;
        }

        string outcome;
        if (!ok)
        {
            outcome = err != null && err.Contains("declined")
                ? "Image list cancelled - Administrator approval was declined."
                : "Could not list the images" + (err != null ? " - " + err : ".");
            AppendOut(TxtImageOutput, outcome + "\r\n");
        }
        else
        {
            string text = "";
            try { text = File.ReadAllText(log).Trim(); } catch { }
            if (text.Length == 0) text = "(wbadmin returned no output.)";
            AppendOut(TxtImageOutput, text + "\r\n");
            // "Backup time:" opens each version block in wbadmin's listing;
            // counting them beats parsing free text. Localized output just
            // falls back to the neutral wording.
            int count = CountOccurrences(text, "Backup time:");
            outcome = count > 0
                ? "Found " + count + " system image version" + (count == 1 ? "" : "s") + " on the destination. Details are in the output."
                : "The image list is ready in the output details.";
        }
        AnnounceSettled(outcome, 2000);
    }

    private static int CountOccurrences(string text, string token)
    {
        int n = 0;
        for (int i = text.IndexOf(token, StringComparison.OrdinalIgnoreCase); i >= 0;
             i = text.IndexOf(token, i + token.Length, StringComparison.OrdinalIgnoreCase)) n++;
        return n;
    }

    private static string PsQuote(string s) => s.Replace("'", "''");

    private async void OnBrowseImageTarget(object sender, RoutedEventArgs e) => await BrowseInto(TxtImageTarget);
    private async void OnTestImageTarget(object sender, RoutedEventArgs e) => await TestConnection(TxtImageTarget.Text);
    private void OnOpenImageLog(object sender, RoutedEventArgs e) => OpenPath(GuardPaths.SystemImageLogPath, "No log found yet. Create a system image first.");

    private async void OnRestoreHelp(object sender, RoutedEventArgs e)
    {
        string target = (TxtImageTarget.Text ?? "").Trim();
        var dlg = new Views.SystemImageRestoreHelpDialog(target) { XamlRoot = Content.XamlRoot };

        // Resolve the share's server name to an IP off the UI thread so a slow or
        // failing DNS lookup never delays the dialog; the await resumes on the UI
        // thread, upgrading the text once it lands (no-op if it could not resolve).
        if (ClassifyImageTarget(target) == "NetworkShare")
            _ = ResolveRestoreIpAsync(dlg, target);

        await ShowDialogAsync(dlg);
    }

    private static async System.Threading.Tasks.Task ResolveRestoreIpAsync(
        Views.SystemImageRestoreHelpDialog dlg, string target)
    {
        string? ip = await System.Threading.Tasks.Task.Run(() => SystemImageScript.ResolveUncToIp(target));
        dlg.SetResolvedIp(ip);
    }

    private async void OnCreateRecoveryMedia(object sender, RoutedEventArgs e)
    {
        var dlg = new Views.RecoveryMediaDialog { XamlRoot = Content.XamlRoot, WindowHandle = WindowHandle };
        await ShowDialogAsync(dlg);
    }
}
